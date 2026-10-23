using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;
namespace FishReader;
public sealed class SettingsWindow : Window
{
    readonly ReaderWindow reader;
    readonly Border textColorSwatch = new() { Width = 26, Height = 26, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
    readonly TextBlock textColorValue = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    public void SetTextColor(Color color)
    {
        reader.State.TextColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        textColorSwatch.Background = new SolidColorBrush(color);
        textColorValue.Text = reader.State.TextColor;
        reader.ApplyAppearance();
    }
    public SettingsWindow(ReaderWindow reader)
    {
        this.reader = reader;
        Title = "阅读设置"; Width = 390; Height = 500; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(245, 242, 234)); Foreground = new SolidColorBrush(Color.FromRgb(48, 59, 56));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(26) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "让阅读融入桌面", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        var theme = new ComboBox { Margin = new Thickness(0, 6, 0, 16) };
        foreach (string item in new[] { "暖纸 · 米白", "薄荷 · 浅绿", "夜读 · 深灰" }) theme.Items.Add(item);
        theme.SelectedIndex = reader.State.BackgroundColor switch { "#EAF3ED" => 1, "#202723" => 2, _ => 0 };
        theme.SelectionChanged += (_, _) => {
            var colors = theme.SelectedIndex switch { 1 => ("#EAF3ED", "#243C33"), 2 => ("#202723", "#DEE7E0"), _ => ("#F5F2EA", "#303B38") };
            reader.State.BackgroundColor = colors.Item1; reader.State.TextColor = colors.Item2;
            SetTextColor(ReaderWindow.ParseColor(reader.State.TextColor, "#303B38"));
        };
        panel.Children.Add(new TextBlock { Text = "配色" }); panel.Children.Add(theme);
        panel.Children.Add(new TextBlock { Text = "字体颜色", Margin = new Thickness(0, 0, 0, 8) });
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 17) };
        colorRow.Children.Add(textColorSwatch);
        var chooseColor = new Button { Content = "选择颜色…", Padding = new Thickness(12, 4, 12, 4) };
        chooseColor.Click += (_, _) => {
            var selected = ReaderWindow.ParseColor(reader.State.TextColor, "#303B38");
            using var dialog = new Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(selected.R, selected.G, selected.B) };
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                SetTextColor(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        };
        colorRow.Children.Add(chooseColor); colorRow.Children.Add(textColorValue); panel.Children.Add(colorRow);
        var initialColor = ReaderWindow.ParseColor(reader.State.TextColor, "#303B38");
        textColorSwatch.Background = new SolidColorBrush(initialColor); textColorValue.Text = reader.State.TextColor;
        AddSlider(panel, "背景不透明度", 0, 100, reader.State.BackgroundOpacity, "%", v => { reader.State.BackgroundOpacity = v; reader.ApplyAppearance(); });
        AddSlider(panel, "基础字号", 12, 40, reader.State.FontSize, " px", v => { reader.State.FontSize = v; reader.ApplyAppearance(); });
        panel.Children.Add(new TextBlock { Text = "0% 为视觉透明，文字仍清晰可见。\n字号会随窗口大小自动缩放，这里设置的是基准值。\n摸鱼模式只显示正文，鼠标移出即隐藏，按住 Ctrl 双击原位置可重新唤出。", FontSize = 12, Opacity = .65, Margin = new Thickness(0, 8, 0, 16), LineHeight = 22 });
        var done = new Button { Content = "完成", Padding = new Thickness(18, 7, 18, 7), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        done.Click += (_, _) => Close(); panel.Children.Add(done);
    }
    static void AddSlider(Panel panel, string label, double min, double max, double value, string unit, Action<double> changed)
    {
        var text = new TextBlock { Text = $"{label}  {value:0}{unit}" }; panel.Children.Add(text);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 10, 0, 18) };
        slider.ValueChanged += (_, e) => { text.Text = $"{label}  {e.NewValue:0}{unit}"; changed(e.NewValue); }; panel.Children.Add(slider);
    }
}
