using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Highlighting;

namespace TxtLiveSync;

/// <summary>
/// 扩展名 → AvalonEdit 自带的语法定义。
///
/// dll 里本来就打包了 21 份定义（C# / VB / JavaScript / Python / Java / C++ / HTML /
/// ASP-XHTML / CSS / PHP / PowerShell / TSQL / Markdown / XML / Json ...），这里只做映射，
/// 不引入新依赖、不增加任何体积。
///
/// 放在项目根而不是 Core\：Core\ 会被不引用 AvalonEdit 的逻辑测试单独编译。
/// </summary>
public static class SyntaxHighlightingMap
{
    private static readonly Dictionary<string, string> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- C 系
            [".cs"] = "C#",
            [".csx"] = "C#",
            [".vb"] = "VB",
            [".java"] = "Java",
            [".c"] = "C++",
            [".h"] = "C++",
            [".cpp"] = "C++",
            [".hpp"] = "C++",
            [".cc"] = "C++",
            [".cxx"] = "C++",
            [".hxx"] = "C++",

            // ---- 脚本
            [".js"] = "JavaScript",
            [".mjs"] = "JavaScript",
            [".cjs"] = "JavaScript",
            [".jsx"] = "JavaScript",
            [".py"] = "Python",
            [".pyw"] = "Python",
            [".pyi"] = "Python",
            [".ps1"] = "PowerShell",
            [".psm1"] = "PowerShell",
            [".psd1"] = "PowerShell",
            [".php"] = "PHP",

            // ---- Web
            [".html"] = "HTML",
            [".htm"] = "HTML",
            [".aspx"] = "ASP/XHTML",
            [".css"] = "CSS",

            // ---- 数据 / 文档
            [".sql"] = "TSQL",
            [".md"] = "MarkDown",
            [".json"] = "Json",
            [".jsonc"] = "Json",

            // ---- XML 家族（和 TextFormat.DetectKind 认的那批保持一致）
            [".xml"] = "XML",
            [".xaml"] = "XML",
            [".config"] = "XML",
            [".csproj"] = "XML",
            [".vbproj"] = "XML",
            [".props"] = "XML",
            [".targets"] = "XML",
            [".resx"] = "XML",
            [".svg"] = "XML",
            [".xsd"] = "XML",
            [".xsl"] = "XML",
            [".xslt"] = "XML",
            [".plist"] = "XML",
            [".nuspec"] = "XML",
        };

    /// <summary>
    /// 按文件路径的扩展名取语法定义；认不出来（含 .txt / .log / 无扩展名）返回 null，
    /// 编辑区就按纯文本显示，不会自作聪明给日志文件上色。
    /// </summary>
    public static IHighlightingDefinition? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;

        var mgr = HighlightingManager.Instance;

        if (ByExtension.TryGetValue(ext, out var name))
            return mgr.GetDefinition(name);

        // 表里没列的交给 AvalonEdit 自己的扩展名表（.ascx .manifest 之类），查不到就是 null
        return mgr.GetDefinitionByExtension(ext);
    }
}
