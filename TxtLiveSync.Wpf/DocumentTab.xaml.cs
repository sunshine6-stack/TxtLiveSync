using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace TxtLiveSync;

/// <summary>
/// 一个标签页 = 一个文件。负责自己的编辑区、SyncEngine、脏标记、保存/重新加载、查找替换、
/// 折叠与格式化，与主窗口只通过事件和几个公开方法交互，方便多标签共存。
///
/// 编辑区用 AvalonEdit 而不是 TextBox：折叠必须在「行可以被隐藏」的渲染器上做，
/// TextBox 只有一条文本流、没有行模型，做不到。
/// </summary>
public partial class DocumentTab : UserControl
{
    /// <summary>超过这个长度就不统计匹配总数了（逐字符扫描会卡）。</summary>
    private const int CountLimit = 1_500_000;

    /// <summary>超过这个长度就不算折叠区间了，免得每次输入都全量扫一遍大文件。</summary>
    private const int FoldLimit = 2_000_000;

    /// <summary>单行里最多标这么多处，防止一行几万个匹配时画到卡。</summary>
    private const int MaxMarksPerScreen = 400;

    private bool _suppressText;   // 程序改写文档时避免误判为编辑
    private bool _dirty;

    /// <summary>还没有对应磁盘文件时，标签上显示的名字（新建的空白文档）。</summary>
    private string _untitledName = "未命名";

    /// <summary>当前命中的起点（-1 = 还没有）。命中只放光标不选中，色块由背景渲染层画。</summary>
    private int _matchStart = -1;

    private readonly SearchHighlightRenderer _highlighter = new();
    private readonly DispatcherTimer _foldTimer;
    private readonly FoldingManager _foldManager;

    public SyncEngine Engine { get; } = new();

    public TextFile? File => Engine.File;
    public string? Path => File?.Path;
    public string DisplayName => File?.Name ?? _untitledName;
    public string ToolTipPath => File?.Path ?? "尚未保存到磁盘的新文档";
    public bool IsDirty => _dirty;

    /// <summary>当前字号（主窗口调字号时用）。</summary>
    public double EditorFontSize => Editor.FontSize;

