using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FishReader;

public sealed class BrowserPanel : Grid, IDisposable
{
    public WebView2CompositionControl View { get; } = new();
    readonly TextBox address = new() { MinWidth = 60, Foreground = System.Windows.Media.Brushes.Black, Background = System.Windows.Media.Brushes.White, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "输入网址后按回车" };
    readonly TextBlock status = new() { Name = "BrowserStatus", Text = "输入网址，开始浏览", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6) };
    readonly Button back, forward, refresh;
    readonly string dataDirectory;
    readonly ReaderState state;
    readonly Action save;
    readonly CheckBox mobile, transparent;
    readonly Button zoomReset;
    readonly SemaphoreSlim optionsLock = new(1, 1);
    string desktopAgent = "", pageScriptId = "";
    Task? initialization;
    bool disposed;
    ulong activeNavigation;

    public BrowserPanel(string dataDirectory, ReaderState state, Action save)
    {
        this.dataDirectory = dataDirectory; this.state = state; this.save = save;
        Margin = new Thickness(5, 0, 5, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new DockPanel { Margin = new Thickness(0, 4, 0, 8) }; Children.Add(toolbar);
        back = Button("‹", "后退", () => { if (View.CoreWebView2?.CanGoBack == true) View.CoreWebView2.GoBack(); });
        forward = Button("›", "前进", () => { if (View.CoreWebView2?.CanGoForward == true) View.CoreWebView2.GoForward(); });
        refresh = Button("↻", "刷新", () => View.CoreWebView2?.Reload());
        foreach (var button in new[] { back, forward, refresh }) { DockPanel.SetDock(button, Dock.Left); toolbar.Children.Add(button); button.IsEnabled = false; }
        var go = Button("打开", "打开网址", () => _ = NavigateFromAddressAsync());
        DockPanel.SetDock(go, Dock.Right); toolbar.Children.Add(go);
        address.Background = System.Windows.Media.Brushes.Transparent;
        address.ClearValue(Control.ForegroundProperty);
        address.Text = state.LastUrl; toolbar.Children.Add(address);
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = NavigateFromAddressAsync(); } };
        var options = new WrapPanel { Margin = new Thickness(5, 0, 0, 7) };
        mobile = new CheckBox { Content = "手机浏览", IsChecked = state.WebMobile, Margin = new Thickness(0, 0, 18, 0), ToolTip = "使用手机标识并按窗口宽度适配；切换会刷新网页" };
        transparent = new CheckBox { Content = "网页透明", IsChecked = state.WebTransparent, ToolTip = "去掉网页底色，跟随阅读器背景透明度；可关闭恢复原样" };
        options.Children.Add(mobile); options.Children.Add(transparent); Grid.SetRow(options, 1); Children.Add(options);
        options.Children.Add(Button("−", "缩小网页，完整查看验证码", () => SetZoom(state.WebZoom - .1)));
        zoomReset = Button("100%", "恢复网页大小", () => SetZoom(1)); options.Children.Add(zoomReset);
        options.Children.Add(Button("+", "放大网页", () => SetZoom(state.WebZoom + .1)));
        options.Children.Add(Button("适应弹窗", "缩小网页以完整显示登录或验证码弹窗", async () => {
            try { await FitPopupAsync(); } catch (Exception ex) { if (!disposed) status.Text = "无法适配弹窗：" + ex.Message; }
        }));
        foreach (var toggle in new[] { mobile, transparent }) { toggle.Checked += OptionsChanged; toggle.Unchecked += OptionsChanged; }
        Grid.SetRow(View, 2); Children.Add(View);
        Grid.SetRow(status, 3); Children.Add(status);
        View.DefaultBackgroundColor = state.WebTransparent ? System.Drawing.Color.Transparent : System.Drawing.Color.White;
        View.ZoomFactor = state.WebZoom; zoomReset.Content = $"{state.WebZoom:P0}";
        IsVisibleChanged += (_, _) => { if (!disposed && View.CoreWebView2 != null) View.CoreWebView2.IsMuted = !IsVisible; };
        View.ZoomFactorChanged += (_, _) => {
            state.WebZoom = Math.Clamp(View.ZoomFactor, .25, 2); zoomReset.Content = $"{state.WebZoom:P0}"; save();
        };
    }

    static Button Button(string title, string tip, Action action)
    {
        var button = new Button { Content = title, ToolTip = tip, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(1) };
        button.Click += (_, _) => action(); return button;
    }

    public static Uri ParseAddress(string text)
    {
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0)
            throw new ArgumentException("请输入有效的 http 或 https 网址，例如 https://www.baidu.com");
        return uri;
    }

    async Task InitializeAsync()
    {
        Directory.CreateDirectory(dataDirectory);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDirectory);
        if (disposed) return;
        await View.EnsureCoreWebView2Async(environment);
        if (disposed) return;
        var core = View.CoreWebView2;
        desktopAgent = core.Settings.UserAgent;
        core.Settings.IsStatusBarEnabled = false;
        await ApplyOptionsAsync();
        core.NavigationStarting += (_, e) => {
            if (e.Uri != "about:blank" && (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))) {
                e.Cancel = true; status.Text = "此链接不属于网页地址"; return;
            }
            activeNavigation = e.NavigationId;
            status.Text = "正在加载…";
        };
        core.SourceChanged += (_, _) => {
            address.Text = core.Source;
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")) { state.LastUrl = core.Source; save(); }
        };
        core.HistoryChanged += (_, _) => { back.IsEnabled = core.CanGoBack; forward.IsEnabled = core.CanGoForward; };
        core.NavigationCompleted += (_, e) => {
            if (e.NavigationId == activeNavigation) status.Text = NavigationStatus(e.IsSuccess, e.WebErrorStatus);
        };
        // ponytail: single-page browser; add tabs only if simultaneous pages are needed.
        core.NewWindowRequested += (_, e) => { e.Handled = true; address.Text = e.Uri; _ = NavigateFromAddressAsync(); };
        refresh.IsEnabled = true;
        core.IsMuted = !IsVisible;
    }

    public async Task NavigateAsync(string text)
    {
        var uri = ParseAddress(text);
        if (disposed) return;
        status.Text = "正在准备网页…";
        try { await (initialization ??= InitializeAsync()); }
        catch { initialization = null; throw; }
        if (!disposed) { address.Text = uri.AbsoluteUri; View.CoreWebView2.Navigate(uri.AbsoluteUri); }
    }

    public async Task NavigateFromAddressAsync()
    {
        try { await NavigateAsync(address.Text); }
        catch (Exception ex) { if (!disposed) status.Text = "无法打开网页：" + ex.Message; }
    }

    public void FocusAddress() { address.Focus(); address.SelectAll(); }
    public static string NavigationStatus(bool success, CoreWebView2WebErrorStatus error) => success ? "加载完成"
        : error == CoreWebView2WebErrorStatus.OperationCanceled ? "本次加载已取消" : $"加载失败：{error}，可刷新重试";

    public void SetZoom(double value)
    {
        if (!double.IsFinite(value)) return;
        state.WebZoom = Math.Clamp(value, .25, 2);
        View.ZoomFactor = state.WebZoom; zoomReset.Content = $"{state.WebZoom:P0}"; save();
    }

    // ponytail: visible dialogs/frames only; manual zoom covers shadow-DOM popups.
    public async Task FitPopupAsync()
    {
        if (disposed || View.CoreWebView2 == null) return;
        string result = await View.CoreWebView2.ExecuteScriptAsync("""
            (() => {
              let elements = [...document.querySelectorAll('[role="dialog"],[aria-modal="true"],iframe')];
              if (!elements.length) elements = [...document.querySelectorAll('body *')].filter(e => getComputedStyle(e).position === 'fixed');
              const boxes = elements.map(e => e.getBoundingClientRect()).filter(r => r.width > 80 && r.height > 80 && r.right > 0 && r.bottom > 0 && r.left < innerWidth && r.top < innerHeight);
              return { width: Math.max(1, ...boxes.map(r => r.width + 24)), height: Math.max(1, ...boxes.map(r => r.height + 24)) };
            })()
            """);
        if (disposed) return;
        using var dimensions = JsonDocument.Parse(result);
        double width = dimensions.RootElement.GetProperty("width").GetDouble(), height = dimensions.RootElement.GetProperty("height").GetDouble();
        if (width <= 1 && height <= 1) { status.Text = "未发现弹窗，可使用 − 手动缩小网页"; return; }
        SetZoom(Math.Min(state.WebZoom, Math.Min(View.ActualWidth / width, View.ActualHeight / height)));
    }
    async void OptionsChanged(object sender, RoutedEventArgs e)
    {
        bool reload = state.WebMobile != (mobile.IsChecked == true);
        state.WebMobile = mobile.IsChecked == true; state.WebTransparent = transparent.IsChecked == true; save();
        try { await ApplyOptionsAsync(reload); } catch (Exception ex) { if (!disposed) status.Text = "设置失败：" + ex.Message; }
    }

    public async Task ApplyOptionsAsync(bool reload = false)
    {
        await optionsLock.WaitAsync();
        try {
            if (disposed) return;
            View.DefaultBackgroundColor = state.WebTransparent ? System.Drawing.Color.Transparent : System.Drawing.Color.White;
            var core = View.CoreWebView2;
            if (core == null) return;
            core.Settings.UserAgent = state.WebMobile
                ? new Regex(@"\([^)]*\)").Replace(desktopAgent, "(Linux; Android 14; Pixel 7)", 1).Replace(" Safari/", " Mobile Safari/").Replace(" Edg/", " EdgA/")
                : desktopAgent;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", JsonSerializer.Serialize(new { enabled = state.WebMobile, maxTouchPoints = 1 }));
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmitTouchEventsForMouse", JsonSerializer.Serialize(new { enabled = state.WebMobile, configuration = "mobile" }));
            if (disposed) return;
            if (pageScriptId.Length > 0) core.RemoveScriptToExecuteOnDocumentCreated(pageScriptId);
            string script = PageScript(state.WebMobile, state.WebTransparent);
            pageScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (disposed) return;
            await core.ExecuteScriptAsync(script);
            if (reload && !disposed) core.Reload();
        } finally { optionsLock.Release(); }
    }

    // ponytail: CSS can clear element colors, not pixels inside images/video/canvas.
    static string PageScript(bool mobile, bool transparent) => $$"""
        (() => {
          window.__fishreaderAppearanceCleanup?.();
          const mobile = {{mobile.ToString().ToLowerInvariant()}}, transparent = {{transparent.ToString().ToLowerInvariant()}};
          let viewport, original, created = false;
          const style = document.createElement('style');
          style.textContent = 'html,body,body *{background-color:transparent!important}';
          function apply() {
            const root = document.head || document.documentElement;
            if (!root) return;
            if (transparent && !style.isConnected) root.appendChild(style);
            if (mobile && window === window.top) {
              if (!viewport?.isConnected) {
                viewport = document.querySelector('meta[name="viewport"]');
                created = !viewport; original = viewport?.getAttribute('content');
                if (!viewport) { viewport = document.createElement('meta'); viewport.name = 'viewport'; root.appendChild(viewport); }
              }
              const value = 'width=device-width,initial-scale=1';
              if (viewport.content !== value) viewport.content = value;
            }
          }
          const observer = new MutationObserver(apply);
          observer.observe(document, {childList:true,subtree:true});
          document.addEventListener('DOMContentLoaded', apply); apply();
          window.__fishreaderAppearanceCleanup = () => {
            observer.disconnect(); document.removeEventListener('DOMContentLoaded', apply); style.remove();
            if (viewport) {
              if (created) viewport.remove();
              else if (original == null) viewport.removeAttribute('content');
              else viewport.setAttribute('content', original);
            }
          };
        })();
        """;

    public void Dispose() { disposed = true; View.Dispose(); }
}
