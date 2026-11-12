using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TxtLiveSync;

/// <summary>
/// 一个标签页 = 一个文件。负责自己的编辑区、SyncEngine、脏标记、保存/重新加载、查找替换，
/// 与主窗口只通过事件和几个公开方法交互，方便多标签共存。
/// </summary>
public partial class DocumentTab : UserControl
{
    /// <summary>超过这个长度就不统计匹配总数了（逐字符扫描会卡）。</summary>
    private const int CountLimit = 1_500_000;

    private bool _suppressText;   // 程序改写 Editor.Text 时避免误判为编辑
    private bool _dirty;

    /// <summary>
    /// 当前命中的起点（-1 = 还没有）。
    /// 「下一个」之后不选中文本、只记下位置：选中样式由高亮层自己画（文字永远不会被色块盖住），
    /// 而 TextBox 的选区在失焦/非活动状态下会把选中文字刷成浅色，浅蓝底上看不见字。
    /// </summary>
    private int _matchStart = -1;

    public SyncEngine Engine { get; } = new();

    public TextFile? File => Engine.File;
    public string? Path => File?.Path;
    public string DisplayName => File?.Name ?? "未命名";
    public string ToolTipPath => File?.Path ?? "";
    public bool IsDirty => _dirty;

    /// <summary>弹窗未关闭时暂停本页轮询，避免同一变更反复询问。</summary>
    public bool IsPrompting { get; private set; }

    public TextBox TextBoxControl => Editor;

    /// <summary>文件名或脏标记变化（用于刷新标签标题与窗口标题）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>状态栏消息（消息, 语气）。</summary>
    public event Action<string, string>? StatusChanged;

    /// <summary>光标位置变化（行, 列，都从 1 开始），给状态栏显示。</summary>
    public event Action<int, int>? CaretMoved;

    /// <summary>光标所在的「第几行第几列」，跟记事本一样从 1 开始。</summary>
    public (int Line, int Column) CaretPosition
    {
        get
        {
            var idx = Compat.Clamp(Editor.CaretIndex, 0, Editor.Text.Length);
            var line = Editor.GetLineIndexFromCharacterIndex(idx);
            if (line < 0) return (1, 1);

            var lineStart = Editor.GetCharacterIndexFromLineIndex(line);
            if (lineStart < 0) lineStart = 0;

            return (line + 1, idx - lineStart + 1);
        }
    }

