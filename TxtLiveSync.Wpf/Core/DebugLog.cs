using System;
using System.IO;

namespace TxtLiveSync;

/// <summary>
/// 轻量诊断日志：只有环境变量 TXTLS_DEBUG=1（兼容旧的 TXTLS_DRAGDEBUG=1）时才写
/// %TEMP%\txtls_drag.log。用于自动化测试时确认事件到底有没有进来。
/// </summary>
public static class DebugLog
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("TXTLS_DEBUG") == "1" ||
        Environment.GetEnvironmentVariable("TXTLS_DRAGDEBUG") == "1";

    private static readonly object Gate = new();

    public static void Write(string s)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "txtls_drag.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志失败无所谓
        }
    }
}
