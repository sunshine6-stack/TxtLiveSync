using System;
using System.Collections.Generic;

namespace TxtLiveSync;

/// <summary>一处同名单词的位置。</summary>
public readonly struct Occurrence
{
    public Occurrence(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Start { get; }
    public int Length { get; }

    public int End => Start + Length;
}

/// <summary>
/// 「高亮同名单词」（双击一个词，把全文一样的词都淡淡标出来）的纯逻辑部分。
///
/// 三条规则，都是为了让结果符合直觉而不是变成一片色块：
/// 1. 只认「整词」：按 <see cref="SelectionTrim.WordAt"/> 的分段规则判边界，
///    所以双击 <c>count</c> 不会命中 <c>counter</c>、<c>account</c>；
/// 2. 不区分大小写（双击 <c>Path</c> 也标出 <c>path</c>），但必须是同一段字符；
/// 3. 空白、换行、单个标点不值得高亮 —— 双击一个空格就把全文空格涂色没有意义。
///
/// 抽成纯函数是为了能直接跑断言，不必真的去点鼠标。
/// </summary>
public static class OccurrenceMatcher
{
    /// <summary>词太长就不标了（双击到一整行时别把整行都算成一个词）。</summary>
    public const int MaxWordLength = 120;

    /// <summary>这个词值不值得高亮同名单词。</summary>
    public static bool IsWorthHighlighting(string? word)
    {
        if (word is null) return false;

        var len = word.Length;
        if (len == 0 || len > MaxWordLength) return false;
        if (word.IndexOf('\r') >= 0 || word.IndexOf('\n') >= 0) return false;

        var cls = SelectionTrim.ClassOf(word[0]);
        if (cls == SelectionTrim.CharClass.Space || cls == SelectionTrim.CharClass.Break) return false;

        // 单个标点（逗号、括号、加号…）到处都是，标出来只是噪声
        if (len == 1 && cls == SelectionTrim.CharClass.Symbol) return false;

        return true;
    }

    /// <summary>
    /// 在 text 的 [from, to) 区间里找出所有与 word 整词相同的片段，最多 limit 处。
    /// word 本身不合法（见 <see cref="IsWorthHighlighting"/>）时返回空表。
    /// </summary>
    /// <param name="caseSensitive">false = 不区分大小写（默认行为）。</param>
    public static List<Occurrence> Find(
        string? text, string? word, int from, int to, int limit, bool caseSensitive = false)
    {
        var result = new List<Occurrence>();
        if (text is null || text.Length == 0 || limit <= 0) return result;
        if (!IsWorthHighlighting(word)) return result;

        var needle = word!;
        var len = needle.Length;

        var lo = Math.Max(0, from);
        var hi = Math.Min(text.Length, to);
        if (hi - lo < len) return result;

        // 词本身的类别决定边界：同类别相邻字符说明它不是一个完整的词
        var cls = SelectionTrim.ClassOf(needle[0]);
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var i = lo;
        while (i <= hi - len && result.Count < limit)
        {
            var hit = text.IndexOf(needle, i, cmp);
            if (hit < 0 || hit + len > hi) break;

            var beforeOk = hit == 0 || SelectionTrim.ClassOf(text[hit - 1]) != cls;
            var end = hit + len;
            var afterOk = end >= text.Length || SelectionTrim.ClassOf(text[end]) != cls;

            if (beforeOk && afterOk) result.Add(new Occurrence(hit, len));

            i = hit + 1;   // 错一位继续找：重叠的匹配也要能命中（比如 "aa" 里的 "aa"）
        }

        return result;
    }
}