    public DocumentTab()
    {
        InitializeComponent();
        Editor.IsInactiveSelectionHighlightEnabled = false;   // 命中靠高亮层显示，不靠失焦选区（失焦选区会把字刷浅）
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => RefreshHighlights()));
        Editor.SizeChanged += (_, _) => RefreshHighlights();
        Editor.SelectionChanged += (_, _) => RaiseCaretMoved();   // 光标移动也会走这里
    }

    private void RaiseCaretMoved()
    {
        var (line, col) = CaretPosition;
        CaretMoved?.Invoke(line, col);
    }

    public void ApplySettings(bool wordWrap, double fontSize)
    {
        Editor.TextWrapping = wordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Editor.FontSize = Compat.Clamp(fontSize, 9, 32);
    }

    public void FocusEditor() => Editor.Focus();

    // ---------------------------------------------------------------- 跳到行尾 / 文末

    /// <summary>
    /// End：光标到当前行的行尾（换行符之前）。
    /// Ctrl+End：光标到全文最后（最后一行之后，也就是文档末尾）。
    /// Shift 一起按时改为"选中到该位置"，跟系统习惯一致。
    ///
    /// 这两个组合 WPF 的 TextBox 本来就有，但仍然自己接管：
    /// 一是行尾要去掉 \r\n（内置行为在 CRLF 文件里会把光标停到换行符后面，
    /// 再按一下 End 才到真正的行尾）；二是长行不换行时要把光标横向滚进可视区，
    /// 内置行为在 NoWrap + 横向滚动条的情况下不总能滚到位。
    /// </summary>
    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.End) return;

        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Alt) != 0) return;             // Alt 组合不抢

        MoveCaretToEnd((mods & ModifierKeys.Control) != 0, (mods & ModifierKeys.Shift) != 0);
        e.Handled = true;
    }

    /// <summary>菜单/按钮调用：跳到当前行行尾。</summary>
    public void JumpToLineEnd()
    {
        Editor.Focus();
        MoveCaretToEnd(toDocumentEnd: false, extend: false);
    }

    /// <summary>菜单/按钮调用：跳到全文最后。</summary>
    public void JumpToDocumentEnd()
    {
        Editor.Focus();
        MoveCaretToEnd(toDocumentEnd: true, extend: false);
    }

    private void MoveCaretToEnd(bool toDocumentEnd, bool extend)
    {
        var text = Editor.Text;
        var target = toDocumentEnd ? text.Length : CurrentLineEnd(text);
        target = Compat.Clamp(target, 0, text.Length);

        if (extend)
        {
            // 以原选区的起点为锚点，向目标位置扩选
            var anchor = Editor.SelectionStart;
            if (target >= anchor) Editor.Select(anchor, target - anchor);
            else Editor.Select(target, anchor - target);
        }
        else
        {
            Editor.CaretIndex = target;      // 赋值会顺手清掉选区
        }

        EnsureCaretVisible();
    }

    /// <summary>光标所在行的行尾位置：不含行尾的 \r\n。</summary>
    private int CurrentLineEnd(string text)
    {
        var idx = Compat.Clamp(Editor.CaretIndex, 0, text.Length);
        var line = Editor.GetLineIndexFromCharacterIndex(idx);
        if (line < 0) return idx;

        var start = Editor.GetCharacterIndexFromLineIndex(line);
        if (start < 0) return idx;

        var end = start + Editor.GetLineLength(line);
        if (end > text.Length) end = text.Length;
        while (end > start && (text[end - 1] == '\r' || text[end - 1] == '\n')) end--;
        return end;
    }

    /// <summary>
    /// 把刚移动到的光标滚进可视区。纵向用 ScrollToLine（可靠）；
    /// 横向没有现成 API，按光标矩形和可视区的差值补一次偏移。
    /// </summary>
    private void EnsureCaretVisible()
    {
        Editor.UpdateLayout();

        var line = Editor.GetLineIndexFromCharacterIndex(Editor.CaretIndex);
        if (line >= 0) Editor.ScrollToLine(line);
        Editor.UpdateLayout();

        var caret = Editor.GetRectFromCharacterIndex(Editor.CaretIndex, false);
        if (caret.IsEmpty) return;

        var view = TextViewportRect();
        if (view.IsEmpty) return;

        const double pad = 24;
        if (caret.Right > view.Right)
            Editor.ScrollToHorizontalOffset(Editor.HorizontalOffset + (caret.Right - view.Right) + pad);
        else if (caret.Left < view.Left)
            Editor.ScrollToHorizontalOffset(Math.Max(0, Editor.HorizontalOffset - (view.Left - caret.Left) - pad));
    }


    // ---------------------------------------------------------------- 载入

    /// <summary>把磁盘内容装进本页，并把它设为同步基准（清脏标记）。</summary>
    public void Load(TextFile file, bool resetCaret = true)
    {
        Engine.Attach(file);

        _suppressText = true;
        Editor.Text = file.Text;
        _suppressText = false;

        _dirty = false;
        _matchStart = -1;          // 换文件/重新加载后旧的命中位置作废
        if (resetCaret)
        {
            Editor.CaretIndex = 0;
            Editor.ScrollToHome();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        RefreshMatchInfo();
    }

    // ---------------------------------------------------------------- 轮询

    public void Poll(bool confirm)
    {
        if (File is null || IsPrompting) return;
        Handle(Engine.Poll(Editor.Text, confirm));
    }

    private void Handle(SyncEvent ev)
    {
        switch (ev.Kind)
        {
            case SyncKind.None:
                return;

            case SyncKind.Reload:
            {
                var lost = _dirty;
                Load(ev.File!);
                StatusChanged?.Invoke(lost
                    ? "已同步外部修改：本地未保存的内容已被磁盘版本覆盖。"
                    : "已同步外部修改。", lost ? "warn" : "ok");
                break;
            }

            case SyncKind.Prompt:
            {
                IsPrompting = true;
                var answer = MessageBox.Show(
                    Window.GetWindow(this),
                    $"文件「{DisplayName}」在磁盘上被修改了。\n\n是否重新加载磁盘内容？\n\n" +
                    "选择「否」将保留当前编辑区内容；该版本不会再次提示，直到外部再次修改。",
                    "外部修改",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                IsPrompting = false;
                Handle(Engine.Answer(answer == MessageBoxResult.Yes, ev.File!));
                break;
            }

            case SyncKind.Missing:
                StatusChanged?.Invoke(ev.Message, "warn");
                break;

            case SyncKind.Recovered:
                StatusChanged?.Invoke(ev.Message, "ok");
                break;
        }
    }

    // ---------------------------------------------------------------- 保存 / 重载

    public bool Save()
    {
        if (File is null) return SaveAs();
        try
        {
            File.Save(Editor.Text);
            Engine.NoteOwnWrite(File);
            _dirty = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke("已保存。", "ok");
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), $"保存失败：{ex.Message}", "保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public bool SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = DisplayName ?? "未命名.txt",
            DefaultExt = ".txt",
        };
        if (!string.IsNullOrWhiteSpace(Path))
            dlg.InitialDirectory = System.IO.Path.GetDirectoryName(Path);

        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return false;

        try
        {
            var bytes = File is null
                ? System.Text.Encoding.UTF8.GetBytes(TextFile.NormalizeToCrlf(Editor.Text))
                : File.EncodeFor(Editor.Text);
            System.IO.File.WriteAllBytes(dlg.FileName, bytes);

            Load(TextFile.Read(dlg.FileName));
            StatusChanged?.Invoke($"已另存为「{DisplayName}」", "ok");
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), $"保存失败：{ex.Message}", "另存为",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>用户主动「从磁盘重新加载」(F5)。</summary>
    public void ReloadFromDisk()
    {
        if (File is null) return;
        Handle(Engine.ForceReload());
    }

    // ---------------------------------------------------------------- 关闭

    /// <summary>关标签前的保存询问。返回 false 表示用户取消了关闭。</summary>
    public bool ConfirmClose()
    {
        if (!_dirty) return true;

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"「{DisplayName}」有未保存的修改，是否保存？",
            "关闭标签",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ---------------------------------------------------------------- 查找 / 替换

    private bool CaseSensitive => CaseBox.IsChecked == true;
    private StringComparison Cmp => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public bool IsFindBarOpen => FindBar.Visibility == Visibility.Visible;

    /// <summary>打开查找条；withReplace = true 时同时显示替换行。</summary>
    public void ShowFind(bool withReplace)
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceBoxHost.Visibility = withReplace ? Visibility.Visible : Visibility.Collapsed;
        ReplaceGroup.Visibility = withReplace ? Visibility.Visible : Visibility.Collapsed;

        // 有单词选中就带进查找框，省得手打
        var sel = Editor.SelectedText;
        if (!string.IsNullOrEmpty(sel) && sel.Length <= 120
            && !Compat.HasChar(sel, '\n') && !Compat.HasChar(sel, '\r'))
            FindBox.Text = sel;

        FindBox.Focus();
        FindBox.SelectAll();
        RefreshMatchInfo();
    }

    /// <summary>Esc：关查找条并回到编辑区。返回是否真的关了。</summary>
    public bool CloseFindIfOpen()
    {
        if (!IsFindBarOpen) return false;
        CloseFindBar();
        return true;
    }

    public void FindNextPublic(bool forward) => FindNext(forward);

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        HighlightLayer.Clear();          // 查找条关了就不再标命中
        _matchStart = -1;
        Editor.Focus();
    }

    private void OnFindClose(object sender, RoutedEventArgs e) => CloseFindBar();

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _matchStart = -1;          // 换了查找词，下次「下一个」从光标处重新开始
        RefreshMatchInfo();
    }

    private void OnCaseChanged(object sender, RoutedEventArgs e) => RefreshMatchInfo();

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(!((Keyboard.Modifiers & ModifierKeys.Shift) != 0));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void OnReplaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ReplaceOne();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext(true);

    private void OnFindPrev(object sender, RoutedEventArgs e) => FindNext(false);

    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceOne();

    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();

    private void FindNext(bool forward)
    {
        var needle = FindBox.Text;
        if (needle.Length == 0) return;

        var hay = Editor.Text;
        int idx;
        if (forward)
        {
            var from = _matchStart >= 0 ? _matchStart + needle.Length : Editor.CaretIndex;
            if (from > hay.Length) from = hay.Length;
            idx = from <= hay.Length ? hay.IndexOf(needle, from, Cmp) : -1;
            if (idx < 0) idx = hay.IndexOf(needle, 0, Cmp);            // 绕回开头
        }
        else
        {
            var to = (_matchStart >= 0 ? _matchStart : Editor.CaretIndex) - 1;
            if (to >= hay.Length) to = hay.Length - 1;
            idx = to >= 0 ? hay.LastIndexOf(needle, to, Cmp) : -1;
            if (idx < 0) idx = hay.LastIndexOf(needle, hay.Length - 1, Cmp);   // 绕回末尾
        }

        if (idx < 0)
        {
            SetFindInfo("无结果", true);
            RefreshHighlights();
            return;
        }

        FocusMatch(idx, needle.Length);
        RefreshMatchInfo(idx);
    }

    /// <summary>把命中设为当前：只放光标、不选中，滚动到可见，并让开右上角的查找面板。</summary>
    private void FocusMatch(int start, int length)
    {
        _matchStart = start;

        // CaretIndex 赋值会顺手清掉选区（用户手动选着一段时，查找不该保留那段高亮）
        if (Editor.CaretIndex != start) Editor.CaretIndex = start;

        Editor.ScrollToLine(Editor.GetLineIndexFromCharacterIndex(start));
        EnsureNotBehindFindBar(start, length);
    }

    /// <summary>当前命中还在原位、且内容仍是查找词吗（文本可能被改过）。</summary>
    private bool MatchIsCurrent()
    {
        var needle = FindBox.Text;
        if (needle.Length == 0 || _matchStart < 0) return false;

        var hay = Editor.Text;
        return _matchStart + needle.Length <= hay.Length
               && string.Compare(hay, _matchStart, needle, 0, needle.Length, Cmp) == 0;
    }

    private void ReplaceOne()
    {
        var needle = FindBox.Text;
        if (needle.Length == 0) return;

        if (!MatchIsCurrent())
        {
            FindNext(true);
            return;
        }

        var at = _matchStart;
        Editor.Select(at, needle.Length);          // 只在这一瞬间用一下选区，写完立刻清掉
        Editor.SelectedText = ReplaceBox.Text;
        Editor.Select(at + ReplaceBox.Text.Length, 0);
        _matchStart = -1;
        FindNext(true);
        StatusChanged?.Invoke("已替换 1 处。", "ok");
    }

    private void ReplaceAll()
    {
        var needle = FindBox.Text;
        if (needle.Length == 0) return;

        var hay = Editor.Text;
        int count;
        if (CaseSensitive)
        {
            count = CountMatches(hay, needle, StringComparison.Ordinal).Total;
            if (count == 0) { SetFindInfo("无结果", true); RefreshHighlights(); return; }
            _suppressText = true;
            Editor.Text = Compat.Replace(hay, needle, ReplaceBox.Text, StringComparison.Ordinal);
            _suppressText = false;
        }
        else
        {
            var pattern = new Regex(Regex.Escape(needle), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            count = pattern.Matches(hay).Count;
            if (count == 0) { SetFindInfo("无结果", true); RefreshHighlights(); return; }
            _suppressText = true;
            Editor.Text = pattern.Replace(hay, _ => ReplaceBox.Text);
            _suppressText = false;
        }

        // 程序改写文本不会触发 TextChanged 里的脏判断，这里补一次
        var dirty = File is not null && !string.Equals(Editor.Text, File.Text, StringComparison.Ordinal);
        if (dirty != _dirty) { _dirty = dirty; StateChanged?.Invoke(this, EventArgs.Empty); }

        Editor.CaretIndex = 0;
        RefreshMatchInfo();
        StatusChanged?.Invoke($"已替换 {count} 处。", "ok");
    }

    /// <summary>
    /// 统计匹配数并刷新高亮；needle 为空或文本过大时给出提示。
    /// currentStart 传命中在正文里的下标，序号（第几处）由它反推——不能直接拿下标当序号。
    /// </summary>
    private void RefreshMatchInfo(int? currentStart = null)
    {
        var needle = FindBox.Text;
        string text;
        var noMatch = false;

        if (!IsFindBarOpen || needle.Length == 0)
        {
            text = "";
        }
        else
        {
            var hay = Editor.Text;
            if (hay.Length > CountLimit)
            {
                text = "…";
            }
            else
            {
                var (total, ordinal) = CountMatches(hay, needle, Cmp, currentStart);
                if (total == 0)
                {
                    text = "无结果";
                    noMatch = true;
                }
                else
                {
                    text = ordinal > 0 ? $"{ordinal}/{total}" : $"{total} 处";
                }
            }
        }

        SetFindInfo(text, noMatch);
        RefreshHighlights();
    }

    /// <summary>
    /// 更新计数文字；无结果时把输入框标红（VS Code 的做法）。
    /// 边框颜色在模板触发器里，靠 Tag="nomatch" 切换，比直接改 BorderBrush 可靠。
    /// </summary>
    private void SetFindInfo(string text, bool noMatch)
    {
        FindInfo.Text = text;
        FindInfo.Foreground = noMatch
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("MutedBrush");

        FindBox.Tag = noMatch ? "nomatch" : null;
    }

    /// <summary>统计匹配总数；给了 currentStart 时顺带算出它是第几处。</summary>
    private static (int Total, int Ordinal) CountMatches(
        string hay, string needle, StringComparison cmp, int? currentStart = null)
    {
        var total = 0;
        var ordinal = 0;

        var i = 0;
        while (i <= hay.Length - needle.Length)
        {
            var hit = hay.IndexOf(needle, i, cmp);
            if (hit < 0) break;

            total++;
            if (ordinal == 0 && currentStart is int cs && hit == cs) ordinal = total;

            i = hit + needle.Length;
        }
        return (total, ordinal);
    }

    // ---------------------------------------------------------------- 命中高亮

    /// <summary>单行里最多标这么多处，防止一行几万个匹配时画到卡。</summary>
    private const int MaxMarksPerScreen = 400;

    /// <summary>
    /// 重新计算可视区内的所有命中矩形，交给高亮层去画。
    /// 只扫可视行，所以大文件滚动时也不会慢。
    /// </summary>
    private void RefreshHighlights()
    {
        if (!IsFindBarOpen)
        {
            HighlightLayer.Clear();
            return;
        }

        var needle = FindBox.Text;
        var hay = Editor.Text;
        if (needle.Length == 0 || hay.Length == 0)
        {
            HighlightLayer.Clear();
            return;
        }

        var firstLine = Editor.GetFirstVisibleLineIndex();
        var lastLine = Editor.GetLastVisibleLineIndex();
        if (firstLine < 0 || lastLine < firstLine)
        {
            HighlightLayer.Clear();
            return;
        }

        var winStart = Editor.GetCharacterIndexFromLineIndex(firstLine);
        if (winStart < 0) winStart = 0;

        var winEnd = Editor.GetCharacterIndexFromLineIndex(lastLine);
        winEnd = winEnd < 0
            ? hay.Length
            : Math.Min(hay.Length, winEnd + Editor.GetLineLength(lastLine));

        // 匹配可能从上一行末尾开始，往回多看一个 needle 的长度
        var scan = Math.Max(0, winStart - Math.Min(needle.Length - 1, 512));

        var curIsMatch = MatchIsCurrent();
        var curStart = _matchStart;

        var marks = new List<HighlightMark>();
        var i = scan;
        while (i <= winEnd - needle.Length && marks.Count < MaxMarksPerScreen)
        {
            var hit = hay.IndexOf(needle, i, Cmp);
            if (hit < 0 || hit + needle.Length > winEnd) break;

            var r = RectFor(hit, needle.Length);
            if (!r.IsEmpty) marks.Add(new HighlightMark(r, curIsMatch && hit == curStart));

            i = hit + Math.Max(needle.Length, 1);
        }

        HighlightLayer.SetClip(TextViewportRect());
        HighlightLayer.SetMarks(marks);
    }

    /// <summary>字符区间 → 屏幕矩形。查找词不含换行，正常都是一行内的事；跨行时只标第一行那段。</summary>
    private Rect RectFor(int start, int length)
    {
        if (length <= 0) return Rect.Empty;

        var head = Editor.GetRectFromCharacterIndex(start, false);
        if (head.IsEmpty) return Rect.Empty;

        var tail = Editor.GetRectFromCharacterIndex(start + length - 1, true);
        if (tail.IsEmpty || Math.Abs(tail.Top - head.Top) > 0.5)
            tail = Editor.GetRectFromCharacterIndex(start, true);   // 跨行/取不到：退化成单字符宽度

        var left = head.Left;
        var right = Math.Max(tail.Right, left + 1);
        var top = Math.Min(head.Top, tail.Top);
        var height = head.Height > 0 ? head.Height : 1;

        return new Rect(left, top + 1, right - left, Math.Max(height - 2, 3));
    }

    /// <summary>正文可视区（不含滚动条），高亮只在里面画。</summary>
    private Rect TextViewportRect()
    {
        var w = Editor.ActualWidth;
        var h = Editor.ActualHeight;
        if (w <= 0 || h <= 0) return Rect.Empty;

        // TextBox 没有 ScrollViewer.ComputedXxx 可用，用 extent/viewport 判断滚动条在不在
        if (Editor.ExtentWidth > Editor.ViewportWidth + 0.5)
            w -= SystemParameters.VerticalScrollBarWidth;
        if (Editor.ExtentHeight > Editor.ViewportHeight + 0.5)
            h -= SystemParameters.HorizontalScrollBarHeight;

        return new Rect(0, 0, Math.Max(w, 0), Math.Max(h, 0));
    }

    /// <summary>
    /// 命中位置如果正好被右上角浮着的查找面板压住，用户会以为"没高亮/没找到"。
    /// 这种时候把编辑区往上滚一点，让命中的行挪到面板下面来。
    /// 注意 ScrollToVerticalOffset 的单位不是像素（是行），所以不自己换算，
    /// 而是每次滚一个单位再量一次，滚到露出来就停；滚不动（到底/到顶）就放弃。
    /// 文档已经在最顶上时挪不动——那几行确实会被面板压住，只能这样。
    /// </summary>
    private void EnsureNotBehindFindBar(int start, int length)
    {
        if (!IsFindBarOpen || FindBar.ActualHeight <= 0) return;

        Editor.UpdateLayout();
        if (Editor.VerticalOffset <= 0) return;

        var origin = FindBar.TranslatePoint(new Point(0, 0), Editor);
        var bar = new Rect(origin.X - 6, origin.Y - 6,
                           FindBar.ActualWidth + 12, FindBar.ActualHeight + 12);

        var lastTop = double.NaN;
        for (var step = 0; step < 80; step++)
        {
            var r = RectFor(start, length);
            if (r.IsEmpty || !r.IntersectsWith(bar)) return;

            if (!double.IsNaN(lastTop) && Math.Abs(r.Top - lastTop) < 0.5) return;   // 滚不动了
            lastTop = r.Top;

            Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset - 1));
            Editor.UpdateLayout();
        }
    }

    // ---------------------------------------------------------------- 编辑状态

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressText) return;

        RefreshMatchInfo();

        var dirty = File is not null && !string.Equals(Editor.Text, File.Text, StringComparison.Ordinal);
        if (dirty == _dirty) return;

        _dirty = dirty;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- 鼠标

    /// <summary>
    /// 双击选词 / 三击选行。
    ///
    /// 为什么不直接修剪 WPF 的默认选区：TextBox 的"双击选词"是在 MouseUp 阶段才执行的
    /// （它要等第三次点击来了才能判断是双击还是三击），所以 MouseDoubleClick（MouseDown 阶段）
    /// 里拿到的选区还是上一次的，修剪等于没做，用户看到的仍然是 "void " 带着尾随空格。
    /// 这里改在 PreviewMouseDown 自己算词边界并选中，彻底不带空白；
    /// 三击选整行（不含换行符），和记事本一致。
    /// </summary>
    private void OnEditorPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount < 2) return;

        var idx = Editor.GetCharacterIndexFromPoint(e.GetPosition(Editor), true);
        if (idx < 0) return;   // 点在文本之外，交给默认处理

        if (e.ClickCount >= 3)
        {
            var line = Editor.GetLineIndexFromCharacterIndex(idx);
            if (line < 0) return;
            var start = Editor.GetCharacterIndexFromLineIndex(line);
            var len = Editor.GetLineLength(line);
            if (start < 0 || len <= 0) return;

            // GetLineLength 把换行符算在内，选整行时要去掉尾部 \r\n（和记事本一致）
            while (len > 0 && (Editor.Text[start + len - 1] == '\r' || Editor.Text[start + len - 1] == '\n'))
                len--;
            if (len <= 0) return;

            Editor.Focus();
            Editor.Select(start, len);
            e.Handled = true;
            return;
        }

        var (wordStart, wordLen) = SelectionTrim.WordAt(Editor.Text, idx);
        if (wordLen <= 0) return;

        Editor.Focus();
        Editor.Select(wordStart, wordLen);
        e.Handled = true;
    }
}
