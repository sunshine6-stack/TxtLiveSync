using System;
using System.IO;
using System.Text;

namespace TxtLiveSync;

/// <summary>一个被打开的文本文件：内容统一为 CRLF 供 TextBox 使用，并记住原始编码/换行/字节。</summary>
public sealed class TextFile
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>解码后的全文，换行已统一成 CRLF（与 TextBox 一致）。</summary>
    public string Text { get; internal set; }

    /// <summary>磁盘上的原始字节，正文未改动时用于逐字节回写。</summary>
    public byte[] RawBytes { get; internal set; }

    public Encoding Encoding { get; internal set; }

    /// <summary>原始 BOM 字节（无 BOM 则为空数组）。写回时原样保留。</summary>
    public byte[] Preamble { get; internal set; } = Array.Empty<byte>();

    public bool HasBom => Preamble.Length > 0;

    public string EncodingLabel { get; internal set; }

    /// <summary>原始换行符："\r\n" / "\n" / "\r"。</summary>
    public string Newline { get; internal set; }

    public long MtimeTicks { get; internal set; }
    public long Size { get; internal set; }

    private TextFile(string path, string text, byte[] raw, Encoding enc, byte[] preamble, string label, string nl)
    {
        Path = path;
        Text = text;
        RawBytes = raw;
        Encoding = enc;
        Preamble = preamble;
        EncodingLabel = label;
        Newline = nl;
    }

    public string NewlineLabel => Newline switch
    {
        "\r\n" => "CRLF",
        "\n" => "LF",
        _ => "CR",
    };

    // ---------------------------------------------------------------- 读取

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Gb18030 = BuildGb();

    private static Encoding BuildGb()
    {
        try
        {
            return Encoding.GetEncoding("GB18030");
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(936);      // 万一 GB18030 不可用，退回 GBK
        }
    }

    public static TextFile Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var d = Decode(bytes);
        var nl = DetectNewline(d.Text);
        var tf = new TextFile(path, NormalizeToCrlf(d.Text), bytes, d.Encoding, d.Preamble, d.Label, nl)
        {
            MtimeTicks = File.GetLastWriteTimeUtc(path).Ticks,
            Size = bytes.LongLength,
        };
        return tf;
    }

    private readonly record struct Decoded(string Text, Encoding Encoding, byte[] Preamble, string Label);

    private static Decoded Decode(byte[] bytes)
    {
        // 1) BOM 优先
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new Decoded(DecodeBody(bytes, 3, Utf8NoBom), Utf8NoBom, Compat.Take(bytes, 3), "UTF-8 BOM");

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new Decoded(DecodeBody(bytes, 2, Encoding.Unicode), Encoding.Unicode, Compat.Take(bytes, 2), "UTF-16 LE");

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new Decoded(DecodeBody(bytes, 2, Encoding.BigEndianUnicode), Encoding.BigEndianUnicode, Compat.Take(bytes, 2), "UTF-16 BE");

        // 2) 严格 UTF-8 解码，成功即 UTF-8
        try
        {
            return new Decoded(StrictUtf8.GetString(bytes), Utf8NoBom, Array.Empty<byte>(), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // 3) 退回 GBK/GB18030，非法序列显示替换字符而不是报错
            var gb = (Encoding)Gb18030.Clone();
            gb.DecoderFallback = DecoderFallback.ReplacementFallback;
            return new Decoded(gb.GetString(bytes), (Encoding)Gb18030.Clone(), Array.Empty<byte>(), "GBK");
        }
    }

    private static string DecodeBody(byte[] bytes, int offset, Encoding enc)
    {
        var e = (Encoding)enc.Clone();
        e.DecoderFallback = DecoderFallback.ReplacementFallback;
        return e.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string DetectNewline(string text)
    {
        if (Compat.Has(text, "\r\n", StringComparison.Ordinal)) return "\r\n";
        if (Compat.HasChar(text, '\n')) return "\n";
        if (Compat.HasChar(text, '\r')) return "\r";
        return "\r\n";
    }

    internal static string NormalizeToCrlf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    // ---------------------------------------------------------------- 保存

    /// <summary>
    /// 保存。正文与打开时完全一致 → 逐字节回写原文件（编码/BOM/换行零改动）；
    /// 有编辑 → 按原编码 + 原换行重写。
    /// </summary>
    public void Save(string text)
    {
        var outBytes = EncodeFor(text);
        File.WriteAllBytes(Path, outBytes);

        // 保存后刷新自身状态，避免把自己这次写入当成外部变更
        RawBytes = outBytes;
        Text = text;
        Size = outBytes.LongLength;
        MtimeTicks = File.GetLastWriteTimeUtc(Path).Ticks;
    }

    /// <summary>按本文件的编码/换行把编辑区文本编码成字节（正文未改动时直接返回原始字节）。</summary>
    public byte[] EncodeFor(string text)
    {
        if (string.Equals(text, Text, StringComparison.Ordinal))
            return RawBytes;

        var body = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (Newline != "\n") body = body.Replace("\n", Newline);
        var payload = Encoding.GetBytes(body);
        var preamble = Preamble;
        var outBytes = new byte[preamble.Length + payload.Length];
        preamble.CopyTo(outBytes, 0);
        payload.CopyTo(outBytes, preamble.Length);
        return outBytes;
    }

    public string SizeLabel => Size < 1024 ? $"{Size} 字节" : $"{Size / 1024.0:0.#} KB";
}
