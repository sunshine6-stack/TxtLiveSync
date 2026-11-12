using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TxtLiveSync;

/// <summary>
/// 只够配置文件用的一点点 JSON 读写。
///
/// 为什么不用 System.Text.Json：它不在 .NET Framework 4.8 里，走 NuGet 会引入额外 dll，
/// 分发就从"一个 exe"变成"exe + 一堆 dll"。配置结构固定、扁平，自己读写反而更省事、零依赖。
/// 输出格式与原来 System.Text.Json（WriteIndented + 不转义中文）保持一致，旧配置文件照样能读。
/// </summary>
internal static class JsonLite
{
    // ---------------------------------------------------------------- 写

    public static string Quote(string? value) =>
        value is null ? "null" : "\"" + Escape(value) + "\"";

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);      // 中文等非 ASCII 原样输出（和 UnsafeRelaxedJsonEscaping 一致）
                    break;
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 读

    /// <summary>把 JSON 对象解析成 键 → 值（string / bool / double / List&lt;object?&gt; / null）。</summary>
    public static Dictionary<string, object?> ParseObject(string json)
    {
        var i = 0;
        var value = ParseValue(json, ref i);
        if (value is Dictionary<string, object?> map) return map;
        throw new FormatException("配置的顶层不是对象");
    }

    private static object? ParseValue(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length) throw new FormatException("内容不完整");

        var c = s[i];
        if (c == '{') return ParseMap(s, ref i);
        if (c == '[') return ParseArray(s, ref i);
        if (c == '"') return ParseString(s, ref i);
        if (Match(s, i, "true")) { i += 4; return true; }
        if (Match(s, i, "false")) { i += 5; return false; }
        if (Match(s, i, "null")) { i += 4; return null; }
        return ParseNumber(s, ref i);
    }

    private static Dictionary<string, object?> ParseMap(string s, ref int i)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        i++;                                  // '{'

        while (true)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("对象没有结束");
            if (s[i] == '}') { i++; return map; }

            var key = ParseString(s, ref i);

            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException($"键 {key} 后面缺少冒号");
            i++;

            map[key] = ParseValue(s, ref i);

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; return map; }
            throw new FormatException("对象成员之间缺少逗号");
        }
    }

    private static List<object?> ParseArray(string s, ref int i)
    {
        var list = new List<object?>();
        i++;                                  // '['

        while (true)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("数组没有结束");
            if (s[i] == ']') { i++; return list; }

            list.Add(ParseValue(s, ref i));

            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; return list; }
            throw new FormatException("数组元素之间缺少逗号");
        }
    }

    private static string ParseString(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length || s[i] != '"') throw new FormatException("这里应该是字符串");
        i++;

        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new FormatException("字符串没有结束");
            var c = s[i++];

            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }

            if (i >= s.Length) throw new FormatException("转义符后面没有内容");
            var e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) throw new FormatException("\\u 转义不完整");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: throw new FormatException($"不认识的转义 \\{e}");
            }
        }
    }

    private static double ParseNumber(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '-' or '+' or '.' or 'e' or 'E')) i++;
        if (i == start) throw new FormatException("这里应该是一个值");

        return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static bool Match(string s, int i, string word) =>
        i + word.Length <= s.Length && string.CompareOrdinal(s, i, word, 0, word.Length) == 0;
}
