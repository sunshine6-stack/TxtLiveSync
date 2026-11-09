using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TxtLiveSync;

/// <summary>
/// 主窗口：菜单 / 工具栏 / 状态栏 + 一个多标签容器。
/// 每个标签是一个 <see cref="DocumentTab"/>，自己管自己的文件与同步。
/// </summary>
public partial class MainWindow : Window
{
    private const int PollMs = 400;

    /// <summary>标签头里的可视元素，脏标记变化时只改这几个。</summary>
    private sealed class TabChrome
    {
        public required TabItem Item { get; init; }
        public required TextBlock Name { get; init; }
        public required Ellipse Dot { get; init; }
        public required Button Close { get; init; }
    }

    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<DocumentTab, TabChrome> _tabs = new();

    private bool _pollBusy;   // 轮询/弹窗期间防止重入

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollMs),
        };
        _timer.Tick += OnTick;
    }

    private IEnumerable<DocumentTab> Docs => Tabs.Items.Cast<TabItem>()
        .Select(t => t.Content).OfType<DocumentTab>();

    private DocumentTab? Active => (Tabs.SelectedItem as TabItem)?.Content as DocumentTab;

    // ---------------------------------------------------------------- 启动

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindow();
        ApplySettings();

        var args = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !a.StartsWith('-'))
            .Where(a => !string.IsNullOrWhiteSpace(a) && System.IO.File.Exists(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (args.Count > 0)
        {
            foreach (var p in args) OpenPath(p, quiet: false);
        }
        else
        {
            var restore = (_cfg.OpenPaths ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var p in restore) OpenPath(p, quiet: true);

            var active = _tabs.Keys.FirstOrDefault(d =>
                string.Equals(d.Path, _cfg.ActivePath, StringComparison.OrdinalIgnoreCase));
            if (active is not null) SelectDoc(active);
        }

        _timer.Start();

        if (Active is { } doc)
        {
            doc.FocusEditor();
            SetStatus(Welcome(doc), "muted");
        }
        else
        {
            SetStatus("就绪 · 拖入文件或按 Ctrl+O 打开", "muted");
        }

        UpdateChrome();

        // 窗口就绪后，把启动期间另一个进程转发过来的文件补上
        (Application.Current as App)?.MarkReady();
    }

    private static string Welcome(DocumentTab doc) => $"已打开「{doc.DisplayName}」";

    private void RestoreWindow()
    {
        if (_cfg.Width is double w && w >= MinWidth) Width = w;
        if (_cfg.Height is double h && h >= MinHeight) Height = h;

        if (_cfg.Left is double x && _cfg.Top is double y)
        {
            // 只接受落在当前屏幕范围内的位置，避免窗口跑到看不见的地方
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;
            if (x > vl - 50 && y > vt - 50 && x < vl + vw - 80 && y < vt + vh - 80)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x;
                Top = y;
            }
        }

        if (_cfg.Maximized) WindowState = WindowState.Maximized;
    }

    private void ApplySettings()
    {
        SingleInstanceItem.IsChecked = _cfg.SingleInstance;
        ConfirmItem.IsChecked = _cfg.ConfirmOnExternalChange;
        WrapItem.IsChecked = _cfg.WordWrap;

        foreach (var doc in Docs) doc.ApplySettings(_cfg.WordWrap, _cfg.FontSize);
    }

    /// <summary>
    /// 另一个进程（双击 .txt 拉起来的那个）把文件转交过来了：
    /// 先把本窗口拉到前台，再按普通"打开文件"的规则处理（已开过就切到那个标签）。
    /// </summary>
    public void OpenFromOtherInstance(IReadOnlyList<string> paths)
    {
        SingleInstance.BringToFront(this);

        if (paths.Count == 0)
        {
            SetStatus("已切换到正在运行的窗口。", "muted");
            return;
        }

        DocumentTab? last = null;
        foreach (var p in paths) last = OpenPath(p, quiet: false) ?? last;

        if (last is null) return;
        SetStatus(paths.Count > 1 ? $"已在当前窗口打开 {paths.Count} 个文件。" : Welcome(last), "muted");
    }

    // ---------------------------------------------------------------- 标签管理

    /// <summary>打开一个文件；已经打开过就直接切到那个标签。</summary>
    private DocumentTab? OpenPath(string path, bool quiet)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            if (!quiet) MessageBox.Show(this, $"路径无效：{path}", "打开文件",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var existing = _tabs.Keys.FirstOrDefault(d =>
            string.Equals(d.Path, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectDoc(existing);
            SetStatus($"「{existing.DisplayName}」已经打开，已切到该标签。", "muted");
            return existing;
        }

        try
        {
            var file = TextFile.Read(full);
            var doc = new DocumentTab();
            doc.ApplySettings(_cfg.WordWrap, _cfg.FontSize);
            doc.Load(file);
            AddTab(doc);
            SelectDoc(doc);
            return doc;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.ArgumentException or NotSupportedException)
        {
            if (quiet) SetStatus($"「{System.IO.Path.GetFileName(path)}」无法打开，已跳过。", "warn");
            else MessageBox.Show(this, $"打开失败：{ex.Message}", "打开文件",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private void AddTab(DocumentTab doc)
    {
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource("AccentBrush"),
            Visibility = Visibility.Collapsed,
        };

        var name = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = doc.DisplayName,
        };

        var close = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("TabCloseButton"),
            ToolTip = "关闭这个标签（Ctrl+W）",
        };
        AutomationProperties.SetName(close, $"关闭 {doc.DisplayName}");
        AutomationProperties.SetAutomationId(close, "TabClose");
        close.Click += (_, _) => CloseTab(doc);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(dot);
        header.Children.Add(name);
        header.Children.Add(close);

        var item = new TabItem { Header = header, Content = doc };
        AutomationProperties.SetName(item, doc.DisplayName);
        AutomationProperties.SetAutomationId(item, "DocTab");
        item.ToolTip = doc.ToolTipPath;

        _tabs[doc] = new TabChrome { Item = item, Name = name, Dot = dot, Close = close };

        doc.StateChanged += (_, _) =>
        {
            RefreshHeader(doc);
            if (ReferenceEquals(doc, Active)) UpdateChrome();
        };
        doc.StatusChanged += (msg, tone) =>
        {
            // 非当前标签的消息带上文件名，免得看起来像当前文件的
            var prefix = ReferenceEquals(doc, Active) ? "" : $"「{doc.DisplayName}」";
            SetStatus(prefix + msg, tone);
        };
        doc.CaretMoved += (line, col) =>
        {
            if (ReferenceEquals(doc, Active)) CaretText.Text = CaretLabel(line, col);
        };

        Tabs.Items.Add(item);
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    private void RefreshHeader(DocumentTab doc)
    {
        if (!_tabs.TryGetValue(doc, out var chrome)) return;

        chrome.Name.Text = doc.DisplayName;
        chrome.Dot.Visibility = doc.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        chrome.Name.ToolTip = doc.ToolTipPath;
        chrome.Item.ToolTip = doc.ToolTipPath;

        // 供 UI 自动化/读屏用：名字里带脏标记
        AutomationProperties.SetName(chrome.Item, doc.DisplayName + (doc.IsDirty ? " *" : ""));
        AutomationProperties.SetName(chrome.Close, $"关闭 {doc.DisplayName}");
    }

    private void SelectDoc(DocumentTab doc)
    {
        if (_tabs.TryGetValue(doc, out var chrome))
        {
            Tabs.SelectedItem = chrome.Item;
            doc.FocusEditor();
        }
    }

    private void CloseTab(DocumentTab doc)
    {
        DebugLog.Write($"CloseTab begin: {doc.DisplayName} dirty={doc.IsDirty} tabs={Tabs.Items.Count}");
        if (!doc.ConfirmClose())
        {
            DebugLog.Write("CloseTab cancelled by user");
            return;
        }

        if (_tabs.TryGetValue(doc, out var chrome))
        {
            var wasActive = ReferenceEquals(doc, Active);
            _tabs.Remove(doc);
            Tabs.Items.Remove(chrome.Item);

            if (Tabs.Items.Count == 0)
            {
                EmptyHint.Visibility = Visibility.Visible;
                SetStatus("所有标签已关闭。", "muted");
            }
            else if (wasActive && Tabs.SelectedItem is TabItem selected)
            {
                ((DocumentTab)selected.Content).FocusEditor();
            }
        }

        UpdateChrome();
        DebugLog.Write($"CloseTab end: tabs={Tabs.Items.Count}");
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;   // 忽略子控件冒泡上来的
        UpdateChrome();

        if (Active is { } doc)
        {
            doc.FocusEditor();
            if (doc.File is { } f)
                SetStatus($"已切到「{doc.DisplayName}」（{f.EncodingLabel} · {f.NewlineLabel}）", "muted");
        }
    }

    // ---------------------------------------------------------------- 轮询

    private void OnTick(object? sender, EventArgs e)
    {
        if (_pollBusy) return;
        _pollBusy = true;
        try
        {
            foreach (var doc in Docs.ToList()) doc.Poll(_cfg.ConfirmOnExternalChange);
        }
        finally
        {
            _pollBusy = false;
        }
    }

    // ---------------------------------------------------------------- 菜单动作

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "文本文件 (*.txt;*.log;*.md;*.ini;*.csv)|*.txt;*.log;*.md;*.ini;*.csv|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        var startDir = Active?.Path ?? _cfg.LastPath;
        if (!string.IsNullOrWhiteSpace(startDir))
        {
            try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(startDir); }
            catch (ArgumentException) { /* 路径异常就不用它 */ }
        }

        if (dlg.ShowDialog(this) != true) return;

        DocumentTab? last = null;
        foreach (var name in dlg.FileNames) last = OpenPath(name, quiet: false) ?? last;

        if (last is not null)
            SetStatus(dlg.FileNames.Length > 1
                ? $"已打开 {dlg.FileNames.Length} 个文件（各占一个标签）。"
                : Welcome(last), "muted");
    }

    private void OnSave(object sender, RoutedEventArgs e) => Active?.Save();

    private void OnSaveAs(object sender, RoutedEventArgs e) => Active?.SaveAs();

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (Active is not { } doc) return;
        doc.ReloadFromDisk();
        UpdateChrome();
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (Active is { } doc) CloseTab(doc);
    }

    private void OnFind(object sender, RoutedEventArgs e)
    {
        if (Active is not { } doc) return;
        doc.ShowFind(false);
        SetStatus("查找：输入内容后按 Enter 找下一个，Shift+Enter 找上一个，Esc 关闭。", "muted");
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (Active is not { } doc) return;
        doc.ShowFind(true);
        SetStatus("替换：填好查找与替换内容，可逐处替换或全部替换。", "muted");
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        if (Active is not { } doc) return;
        if (!doc.IsFindBarOpen) doc.ShowFind(false);
        else doc.FindNextPublic(!((Keyboard.Modifiers & ModifierKeys.Shift) != 0));
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- 关闭窗口

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 逐个标签问保存，任何一个取消就整体不关
        foreach (var doc in Docs.ToList())
        {
            if (!doc.ConfirmClose())
            {
                e.Cancel = true;
                return;
            }
        }

        _timer.Stop();

        _cfg.Maximized = WindowState == WindowState.Maximized;
        if (!_cfg.Maximized && WindowState == WindowState.Normal)
        {
            _cfg.Left = Left;
            _cfg.Top = Top;
            _cfg.Width = Width;
            _cfg.Height = Height;
        }

        _cfg.OpenPaths = Docs.Select(d => d.Path).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!).Take(30).ToList();
        _cfg.ActivePath = Active?.Path;
        _cfg.LastPath = Active?.Path ?? _cfg.LastPath;
        _cfg.WordWrap = WrapItem.IsChecked;
        _cfg.FontSize = Active is { } a ? a.TextBoxControl.FontSize : _cfg.FontSize;
        _cfg.ConfirmOnExternalChange = ConfirmItem.IsChecked;
        _cfg.Save();
    }

    // ---------------------------------------------------------------- 界面状态

    private void UpdateChrome()
    {
        var doc = Active;

        if (doc is null)
        {
            Title = "TxtLiveSync";
            MetaText.Text = "";
            CaretText.Text = "";
            return;
        }

        var (line, col) = doc.CaretPosition;
        CaretText.Text = CaretLabel(line, col);

        Title = doc.IsDirty ? $"{doc.DisplayName} * — TxtLiveSync" : $"{doc.DisplayName} — TxtLiveSync";

        if (doc.File is not { } f)
        {
            MetaText.Text = "未保存的新文件";
            return;
        }

        var index = _tabs.TryGetValue(doc, out var chrome) ? Tabs.Items.IndexOf(chrome.Item) + 1 : 0;
        MetaText.Text = string.Join("   ·   ", new[]
        {
            f.EncodingLabel,
            f.NewlineLabel,
            doc.IsDirty ? "未保存" : "已保存",
            f.SizeLabel,
            $"{index}/{Tabs.Items.Count}",
        });
    }

    private static string CaretLabel(int line, int col) => $"第 {line} 行，第 {col} 列";

    private void SetStatus(string text, string tone)
    {
        StatusText.Text = text;
        StatusText.Foreground = tone switch
        {
            "ok" => (Brush)FindResource("OkBrush"),
            "warn" => (Brush)FindResource("WarnBrush"),
            _ => (Brush)FindResource("MutedBrush"),
        };
    }

    // ---------------------------------------------------------------- 设置项

    private void OnConfirmToggle(object sender, RoutedEventArgs e)
    {
        _cfg.ConfirmOnExternalChange = ConfirmItem.IsChecked;
        _cfg.Save();
        SetStatus(_cfg.ConfirmOnExternalChange
            ? "已开启：外部修改时弹窗询问。"
            : "已关闭：外部修改时静默以磁盘内容为准。", "muted");
    }

    private void OnSingleInstanceToggle(object sender, RoutedEventArgs e)
    {
        _cfg.SingleInstance = SingleInstanceItem.IsChecked;
        _cfg.Save();

        if (!_cfg.SingleInstance)
        {
            SingleInstance.ReleaseOwnership();
            SetStatus("已关闭：以后每次双击文件都会启动一个新窗口。", "muted");
            return;
        }

        // 立刻生效：能抢到独占就从现在开始接文件
        var owned = SingleInstance.EnsureOwner(paths => (Application.Current as App)?.OpenFromOther(paths));
        SetStatus(owned
            ? "已开启：双击文件会在本窗口里打开，不再启动新窗口。"
            : "已开启：另一个窗口正在接管双击，本窗口只负责自己的标签。", "muted");
    }

    private void OnWrapChanged(object sender, RoutedEventArgs e)
    {
        foreach (var doc in Docs)
        {
            doc.ApplySettings(WrapItem.IsChecked, doc.TextBoxControl.FontSize);
        }
    }

    private void OnFontBigger(object sender, RoutedEventArgs e) => ChangeFont(1);

    private void OnFontSmaller(object sender, RoutedEventArgs e) => ChangeFont(-1);

    private void ChangeFont(int delta)
    {
        var size = Math.Clamp((Active?.TextBoxControl.FontSize ?? _cfg.FontSize) + delta, 9, 32);
        foreach (var doc in Docs) doc.ApplySettings(WrapItem.IsChecked, size);
        _cfg.FontSize = size;
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "TxtLiveSync\n\n轻量级 TXT 实时同步查看 / 编辑器（多标签）\n" +
            $".NET {Environment.Version} · WPF\n轮询间隔 {PollMs} ms\n\n" +
            "默认策略：外部修改时静默以磁盘内容为准。\n\n" +
            "Ctrl+F 查找 · Ctrl+H 替换 · Ctrl+W 关闭标签 · Ctrl+Tab 切换标签",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- 拖放与快捷键

    // 诊断：设置 TXTLS_DEBUG=1 时把拖放过程写进 %TEMP%\txtls_drag.log
    private static void DragLog(string s) => DebugLog.Write(s);

    /// <summary>
    /// 从拖放数据里取出所有真实存在的文件。
    /// 用 Preview 阶段拦截：正文铺满窗口，若只挂普通 Drop，事件会被子控件吃掉。
    /// </summary>
    private static List<string> GetDroppedFiles(IDataObject? data)
    {
        var list = new List<string>();
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return list;
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return list;

        foreach (var p in files)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { if (System.IO.File.Exists(p)) list.Add(System.IO.Path.GetFullPath(p)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.IOException)
            { /* 跳过非法路径 */ }
        }
        return list;
    }

    private void ShowDropHint(string? text, string? sub)
    {
        if (text is null)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        DropOverlayText.Text = text;
        DropOverlaySub.Text = sub ?? "拖入单个文件即可";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DragLog($"DragOver formats=[{string.Join(",", e.Data.GetFormats())}]");

        var files = GetDroppedFiles(e.Data);
        if (files.Count == 0)
        {
            e.Effects = DragDropEffects.None;
            DragLog("  -> None (no file)");
            ShowDropHint(null, null);
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DragLog("  -> Copy " + string.Join(" | ", files));

        if (files.Count == 1)
        {
            var name = System.IO.Path.GetFileName(files[0]);
            var open = _tabs.Keys.Any(d => string.Equals(d.Path, files[0], StringComparison.OrdinalIgnoreCase));
            ShowDropHint(open ? $"{name} 已在标签中，松开切换到它" : $"松开以新标签打开  {name}",
                "也可以拖到工具栏或状态栏任意位置");
        }
        else
        {
            ShowDropHint($"松开以新标签打开 {files.Count} 个文件", "每个文件各占一个标签");
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ShowDropHint(null, null);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DragLog($"Drop formats=[{string.Join(",", e.Data.GetFormats())}]");
        ShowDropHint(null, null);

        var files = GetDroppedFiles(e.Data);
        if (files.Count == 0)
        {
            SetStatus("拖入的内容不是文件（或文件已不存在），已忽略。", "warn");
            return;
        }

        var opened = 0;
        DocumentTab? last = null;
        foreach (var p in files)
        {
            var doc = OpenPath(p, quiet: false);
            if (doc is null) continue;
            opened++;
            last = doc;
        }

        if (last is null) return;

        SetStatus(opened > 1
            ? $"已用 {opened} 个标签打开拖入的文件。"
            : Welcome(last), "muted");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == Key.O) { OnOpen(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { if (shift) OnSaveAs(this, new RoutedEventArgs()); else OnSave(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { OnFind(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { OnReplace(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F3) { OnFindNext(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape && Active is { } d && d.CloseFindIfOpen()) { e.Handled = true; }
        else if (ctrl && e.Key == Key.W) { OnCloseTab(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab) { CycleTab(shift ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.F5) { OnReload(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { ChangeFont(1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { ChangeFont(-1); e.Handled = true; }

        base.OnPreviewKeyDown(e);
    }

    private void CycleTab(int delta)
    {
        var count = Tabs.Items.Count;
        if (count < 2) return;

        var index = Tabs.SelectedIndex < 0 ? 0 : Tabs.SelectedIndex;
        index = (index + delta + count) % count;
        Tabs.SelectedIndex = index;
    }
}
