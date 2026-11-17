using System;
using System.Collections.Generic;

namespace TxtLiveSync;

/// <summary>一段可折叠区间：[StartOffset, EndOffset)，EndOffset 不含。</summary>
public readonly struct FoldRange
{
    public FoldRange(int startOffset, int endOffset)
    {
        StartOffset = startOffset;
        EndOffset = endOffset;
    }

    public int StartOffset { get; }
    public int EndOffset { get; }
}

/// <summary>
/// 算「哪些区间可以折叠」：JSON 按 {} / [] 配对，XML 按元素配对。
/// 只做纯文本扫描，不依赖编辑器，方便直接跑逻辑测试。
/// 单行区间不算（折起来什么也省不了），注释/CDATA/处理指令里的括号不参与配对。
/// </summary>
public static class FoldingRanges
{
    /// <summary>折叠段过多会拖慢渲染，超过就只保留前面的。</summary>
    private const int MaxRanges = 20000;

    public static List<FoldRange> Build(string text, TextKind kind)
    {
        if (string.IsNullOrEmpty(text)) return new List<FoldRange>();

        switch (kind)
        {
            case TextKind.Json:
                return Json(text);

            case TextKind.Xml:
                return Xml(text);

            default:
                return new List<FoldRange>();
        }
    }

    // ---------------------------------------------------------------- JSON

    private static List<FoldRange> Json(string text)
    {
        var result = new List<FoldRange>();
        var stack = new Stack<(int Offset, int Line)>();
        var line = 1;
        var inString = false;
        var escape = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') line++;

            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{' || c == '[')
            {
                stack.Push((i, line));
            }
            else if (c == '}' || c == ']')
            {
                if (stack.Count == 0) continue;
                var open = stack.Pop();

                // 跨行才有折叠的意义（{ } 写在一行的不显示折叠标记）
                if (line > open.Line && result.Count < MaxRanges)
                    result.Add(new FoldRange(open.Offset, i + 1));
            }
        }

        Sort(result);
        return result;
    }

    // ---------------------------------------------------------------- XML

    private static List<FoldRange> Xml(string text)
    {
        var result = new List<FoldRange>();
        var stack = new Stack<(string Name, int Offset, int Line)>();
        var line = 1;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }
            if (c != '<')
            {
                i++;
                continue;
            }

            // 注释 / CDATA / 处理指令 / DOCTYPE 整段跳过，里面的 < > 不参与配对
            if (Match(text, i, "<!--")) { i = SkipPast(text, i + 4, "-->", ref line); continue; }
            if (Match(text, i, "<![CDATA[")) { i = SkipPast(text, i + 9, "]]>", ref line); continue; }
            if (Match(text, i, "<?")) { i = SkipPast(text, i + 2, "?>", ref line); continue; }
            if (Match(text, i, "<!")) { i = SkipPast(text, i + 2, ">", ref line); continue; }

            var tagEnd = FindTagEnd(text, i);
            if (tagEnd < 0) break;

            var isClose = i + 1 < text.Length && text[i + 1] == '/';
            var isSelfClose = tagEnd - 1 > i && text[tagEnd - 1] == '/';
            var name = ReadTagName(text, i, isClose);

            if (isClose)
            {
                // 找到同名的开标签（容忍中间有没配上的标签）
                var found = -1;
                var probe = stack.ToArray();
                for (var k = 0; k < probe.Length; k++)
                {
                    if (string.Equals(probe[k].Name, name, StringComparison.Ordinal)) { found = k; break; }
                }

                if (found >= 0)
                {
                    (string Name, int Offset, int Line) open = default;
                    for (var k = 0; k <= found; k++) open = stack.Pop();
                    if (line > open.Line && result.Count < MaxRanges)
                        result.Add(new FoldRange(open.Offset, tagEnd + 1));
                }
            }
            else if (!isSelfClose && name.Length > 0)
            {
                stack.Push((name, i, line));
            }

            for (var k = i; k <= tagEnd && k < text.Length; k++)
            {
                if (text[k] == '\n') line++;
            }
            i = tagEnd + 1;
        }

        Sort(result);
        return result;
    }

    private static int FindTagEnd(string text, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '>') return i;
        }
        return -1;
    }

    private static string ReadTagName(string text, int start, bool isClose)
    {
        var i = start + (isClose ? 2 : 1);
        var begin = i;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '>' || c == '/') break;
            i++;
        }
        return i > begin ? text.Substring(begin, i - begin) : "";
    }

    /// <summary>跳过一整段（含结束标记），顺手统计其中的换行。</summary>
    private static int SkipPast(string text, int from, string terminator, ref int line)
    {
        var idx = text.IndexOf(terminator, from, StringComparison.Ordinal);
        var end = idx < 0 ? text.Length : idx + terminator.Length;

        for (var i = from; i < end; i++)
        {
            if (text[i] == '\n') line++;
        }
        return end;
    }

    private static bool Match(string text, int index, string value)
    {
        if (index + value.Length > text.Length) return false;
        return string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
    }

    private static void Sort(List<FoldRange> ranges)
    {
        ranges.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
    }
}
