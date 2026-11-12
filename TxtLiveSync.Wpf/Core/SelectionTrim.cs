using System;

namespace TxtLiveSync;

/// <summary>
/// 双击选词的选区计算。
/// WPF 的 TextBox 双击时会把单词后面紧跟的空格/换行一起选进去（记事本、VS Code 都不会），
/// 而且这个默认选词发生在 MouseUp 阶段，靠 MouseDoubleClick 去修剪拿不到选中结果，
/// 所以在 PreviewMouseDown 阶段自己算词边界：选中「词本身」，不带任何空白。
/// 抽成纯函数是为了能在逻辑测试里覆盖，不必真的去点鼠标。
/// </summary>
public static class SelectionTrim
{
    /// <summary>old：把一段选区首尾的空白裁掉。纯工具函数，保留给兜底/测试用。</summary>
    public static (int Start, int Length) Trim(string? text, int start, int length)
    {
        if (text is null || length <= 0) return (start, Math.Max(length, 0));

        var s = Math.Max(0, start);
        var end = Math.Min(s + length, text.Length);
        if (end <= s) return (start, length);

        var a = s;
        while (a < end && char.IsWhiteSpace(text[a])) a++;

        var b = end;
        while (b > a && char.IsWhiteSpace(text[b - 1])) b--;

        if (b <= a) return (start, length);   // 全是空白，保持不变
        return (a, b - a);
    }

    /// <summary>
    /// 双击落在 index 这个字符上时，应该选中哪一段。
    /// 规则和主流编辑器一致：字母/数字/下划线算一个词，标点连续段算一个符号，空白连续段算一段空白，
    /// 三者互不跨越，且都不跨越换行符（双击行尾空白也不会把下一行吞进来）。
    /// </summary>
    public static (int Start, int Length) WordAt(string? text, int index)
    {
        // net48 的 string.IsNullOrEmpty 没带可空性注解，编译器推断不出非空，这里显式判一次
        if (text is null || text.Length == 0) return (0, 0);

        var i = Compat.Clamp(index, 0, text.Length - 1);
        if (text[i] == '\r' || text[i] == '\n') return (i, 1);   // 正好点在换行上：只选这一个字符

        var cls = Classify(text[i]);
        var a = i;
        while (a > 0 && Classify(text[a - 1]) == cls) a--;

        var b = i + 1;
        while (b < text.Length && Classify(text[b]) == cls) b++;

        return (a, b - a);
    }

    private enum CharClass { Word, Symbol, Space, Break }

    private static CharClass Classify(char c)
    {
        if (c == '\r' || c == '\n') return CharClass.Break;
        if (char.IsWhiteSpace(c)) return CharClass.Space;
        if (char.IsLetterOrDigit(c) || c == '_') return CharClass.Word;
        return CharClass.Symbol;
    }
}
