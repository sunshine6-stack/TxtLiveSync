using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TxtLiveSync;

public enum TextKind
{
    Plain,
    Json,
    Xml,
}

/// <summary>
/// 「格式化文档」用的纯文本变换：JSON 缩进重排、XML 缩进重排。
/// 这里不碰界面、不碰 AvalonEdit，方便直接跑逻辑测试。
/// 约定：解析失败时不产出半成品，原样保留（formatted 返回原文，error 给出原因）。
/// </summary>
public static class TextFormat
{
    private const string NewLine = "\r\n";
    private const int IndentSize = 2;

    /// <summary>按扩展名判断该当 JSON 还是 XML 处理；认不出来的扩展名就按内容猜。</summary>
    public static TextKind DetectKind(string? path, string text)
    {
        if (!string.IsNullOrEmpty(path))
        {
            var ext = System.IO.Path.GetExtension(path);
            if (ext is not null) ext = ext.ToLowerInvariant();

            switch (ext)
            {
                case ".json":
                    return TextKind.Json;

                case ".xml":
                case ".xaml":
                case ".config":
                case ".csproj":
                case ".vbproj":
                case ".props":
                case ".targets":
                case ".resx":
                case ".svg":
                case ".xsd":
                case ".xsl":
                case ".xslt":
                case ".plist":
                case ".nuspec":
                    return TextKind.Xml;

                // 这些是明确的纯文本，别自作聪明去格式化
                case ".txt":
                case ".log":
                case ".md":
                case ".ini":
                case ".csv":
                case ".bat":
                case ".ps1":
                case ".py":
                case ".cs":
                    return TextKind.Plain;
            }
        }

        // 没有路径（新建文档）或扩展名很陌生：看第一个有意义的字符
        foreach (var c in text)
        {
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF') continue;
            if (c == '{' || c == '[') return TextKind.Json;
            if (c == '<') return TextKind.Xml;
            break;
        }
        return TextKind.Plain;
    }

    public static bool TryFormat(string text, TextKind kind, out string formatted, out string error)
    {
        switch (kind)
        {
            case TextKind.Json:
                return TryFormatJson(text, out formatted, out error);

            case TextKind.Xml:
                return TryFormatXml(text, out formatted, out error);

            default:
                formatted = text;
                error = "只支持格式化 JSON 和 XML。";
                return false;
        }
    }

    // ---------------------------------------------------------------- JSON

    private enum JsonTokenKind
    {
        OpenBrace,
        CloseBrace,
        OpenBracket,
        CloseBracket,
        Comma,
        Colon,
        Scalar,
    }

    private readonly struct JsonToken
    {
        public JsonToken(JsonTokenKind kind, string text, int pos)
        {
            Kind = kind;
            Text = text;
            Pos = pos;
        }

        public JsonTokenKind Kind { get; }
        public string Text { get; }
        public int Pos { get; }
    }

    public static bool TryFormatJson(string text, out string formatted, out string error)
    {
        formatted = text;
        error = "";

        var tokens = TokenizeJson(text, out error);
        if (tokens is null) return false;

        if (!CheckBrackets(tokens, text, out error)) return false;

        var sb = new StringBuilder(text.Length + text.Length / 3 + 32);
        var depth = 0;
        var needNewLine = false;

        for (var t = 0; t < tokens.Count; t++)
        {
            var tok = tokens[t];
            switch (tok.Kind)
            {
                case JsonTokenKind.OpenBrace:
                case JsonTokenKind.OpenBracket:
                {
                    EmitValueStart(sb, depth, ref needNewLine);

                    var isBrace = tok.Kind == JsonTokenKind.OpenBrace;
                    var closeKind = isBrace ? JsonTokenKind.CloseBrace : JsonTokenKind.CloseBracket;
                    var empty = t + 1 < tokens.Count && tokens[t + 1].Kind == closeKind;

                    sb.Append(isBrace ? '{' : '[');
                    if (empty)
                    {
                        sb.Append(isBrace ? '}' : ']');
                        t++;                       // 把配对的右括号一起吃掉，写成 {} / []
                    }
                    else
                    {
                        depth++;
                        needNewLine = true;
                    }
                    break;
                }

                case JsonTokenKind.CloseBrace:
                case JsonTokenKind.CloseBracket:
                {
                    depth--;
                    if (depth < 0) depth = 0;
                    sb.Append(NewLine);
                    AppendIndent(sb, depth);
                    sb.Append(tok.Kind == JsonTokenKind.CloseBrace ? '}' : ']');
                    needNewLine = false;
                    break;
                }

                case JsonTokenKind.Comma:
                    sb.Append(',');
                    needNewLine = true;
                    break;

                case JsonTokenKind.Colon:
                    sb.Append(": ");
                    break;

                default:
                    EmitValueStart(sb, depth, ref needNewLine);
                    sb.Append(tok.Text);
                    break;
            }
        }

        formatted = sb.ToString();
        return true;
    }

    private static void EmitValueStart(StringBuilder sb, int depth, ref bool needNewLine)
    {
        if (!needNewLine) return;
        sb.Append(NewLine);
        AppendIndent(sb, depth);
        needNewLine = false;
    }

    private static void AppendIndent(StringBuilder sb, int depth)
    {
        sb.Append(' ', depth * IndentSize);
    }

