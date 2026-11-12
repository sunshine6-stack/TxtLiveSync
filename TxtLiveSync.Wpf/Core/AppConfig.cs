using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TxtLiveSync;

/// <summary>
/// 窗口与行为设置的持久化（%APPDATA%\TxtLiveSync\config.wpf.json）。
/// 用的是自带的 JsonLite 而不是 System.Text.Json：后者在 .NET Framework 4.8 里没有，
/// 走 NuGet 会引入额外 dll，分发就不再是一个 exe 了。
/// </summary>
public sealed class AppConfig
{
    public bool ConfirmOnExternalChange { get; set; }      // 默认关：静默以外部为准
    public bool SingleInstance { get; set; } = true;        // 默认开：双击文件交给已开的窗口
    public string? LastPath { get; set; }
    public List<string>? OpenPaths { get; set; } = new();   // 上次打开的标签（下次启动恢复）
    public string? ActivePath { get; set; }                 // 上次选中的标签
    public double? Left { get; set; }                      // null = 没记录过，用系统默认位置
    public double? Top { get; set; }
    public double? Width { get; set; } = 1000;
    public double? Height { get; set; } = 680;
    public bool Maximized { get; set; }
    public bool WordWrap { get; set; }
    public double FontSize { get; set; } = 14;

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TxtLiveSync");

    private static string FilePath => Path.Combine(Dir, "config.wpf.json");

    // ---------------------------------------------------------------- 读

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppConfig();

            var map = JsonLite.ParseObject(File.ReadAllText(FilePath));
            return new AppConfig
            {
                ConfirmOnExternalChange = GetBool(map, "ConfirmOnExternalChange"),
                SingleInstance = GetBool(map, "SingleInstance", true),
                LastPath = GetString(map, "LastPath"),
                OpenPaths = GetStringList(map, "OpenPaths"),
                ActivePath = GetString(map, "ActivePath"),
                Left = GetNumber(map, "Left"),
                Top = GetNumber(map, "Top"),
                Width = GetNumber(map, "Width") ?? 1000,
                Height = GetNumber(map, "Height") ?? 680,
                Maximized = GetBool(map, "Maximized"),
                WordWrap = GetBool(map, "WordWrap"),
                FontSize = GetNumber(map, "FontSize") ?? 14,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or FormatException or ArgumentException or OverflowException)
        {
            // 配置损坏就回默认值，不影响使用
            return new AppConfig();
        }
    }

    private static bool GetBool(Dictionary<string, object?> map, string key, bool fallback = false) =>
        map.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    private static string? GetString(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) ? v as string : null;

    private static double? GetNumber(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is double d ? d : null;

    private static List<string>? GetStringList(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is not List<object?> list) return null;

        var result = new List<string>(list.Count);
        foreach (var item in list)
            if (item is string s) result.Add(s);
        return result;
    }

    // ---------------------------------------------------------------- 写

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, ToJson(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // 保存失败不打断退出流程
        }
    }

    private string ToJson()
    {
        const string q = "  ";
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append(q).Append("\"ConfirmOnExternalChange\": ").Append(Bool(ConfirmOnExternalChange)).Append(",\n");
        sb.Append(q).Append("\"SingleInstance\": ").Append(Bool(SingleInstance)).Append(",\n");
        sb.Append(q).Append("\"LastPath\": ").Append(JsonLite.Quote(LastPath)).Append(",\n");
        sb.Append(q).Append("\"OpenPaths\": ").Append(StringArray(OpenPaths)).Append(",\n");
        sb.Append(q).Append("\"ActivePath\": ").Append(JsonLite.Quote(ActivePath)).Append(",\n");
        sb.Append(q).Append("\"Left\": ").Append(Number(Left)).Append(",\n");
        sb.Append(q).Append("\"Top\": ").Append(Number(Top)).Append(",\n");
        sb.Append(q).Append("\"Width\": ").Append(Number(Width)).Append(",\n");
        sb.Append(q).Append("\"Height\": ").Append(Number(Height)).Append(",\n");
        sb.Append(q).Append("\"Maximized\": ").Append(Bool(Maximized)).Append(",\n");
        sb.Append(q).Append("\"WordWrap\": ").Append(Bool(WordWrap)).Append(",\n");
        sb.Append(q).Append("\"FontSize\": ").Append(Number(FontSize)).Append('\n');
        sb.Append('}');
        return sb.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Number(double? value) =>
        value is double d ? d.ToString("0.############", CultureInfo.InvariantCulture) : "null";

    private static string StringArray(List<string>? items)
    {
        if (items is null) return "null";

        var sb = new StringBuilder("[\n");
        for (var i = 0; i < items.Count; i++)
        {
            sb.Append("    ").Append(JsonLite.Quote(items[i]));
            if (i < items.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]");
        return sb.ToString();
    }
}
