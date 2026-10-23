using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FishReader;

public sealed class ReaderWindow : Window
{
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    const int GwlExStyle = -20, WsExToolWindow = 0x00000080, WsExAppWindow = 0x00040000, SwHide = 0, SwShowNoActivate = 4;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [StructLayout(LayoutKind.Sequential)] struct NativeMouseHook { public int X, Y, MouseData, Flags, Time; public IntPtr ExtraInfo; }
    delegate IntPtr MouseHookProc(int code, IntPtr message, IntPtr data);
    const int WhMouseLl = 14, WmLeftButtonDown = 0x0201, VkControl = 0x11, SmCxDoubleClick = 36, SmCyDoubleClick = 37;
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, MouseHookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    public ReaderState State { get; }
    public TextBox Editor { get; } = new();
    public IReadOnlyList<Chapter> BookChapters { get; private set; } = [new Chapter("全文", 0, 0)];
    public int CurrentChapterIndex { get; private set; }
    public void NextPage()
    {
        if (currentFile.Length > 0 && CurrentChapterIndex + 1 < BookChapters.Count && IsAtChapterEnd())
            SelectChapter(CurrentChapterIndex + 1);
        else Editor.PageDown();
    }
    bool IsAtChapterEnd() => Editor.VerticalOffset >= Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight) - 1;
    public void SelectChapter(int index)
    {
        if (restoring || currentFile.Length == 0 || index < 0 || index >= BookChapters.Count) return;
        SaveProgress(); RestorePosition(BookChapters[index].Start); SaveProgress();
    }
    readonly StateStore store;
    readonly Border surface = new();
    readonly Grid grid = new();
    readonly DockPanel header = new() { Background = Brushes.Transparent };
    readonly WrapPanel toolbar = new() { MinHeight = 42, Margin = new Thickness(4, 0, 0, 0) };
    readonly DockPanel chaptersBar = new() { Margin = new Thickness(10, 2, 8, 2) };
    readonly DockPanel footer = new() { Margin = new Thickness(12, 0, 0, 0) };
    readonly DispatcherTimer fitTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly StackPanel paging = new() { Orientation = Orientation.Horizontal };
    Button webModeButton = null!;
    public BrowserPanel? Browser { get; private set; }
    public bool WebMode { get; private set; }
    int textPosition;
    readonly TextBlock bookTitle = new(), progress = new(), saveStatus = new();
    readonly CheckBox pin = new() { Content = "置顶" }, stealth = new() { Content = "摸鱼" };
    readonly ComboBox chapterPicker = new() { Name = "ChapterPicker", DisplayMemberPath = "Title", IsTextSearchEnabled = true, MaxDropDownHeight = 340 };
    readonly TextBlock chapterPage = new() { Name = "ChapterPage", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    Button previousChapter = null!, nextChapter = null!;
    readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly DispatcherTimer periodicTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly Forms.NotifyIcon tray;
    string currentFile = "";
    string bookText = "";
    int displayedChapter = -1;
    int resizeAnchor;
    bool restoring, initialized, closed, revealed = true;
    readonly MouseHookProc shortcutHookProc;
    IntPtr shortcutHook;
    NativePoint lastClick;
    uint lastClickTime;
    int suspended;

    public ReaderWindow(StateStore store)
    {
        this.store = store; State = store.Load(); shortcutHookProc = MouseShortcut;
        Title = "轻阅 · TXT 阅读器";
        Width = State.Width; Height = State.Height; MinWidth = 300; MinHeight = 260;
        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        Left = State.Left; Top = State.Top;
        SourceInitialized += (_, _) => { FitToWorkArea(); ApplyStealthLayout(); };
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Resources.Add(typeof(Button), ButtonStyle());
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/FishReader;component/ChapterPickerStyle.xaml", UriKind.Relative) });
        surface.CornerRadius = new CornerRadius(12); surface.BorderThickness = new Thickness(1);
        surface.Padding = new Thickness(10, 0, 10, 0); Content = surface;
        surface.Child = grid;
        foreach (var h in new[] { new GridLength(50), GridLength.Auto, new GridLength(38), new GridLength(1, GridUnitType.Star), new GridLength(38) })
            grid.RowDefinitions.Add(new RowDefinition { Height = h });
        grid.Children.Add(header);
        header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) { suspended++; try { DragMove(); } finally { suspended--; } } };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(controls, Dock.Right); header.Children.Add(controls);
        controls.Children.Add(MakeButton("—", () => { SaveProgress(); WindowState = WindowState.Minimized; }, "最小化"));
        controls.Children.Add(MakeButton("×", Close, "保存并退出"));
        header.Children.Add(new TextBlock { Text = "轻阅", FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) });
        bookTitle.Text = "留一点时间，给故事。"; bookTitle.VerticalAlignment = VerticalAlignment.Center;
        bookTitle.TextTrimming = TextTrimming.CharacterEllipsis; bookTitle.Opacity = .65; header.Children.Add(bookTitle);
        Grid.SetRow(toolbar, 1); grid.Children.Add(toolbar);
        toolbar.Children.Add(MakeButton("打开 TXT", ChooseBook, "Ctrl+O 打开文件，也可拖入 TXT"));
        webModeButton = MakeButton("网页", () => SetWebMode(!WebMode), "切换 TXT / 网页"); toolbar.Children.Add(webModeButton);
        foreach (var toggle in new[] { pin, stealth })
        { toggle.VerticalAlignment = VerticalAlignment.Center; toggle.Margin = new Thickness(12, 0, 4, 0); toolbar.Children.Add(toggle); }
        pin.ToolTip = "始终显示在其他窗口上方"; stealth.ToolTip = "移出隐藏；按住 Ctrl 双击原窗口位置唤出；托盘可恢复";
        toolbar.Children.Add(MakeButton("设置", OpenSettings, "背景、字体颜色、透明度和字号"));
        pin.IsChecked = State.Topmost; stealth.IsChecked = State.Stealth;
        pin.Click += (_, _) => { State.Topmost = pin.IsChecked == true; ApplyAppearance(); SaveProgress(); };
        stealth.Click += (_, _) => { State.Stealth = stealth.IsChecked == true; ApplyStealthLayout(); UpdateLayout(); SaveProgress(); PollPointer(); };
        Grid.SetRow(chaptersBar, 2); grid.Children.Add(chaptersBar);
        previousChapter = MakeButton("‹", () => SelectChapter(CurrentChapterIndex - 1), "上一章 · Ctrl+Left");
        nextChapter = MakeButton("›", () => SelectChapter(CurrentChapterIndex + 1), "下一章 · Ctrl+Right");
        var chapterLabel = new TextBlock { Text = "章节", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Opacity = .65 };
        DockPanel.SetDock(chapterLabel, Dock.Left); chaptersBar.Children.Add(chapterLabel);
        DockPanel.SetDock(previousChapter, Dock.Left); chaptersBar.Children.Add(previousChapter);
        DockPanel.SetDock(nextChapter, Dock.Right); chaptersBar.Children.Add(nextChapter);
        chapterPicker.ToolTip = "选择章节 · 当前章节正文单独显示";
        chapterPicker.Style = (Style)Resources["ChapterPickerStyle"];
        chapterPicker.VerticalAlignment = VerticalAlignment.Center;
        chapterPicker.ItemsSource = new[] { new Chapter("打开 TXT 后选择章节", 0, 0) }; chapterPicker.SelectedIndex = 0;
        chapterPicker.SelectionChanged += (_, _) => { if (!restoring) SelectChapter(chapterPicker.SelectedIndex); };
        chapterPicker.DropDownOpened += (_, _) => suspended++;
        chapterPicker.DropDownClosed += (_, _) => suspended = Math.Max(0, suspended - 1);
        chapterPicker.IsEnabled = false; previousChapter.IsEnabled = false; nextChapter.IsEnabled = false;
        chaptersBar.Children.Add(chapterPicker);
        Editor.IsReadOnly = true; Editor.AcceptsReturn = true; Editor.TextWrapping = TextWrapping.Wrap;
        Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Editor.Background = Brushes.Transparent; Editor.BorderThickness = new Thickness(0); Editor.Padding = new Thickness(16, 18, 14, 22);
        Editor.CaretBrush = Brushes.Transparent; Editor.SelectionBrush = new SolidColorBrush(Color.FromArgb(80, 93, 155, 133));
        Editor.FontFamily = new FontFamily("Microsoft YaHei");
        Editor.Text = "把故事带到桌面上\r\n\r\n点击「打开 TXT」，或将文件拖入窗口。\r\n\r\n· 选择章节，单独阅读当前章\r\n· 自动记住章节和阅读位置\r\n· 透明背景，移出隐藏，按住 Ctrl 双击唤出\r\n\r\n拖动顶栏移动窗口，拖动任意边缘或四角调整大小。\r\n找不到窗口时，可右键托盘图标恢复。";
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => {
            if (!restoring && initialized) { UpdateProgress(); saveTimer.Stop(); saveTimer.Start(); }
        }));
        Editor.PreviewMouseWheel += (_, e) => {
            if (e.Delta < 0 && currentFile.Length > 0 && CurrentChapterIndex + 1 < BookChapters.Count && IsAtChapterEnd()) {
                e.Handled = true; SelectChapter(CurrentChapterIndex + 1);
            }
        };
        Grid.SetRow(Editor, 3); grid.Children.Add(Editor);
        Grid.SetRow(footer, 4); grid.Children.Add(footer);
        var grip = new Thumb { Width = 18, Height = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeNWSE, VerticalAlignment = VerticalAlignment.Center, ToolTip = "拖动调整大小" };
        var factory = new FrameworkElementFactory(typeof(TextBlock)); factory.SetValue(TextBlock.TextProperty, "◢"); factory.SetValue(TextBlock.OpacityProperty, .35);
        grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
        WireResize(grip, ResizeEdge.Right | ResizeEdge.Bottom);
        DockPanel.SetDock(grip, Dock.Right); footer.Children.Add(grip);
        paging.Children.Add(MakeButton("↑", () => Editor.PageUp(), "上一页 · Page Up"));
        paging.Children.Add(MakeButton("↓", NextPage, "下一页，章末进入下一章 · Page Down / 空格"));
        DockPanel.SetDock(paging, Dock.Right); footer.Children.Add(paging);
        progress.VerticalAlignment = VerticalAlignment.Center; progress.FontSize = 12; DockPanel.SetDock(progress, Dock.Left); footer.Children.Add(progress);
        chapterPage.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(chapterPage, Dock.Left); footer.Children.Add(chapterPage);
        saveStatus.VerticalAlignment = VerticalAlignment.Center; saveStatus.Margin = new Thickness(14, 0, 0, 0); saveStatus.FontSize = 11; saveStatus.Opacity = .65;
        saveStatus.TextTrimming = TextTrimming.CharacterEllipsis; footer.Children.Add(saveStatus);
        AddResizeHandles(grid);
        AllowDrop = true;
        PreviewDragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) TryOpen(files[0]); };
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { ChooseBook(); e.Handled = true; }
            else if (WebMode) {
                if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { Browser?.FocusAddress(); e.Handled = true; }
                else if (e.Key == Key.F5) { Browser?.View.CoreWebView2?.Reload(); e.Handled = true; }
            }
            else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control) { SelectChapter(CurrentChapterIndex - 1); e.Handled = true; }
            else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control) { SelectChapter(CurrentChapterIndex + 1); e.Handled = true; }
            else if (!chapterPicker.IsDropDownOpen && !chapterPicker.IsKeyboardFocusWithin && e.Key == Key.PageDown && Keyboard.Modifiers == ModifierKeys.None) { NextPage(); e.Handled = true; }
            else if (!chapterPicker.IsDropDownOpen && !chapterPicker.IsKeyboardFocusWithin && e.Key == Key.Space) { if (Keyboard.Modifiers == ModifierKeys.Shift) Editor.PageUp(); else NextPage(); e.Handled = true; }
        };
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "轻阅 · 右键恢复窗口", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示阅读器（关闭摸鱼）", null, (_, _) => Dispatcher.Invoke(Recover));
        menu.Items.Add("打开 TXT", null, (_, _) => Dispatcher.Invoke(() => { Recover(); ChooseBook(); }));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { Recover(); OpenSettings(); }));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Recover);
        hoverTimer.Tick += (_, _) => PollPointer();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveProgress(); };
        periodicTimer.Tick += (_, _) => SaveProgress();
        fitTimer.Tick += (_, _) => { fitTimer.Stop(); RefitText(); };
        SizeChanged += (_, _) => { if (!initialized) return; fitTimer.Stop(); fitTimer.Start(); };
        Loaded += (_, _) => {
            if (initialized) return; initialized = true;
            ApplyStealthLayout(); ApplyFontSize();
            if (!string.IsNullOrEmpty(State.LastFile)) TryOpen(State.LastFile);
            hoverTimer.Start(); periodicTimer.Start(); UpdateProgress();
        };
        Closing += (_, _) => SaveProgress(true);
        Closed += (_, _) => { closed = true; SetShortcutHook(false); hoverTimer.Stop(); fitTimer.Stop(); saveTimer.Stop(); periodicTimer.Stop(); Browser?.Dispose(); tray.Dispose(); };
        ApplyAppearance();
    }
    static Style ButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        return style;
    }
    static Button MakeButton(string title, Action action, string hint)
    { var button = new Button { Content = title, ToolTip = hint }; button.Click += (_, _) => action(); return button; }
    public void OpenBook(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择 .txt 文本文件。");
        string text = TextFiles.Read(path);
        var parsed = Chapters.Parse(text);
        SaveProgress(); SetWebMode(false); saveTimer.Stop(); currentFile = path; State.LastFile = path;
        restoring = true; bookText = text; BookChapters = parsed; displayedChapter = -1;
        chapterPicker.ItemsSource = BookChapters; chapterPicker.IsEnabled = true;
        bookTitle.Text = Path.GetFileNameWithoutExtension(path); bookTitle.ToolTip = path; Title = bookTitle.Text + " · 轻阅";
        RestorePosition(State.Positions.TryGetValue(path, out int position) ? position : 0); UpdateProgress();
    }
    void TryOpen(string path)
    {
        suspended++;
        try { OpenBook(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { MessageBox.Show(this, ex.Message, "无法打开文件", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { suspended--; }
    }
    void ChooseBook()
    {
        suspended++;
        try { var dialog = new OpenFileDialog { Title = "选择一本 TXT", Filter = "TXT 文本文件|*.txt", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) TryOpen(dialog.FileName); }
        finally { suspended--; }
    }
    void OpenSettings()
    { suspended++; try { new SettingsWindow(this) { Owner = this }.ShowDialog(); SaveProgress(); } finally { suspended--; } }
    public int CapturePosition()
    {
        if (WebMode) return textPosition;
        if (currentFile.Length == 0) return 0;
        int start = BookChapters[CurrentChapterIndex].Start;
        if (Editor.Text.Length == 0) return start;
        int line = Editor.GetFirstVisibleLineIndex(); return start + (line < 0 ? 0 : Math.Max(0, Editor.GetCharacterIndexFromLineIndex(line)));
    }
    public void RestorePosition(int position)
    {
        if (WebMode) { textPosition = position; return; }
        restoring = true;
        try {
            int offset = Reading.ClampOffset(position, bookText.Length);
            if (currentFile.Length > 0) {
                CurrentChapterIndex = Chapters.At(BookChapters, offset);
                var chapter = BookChapters[CurrentChapterIndex];
                if (displayedChapter != CurrentChapterIndex) {
                    Editor.Text = bookText.Substring(chapter.Start, chapter.Length);
                    displayedChapter = CurrentChapterIndex;
                }
                chapterPicker.SelectedIndex = CurrentChapterIndex;
                previousChapter.IsEnabled = CurrentChapterIndex > 0;
                nextChapter.IsEnabled = CurrentChapterIndex + 1 < BookChapters.Count;
                offset -= chapter.Start;
            }
            Editor.UpdateLayout(); int line = Editor.GetLineIndexFromCharacterIndex(Math.Max(0, offset));
            Editor.ScrollToLine(Math.Max(0, line)); Editor.UpdateLayout();
        }
        finally { restoring = false; }
        UpdateProgress();
    }
    public void ApplyAppearance()
    {
        int anchor = CapturePosition(); Topmost = State.Topmost; pin.IsChecked = State.Topmost; stealth.IsChecked = State.Stealth;
        var bg = ParseColor(State.BackgroundColor, "#F5F2EA");
        // Alpha 1 keeps the visually transparent surface mouse-interactive on Windows.
        bg.A = (byte)Math.Max(1, Math.Round(255 * State.BackgroundOpacity / 100));
        surface.Background = new SolidColorBrush(bg);
        surface.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(28 * State.BackgroundOpacity / 100), 80, 110, 100));
        Foreground = new SolidColorBrush(ParseColor(State.TextColor, "#303B38"));
        chapterPicker.Background = Brushes.Transparent;
        chapterPicker.BorderBrush = surface.BorderBrush;
        chapterPicker.Foreground = Foreground;
        chapterPicker.ApplyTemplate();
        if (chapterPicker.Template.FindName("PART_Popup", chapterPicker) is Popup popup && popup.Child is Border listFrame) {
            var listColor = bg; listColor.A = 255;
            listFrame.Background = new SolidColorBrush(listColor);
        }
        Editor.Foreground = Foreground;
        ApplyStealthLayout(); ApplyFontSize();
        if (initialized) RestorePosition(anchor);
    }
    // 摸鱼模式只保留正文：标题栏、工具栏、章节栏、状态栏、滚动条与边框全部不显示。
    void ApplyStealthLayout()
    {
        bool stealthOn = State.Stealth;
        var chrome = stealthOn ? Visibility.Collapsed : Visibility.Visible;
        header.Visibility = toolbar.Visibility = footer.Visibility = chrome;
        chaptersBar.Visibility = stealthOn || WebMode ? Visibility.Collapsed : Visibility.Visible;
        paging.Visibility = chapterPage.Visibility = stealthOn || WebMode ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = WebMode ? Visibility.Collapsed : Visibility.Visible;
        grid.RowDefinitions[0].Height = stealthOn ? new GridLength(0) : new GridLength(50);
        grid.RowDefinitions[1].Height = stealthOn ? new GridLength(0) : GridLength.Auto;
        grid.RowDefinitions[2].Height = stealthOn ? new GridLength(0) : new GridLength(38);
        grid.RowDefinitions[4].Height = stealthOn ? new GridLength(0) : new GridLength(38);
        surface.BorderThickness = new Thickness(stealthOn ? 0 : 1);
        surface.Padding = stealthOn ? new Thickness(0) : new Thickness(10, 0, 10, 0);
        surface.CornerRadius = new CornerRadius(stealthOn ? 0 : 12);
        Editor.VerticalScrollBarVisibility = stealthOn ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        Editor.Padding = stealthOn ? new Thickness(10, 12, 8, 14) : new Thickness(16, 18, 14, 22);
        ApplyTaskbarMode(true);
        SetShortcutHook(stealthOn);
    }
    // 摸鱼时不响应悬停，只监听全局 Ctrl+双击；钩子只观察不拦截，不影响任何正常点击。
    void SetShortcutHook(bool enabled)
    {
        if (enabled && shortcutHook == IntPtr.Zero)
            shortcutHook = SetWindowsHookEx(WhMouseLl, shortcutHookProc, GetModuleHandle(null), 0);
        else if (!enabled && shortcutHook != IntPtr.Zero) { UnhookWindowsHookEx(shortcutHook); shortcutHook = IntPtr.Zero; }
    }
    IntPtr MouseShortcut(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && message == WmLeftButtonDown && State.Stealth && !closed && !IsVisible)
            {
                var info = Marshal.PtrToStructure<NativeMouseHook>(data);
                bool ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
                uint now = (uint)Environment.TickCount;
                bool repeated = ctrl && now - lastClickTime <= GetDoubleClickTime()
                    && Math.Abs(info.X - lastClick.X) <= Math.Max(2, GetSystemMetrics(SmCxDoubleClick) / 2)
                    && Math.Abs(info.Y - lastClick.Y) <= Math.Max(2, GetSystemMetrics(SmCyDoubleClick) / 2);
                if (repeated)
                {
                    lastClickTime = 0;
                    // 只有双击落在窗口原来的范围内才唤出，避免在别处 Ctrl+双击时窗口突然出现。
                    if (GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds)
                        && info.X >= bounds.Left - 4 && info.X < bounds.Right + 4 && info.Y >= bounds.Top - 4 && info.Y < bounds.Bottom + 4)
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Reveal));
                }
                else if (ctrl) { lastClickTime = now; lastClick.X = info.X; lastClick.Y = info.Y; }
                else lastClickTime = 0;
            }
        }
        catch { /* 钩子内绝不抛异常，避免影响系统输入 */ }
        return CallNextHookEx(shortcutHook, code, message, data);
    }
    // Ctrl+双击唤出：鼠标在窗口范围内时显示，之后鼠标移出再次隐藏。
    public void Reveal()
    {
        if (closed) return;
        revealed = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); ApplyTaskbarMode();
    }
    // 摸鱼模式下不出现任务栏按钮：使用工具窗口样式，鼠标移入显示时也不会露图标。
    void ApplyTaskbarMode(bool refresh = false)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GwlExStyle);
        int wanted = State.Stealth ? (style | WsExToolWindow) & ~WsExAppWindow : (style | WsExAppWindow) & ~WsExToolWindow;
        if (wanted == style) return;
        SetWindowLong(hwnd, GwlExStyle, wanted);
        if (refresh && IsVisible) { ShowWindow(hwnd, SwHide); ShowWindow(hwnd, SwShowNoActivate); }
    }
    void ApplyFontSize() => Editor.FontSize = FitFontSize();
    // 字号跟随窗口尺寸缩放，保证换行字数与可视行数大致稳定。
    double FitFontSize()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(width) || width <= 0) width = 580;
        if (!double.IsFinite(height) || height <= 0) height = 680;
        double scale = Math.Clamp(Math.Min(width / 560, height / 660), .7, 2.4);
        return Math.Round(Math.Clamp(State.FontSize * scale, 9, 96), 1);
    }
    void RefitText()
    {
        if (WebMode || currentFile.Length == 0) { ApplyFontSize(); return; }
        int anchor = CapturePosition();
        ApplyFontSize();
        RestorePosition(anchor);
    }
    internal static Color ParseColor(string value, string fallback)
    { try { var color = (Color)ColorConverter.ConvertFromString(value); color.A = 255; return color; } catch { return (Color)ColorConverter.ConvertFromString(fallback); } }
    void UpdateProgress()
    {
        if (WebMode) { progress.Text = "网页浏览"; return; }
        if (currentFile.Length == 0) { progress.Text = "TXT · 本地阅读"; return; }
        double percent = bookText.Length == 0 ? 100 : 100.0 * CapturePosition() / bookText.Length;
        if (CurrentChapterIndex == BookChapters.Count - 1 && Editor.VerticalOffset >= Editor.ExtentHeight - Editor.ViewportHeight - 1) percent = 100;
        progress.Text = $"全书 {percent:0.0}%";
        double viewport = Math.Max(1, Editor.ViewportHeight);
        int pages = Math.Max(1, (int)Math.Ceiling(Editor.ExtentHeight / viewport));
        int page = Math.Clamp((int)Math.Ceiling((Editor.VerticalOffset + viewport) / viewport), 1, pages);
        chapterPage.Text = $"本章 {page}/{pages} 页";
        chapterPage.ToolTip = $"第 {CurrentChapterIndex + 1}/{BookChapters.Count} 章 · 页码按当前窗口排版估算";
    }
    public void SaveProgress(bool showError = false)
    {
        if (!initialized || restoring || closed) return;
        if (currentFile.Length > 0) State.Positions[currentFile] = CapturePosition();
        if (WindowState == WindowState.Normal) { State.Left = Left; State.Top = Top; State.Width = Width; State.Height = Height; }
        try { store.Save(State); saveStatus.Text = "已自动保存"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { saveStatus.Text = "保存失败"; saveStatus.ToolTip = ex.Message; if (showError) MessageBox.Show(this, "阅读进度未能保存：\n" + ex.Message, "轻阅", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public void PollPointer()
    {
        if (closed || !initialized || WindowState == WindowState.Minimized) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (!GetCursorPos(out var p) || !GetWindowRect(hwnd, out var bounds)) return;
        bool inside = p.X >= bounds.Left && p.X < bounds.Right && p.Y >= bounds.Top && p.Y < bounds.Bottom;
        bool visible = HoverPolicy.ShouldShow(State.Stealth, inside, suspended > 0 || IsMouseCaptureWithin || Mouse.Captured != null, revealed);
        // 摸鱼模式不再依靠悬停唤出：鼠标移出后隐藏，只有 Ctrl+双击（或托盘）才能重新显示。
        if (!visible && IsVisible) { SaveProgress(); revealed = false; Hide(); }
    }
    void FitToWorkArea()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        var screen = Forms.Screen.FromHandle(hwnd).WorkingArea;
        var transform = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var workArea = new Rect(transform.Transform(new Point(screen.Left, screen.Top)), transform.Transform(new Point(screen.Right, screen.Bottom)));
        var fit = WindowPlacement.Fit(new Rect(Left, Top, Width, Height), workArea);
        MinWidth = Math.Min(300, workArea.Width); MinHeight = Math.Min(260, workArea.Height);
        Width = fit.Width; Height = fit.Height; Left = fit.Left; Top = fit.Top;
    }
    void Recover()
    { State.Stealth = false; stealth.IsChecked = false; revealed = true; WindowState = WindowState.Normal; Show(); ApplyStealthLayout(); FitToWorkArea(); Activate(); SaveProgress(); }
    public void SetWebMode(bool enabled)
    {
        if (enabled == WebMode) return;
        if (enabled) { textPosition = CapturePosition(); SaveProgress(); }
        WebMode = enabled;
        if (enabled && Browser == null) {
            Browser = new BrowserPanel(Path.Combine(store.DataDirectory, "WebView2"), State, () => SaveProgress());
            Grid.SetRow(Browser, 2); Grid.SetRowSpan(Browser, 2); grid.Children.Add(Browser);
            if (!string.IsNullOrWhiteSpace(State.LastUrl)) _ = Browser.NavigateFromAddressAsync();
        }
        if (Browser != null) Browser.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyStealthLayout();
        webModeButton.Content = enabled ? "返回 TXT" : "网页";
        bookTitle.Text = enabled ? "网页浏览" : (currentFile.Length > 0 ? Path.GetFileNameWithoutExtension(currentFile) : "留一点时间，给故事。");
        Title = enabled ? "轻阅 · 网页浏览" : (currentFile.Length > 0 ? Path.GetFileNameWithoutExtension(currentFile) + " · 轻阅" : "轻阅 · TXT 阅读器");
        if (!enabled) { UpdateLayout(); RestorePosition(textPosition); }
        UpdateProgress();
    }
    void WireResize(Thumb thumb, ResizeEdge edge)
    {
        thumb.DragStarted += (_, _) => { suspended++; resizeAnchor = CapturePosition(); };
        thumb.DragDelta += (_, e) => {
            var bounds = WindowResize.Apply(new Rect(Left, Top, ActualWidth, ActualHeight), edge, e.HorizontalChange, e.VerticalChange, MinWidth, MinHeight);
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            UpdateLayout(); RestorePosition(resizeAnchor);
        };
        thumb.DragCompleted += (_, _) => { suspended = Math.Max(0, suspended - 1); SaveProgress(); };
    }
    void AddResizeHandles(Grid grid)
    {
        var edges = new[] {
            ("Left", ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE),
            ("Right", ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE),
            ("Top", ResizeEdge.Top, HorizontalAlignment.Stretch, VerticalAlignment.Top, Cursors.SizeNS),
            ("Bottom", ResizeEdge.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS),
            ("TopLeft", ResizeEdge.Top | ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE),
            ("TopRight", ResizeEdge.Top | ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW),
            ("BottomLeft", ResizeEdge.Bottom | ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW),
            ("BottomRight", ResizeEdge.Bottom | ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE)
        };
        foreach (var (name, edge, horizontal, vertical, cursor) in edges) {
            var thumb = new Thumb { Name = "ResizeGrip" + name, HorizontalAlignment = horizontal, VerticalAlignment = vertical, Cursor = cursor, ToolTip = "拖动调整窗口大小" };
            bool corner = horizontal != HorizontalAlignment.Stretch && vertical != VerticalAlignment.Stretch;
            if (horizontal != HorizontalAlignment.Stretch) thumb.Width = corner ? 12 : 7;
            if (vertical != VerticalAlignment.Stretch) thumb.Height = corner ? 12 : 7;
            var visual = new FrameworkElementFactory(typeof(Border));
            visual.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
            thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = visual };
            // Extend through the surface padding so the outermost edges are draggable.
            thumb.Margin = new Thickness(horizontal == HorizontalAlignment.Left ? -10 : 0, 0, horizontal == HorizontalAlignment.Right ? -10 : 0, 0);
            Grid.SetRowSpan(thumb, 5); Panel.SetZIndex(thumb, corner ? 20 : 10);
            WireResize(thumb, edge); grid.Children.Add(thumb);
        }
    }
}
