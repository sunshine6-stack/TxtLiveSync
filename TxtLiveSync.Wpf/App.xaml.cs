using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace TxtLiveSync;

public partial class App : Application
{
    private readonly List<string> _pending = new();   // 窗口还没就绪时收到的文件，先攒着
    private bool _ready;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 中文路径/GBK 读写需要注册代码页提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogError(args.ExceptionObject as Exception);

        base.OnStartup(e);

        // 双击 .txt 时，如果已经有窗口在跑，就把文件交给它，自己直接退出
        if (TryHandOverToRunningInstance(e.Args)) return;

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    /// <summary>
    /// 返回 true 表示"活已经交出去了，本次启动到这就结束"。
    /// 任何一步不成立都返回 false，照常自己开窗口。
    /// </summary>
    private bool TryHandOverToRunningInstance(string[] args)
    {
        var cfg = AppConfig.Load();
        if (!cfg.SingleInstance) return false;

        if (SingleInstance.EnsureOwner(PostFiles))
            return false;                        // 我是第一个，继续走正常启动

        var files = args
            .Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-'))
            .Select(a =>
            {
                try { return Path.GetFullPath(a.Trim('"')); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
            })
            .Where(p => p is not null && File.Exists(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!SingleInstance.TryForward(files)) return false;   // 对面没在听，自己开

        Shutdown(0);
        return true;
    }

    // ---------------------------------------------------------------- 来自其它实例的请求

    /// <summary>管道线程回调：转到 UI 线程处理。</summary>
    private static void PostFiles(string[] paths) => (Current as App)?.QueueFiles(paths);

    /// <summary>转发来的文件（界面里开/关单实例时也走这条）。</summary>
    internal void OpenFromOther(string[] paths) => QueueFiles(paths);

    private void QueueFiles(string[] paths)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => QueueFiles(paths)));
            return;
        }

        if (!_ready || MainWindow is not MainWindow win)
        {
            _pending.AddRange(paths);
            return;
        }

        win.OpenFromOtherInstance(paths);
    }

    /// <summary>主窗口 Loaded 完成后调用：把攒下的转发请求放出去。</summary>
    internal void MarkReady()
    {
        _ready = true;
        if (_pending.Count == 0) return;

        var paths = _pending.ToArray();
        _pending.Clear();

        if (MainWindow is MainWindow win) win.OpenFromOtherInstance(paths);
    }

    // ---------------------------------------------------------------- 错误

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show(
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n详情已写入日志。",
            "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    internal static void LogError(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.AppendAllText(
                Path.Combine(AppConfig.Dir, "error.log"),
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n",
                Encoding.UTF8);
        }
        catch (IOException)
        {
            // 日志本身失败就算了，不要因此再抛异常
        }
    }
}
