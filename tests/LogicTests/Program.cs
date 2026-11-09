using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TxtLiveSync;

/// <summary>
/// WPF 版核心逻辑验收：覆盖需求的四条硬性行为 + 编码/换行保持 + 配置持久化。
/// 运行后把报告写到 tests/logic_log.txt（PowerShell 不回显子进程 stdout）。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> _fail = new();
    private static readonly List<string> _lines = new();
    private static string _dir = "";

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            _lines.Add($"PASS  {name}");
        }
        else
        {
            _fail.Add(name + (detail is null ? "" : $"\n        -> {detail}"));
            _lines.Add($"FAIL  {name}" + (detail is null ? "" : $"   -> {detail}"));
        }
    }

    private static void Section(string title) => _lines.Add($"\n== {title} ==");

    private static string P(string name) => Path.Combine(_dir, name);

    private static void W(string name, byte[] bytes) => File.WriteAllBytes(P(name), bytes);

    private static Encoding Enc(string kind) => kind switch
    {
        "gbk" => Encoding.GetEncoding("GB18030"),
        "utf8bom" => new UTF8Encoding(true),
        _ => new UTF8Encoding(false),
    };

    private static byte[] Bytes(string text, string kind)
    {
        var enc = Enc(kind);
        var body = enc.GetBytes(text);
        if (kind == "utf8bom")
        {
            var pre = enc.GetPreamble();
            var all = new byte[pre.Length + body.Length];
            pre.CopyTo(all, 0);
            body.CopyTo(all, pre.Length);
            return all;
        }
        return body;
    }

    private static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _dir = Path.Combine(Path.GetTempPath(), "txtls_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);

        try
        {
            ReadTests();
            SaveTests();
            SyncTests();
            ConfigTests();
            SelectionTests();
        }
        catch (Exception ex)
        {
            _lines.Add($"\nEXCEPTION: {ex}");
            _fail.Add("测试脚本本身异常: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        _lines.Add($"\n共 {_pass + _fail.Count} 项断言，通过 {_pass}，失败 {_fail.Count}");
        if (_fail.Count > 0)
        {
            _lines.Add("失败项：");
            foreach (var f in _fail) _lines.Add("  * " + f);
        }

        var report = string.Join("\n", _lines);
        var logPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "logic_log.txt");
        File.WriteAllText(Path.GetFullPath(logPath), report, Encoding.UTF8);
        Console.WriteLine(report);
        return _fail.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 双击选词

    private static void SelectionTests()
    {
        Section("双击选词：裁掉 WPF 自动带上的空白");

        static string Run(string text, int start, int len)
        {
            var (s, l) = SelectionTrim.Trim(text, start, len);
            return text.Substring(s, Math.Min(l, text.Length - s));
        }

        // WPF 默认会把单词后面紧跟的空格一起选进去
        Check("尾随空格被裁掉", Run("hello world", 0, 6) == "hello", Run("hello world", 0, 6));
        Check("行尾 CRLF 被裁掉", Run("word\r\nnext", 0, 6) == "word", Run("word\r\nnext", 0, 6).Replace("\r", "\\r").Replace("\n", "\\n"));
        Check("中文词不带后空格", Run("中文 测试", 0, 3) == "中文", Run("中文 测试", 0, 3));
        Check("前导空白也裁掉", Run("  word here", 0, 7) == "word", Run("  word here", 0, 7));
        Check("中间词只取词本身", Run("aa bb cc", 3, 3) == "bb", Run("aa bb cc", 3, 3));

        // 边界
        Check("纯空白选区保持原样", Run("    x", 0, 4) == "    ", "[" + Run("    x", 0, 4) + "]");
        Check("整行没有空白时不改动", Run("abc", 0, 3) == "abc", Run("abc", 0, 3));
        Check("长度为 0 时不报错", SelectionTrim.Trim("abc", 1, 0) == (1, 0));
        Check("越界的长度被夹住", Run("abc", 1, 99) == "bc", Run("abc", 1, 99));
        Check("空文本不抛异常", SelectionTrim.Trim("", 0, 0) == (0, 0));

        Section("双击选词：按字符类别取整段，绝不带空白");

        static string Word(string text, int idx)
        {
            var (a, l) = SelectionTrim.WordAt(text, idx);
            return text.Substring(a, Math.Min(l, text.Length - a));
        }

        Check("private void Test：双击 void 只选 void", Word("private void Test", 9) == "void", Word("private void Test", 9));
        Check("private void Test：词尾下标也是同一个词", Word("private void Test", 11) == "void", Word("private void Test", 11));
        Check("词后紧跟逗号时不带逗号", Word("void,Test", 1) == "void", Word("void,Test", 1));
        Check("下划线算词内字符", Word("a_b c", 1) == "a_b", Word("a_b c", 1));
        Check("数字算词内字符", Word("x1 y", 1) == "x1", Word("x1 y", 1));
        Check("标点连续段一起选", Word("a(); b", 2) == "();", Word("a(); b", 2));
        Check("双击空白只选空白段", Word("a   b", 2) == "   ", "[" + Word("a   b", 2) + "]");
        Check("空白不跨越换行", Word("a  \r\nb", 2) == "  ", "[" + Word("a  \r\nb", 2).Replace("\r", "\\r").Replace("\n", "\\n") + "]");
        Check("中文连续串整体选中", Word("中文 测试", 1) == "中文", Word("中文 测试", 1));
        Check("正好点在换行上只选换行", SelectionTrim.WordAt("ab\r\ncd", 2) == (2, 1), SelectionTrim.WordAt("ab\r\ncd", 2).ToString());
        Check("下标越界被夹到末尾那个词", Word("abc", 99) == "abc", Word("abc", 99));
        Check("负下标被夹到开头", Word("abc", -5) == "abc", Word("abc", -5));
        Check("空文本返回空", SelectionTrim.WordAt("", 0) == (0, 0), SelectionTrim.WordAt("", 0).ToString());
    }

    // ---------------------------------------------------------------- 读取

    private static void ReadTests()
    {
        Section("读取与编码识别");

        W("crlf.txt", Bytes("第一行\r\n第二行\r\n", "utf8"));
        var a = TextFile.Read(P("crlf.txt"));
        Check("UTF-8 + CRLF：换行识别为 CRLF", a.Newline == "\r\n", a.Newline);
        Check("UTF-8 + CRLF：编码标签", a.EncodingLabel == "UTF-8", a.EncodingLabel);
        Check("UTF-8 + CRLF：内容完整", a.Text == "第一行\r\n第二行\r\n", a.Text.Replace("\r", "\\r").Replace("\n", "\\n"));

        W("lf.txt", Bytes("a\nb\n", "utf8"));
        var b = TextFile.Read(P("lf.txt"));
        Check("LF 文件：换行识别为 LF", b.Newline == "\n", b.Newline);
        Check("LF 文件：编辑区文本统一成 CRLF", b.Text == "a\r\nb\r\n", b.Text.Replace("\r", "\\r").Replace("\n", "\\n"));

        W("cr.txt", Bytes("a\rb", "utf8"));
        var c = TextFile.Read(P("cr.txt"));
        Check("老式 CR：换行识别为 CR", c.Newline == "\r");
        Check("老式 CR：编辑区文本统一成 CRLF", c.Text == "a\r\nb", c.Text.Replace("\r", "\\r").Replace("\n", "\\n"));

        W("gbk.txt", Bytes("中文测试\r\n", "gbk"));
        var d = TextFile.Read(P("gbk.txt"));
        Check("GBK 文件：解码正确", d.Text == "中文测试\r\n", d.Text);
        Check("GBK 文件：编码标签为 GBK", d.EncodingLabel == "GBK", d.EncodingLabel);

        W("bom.txt", Bytes("带 BOM\r\n", "utf8bom"));
        var e = TextFile.Read(P("bom.txt"));
        Check("UTF-8 BOM：识别出 BOM", e.HasBom);
        Check("UTF-8 BOM：BOM 不进入正文", !e.Text.Contains('\uFEFF'), e.Text);
        Check("UTF-8 BOM：编码标签", e.EncodingLabel == "UTF-8 BOM", e.EncodingLabel);

        W("empty.txt", Array.Empty<byte>());
        var f = TextFile.Read(P("empty.txt"));
        Check("空文件：不报错，内容为空", f.Text == "" && f.Size == 0, f.Text);

        W("bad.txt", new byte[] { 0xFF, 0x41, 0xFE, 0x42 });
        var g = TextFile.Read(P("bad.txt"));
        Check("非法字节：容错读取而不是抛异常", g.Text.Length > 0);

        W("big.txt", Bytes(string.Join("\r\n", Enumerable.Range(1, 20000)), "utf8"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var h = TextFile.Read(P("big.txt"));
        sw.Stop();
        Check("约 200KB 文件：读取耗时可接受", sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms, {h.Size} B");
    }

    // ---------------------------------------------------------------- 保存

    private static void SaveTests()
    {
        Section("保存：编码与换行不被改写");

        // 未改动 → 逐字节回写
        var lfBytes = Bytes("keep\nlf\n", "utf8");
        W("s1.txt", lfBytes);
        var t1 = TextFile.Read(P("s1.txt"));
        t1.Save(t1.Text);
        Check("未改动保存：逐字节一致（LF 不变成 CRLF）",
            File.ReadAllBytes(P("s1.txt")).SequenceEqual(lfBytes));

        var gbkBom = Bytes("中文\n", "gbk");
        W("s2.txt", gbkBom);
        var t2 = TextFile.Read(P("s2.txt"));
        t2.Save(t2.Text);
        Check("未改动保存：GBK 逐字节一致", File.ReadAllBytes(P("s2.txt")).SequenceEqual(gbkBom));

        var bomBytes = Bytes("带BOM\n", "utf8bom");
        W("s3.txt", bomBytes);
        var t3 = TextFile.Read(P("s3.txt"));
        t3.Save(t3.Text);
        Check("未改动保存：BOM 保留", File.ReadAllBytes(P("s3.txt")).SequenceEqual(bomBytes));

        // 有编辑 → 保持原换行
        W("s4.txt", Bytes("a\nb\n", "utf8"));
        var t4 = TextFile.Read(P("s4.txt"));
        t4.Save(t4.Text + "c\r\n");
        var after4 = File.ReadAllBytes(P("s4.txt"));
        Check("编辑后保存：LF 文件仍是纯 LF", !after4.Contains((byte)'\r'), Encoding.UTF8.GetString(after4).Replace("\r", "\\r"));
        Check("编辑后保存：新内容写入", Encoding.UTF8.GetString(after4) == "a\nb\nc\n",
            Encoding.UTF8.GetString(after4).Replace("\r", "\\r").Replace("\n", "\\n"));

        // 有编辑 → 保持原编码
        W("s5.txt", Bytes("原有\r\n", "gbk"));
        var t5 = TextFile.Read(P("s5.txt"));
        t5.Save(t5.Text + "追加一行\r\n");
        var after5 = File.ReadAllBytes(P("s5.txt"));
        var decoded5 = Encoding.GetEncoding("GB18030").GetString(after5);
        Check("编辑后保存：GBK 文件仍是 GBK（能按 GBK 解码出中文）", decoded5 == "原有\r\n追加一行\r\n",
            decoded5.Replace("\r", "\\r").Replace("\n", "\\n"));

        // 有编辑 → 保留 BOM
        W("s6.txt", Bytes("内容\r\n", "utf8bom"));
        var t6 = TextFile.Read(P("s6.txt"));
        t6.Save(t6.Text + "加了\r\n");
        var after6 = File.ReadAllBytes(P("s6.txt"));
        Check("编辑后保存：BOM 仍在", after6.Length >= 3 && after6[0] == 0xEF && after6[1] == 0xBB && after6[2] == 0xBF);

        // 编辑后 CRLF 文件不产生纯 LF
        W("s7.txt", Bytes("x\r\ny\r\n", "utf8"));
        var t7 = TextFile.Read(P("s7.txt"));
        t7.Save("x\r\ny\r\nz\r\n");
        var s7 = Encoding.UTF8.GetString(File.ReadAllBytes(P("s7.txt")));
        Check("编辑后保存：CRLF 文件仍是 CRLF", s7 == "x\r\ny\r\nz\r\n", s7.Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    // ---------------------------------------------------------------- 同步引擎

    private static void SyncTests()
    {
        Section("同步引擎：静默以外部为准");
        {
            W("e1.txt", Bytes("原始\n", "utf8"));
            var tf = TextFile.Read(P("e1.txt"));
            var eng = new SyncEngine();
            eng.Attach(tf);

            W("e1.txt", Bytes("外部改过\n", "utf8"));
            File.SetLastWriteTimeUtc(P("e1.txt"), DateTime.UtcNow.AddSeconds(3));
            var ev = eng.Poll(tf.Text, confirm: false);
            Check("静默模式：外部修改 → Reload", ev.Kind == SyncKind.Reload, ev.Kind.ToString());
            Check("静默模式：载入的是磁盘新内容", ev.File!.Text == "外部改过\r\n", ev.File.Text.Replace("\r", "\\r"));
        }
        {
            // 关键：本地有未保存修改时，仍然静默覆盖，且绝不弹窗
            W("e2.txt", Bytes("磁盘版本\n", "utf8"));
            var tf = TextFile.Read(P("e2.txt"));
            var eng = new SyncEngine();
            eng.Attach(tf);

            W("e2.txt", Bytes("外部又改\n", "utf8"));
            File.SetLastWriteTimeUtc(P("e2.txt"), DateTime.UtcNow.AddSeconds(3));

            var ev = eng.Poll("本地未保存的内容", confirm: false);   // 编辑区已被用户改过
            Check("静默模式：本地有未保存修改也被覆盖", ev.Kind == SyncKind.Reload, ev.Kind.ToString());
            Check("静默模式：绝不产生 Prompt", ev.Kind != SyncKind.Prompt);
            Check("静默模式：界面内容被替换为磁盘版本", ev.File!.Text == "外部又改\r\n", ev.File.Text.Replace("\r", "\\r"));
        }

        Section("同步引擎：弹窗确认（可选设置）");
        {
            W("e3.txt", Bytes("v1\n", "utf8"));
            var tf = TextFile.Read(P("e3.txt"));
            var eng = new SyncEngine();
            eng.Attach(tf);

            W("e3.txt", Bytes("v2\n", "utf8"));
            File.SetLastWriteTimeUtc(P("e3.txt"), DateTime.UtcNow.AddSeconds(3));
            var ev = eng.Poll(tf.Text, confirm: true);
            Check("弹窗模式：外部修改 → Prompt", ev.Kind == SyncKind.Prompt, ev.Kind.ToString());
            Check("弹窗模式：提示文案包含是否重新加载", ev.Message.Contains("重新加载"), ev.Message);

            var again = eng.Poll(tf.Text, confirm: true);
            Check("弹窗模式：等用户回答期间不重复弹窗", again.Kind == SyncKind.None, again.Kind.ToString());

            var keep = eng.Answer(reload: false, ev.File!);
            Check("选「否」：保留本地，不重载", keep.Kind == SyncKind.None, keep.Kind.ToString());

            var afterKeep = eng.Poll(tf.Text, confirm: true);
            Check("选「否」：同一版本不再提示", afterKeep.Kind == SyncKind.None, afterKeep.Kind.ToString());

            W("e3.txt", Bytes("v3\n", "utf8"));
            File.SetLastWriteTimeUtc(P("e3.txt"), DateTime.UtcNow.AddSeconds(6));
            var ev3 = eng.Poll(tf.Text, confirm: true);
            Check("选「否」后外部再改：重新提示", ev3.Kind == SyncKind.Prompt, ev3.Kind.ToString());

            var yes = eng.Answer(reload: true, ev3.File!);
            Check("选「是」：重新加载", yes.Kind == SyncKind.Reload, yes.Kind.ToString());
            Check("选「是」：内容为磁盘版本", yes.File!.Text == "v3\r\n", yes.File.Text.Replace("\r", "\\r"));
        }

        Section("同步引擎：自保存不误触发");
        {
            W("e4.txt", Bytes("我的内容\n", "utf8"));
            var tf = TextFile.Read(P("e4.txt"));
            var eng = new SyncEngine();
            eng.Attach(tf);

            tf.Save("我的内容改过\r\n");
            eng.NoteOwnWrite(tf);
            var ev = eng.Poll(tf.Text, confirm: false);
            Check("保存后轮询：无事件（不重载/不弹窗）", ev.Kind == SyncKind.None, ev.Kind.ToString());

            var ev2 = eng.Poll(tf.Text, confirm: true);
            Check("保存后轮询（弹窗模式下）：同样无事件", ev2.Kind == SyncKind.None, ev2.Kind.ToString());
        }
        {
            // 时间戳变了但内容没变 → 不应重载
            W("e5.txt", Bytes("同样内容\n", "utf8"));
            var tf = TextFile.Read(P("e5.txt"));
            var eng = new SyncEngine();
            eng.Attach(tf);

            File.SetLastWriteTimeUtc(P("e5.txt"), DateTime.UtcNow.AddSeconds(9));
            var ev = eng.Poll(tf.Text, confirm: false);
            Check("仅时间戳变化：不重载", ev.Kind == SyncKind.None, ev.Kind.ToString());
        }

        Section("同步引擎：文件异常容错");
        {
            W("e6.txt", Bytes("要删掉\n", "utf8"));
            var tf = TextFile.Read(P("e6.txt"));
            var ticks = File.GetLastWriteTimeUtc(P("e6.txt"));
            var eng = new SyncEngine();
            eng.Attach(tf);

            File.Delete(P("e6.txt"));
            var gone = eng.Poll(tf.Text, confirm: false);
            Check("文件被删除：返回 Missing 而不是崩溃", gone.Kind == SyncKind.Missing, gone.Kind.ToString());
            Check("文件被删除：提示进入等待恢复", gone.Message.Contains("等待恢复"), gone.Message);

            var again = eng.Poll(tf.Text, confirm: false);
            Check("文件持续缺失：只提示一次，不刷屏", again.Kind == SyncKind.None, again.Kind.ToString());

            W("e6.txt", Bytes("要删掉\n", "utf8"));
            File.SetLastWriteTimeUtc(P("e6.txt"), ticks);
            var back = eng.Poll(tf.Text, confirm: false);
            Check("文件恢复：返回 Recovered", back.Kind == SyncKind.Recovered, back.Kind.ToString());
        }
        {
            W("e7.txt", Bytes("强制重载\n", "utf8"));
            var tf = TextFile.Read(P("e7.txt"));
            var eng = new SyncEngine();
            eng.Attach(tf);
            var ev = eng.ForceReload();
            Check("主动从磁盘重新加载（F5）→ Reload", ev.Kind == SyncKind.Reload, ev.Kind.ToString());
        }
        {
            var eng = new SyncEngine();
            Check("未打开文件时轮询：无事件", eng.Poll("x", false).Kind == SyncKind.None);
            Check("Detach 后轮询：无事件", new SyncEngine().Poll("x", false).Kind == SyncKind.None);
        }
    }

    // ---------------------------------------------------------------- 配置

    private static void ConfigTests()
    {
        Section("配置持久化");

        var path = Path.Combine(AppConfig.Dir, "config.wpf.json");
        byte[]? backup = File.Exists(path) ? File.ReadAllBytes(path) : null;

        try
        {
            Check("默认设置：外部变更弹窗为关闭（静默优先）", !new AppConfig().ConfirmOnExternalChange);

            if (File.Exists(path)) File.Delete(path);
            var cfg = new AppConfig
            {
                ConfirmOnExternalChange = true,
                LastPath = @"C:\中转目录\中文 文件.txt",
                Left = 120.5,
                Top = 88.25,
                Width = 912,
                Height = 640,
                Maximized = true,
                WordWrap = true,
                FontSize = 16,
            };
            cfg.Save();

            Check("保存后配置文件真的写出来了", File.Exists(path));

            var back = AppConfig.Load();
            Check("往返：弹窗开关", back.ConfirmOnExternalChange);
            Check("往返：中文路径", back.LastPath == cfg.LastPath, back.LastPath);
            Check("往返：窗口位置", back.Left == 120.5 && back.Top == 88.25, $"{back.Left},{back.Top}");
            Check("往返：窗口大小", back.Width == 912 && back.Height == 640, $"{back.Width}x{back.Height}");
            Check("往返：最大化标记", back.Maximized);
            Check("往返：字号", Math.Abs(back.FontSize - 16) < 0.001, back.FontSize.ToString("0.##"));

            // 回归：默认配置（Left/Top 为 null）也必须能序列化
            if (File.Exists(path)) File.Delete(path);
            new AppConfig().Save();
            Check("回归：默认配置可序列化（不再因 NaN 抛异常）", File.Exists(path));
            var def = AppConfig.Load();
            Check("回归：默认窗口位置为空", def.Left is null && def.Top is null);

            // 损坏的配置不应导致启动失败
            File.WriteAllText(path, "{ 这不是 JSON");
            var broken = AppConfig.Load();
            Check("配置损坏：回退默认值而不崩溃", broken is not null && !broken.ConfirmOnExternalChange);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (backup is not null)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                File.WriteAllBytes(path, backup);
            }
        }
    }
}
