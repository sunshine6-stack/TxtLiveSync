using System;
using System.IO;
using System.Security.Cryptography;

namespace TxtLiveSync;

public enum SyncKind
{
    None,
    Reload,      // 采纳磁盘版本
    Prompt,      // 需要询问用户
    Missing,     // 文件消失/不可读
    Recovered,   // 文件回来了
}

public sealed class SyncEvent
{
    public SyncKind Kind { get; init; }
    public string Message { get; init; } = "";
    public TextFile? File { get; init; }

    public static readonly SyncEvent None = new() { Kind = SyncKind.None };
}

/// <summary>
/// 文件监视与冲突裁决。与界面解耦：只接收"当前编辑区文本"，返回事件，
/// 便于脱离 UI 直接测试。
/// </summary>
public sealed class SyncEngine
{
    private readonly record struct Sig(long Ticks, long Size);

    private Sig? _sig;          // 已确认的最新磁盘版本
    private Sig? _awaiting;     // 已弹窗，正在等用户回答的版本
    private Sig? _rejected;     // 用户选择"保留本地"的版本
    private string? _ownDigest; // 自身刚写入内容的 SHA1
    private bool _missing;

    public TextFile? File { get; private set; }
    public bool IsMissing => _missing;

    public void Attach(TextFile tf)
    {
        File = tf;
        _sig = new Sig(tf.MtimeTicks, tf.Size);
        _awaiting = null;
        _rejected = null;
        _ownDigest = null;
        _missing = false;
    }

    public void Detach()
    {
        File = null;
        _sig = null;
        _awaiting = null;
        _rejected = null;
        _ownDigest = null;
        _missing = false;
    }

    /// <summary>本工具自己保存后调用：记住新版本与内容摘要，避免误判为外部变更。</summary>
    public void NoteOwnWrite(TextFile tf)
    {
        File = tf;
        _sig = new Sig(tf.MtimeTicks, tf.Size);
        _ownDigest = Hash(tf.RawBytes);
        _awaiting = null;
        _rejected = null;
        _missing = false;
    }

    /// <summary>轮询一次。</summary>
    /// <param name="currentText">编辑区当前文本（CRLF 规范）。</param>
    /// <param name="confirm">外部变更时是否弹窗确认。</param>
    public SyncEvent Poll(string currentText, bool confirm)
    {
        if (File is null) return SyncEvent.None;
        var path = File.Path;

        Sig sig;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException();
            sig = new Sig(fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (_missing) return SyncEvent.None;
            _missing = true;
            return new SyncEvent { Kind = SyncKind.Missing, Message = "文件已不可访问（被删除或重命名），等待恢复…" };
        }

        if (_missing)
        {
            _missing = false;
            if (sig == _sig)
                return new SyncEvent { Kind = SyncKind.Recovered, Message = "文件已恢复。" };
        }

        if (sig == _sig) return SyncEvent.None;

        TextFile fresh;
        try
        {
            fresh = TextFile.Read(File.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_missing) return SyncEvent.None;
            _missing = true;
            return new SyncEvent { Kind = SyncKind.Missing, Message = "文件暂时无法读取，等待恢复…" };
        }

        // 1) 是自己刚才写的内容 → 只更新签名
        var digest = Hash(fresh.RawBytes);
        if (_ownDigest is not null && digest == _ownDigest)
        {
            _sig = new Sig(fresh.MtimeTicks, fresh.Size);
            _ownDigest = null;
            return SyncEvent.None;
        }

        // 2) 磁盘内容与编辑区一致 → 只更新签名（例如时间戳变了但内容没变）
        if (string.Equals(fresh.Text, currentText, StringComparison.Ordinal))
        {
            _sig = new Sig(fresh.MtimeTicks, fresh.Size);
            _rejected = null;
            return SyncEvent.None;
        }

        // 3) 需要询问
        if (confirm)
        {
            if (sig == _awaiting || sig == _rejected) return SyncEvent.None;
            _awaiting = sig;
            return new SyncEvent { Kind = SyncKind.Prompt, Message = "检测到外部修改，是否重新加载？", File = fresh };
        }

        // 4) 默认：静默以外部为准（即使本地有未保存修改）
        Accept(fresh);
        return new SyncEvent { Kind = SyncKind.Reload, Message = "已同步外部修改。", File = fresh };
    }

    /// <summary>用户回答了"是否重新加载"。</summary>
    public SyncEvent Answer(bool reload, TextFile fresh)
    {
        if (!reload)
        {
            _rejected = _awaiting ?? _sig;
            _awaiting = null;
            return new SyncEvent { Kind = SyncKind.None, Message = "已保留本地内容；外部再次修改时才重新提示。" };
        }
        Accept(fresh);
        return new SyncEvent { Kind = SyncKind.Reload, Message = "已重新加载。", File = fresh };
    }

    /// <summary>用户主动 "从磁盘重新加载"（F5）。</summary>
    public SyncEvent ForceReload()
    {
        if (File is null) return SyncEvent.None;
        try
        {
            var fresh = TextFile.Read(File.Path);
            Accept(fresh);
            return new SyncEvent { Kind = SyncKind.Reload, Message = "已从磁盘重新加载。", File = fresh };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _missing = true;
            return new SyncEvent { Kind = SyncKind.Missing, Message = "读取失败，文件可能已被移动或删除。" };
        }
    }

    private void Accept(TextFile fresh)
    {
        File = fresh;
        _sig = new Sig(fresh.MtimeTicks, fresh.Size);
        _ownDigest = null;
        _awaiting = null;
        _rejected = null;
        _missing = false;
    }

    internal static string Hash(byte[] data)
    {
        using var sha = SHA1.Create();
        return Compat.ToHex(sha.ComputeHash(data));
    }
}
