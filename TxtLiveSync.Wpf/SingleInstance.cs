using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace TxtLiveSync;

/// <summary>
/// 单实例：双击 .txt 时如果已经有窗口开着，就把文件转发给它、自己立刻退出。
///
/// 做法：第一个实例持有命名互斥体并开一个命名管道；后来的实例连上管道，
/// 发一行"置顶"标记 + 若干行完整路径，然后退出。
/// 互斥体/管道名里带 exe 路径的哈希，所以不同目录下的副本（比如桌面 run 里那份）互不干扰。
/// 任何一个环节出错都退回"各开一个窗口"的老行为，不会把打开文件这件事搞挂。
/// </summary>
internal static class SingleInstance
{
    /// <summary>只置顶、不带文件的请求。</summary>
    private const string BringFrontLine = "\u0001";

    private const int AsfwAny = -1;   // AllowSetForegroundWindow 的"任意进程"常量

    private static Mutex? _mutex;
    private static CancellationTokenSource? _cts;
    private static Task? _loop;

    public static bool IsOwner => _mutex is not null;

    // ---------------------------------------------------------------- 名字

    /// <summary>
    /// 用 exe 完整路径（小写）的哈希做区分。
    /// 单文件发布时它就是 exe 本身；`dotnet run` 时它是 dotnet.exe，改用自己的程序集目录。
    /// （net48 没有 Environment.ProcessPath，用 Process.MainModule 取，两条路都走同一个分支逻辑。）
    /// </summary>
    private static string Key
    {
        get
        {
            var exe = CurrentExePath();
            if (exe.Length == 0 ||
                string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                exe = AppContext.BaseDirectory;   // 单文件发布时这里就是 exe 所在目录
            }
            if (exe.Length == 0) exe = "TxtLiveSync";

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(exe.ToLowerInvariant()));
            return Compat.ToHex(hash).Substring(0, 16);
        }
    }

    private static string CurrentExePath()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.MainModule?.FileName ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            return "";
        }
    }

    private static string MutexName => $@"Local\TxtLiveSync.Instance.{Key}";

    private static string PipeName => $"TxtLiveSync.Instance.{Key}";

    // ---------------------------------------------------------------- 占有

    /// <summary>尝试成为唯一实例；已经有别的实例持有时返回 false。</summary>
    public static bool TryBecomeOwner()
    {
        if (_mutex is not null) return true;

        try
        {
            var m = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                m.Dispose();          // 没抢到，别把这个句柄留住
                return false;
            }

            _mutex = m;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            App.LogError(ex);         // 环境不允许就算了，按多实例跑
            return false;
        }
    }

    /// <summary>占有并开始接客；抢不到返回 false。</summary>
    public static bool EnsureOwner(Action<string[]> onPaths)
    {
        if (!TryBecomeOwner()) return false;
        StartServer(onPaths);
        return true;
    }

    /// <summary>放掉独占（设置里关掉单实例时用）。</summary>
    public static void ReleaseOwnership()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _cts = null;
        _loop = null;

        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* 不是当初那个线程，忽略 */ }
        _mutex.Dispose();
        _mutex = null;
    }

    // ---------------------------------------------------------------- 服务端

    public static void StartServer(Action<string[]> onPaths)
    {
        if (_loop is not null || _mutex is null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => ServerLoopAsync(onPaths, token), token);
    }

    private static async Task ServerLoopAsync(Action<string[]> onPaths, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await WaitForConnectionAsync(server, token).ConfigureAwait(false);

                string text;
                using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true))
                {
                    text = await ReadToEndAsync(reader, token).ConfigureAwait(false);
                }

                var paths = new List<string>();
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line == BringFrontLine) continue;
                    paths.Add(line);
                }

                onPaths(paths.ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // 对方中途断开（关窗口、被杀），继续等下一个
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                try { await Task.Delay(200, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    /// <summary>
    /// 等管道连上来。net48 的 WaitForConnectionAsync 没有 CancellationToken 重载，
    /// 用"取消时把管道 Dispose 掉"来打断（会抛 ObjectDisposedException，被上层当成退出信号）。
    /// </summary>
    private static async Task WaitForConnectionAsync(NamedPipeServerStream server, CancellationToken token)
    {
#if NETFRAMEWORK
        using (token.Register(() =>
        {
            try { server.Dispose(); }
            catch (ObjectDisposedException) { }
        }))
        {
            await server.WaitForConnectionAsync().ConfigureAwait(false);
        }
#else
        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
#endif
    }

    private static async Task<string> ReadToEndAsync(StreamReader reader, CancellationToken token)
    {
#if NETFRAMEWORK
        return await reader.ReadToEndAsync().ConfigureAwait(false);
#else
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
#endif
    }

    // ---------------------------------------------------------------- 客户端

    /// <summary>
    /// 把文件转发给已在运行的实例。成功返回 true（调用方应当直接退出）；
    /// 连不上（对面没在听）返回 false，调用方照常自己开窗口。
    /// </summary>
    public static bool TryForward(IReadOnlyList<string> paths, int timeoutMs = 2000)
    {
        try
        {
            // 我们是由资源管理器拉起来的，有资格授权那个进程抢前台
            AllowSetForegroundWindow(AsfwAny);

            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(BringFrontLine);
            foreach (var p in paths) writer.WriteLine(p);
            writer.Flush();
            // 离开 using 时关管道，对方读到 EOF 才开始处理

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
                                       or OperationCanceledException or NotSupportedException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 前台

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>把窗口从最小化/背后拉到前台。</summary>
    internal static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (!w.IsVisible) w.Show();

        w.Activate();

        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;

        SetForegroundWindow(h);
        if (GetForegroundWindow() == h) return;

        // 系统不放行（前台锁定）时的兜底：短暂置顶再取消
        var wasTopmost = w.Topmost;
        w.Topmost = true;
        w.Topmost = wasTopmost;
        w.Activate();
    }
}
