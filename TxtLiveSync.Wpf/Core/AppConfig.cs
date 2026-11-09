using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TxtLiveSync;

/// <summary>窗口与行为设置的持久化（%APPDATA%\TxtLiveSync\config.wpf.json）。</summary>
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg is not null) return cfg;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置损坏就回默认值，不影响使用
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // 保存失败不打断退出流程
        }
    }
}
