using System;
using System.IO;
using System.Text;
using FishReader;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    static int failures;
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    static void Equal<T>(T want, T actual)
    {
        if (!Equals(want, actual)) throw new Exception($"Expected {want}, got {actual}");
    }
    [STAThread]
    static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string dir = Path.Combine(Path.GetTempPath(), "FishReaderTests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Check("UTF8 BOM", () => Equal("你好", TextFiles.Decode([0xef, 0xbb, 0xbf, 0xe4, 0xbd, 0xa0, 0xe5, 0xa5, 0xbd])));
        Check("GB18030 Chinese", () => Equal("中文", TextFiles.Decode([0xd6, 0xd0, 0xce, 0xc4])));
        Check("UTF16 little endian", () => Equal("你好", TextFiles.Decode([0xff, 0xfe, 0x60, 0x4f, 0x7d, 0x59])));
        Check("UTF16 big endian", () => Equal("你好", TextFiles.Decode([0xfe, 0xff, 0x4f, 0x60, 0x59, 0x7d])));
        Check("Empty text", () => Equal("", TextFiles.Decode([])));
        Check("Clamp shortened book", () => { Equal(19, Reading.ClampOffset(100, 20)); Equal(0, Reading.ClampOffset(-1, 20)); Equal(0, Reading.ClampOffset(9, 0)); Equal(12, Reading.ClampOffset(12, 20)); });
        Check("Per-book progress survives restart", () => {
            var store = new StateStore(Path.Combine(dir, "state.json"));
            var state = new ReaderState { LastFile = "B.txt" };
            state.Positions["A.txt"] = 1234; state.Positions["B.txt"] = 5678;
            store.Save(state);
            var read = new StateStore(Path.Combine(dir, "state.json")).Load();
            Equal(1234, read.Positions["a.txt"]); Equal(5678, read.Positions["B.txt"]); Equal("B.txt", read.LastFile);
            read.Positions["A.txt"] = 9; store.Save(read); Equal(9, store.Load().Positions["A.txt"]);
        });
        Check("Corrupt state preserved", () => {
            string path = Path.Combine(dir, "broken.json"); File.WriteAllText(path, "{broken");
            Equal(0, new StateStore(path).Load().Positions.Count);
            Equal(true, Directory.GetFiles(dir, "broken.json.corrupt-*").Length == 1);
        });
        Check("Hover switch requires Ctrl double click reveal", () => {
            Equal(false, HoverPolicy.ShouldShow(true, false, false, false));
            Equal(false, HoverPolicy.ShouldShow(true, true, false, false));
            Equal(true, HoverPolicy.ShouldShow(false, false, false, false));
            Equal(true, HoverPolicy.ShouldShow(true, true, false, true));
            Equal(true, HoverPolicy.ShouldShow(true, false, true, true));
            Equal(false, HoverPolicy.ShouldShow(true, false, false, true));
        });
        Check("High DPI work area keeps resize grip reachable", () => {
            var fitted = WindowPlacement.Fit(new Rect(120, 120, 580, 680), new Rect(0, 0, 960, 500));
            Equal(500.0, fitted.Height); Equal(0.0, fitted.Top); Equal(580.0, fitted.Width);
            var disconnected = WindowPlacement.Fit(new Rect(2200, -900, 580, 680), new Rect(-960, 0, 960, 700));
            Equal(-580.0, disconnected.Left); Equal(0.0, disconnected.Top);
        });
        Check("Canceled navigation remains distinct from a network failure", () => {
            Equal(false, BrowserPanel.NavigationStatus(false, Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.OperationCanceled).Contains("加载失败"));
            Equal(true, BrowserPanel.NavigationStatus(false, Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.CannotConnect).Contains("加载失败"));
        });
        Check("Chapter headings preserve offsets and preface", () => {
            var chapters = Chapters.Parse("前言\r\n第一章 开始\r\n正文\r\n第2章 继续\r\n结束");
            Equal(3, chapters.Count); Equal("前言", chapters[0].Title);
            Equal(4, chapters[1].Start); Equal(12, chapters[1].Length);
            Equal("第2章 继续", chapters[2].Title); Equal(16, chapters[2].Start);
            Equal(1, Chapters.At(chapters, 15)); Equal(2, Chapters.At(chapters, 16));
            Equal(0, Chapters.At(chapters, -1)); Equal(2, Chapters.At(chapters, 1000));
        });
        Check("Chapter formats and plain text fallback", () => {
            Equal(3, Chapters.Parse("第001章 起点\n甲\n第二章：出发\n乙\nChapter 3 Arrival\n丙").Count);
            Equal(1, Chapters.Parse("这是正文。\n他在第一章里讲了一个故事。\n第3行仍然是正文。").Count);
            Equal("全文", Chapters.Parse("")[0].Title);
        });
        Check("Resize from all edges and preserve opposite corner at minimum", () => {
            var start = new Rect(100, 100, 580, 680);
            Equal(new Rect(80, 70, 600, 710), WindowResize.Apply(start, ResizeEdge.Left | ResizeEdge.Top, -20, -30, 300, 260));
            Equal(new Rect(100, 100, 630, 720), WindowResize.Apply(start, ResizeEdge.Right | ResizeEdge.Bottom, 50, 40, 300, 260));
            Equal(new Rect(380, 520, 300, 260), WindowResize.Apply(start, ResizeEdge.Left | ResizeEdge.Top, 1000, 1000, 300, 260));
        });
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string book = Path.Combine(dir, "测试小说.txt");
        File.WriteAllText(book, string.Join("\n", Enumerable.Range(1, 1200).Select(i => $"第{i}行：窗外微风拂过树梢，书页里的故事仍在继续。")));
        Check("Real reader opens Chinese TXT and restores progress", () => {
            var store = new StateStore(Path.Combine(dir, "ui.json"));
            var w = new ReaderWindow(store); w.Show(); Pump();
            try {
                w.OpenBook(book); Pump(); Equal(true, w.Editor.Text.Contains("第1200行"));
                w.RestorePosition(10000); Pump(); int offset = w.CapturePosition();
                Equal(true, offset > 9900 && offset <= 10000);
                w.Close(); Pump();
                var next = new ReaderWindow(store); next.Show(); Pump();
                try { next.OpenBook(book); Pump(); Equal(offset, next.CapturePosition()); }
                finally { next.Close(); }
            } finally { if (w.IsVisible) w.Close(); }
        });
        Check("Native window hides on leave and only reveals on demand", () => {
            GetCursorPos(out var previous);
            var w = new ReaderWindow(new StateStore(Path.Combine(dir, "hover.json")));
            w.Show(); Pump();
            try {
                w.State.Topmost = true; w.State.BackgroundOpacity = 0; w.State.Stealth = true; w.ApplyAppearance();
                Equal(true, w.Topmost);
                Equal((byte)1, ((SolidColorBrush)((Border)w.Content).Background).Color.A);
                Equal((byte)255, ((SolidColorBrush)w.Editor.Foreground).Color.A);
                var center = w.PointToScreen(new System.Windows.Point(100, 100));
                if (!SetCursorPos(0, 0)) throw new Exception("Desktop permission required: SetCursorPos failed");
                w.PollPointer(); Equal(false, w.IsVisible);
                if (!SetCursorPos((int)center.X, (int)center.Y)) throw new Exception("Desktop permission required: SetCursorPos failed");
                w.PollPointer(); Equal(false, w.IsVisible);   // 悬停不再唤出
                w.Reveal(); Pump(); Equal(true, w.IsVisible);
                w.PollPointer(); Equal(true, w.IsVisible);    // 唤出后鼠标在窗口内保持显示
                w.State.Stealth = false; SetCursorPos(0, 0); w.PollPointer(); Equal(true, w.IsVisible);
                w.State.Topmost = false; w.ApplyAppearance(); Equal(false, w.Topmost);
            } finally { w.Close(); SetCursorPos(previous.X, previous.Y); }
        });
        Check("Switch books, failed open, font resize and empty file", () => {
            string second = Path.Combine(dir, "第二本.txt"), empty = Path.Combine(dir, "空.txt");
            File.Copy(book, second); File.WriteAllText(empty, "");
            var w = new ReaderWindow(new StateStore(Path.Combine(dir, "switch.json"))); w.Show(); Pump();
            try {
                w.OpenBook(book); w.RestorePosition(10000); Pump(); int first = w.CapturePosition();
                w.OpenBook(second); w.RestorePosition(20000); Pump(); int secondOffset = w.CapturePosition();
                w.OpenBook(book); Pump(); Equal(first, w.CapturePosition());
                w.OpenBook(second); Pump(); Equal(secondOffset, w.CapturePosition());
                try { w.OpenBook(Path.Combine(dir, "missing.txt")); throw new Exception("Missing file accepted"); } catch (FileNotFoundException) { }
                Equal(secondOffset, w.CapturePosition());
                w.State.FontSize = 30; w.ApplyAppearance(); Pump();
                Equal(true, Math.Abs(w.CapturePosition() - secondOffset) < 50);
                w.OpenBook(empty); Pump(); Equal(0, w.CapturePosition()); Equal("", w.Editor.Text);
            } finally { w.Close(); }
        });
        Check("Chapter selection shows only that chapter and survives restart", () => {
            string path = Path.Combine(dir, "分章小说.txt");
            File.WriteAllText(path, "简介\n第一章 开始\n" + string.Concat(Enumerable.Repeat("甲章正文独有，窗外有一片树林。\n", 80)) + "第二章 继续\n" + string.Concat(Enumerable.Repeat("乙章正文独有，河岸边停着一艘小船。\n", 100)) + "第三章 尾声\n完结。");
            var store = new StateStore(Path.Combine(dir, "chapters.json"));
            int legacyOffset = TextFiles.Read(path).IndexOf("第二章 继续", StringComparison.Ordinal);
            var legacy = new ReaderState { LastFile = path }; legacy.Positions[path] = legacyOffset; store.Save(legacy);
            var w = new ReaderWindow(store); w.Show(); Pump();
            try {
                Equal(2, w.CurrentChapterIndex); Equal(legacyOffset, w.CapturePosition());
                w.OpenBook(path); Pump(); Equal(4, w.BookChapters.Count); w.SelectChapter(0);
                var picker = Descendants(w).OfType<ComboBox>().Single(c => c.Name == "ChapterPicker");
                var toggle = Descendants(picker).OfType<ToggleButton>().Single();
                toggle.IsChecked = true; Pump();
                Equal(true, picker.IsDropDownOpen);
                Equal(true, ((Popup)picker.Template.FindName("PART_Popup", picker)).IsOpen);
                picker.SelectedIndex = 2; picker.IsDropDownOpen = false;
                Pump(); Equal(2, w.CurrentChapterIndex);
                Equal(true, w.Editor.Text.StartsWith("第二章 继续")); Equal(false, w.Editor.Text.Contains("甲章正文独有"));
                w.RestorePosition(w.BookChapters[2].Start + 500); Pump();
                int anchor = w.CapturePosition(); w.SaveProgress(); w.Close();
                var next = new ReaderWindow(store); next.Show(); Pump();
                try {
                    Equal(2, next.CurrentChapterIndex); Equal(anchor, next.CapturePosition());
                    next.SelectChapter(3); Pump(); Equal(3, next.CurrentChapterIndex); Equal(true, next.Editor.Text.Contains("完结。"));
                    next.SelectChapter(99); Equal(3, next.CurrentChapterIndex);
                } finally { next.Close(); }
            } finally { if (w.IsVisible) w.Close(); }
        });
        Check("Eight resize handles change actual window and preserve text position", () => {
            var w = new ReaderWindow(new StateStore(Path.Combine(dir, "resize.json"))); w.Show(); Pump();
            try {
                w.OpenBook(book); w.RestorePosition(10000); Pump(); int anchor = w.CapturePosition();
                var grips = Descendants(w).OfType<Thumb>().Where(t => t.Name.StartsWith("ResizeGrip")).ToList();
                Equal(8, grips.Count);
                var corner = grips.Single(g => g.Name == "ResizeGripTopLeft");
                double right = w.Left + w.Width, bottom = w.Top + w.Height;
                corner.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                corner.RaiseEvent(new DragDeltaEventArgs(-30, -20) { RoutedEvent = Thumb.DragDeltaEvent });
                corner.RaiseEvent(new DragCompletedEventArgs(-30, -20, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Pump(); Equal(right, w.Left + w.Width); Equal(bottom, w.Top + w.Height);
                Equal(true, Math.Abs(anchor - w.CapturePosition()) < 50);
                Equal(w.Width, w.State.Width);
            } finally { w.Close(); }
        });
        Check("Custom text color persists and chapter bar follows opacity", () => {
            var store = new StateStore(Path.Combine(dir, "colors.json"));
            var w = new ReaderWindow(store); w.Show(); Pump();
            try {
                var settings = new SettingsWindow(w) { Owner = w }; settings.Show(); Pump();
                try { settings.SetTextColor(Color.FromRgb(18, 52, 86)); }
                finally { settings.Close(); }
                Equal("#123456", w.State.TextColor);
                Equal(Color.FromRgb(18, 52, 86), ((SolidColorBrush)w.Editor.Foreground).Color);
                w.State.BackgroundOpacity = 0; w.ApplyAppearance(); Pump();
                var picker = Descendants(w).OfType<ComboBox>().Single(c => c.Name == "ChapterPicker");
                var sample = picker.TranslatePoint(new System.Windows.Point(picker.ActualWidth * .75, picker.ActualHeight / 2), w);
                var bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(w);
                byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)sample.X, (int)sample.Y, 1, 1), pixel, 4, 0);
                Equal(true, pixel[3] is > 0 and <= 5);
                w.State.BackgroundOpacity = 100; w.ApplyAppearance(); Pump();
                bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(w);
                bitmap.CopyPixels(new Int32Rect((int)sample.X, (int)sample.Y, 1, 1), pixel, 4, 0);
                Equal((byte)255, pixel[3]);
                w.SaveProgress(); Equal("#123456", store.Load().TextColor);
            } finally { w.Close(); }
        });
        Check("Page control and wheel continue to next chapter only after the last page", () => {
            string path = Path.Combine(dir, "短章节.txt"); File.WriteAllText(path, "第一章 起点\n短文\n第二章 继续\n结尾");
            var w = new ReaderWindow(new StateStore(Path.Combine(dir, "auto-next.json"))); w.Show(); Pump();
            try {
                w.OpenBook(path); Pump(); Equal(0, w.CurrentChapterIndex);
                var next = Descendants(w).OfType<Button>().Single(b => b.ToolTip?.ToString()?.StartsWith("下一页") == true);
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Equal(1, w.CurrentChapterIndex);
                string longBook = Path.Combine(dir, "长章节.txt");
                File.WriteAllText(longBook, "第一章 长章\n" + string.Concat(Enumerable.Repeat("这一章还有许多内容，暂时不能跳到下一章。\n", 80)) + "第二章 接续\n继续阅读。");
                w.OpenBook(longBook); Pump(); Equal(0, w.CurrentChapterIndex);
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Equal(0, w.CurrentChapterIndex);
                w.Editor.ScrollToEnd(); Pump();
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Equal(1, w.CurrentChapterIndex);
                w.OpenBook(path); w.SelectChapter(0); Pump();
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                w.Editor.RaiseEvent(wheel); Pump(); Equal(1, w.CurrentChapterIndex);
                next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); Equal(1, w.CurrentChapterIndex);
            } finally { w.Close(); }
        });
        Check("Render reader and settings", () => {
            var w = new ReaderWindow(new StateStore(Path.Combine(dir, "render.json"))); w.Show(); Pump();
            try {
                string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
                Directory.CreateDirectory(output);
                Render(w, Path.Combine(output, "reader.png"));
                string sample = Path.Combine(dir, "海边的信.txt");
                File.WriteAllText(sample, "第一章 起点\n故事从这里开始。\n第二章 海边的信\n\n" + string.Concat(Enumerable.Repeat("海风穿过窗沿，将信纸轻轻掀起。她在桌边坐下，读到一个久违的名字。\n\n", 30)) + "第三章 归途\n故事仍在继续。");
                w.OpenBook(sample); w.SelectChapter(1); Pump(); Render(w, Path.Combine(output, "chapters.png"));
                var pageLabel = Descendants(w).OfType<TextBlock>().Single(t => t.Name == "ChapterPage");
                string before = pageLabel.Text;
                w.Width = 300; w.Height = 340; w.UpdateLayout(); Pump();
                Equal(false, before == pageLabel.Text);
                Render(w, Path.Combine(output, "reader-small.png"));
                w.Width = 580; w.Height = 680; w.UpdateLayout(); Pump();
                var settings = new SettingsWindow(w) { Owner = w }; settings.Show(); Pump();
                try { Render(settings, Path.Combine(output, "settings.png")); } finally { settings.Close(); }
            } finally { w.Close(); }
        });
        Check("Embedded browser navigates, preserves TXT, and supports hover hiding", () => {
            Equal("https://example.com/", BrowserPanel.ParseAddress("example.com").AbsoluteUri);
            foreach (var bad in new[] { "", "javascript:alert(1)", "file:///C:/secret.txt", "https://user:pass@example.com" }) {
                try { BrowserPanel.ParseAddress(bad); throw new Exception("Unsafe address accepted: " + bad); } catch (ArgumentException) { }
            }
            using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            string origin = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
            using var cancel = new CancellationTokenSource();
            var serving = Task.Run(async () => {
                try { while (!cancel.IsCancellationRequested) {
                    using var client = await server.AcceptTcpClientAsync(cancel.Token);
                    using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                    string first = await reader.ReadLineAsync(cancel.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancel.Token))) { }
                    string html = "<meta name='viewport' content='width=980'><style>html,body,.card{background-color:white}body{margin:12px}.card{padding:12px}</style>" + (first.Contains("/two") ? "<title>第二页</title><div class='card'><h1>网页浏览成功</h1></div>" : "<title>测试首页</title><div class='card'><h1>轻阅内置网页测试</h1><a id='next' href='/two' target='_blank'>下一页</a></div>");
                    byte[] body = Encoding.UTF8.GetBytes(html);
                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, cancel.Token); await stream.WriteAsync(body, cancel.Token);
                } } catch (OperationCanceledException) { }
            });
            var store = new StateStore(Path.Combine(dir, "web.json"));
            var w = new ReaderWindow(store); w.Show(); Pump(); GetCursorPos(out var cursor);
            try {
                w.OpenBook(book); w.RestorePosition(10000); Pump(); int anchor = w.CapturePosition();
                w.SetWebMode(true); Pump();
                var panel = w.Browser!; var start = panel.NavigateAsync(origin); WaitFor(() => start.IsCompleted); start.GetAwaiter().GetResult();
                var core = panel.View.CoreWebView2;
                WaitFor(() => core.DocumentTitle == "测试首页");
                string Eval(string script) { var task = core.ExecuteScriptAsync(script); WaitFor(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
                Equal("true", Eval("navigator.userAgent.includes('Mobile')"));
                Equal("true", Eval("getComputedStyle(document.body).backgroundColor === 'rgba(0, 0, 0, 0)'"));
                Equal("true", Eval("getComputedStyle(document.querySelector('.card')).backgroundColor === 'rgba(0, 0, 0, 0)'"));
                Equal("true", Eval("document.querySelector('meta[name=viewport]').content.includes('device-width')"));
                w.Width = 380; w.UpdateLayout(); Pump();
                WaitFor(() => Math.Abs(double.Parse(Eval("innerWidth"), System.Globalization.CultureInfo.InvariantCulture) - panel.View.ActualWidth) <= 1);
                var transparency = Descendants(panel).OfType<CheckBox>().Single(c => Equals(c.Content, "网页透明"));
                transparency.IsChecked = false;
                WaitFor(() => Eval("getComputedStyle(document.body).backgroundColor === 'rgb(255, 255, 255)'") == "true");
                Equal((byte)255, panel.View.DefaultBackgroundColor.A); Equal(false, store.Load().WebTransparent);
                transparency.IsChecked = true;
                WaitFor(() => Eval("getComputedStyle(document.body).backgroundColor === 'rgba(0, 0, 0, 0)'") == "true");
                var mobileToggle = Descendants(panel).OfType<CheckBox>().Single(c => Equals(c.Content, "手机浏览"));
                mobileToggle.IsChecked = false;
                WaitFor(() => Eval("!navigator.userAgent.includes('Mobile') && document.querySelector('meta[name=viewport]')?.content === 'width=980'") == "true");
                Equal(false, store.Load().WebMobile);
                mobileToggle.IsChecked = true;
                WaitFor(() => Eval("navigator.userAgent.includes('Mobile') && document.querySelector('meta[name=viewport]')?.content.includes('device-width')") == "true");
                w.Width = 580; w.UpdateLayout(); Pump();
                WaitFor(() => Math.Abs(double.Parse(Eval("innerWidth"), System.Globalization.CultureInfo.InvariantCulture) - panel.View.ActualWidth) <= 1);
                Equal(true, store.Load().WebMobile); Equal(true, store.Load().WebTransparent);
                var click = core.ExecuteScriptAsync("document.getElementById('next').click()"); WaitFor(() => click.IsCompleted); click.GetAwaiter().GetResult();
                WaitFor(() => core.DocumentTitle == "第二页"); Equal(origin + "/two", store.Load().LastUrl);
                Descendants(panel).OfType<Button>().Single(b => Equals(b.ToolTip, "后退")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WaitFor(() => core.DocumentTitle == "测试首页");
                Descendants(panel).OfType<Button>().Single(b => Equals(b.ToolTip, "前进")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WaitFor(() => core.DocumentTitle == "第二页");
                string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/browser-page.png"));
                using (var preview = File.Create(output)) {
                    var capture = core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, preview);
                    WaitFor(() => capture.IsCompleted); capture.GetAwaiter().GetResult();
                }
                var decoder = new PngBitmapDecoder(new Uri(output), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bgra = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                byte[] background = new byte[4]; bgra.CopyPixels(new Int32Rect(1, 1, 1, 1), background, 4, 0); Equal((byte)0, background[3]);
                Render(w, Path.Combine(Path.GetDirectoryName(output)!, "browser-mobile.png"));
                var replaced = panel.NavigateAsync(origin); WaitFor(() => replaced.IsCompleted); replaced.GetAwaiter().GetResult();
                var resume = panel.NavigateAsync(origin + "/two"); WaitFor(() => resume.IsCompleted); resume.GetAwaiter().GetResult();
                var browserStatus = Descendants(panel).OfType<TextBlock>().Single(t => t.Name == "BrowserStatus");
                WaitFor(() => browserStatus.Text == "加载完成");
                w.Width = 380; w.Height = 420; w.UpdateLayout(); Pump();
                WaitFor(() => Math.Abs(double.Parse(Eval("innerWidth"), System.Globalization.CultureInfo.InvariantCulture) - panel.View.ActualWidth) <= 1);
                Eval("""
                    document.documentElement.style.overflow = 'hidden'; document.body.style.overflow = 'hidden';
                    const dialog = document.createElement('div'); dialog.id = 'test-dialog'; dialog.setAttribute('role', 'dialog');
                    dialog.style.cssText = 'position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);width:320px;height:520px;border:2px solid black';
                    dialog.innerHTML = '<h2>验证码显示测试</h2><iframe style="width:280px;height:100px" srcdoc="<meta name=viewport content=width=320>"></iframe><button style="position:absolute;bottom:4px;left:4px">验证码底部</button>';
                    document.body.appendChild(dialog);
                    """);
                Equal("true", Eval("document.querySelector('#test-dialog').getBoundingClientRect().bottom > innerHeight"));
                WaitFor(() => Eval("document.querySelector('#test-dialog iframe').contentDocument?.querySelector('meta[name=viewport]')?.content === 'width=320'") == "true");
                var fit = panel.FitPopupAsync(); WaitFor(() => fit.IsCompleted); fit.GetAwaiter().GetResult();
                WaitFor(() => Eval("(() => { const r=document.querySelector('#test-dialog').getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight&&r.left>=0&&r.right<=innerWidth })()") == "true");
                Equal(true, store.Load().WebZoom < 1);
                using (var preview = File.Create(Path.Combine(Path.GetDirectoryName(output)!, "browser-popup-content.png"))) {
                    var capture = core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, preview);
                    WaitFor(() => capture.IsCompleted); capture.GetAwaiter().GetResult();
                }
                var popupFrame = new PngBitmapDecoder(new Uri(Path.Combine(Path.GetDirectoryName(output)!, "browser-popup-content.png")), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var dpi = VisualTreeHelper.GetDpi(panel.View);
                Equal(true, Math.Abs(popupFrame.PixelWidth - panel.View.ActualWidth * dpi.DpiScaleX) <= 2);
                Equal(true, Math.Abs(popupFrame.PixelHeight - panel.View.ActualHeight * dpi.DpiScaleY) <= 2);
                var composed = Task.Delay(200); WaitFor(() => composed.IsCompleted);
                Render(w, Path.Combine(Path.GetDirectoryName(output)!, "browser-popup-fit.png"));
                Descendants(panel).OfType<Button>().Single(b => Equals(b.ToolTip, "恢复网页大小")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Equal(1.0, panel.View.ZoomFactor);
                Eval("document.querySelector('#test-dialog').remove();document.documentElement.style.overflow='';document.body.style.overflow=''");
                w.Width = 580; w.Height = 680; w.UpdateLayout(); Pump();
                Eval("""
                    const frame = document.createElement('iframe'); frame.id='touch-test';
                    frame.style.cssText='position:fixed;left:8px;top:8px;width:340px;height:120px;z-index:2147483647';
                    frame.srcdoc=`<style>body{margin:0}#track{position:relative;width:320px;height:60px;background:#ddd}#thumb{position:absolute;left:0;width:50px;height:60px;background:#468}</style><div id=track><button id=thumb>拖动</button></div><script>
                      window.dragValue=0;let start=null;const thumb=document.querySelector('#thumb');
                      thumb.addEventListener('touchstart',e=>{start=e.touches[0].clientX;e.preventDefault()},{passive:false});
                      window.addEventListener('touchmove',e=>{if(start!==null){window.dragValue=Math.max(0,Math.min(270,e.touches[0].clientX-start));thumb.style.left=window.dragValue+'px';e.preventDefault()}},{passive:false});
                      window.addEventListener('touchend',()=>start=null);
                    <\/script>`;
                    document.body.appendChild(frame);
                    """);
                WaitFor(() => Eval("document.querySelector('#touch-test').contentWindow.dragValue === 0") == "true");
                panel.SetZoom(.5);
                WaitFor(() => Math.Abs(double.Parse(Eval("innerWidth"), System.Globalization.CultureInfo.InvariantCulture) - panel.View.ActualWidth / .5) <= 2);
                void DragMouse()
                {
                    IntPtr previousFocus = GetForegroundWindow();
                    try {
                        w.Activate(); panel.View.Focus(); Pump();
                        var ready = Task.Delay(150); WaitFor(() => ready.IsCompleted);
                        var startPoint = panel.View.PointToScreen(new System.Windows.Point(35 * .5, 40 * .5));
                        SetCursorPos((int)startPoint.X, (int)startPoint.Y); Pump();
                        w.State.Stealth = true; mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                        for (int step = 1; step <= 20; step++) {
                            var point = panel.View.PointToScreen(new System.Windows.Point((35 + step * 10) * .5, 40 * .5));
                            SetCursorPos((int)point.X, (int)point.Y); Pump(); Thread.Sleep(15);
                        }
                        mouse_event(4, 0, 0, 0, UIntPtr.Zero); Pump(); Equal(true, w.IsVisible);
                    } finally { w.State.Stealth = false; mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetForegroundWindow(previousFocus); }
                }
                DragMouse(); Equal("true", Eval("document.querySelector('#touch-test').contentWindow.dragValue > 150"));
                Eval("document.querySelector('#touch-test').remove()"); panel.SetZoom(1);
                bool desktopReady = false;
                EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs> desktopLoaded = (_, e) => desktopReady = e.IsSuccess;
                core.NavigationCompleted += desktopLoaded;
                mobileToggle.IsChecked = false;
                WaitFor(() => desktopReady); core.NavigationCompleted -= desktopLoaded;
                WaitFor(() => Eval("!navigator.userAgent.includes('Mobile') && document.readyState === 'complete'") == "true");
                Eval("""
                    window.mouseDragValue=0;const handle=document.createElement('button');handle.textContent='鼠标拖动';
                    handle.style.cssText='position:fixed;left:20px;top:20px;width:50px;height:50px;z-index:2147483647';document.body.appendChild(handle);
                    let origin=null;handle.onmousedown=e=>{origin=e.clientX;e.preventDefault()};
                    document.onmousemove=e=>{if(origin!==null){window.mouseDragValue=e.clientX-origin;handle.style.left=(20+window.mouseDragValue)+'px'}};
                    document.onmouseup=()=>origin=null;
                    """);
                panel.SetZoom(.5);
                WaitFor(() => Math.Abs(double.Parse(Eval("innerWidth"), System.Globalization.CultureInfo.InvariantCulture) - panel.View.ActualWidth / .5) <= 2);
                DragMouse(); Equal("true", Eval("window.mouseDragValue > 150")); panel.SetZoom(1);
                w.State.Stealth = true;
                var center = w.PointToScreen(new System.Windows.Point(100, 150));
                if (!SetCursorPos(0, 0)) throw new Exception("Desktop mouse access required");
                w.PollPointer(); Equal(false, w.IsVisible);
                SetCursorPos((int)center.X, (int)center.Y); w.PollPointer(); Equal(false, w.IsVisible);
                w.Reveal(); Pump(); Equal(true, w.IsVisible);
                w.State.Stealth = false;
                w.SetWebMode(false); Pump(); Equal(anchor, w.CapturePosition());
                w.SaveProgress(); Equal(anchor, store.Load().Positions[book]);
            } finally { w.Close(); SetCursorPos(cursor.X, cursor.Y); cancel.Cancel(); server.Stop(); }
        });
        app.Shutdown();
        Console.WriteLine($"Failures: {failures}; fixtures: {dir}");
        return failures == 0 ? 0 : 1;
    }
    static void Render(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    static void WaitFor(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Browser check timed out"); Pump(); Thread.Sleep(10); }
        Pump();
    }
    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}