    /// <summary>弹窗未关闭时暂停本页轮询，避免同一变更反复询问。</summary>
    public bool IsPrompting { get; private set; }

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
            var caret = Editor.TextArea.Caret;
            return (Math.Max(caret.Line, 1), Math.Max(caret.Column, 1));
        }
    }

    public DocumentTab()
    {
        InitializeComponent();

        // 保留 Tab 字符、不自动替换成空格：这是 TXT 编辑器，别偷偷改用户的缩进
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.IndentationSize = 4;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.CutCopyWholeLine = true;

        Editor.TextArea.TextView.BackgroundRenderers.Add(_highlighter);

        // 折叠：Install 会顺手把左侧的 +/- 按钮（FoldingMargin）挂到边距栏里
        _foldManager = FoldingManager.Install(Editor.TextArea);

        // 反过来把它挪到行号左边：VS / VS Code 的折叠按钮都在最左侧
        FoldingMargin? foldingMargin = null;
        foreach (var margin in Editor.TextArea.LeftMargins)
        {
            if (margin is FoldingMargin fm) { foldingMargin = fm; break; }
        }
        if (foldingMargin is not null)
        {
            Editor.TextArea.LeftMargins.Remove(foldingMargin);
            Editor.TextArea.LeftMargins.Insert(0, foldingMargin);
        }

        _foldTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _foldTimer.Tick += (_, _) =>
        {
            _foldTimer.Stop();
            UpdateFoldings();
        };

        Editor.TextArea.Caret.PositionChanged += (_, _) => RaiseCaretMoved();
        Editor.TextArea.SelectionChanged += (_, _) => RaiseCaretMoved();
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => RefreshHighlights();
        Editor.SizeChanged += (_, _) => RefreshHighlights();
    }

    private void RaiseCaretMoved()
    {
        var (line, col) = CaretPosition;
        CaretMoved?.Invoke(line, col);
    }

    public void ApplySettings(bool wordWrap, double fontSize)
    {
        Editor.WordWrap = wordWrap;
        Editor.FontSize = Compat.Clamp(fontSize, 9, 32);
    }

    public void FocusEditor() => Editor.TextArea.Focus();

    // ---------------------------------------------------------------- 跳到行尾 / 文末

    /// <summary>
    /// End：光标到当前行的行尾（换行符之前）。Ctrl+End：光标到全文最后。
    /// Shift 一起按时改为「选中到该位置」。
    /// 另外接管 Ctrl+Shift+[ / ] 做折叠、展开。
    /// </summary>
    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        if (e.Key == Key.End && (mods & ModifierKeys.Alt) == 0)
        {
            MoveCaretToEnd((mods & ModifierKeys.Control) != 0, (mods & ModifierKeys.Shift) != 0);
            e.Handled = true;
            return;
        }

        if ((mods & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.OemOpenBrackets)
            {
                ToggleFoldAtCaret(true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemCloseBrackets)
            {
                ToggleFoldAtCaret(false);
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>菜单/按钮调用：跳到当前行行尾。</summary>
    public void JumpToLineEnd()
    {
        FocusEditor();
        MoveCaretToEnd(toDocumentEnd: false, extend: false);
    }

    /// <summary>菜单/按钮调用：跳到全文最后。</summary>
    public void JumpToDocumentEnd()
    {
        FocusEditor();
        MoveCaretToEnd(toDocumentEnd: true, extend: false);
    }

    private void MoveCaretToEnd(bool toDocumentEnd, bool extend)
    {
        var doc = Editor.Document;
        var caret = Editor.TextArea.Caret;
        var offset = Compat.Clamp(caret.Offset, 0, doc.TextLength);

        int target;
        if (toDocumentEnd)
        {
            target = doc.TextLength;
        }
        else
        {
            var line = doc.GetLineByOffset(offset);
            target = line.EndOffset;
            // End 要停在换行符之前：CRLF 文件里默认行为会把光标放到 \n 后面
            while (target > line.Offset)
            {
                var ch = doc.GetCharAt(target - 1);
                if (ch == '\r' || ch == '\n') target--;
                else break;
            }
        }

        if (!extend)
        {
            Editor.CaretOffset = target;
        }
        else
        {
            // 锚点取「选区的另一端」，这样 Shift+End 是延续已有选区，而不是重新开始
            var anchor = offset;
            if (Editor.SelectionLength > 0)
            {
                var s = Editor.SelectionStart;
                var e = s + Editor.SelectionLength;
                anchor = offset <= s ? e : s;
            }

            if (target >= anchor) Editor.Select(anchor, target - anchor);
            else Editor.Select(target, anchor - target);
        }

        Editor.TextArea.Caret.BringCaretToView();
    }

    // ---------------------------------------------------------------- 载入 / 新建

    /// <summary>把磁盘内容装进本页，并把它设为同步基准（清脏标记）。</summary>
    public void Load(TextFile file, bool resetCaret = true)
    {
        Engine.Attach(file);
        SetTextQuiet(file.Text);
        Editor.Document.UndoStack.ClearAll();

        _dirty = false;
        _matchStart = -1;          // 换文件/重新加载后旧的命中位置作废

        if (resetCaret)
        {
            Editor.CaretOffset = 0;
            Editor.ScrollToHome();
        }

        ApplySyntaxHighlighting();
        UpdateFoldings();
        StateChanged?.Invoke(this, EventArgs.Empty);
        RefreshMatchInfo();
    }

    /// <summary>
    /// 把本页变成一张空白的、不关联磁盘文件的新文档。
    /// 之后保存会走「另存为」，让用户自己挑位置和文件名。
    /// </summary>
    public void NewDocument(string untitledName)
    {
        Engine.Detach();                                  // 断开与旧文件的同步
        _untitledName = string.IsNullOrWhiteSpace(untitledName) ? "未命名" : untitledName;

        SetTextQuiet("");
        Editor.Document.UndoStack.ClearAll();

        _dirty = false;
        _matchStart = -1;

        if (IsFindBarOpen)
        {
            FindBar.Visibility = Visibility.Collapsed;
            FindBox.Text = "";
            _highlighter.Clear();
        }

        Editor.CaretOffset = 0;
        Editor.ScrollToHome();

        ApplySyntaxHighlighting();
        UpdateFoldings();
        StateChanged?.Invoke(this, EventArgs.Empty);
        RefreshMatchInfo();
    }

    private void SetTextQuiet(string text)
    {
        _suppressText = true;
        try
        {
            Editor.Text = text;
        }
        finally
        {
            _suppressText = false;
        }
    }

    // ---------------------------------------------------------------- 折叠

    /// <summary>按文件类型重算可折叠区间。输入时走 350ms 防抖，载入/格式化时立即算。</summary>
    private void UpdateFoldings()
    {
        var text = Editor.Text;
        if (text.Length > FoldLimit)
        {
            _foldManager.UpdateFoldings(Array.Empty<NewFolding>(), -1);
            return;
        }

        var kind = TextFormat.DetectKind(Path, text);
        var ranges = FoldingRanges.Build(text, kind);

        var foldings = new List<NewFolding>(ranges.Count);
        foreach (var r in ranges)
        {
            foldings.Add(new NewFolding(r.StartOffset, r.EndOffset));
        }

        _foldManager.UpdateFoldings(foldings, -1);
    }

    /// <summary>Ctrl+Shift+[ / ]：折叠或展开光标所在的那一段。</summary>
    private void ToggleFoldAtCaret(bool fold)
    {
        var offset = Compat.Clamp(Editor.CaretOffset, 0, Editor.Document.TextLength);

        FoldingSection? best = null;
        foreach (var section in _foldManager.GetFoldingsContaining(offset))
        {
            if (section.IsFolded == fold) continue;
            if (best is null || section.StartOffset >= best.StartOffset) best = section;
        }

        if (best is null)
        {
            StatusChanged?.Invoke(fold ? "光标处没有可折叠的块。" : "光标处没有已折叠的块。", "muted");
            return;
        }

        best.IsFolded = fold;
    }

    /// <summary>视图菜单：全部折叠 / 全部展开。</summary>
    public void FoldAll(bool fold)
    {
        var count = 0;
        foreach (var section in _foldManager.AllFoldings)
        {
            if (section.IsFolded == fold) continue;
            section.IsFolded = fold;
            count++;
        }
        StatusChanged?.Invoke(fold ? $"已折叠 {count} 处。" : $"已展开 {count} 处。", "muted");
    }

    // ---------------------------------------------------------------- 格式化

    /// <summary>格式化当前文档（JSON / XML）。走 Document.Replace，所以 Ctrl+Z 能撤回。</summary>
    public bool FormatDocument()
    {
        var text = Editor.Text;
        var kind = TextFormat.DetectKind(Path, text);

        if (kind == TextKind.Plain)
        {
            StatusChanged?.Invoke("这个文件按扩展名不是 JSON / XML，未做格式化。", "warn");
            return false;
        }

        if (!TextFormat.TryFormat(text, kind, out var formatted, out var error))
        {
            StatusChanged?.Invoke(error, "warn");
            return false;
        }

        if (string.Equals(formatted, text, StringComparison.Ordinal))
        {
            StatusChanged?.Invoke("已经是格式化后的样子，没有改动。", "muted");
            return false;
        }

        var caret = Editor.TextArea.Caret;
        var line = caret.Line;
        var column = caret.Column;

        _suppressText = true;
        try
        {
            Editor.Document.Replace(0, Editor.Document.TextLength, formatted);
        }
        finally
        {
            _suppressText = false;
        }

        // 光标按原来的行列还原（格式化后行号会变，尽量落在原来那一行的同一列）
        var doc = Editor.Document;
        var targetLine = doc.GetLineByNumber(Compat.Clamp(line, 1, doc.LineCount));
        var targetColumn = Compat.Clamp(column, 1, targetLine.Length + 1);
        Editor.CaretOffset = targetLine.Offset + targetColumn - 1;
        caret.BringCaretToView();

        var dirty = ComputeDirty();
        _dirty = dirty;

        ApplySyntaxHighlighting();
        _foldTimer.Stop();
        UpdateFoldings();
        RefreshMatchInfo();
        StateChanged?.Invoke(this, EventArgs.Empty);

        StatusChanged?.Invoke(kind == TextKind.Json ? "已格式化 JSON。" : "已格式化 XML。", "ok");
        return true;
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
            Filter = "文本文件 (*.txt)|*.txt|JSON (*.json)|*.json|XML (*.xml)|*.xml|所有文件 (*.*)|*.*",
            FileName = File is null ? "新建文本文档.txt" : DisplayName,
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
            && sel.IndexOf('\n') < 0 && sel.IndexOf('\r') < 0)
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
        ClearHighlights();
        _matchStart = -1;
        Editor.TextArea.Focus();
    }

    private void ClearHighlights()
    {
        _highlighter.Clear();
        Editor.TextArea.TextView.Redraw();
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
            var from = _matchStart >= 0 ? _matchStart + needle.Length : Editor.CaretOffset;
            if (from > hay.Length) from = hay.Length;
            idx = from <= hay.Length ? hay.IndexOf(needle, from, Cmp) : -1;
            if (idx < 0) idx = hay.IndexOf(needle, 0, Cmp);            // 绕回开头
        }
        else
        {
            var to = (_matchStart >= 0 ? _matchStart : Editor.CaretOffset) - 1;
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

        // CaretOffset 赋值会顺手清掉选区（用户手动选着一段时，查找不该保留那段高亮）
        if (Editor.CaretOffset != start) Editor.CaretOffset = start;

        Editor.ScrollToLine(Editor.Document.GetLineByOffset(start).LineNumber);
        Editor.TextArea.Caret.BringCaretToView();
        EnsureNotBehindFindBar(start, length);
        RefreshHighlights();
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
        Editor.Select(at, needle.Length);
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
        string replaced;

        if (CaseSensitive)
        {
            count = CountMatches(hay, needle, StringComparison.Ordinal).Total;
            if (count == 0) { SetFindInfo("无结果", true); RefreshHighlights(); return; }
            replaced = Compat.Replace(hay, needle, ReplaceBox.Text, StringComparison.Ordinal);
        }
        else
        {
            var pattern = new Regex(Regex.Escape(needle), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            count = pattern.Matches(hay).Count;
            if (count == 0) { SetFindInfo("无结果", true); RefreshHighlights(); return; }
            replaced = pattern.Replace(hay, _ => ReplaceBox.Text);
        }

        _suppressText = true;
        try
        {
            Editor.Document.Replace(0, Editor.Document.TextLength, replaced);
        }
        finally
        {
            _suppressText = false;
        }

        // 程序改写文本不会触发 TextChanged 里的脏判断，这里补一次
        var dirty = ComputeDirty();
        if (dirty != _dirty)
        {
            _dirty = dirty;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        Editor.CaretOffset = 0;
        ScheduleFoldUpdate();
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

    /// <summary>
    /// 重新计算可视区内的所有命中区间，交给背景渲染层去画。
    /// 只扫可视行，所以大文件滚动时也不会慢。
    /// </summary>
    private void RefreshHighlights()
    {
        if (!IsFindBarOpen)
        {
            if (_highlighter.Count > 0) ClearHighlights();
            return;
        }

        var needle = FindBox.Text;
        var hay = Editor.Text;
        if (needle.Length == 0 || hay.Length == 0)
        {
            ClearHighlights();
            return;
        }

        var view = Editor.TextArea.TextView;
        if (!view.VisualLinesValid) view.EnsureVisualLines();
        var lines = view.VisualLines;
        if (lines.Count == 0)
        {
            ClearHighlights();
            return;
        }

        var doc = Editor.Document;
        var firstLine = lines[0].FirstDocumentLine.LineNumber;
        var lastLine = lines[lines.Count - 1].LastDocumentLine.LineNumber;

        var winStart = doc.GetLineByNumber(firstLine).Offset;
        var winEnd = doc.GetLineByNumber(lastLine).EndOffset;

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

            marks.Add(new HighlightMark(hit, needle.Length, curIsMatch && hit == curStart));
            i = hit + Math.Max(needle.Length, 1);
        }

        _highlighter.SetMarks(marks);
        view.Redraw();
    }

    /// <summary>
    /// 命中位置如果正好被右上角浮着的查找面板压住，用户会以为"没高亮/没找到"。
    /// 这种时候把编辑区往上滚一点，让命中的行挪到面板下面来。
    /// </summary>
    private void EnsureNotBehindFindBar(int start, int length)
    {
        if (!IsFindBarOpen || FindBar.ActualHeight <= 0) return;

        var view = Editor.TextArea.TextView;
        if (!view.VisualLinesValid) view.EnsureVisualLines();

        var origin = FindBar.TranslatePoint(new Point(0, 0), view);
        var bar = new Rect(origin.X - 6, origin.Y - 6,
                           FindBar.ActualWidth + 12, FindBar.ActualHeight + 12);

        var seg = new TextSegment { StartOffset = start, Length = Math.Max(1, length) };

        for (var step = 0; step < 40; step++)
        {
            var found = false;
            var rect = Rect.Empty;
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(view, seg))
            {
                rect = r;
                found = true;
                break;
            }
            if (!found || !rect.IntersectsWith(bar)) return;

            var delta = bar.Bottom - rect.Top + 12;
            if (delta <= 1) return;

            var before = view.VerticalOffset;
            Editor.ScrollToVerticalOffset(Math.Max(0, before - delta));
            if (!view.VisualLinesValid) view.EnsureVisualLines();
            if (Math.Abs(view.VerticalOffset - before) < 0.5) return;   // 到顶了，滚不动
        }
    }

    // ---------------------------------------------------------------- 语法高亮

    private void ApplySyntaxHighlighting()
    {
        var kind = TextFormat.DetectKind(Path, Editor.Text);
        IHighlightingDefinition? definition = null;

        if (kind == TextKind.Json)
            definition = HighlightingManager.Instance.GetDefinition("Json");
        else if (kind == TextKind.Xml)
            definition = HighlightingManager.Instance.GetDefinition("XML");

        Editor.SyntaxHighlighting = definition;
    }

    // ---------------------------------------------------------------- 编辑状态

    /// <summary>
    /// 是否算「有未保存的修改」。
    /// 新文档（没关联文件）只要写了字就算脏，否则关标签会悄悄丢掉内容。
    /// </summary>
    private bool ComputeDirty()
        => File is null
            ? Editor.Text.Length > 0
            : !string.Equals(Editor.Text, File.Text, StringComparison.Ordinal);

    private void OnTextChanged(object sender, EventArgs e)
    {
        if (_suppressText) return;

        RefreshMatchInfo();
        ScheduleFoldUpdate();

        var dirty = ComputeDirty();
        if (dirty == _dirty) return;

        _dirty = dirty;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>输入时不要每敲一个字就重算折叠，停手 350ms 再算。</summary>
    private void ScheduleFoldUpdate()
    {
        if (Editor.Text.Length > FoldLimit) return;
        _foldTimer.Stop();
        _foldTimer.Start();
    }

    // ---------------------------------------------------------------- 鼠标

    /// <summary>
    /// 双击选词 / 三击选行。
    ///
    /// AvalonEdit 自带的双击会把词后面的空白一起选上（"void "），
    /// 这里在 Preview 阶段自己按字符类别算词边界并选中，彻底不带空白；
    /// 三击选整行（不含换行符），和记事本一致。
    /// </summary>
    private void OnEditorPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount < 2) return;

        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (pos is null) return;

        var doc = Editor.Document;
        var hitLine = pos.Value.Line;
        if (hitLine < 1 || hitLine > doc.LineCount) return;

        var line = doc.GetLineByNumber(hitLine);
        var column = Compat.Clamp(pos.Value.Column, 1, line.Length + 1);
        var idx = line.Offset + column - 1;

        if (e.ClickCount >= 3)
        {
            if (line.Length <= 0) return;
            Editor.TextArea.Focus();
            Editor.Select(line.Offset, line.Length);
            e.Handled = true;
            return;
        }

        var (wordStart, wordLen) = SelectionTrim.WordAt(doc.Text, idx);
        if (wordLen <= 0) return;

        Editor.TextArea.Focus();
        Editor.Select(wordStart, wordLen);
        e.Handled = true;
    }
}
