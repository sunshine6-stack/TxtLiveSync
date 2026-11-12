using System;
using System.Text;

namespace TxtLiveSync;

/// <summary>
/// 几个 .NET Core 才有的便捷 API 的等价实现，保证同一份源码能同时编 net48 和 net8。
/// </summary>
internal static class Compat
{
    /// <summary>net48 没有 Convert.ToHexString，手写一遍（大写、无分隔）。</summary>
    public static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    /// <summary>截取前 n 个字节（替代 net8 的 bytes[..n]）。</summary>
    public static byte[] Take(byte[] source, int count)
    {
        if (count <= 0) return Array.Empty<byte>();
        if (count >= source.Length) return (byte[])source.Clone();

        var result = new byte[count];
        Array.Copy(source, result, count);
        return result;
    }

    /// <summary>替代 string.Contains(char)：net48 只有 string 重载。</summary>
    public static bool HasChar(string text, char value) => text.IndexOf(value) >= 0;

    /// <summary>替代 string.Contains(string, StringComparison)。</summary>
    public static bool Has(string text, string value, StringComparison comparison) =>
        text.IndexOf(value, comparison) >= 0;

    /// <summary>替代 string.Replace(string, string, StringComparison)。</summary>
    public static string Replace(string text, string oldValue, string newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue)) throw new ArgumentException("oldValue 不能为空", nameof(oldValue));

        var start = 0;
        var hit = text.IndexOf(oldValue, comparison);
        if (hit < 0) return text;

        var sb = new StringBuilder(text.Length);
        while (hit >= 0)
        {
            sb.Append(text, start, hit - start).Append(newValue);
            start = hit + oldValue.Length;
            hit = start <= text.Length ? text.IndexOf(oldValue, start, comparison) : -1;
        }
        sb.Append(text, start, text.Length - start);
        return sb.ToString();
    }

    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    public static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