    /// <summary>先扫一遍括号配对，避免把残缺的 JSON 排成更难看的样子。</summary>
    private static bool CheckBrackets(List<JsonToken> tokens, string text, out string error)
    {
        error = "";
        var stack = new Stack<JsonToken>();

        foreach (var tok in tokens)
        {
            switch (tok.Kind)
            {
                case JsonTokenKind.OpenBrace:
                case JsonTokenKind.OpenBracket:
                    stack.Push(tok);
                    break;

                case JsonTokenKind.CloseBrace:
                case JsonTokenKind.CloseBracket:
                {
                    if (stack.Count == 0)
                    {
                        error = $"JSON 有误：第 {LineOf(text, tok.Pos)} 行多了一个 {(tok.Kind == JsonTokenKind.CloseBrace ? "}" : "]")}。";
                        return false;
                    }

                    var open = stack.Pop();
                    var wantBrace = open.Kind == JsonTokenKind.OpenBrace;
                    var isBrace = tok.Kind == JsonTokenKind.CloseBrace;
                    if (wantBrace != isBrace)
                    {
                        error = $"JSON 有误：第 {LineOf(text, open.Pos)} 行的括号与第 {LineOf(text, tok.Pos)} 行不匹配。";
                        return false;
                    }
                    break;
                }
            }
        }

        if (stack.Count > 0)
        {
            var open = stack.Peek();
            error = $"JSON 有误：第 {LineOf(text, open.Pos)} 行的括号没有闭合。";
            return false;
        }
        return true;
    }

    private static List<JsonToken>? TokenizeJson(string text, out string error)
    {
        error = "";
        var tokens = new List<JsonToken>();
        var i = 0;
        if (text.Length > 0 && text[0] == '\uFEFF') i = 1;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '{': tokens.Add(new JsonToken(JsonTokenKind.OpenBrace, "{", i)); i++; continue;
                case '}': tokens.Add(new JsonToken(JsonTokenKind.CloseBrace, "}", i)); i++; continue;
                case '[': tokens.Add(new JsonToken(JsonTokenKind.OpenBracket, "[", i)); i++; continue;
                case ']': tokens.Add(new JsonToken(JsonTokenKind.CloseBracket, "]", i)); i++; continue;
                case ',': tokens.Add(new JsonToken(JsonTokenKind.Comma, ",", i)); i++; continue;
                case ':': tokens.Add(new JsonToken(JsonTokenKind.Colon, ":", i)); i++; continue;

                case '"':
                {
                    var end = SkipJsonString(text, i);
                    if (end < 0)
                    {
                        error = $"JSON 有误：第 {LineOf(text, i)} 行的字符串没有闭合。";
                        return null;
                    }
                    tokens.Add(new JsonToken(JsonTokenKind.Scalar, text.Substring(i, end - i), i));
                    i = end;
                    continue;
                }
            }

            if (c == '-' || c == '+' || c == '.' || (c >= '0' && c <= '9'))
            {
                var start = i;
                while (i < text.Length && "-+.eE0123456789".IndexOf(text[i]) >= 0) i++;

                // 数字后面直接跟字母/下划线（比如 12abc、NaN）说明不是合法 JSON
                if (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    error = $"JSON 有误：第 {LineOf(text, start)} 行第 {ColumnOf(text, start)} 列的数字写法不合法。";
                    return null;
                }

                tokens.Add(new JsonToken(JsonTokenKind.Scalar, text.Substring(start, i - start), start));
                continue;
            }

            if (StartsWith(text, i, "true") || StartsWith(text, i, "false") || StartsWith(text, i, "null"))
            {
                var len = StartsWith(text, i, "true") ? 4 : StartsWith(text, i, "false") ? 5 : 4;
                tokens.Add(new JsonToken(JsonTokenKind.Scalar, text.Substring(i, len), i));
                i += len;
                continue;
            }

            error = $"JSON 有误：第 {LineOf(text, i)} 行第 {ColumnOf(text, i)} 列出现了无法识别的字符「{c}」。";
            return null;
        }

        return tokens;
    }

    private static int SkipJsonString(string text, int start)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == '"') return i + 1;
            if (c == '\r' || c == '\n') return -1;      // JSON 字符串里不允许裸换行
            i++;
        }
        return -1;
    }

    private static bool StartsWith(string text, int index, string value)
    {
        if (index + value.Length > text.Length) return false;
        return string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
    }

    // ---------------------------------------------------------------- XML

    public static bool TryFormatXml(string text, out string formatted, out string error)
    {
        formatted = text;
        error = "";

        if (text.Trim().Length == 0)
        {
            error = "文档是空的，没有可格式化的内容。";
            return false;
        }

        try
        {
            // LoadOptions.None：丢掉元素之间的纯空白，才能按缩进重新排版
            var doc = XDocument.Parse(text, LoadOptions.None);
            if (doc.Root is null)
            {
                error = "XML 里没有根元素。";
                return false;
            }

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = new string(' ', IndentSize),
                NewLineChars = NewLine,
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = doc.Declaration is null,
                Encoding = new UTF8Encoding(false),
            };

            var sb = new StringBuilder(text.Length + text.Length / 3 + 32);
            using (var writer = XmlWriter.Create(sb, settings))
            {
                doc.Save(writer);
            }

            formatted = sb.ToString();
            return true;
        }
        catch (XmlException ex)
        {
            error = $"XML 有误：第 {ex.LineNumber} 行第 {ex.LinePosition} 列 —— {Shorten(ex.Message)}";
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            error = $"XML 格式化失败：{Shorten(ex.Message)}";
            return false;
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 120 ? s : s.Substring(0, 120) + "…";
    }

    private static int LineOf(string text, int pos)
    {
        var line = 1;
        var end = Math.Min(pos, text.Length);
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private static int ColumnOf(string text, int pos)
    {
        var end = Math.Min(pos, text.Length);
        var lineStart = 0;
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n') lineStart = i + 1;
        }
        return end - lineStart + 1;
    }
}
