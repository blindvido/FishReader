using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FishReader;
public sealed class ReaderState
{
    public string LastFile { get; set; } = "";
    public string LastUrl { get; set; } = "";
    public bool WebMobile { get; set; } = true;
    public bool WebTransparent { get; set; } = true;
    public double WebZoom { get; set; } = 1;
    public Dictionary<string, int> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Left { get; set; } = 120;
    public double Top { get; set; } = 120;
    public double Width { get; set; } = 580;
    public double Height { get; set; } = 680;
    public double FontSize { get; set; } = 20;
    public double BackgroundOpacity { get; set; } = 96;
    public string BackgroundColor { get; set; } = "#F5F2EA";
    public string TextColor { get; set; } = "#303B38";
    public bool Topmost { get; set; } = true;
    public bool Stealth { get; set; }
}
public sealed class StateStore(string path)
{
    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(path))!;
    public ReaderState Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<ReaderState>(File.ReadAllText(path)) ?? throw new JsonException();
            state.Positions = new Dictionary<string, int>(state.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
            state.FontSize = Math.Clamp(state.FontSize, 12, 40);
            state.BackgroundOpacity = Math.Clamp(state.BackgroundOpacity, 0, 100);
            state.WebZoom = double.IsFinite(state.WebZoom) ? Math.Clamp(state.WebZoom, .25, 2) : 1;
            state.Width = Math.Clamp(state.Width, 300, 10000);
            state.Height = Math.Clamp(state.Height, 260, 10000);
            return state;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true);
            return new();
        }
    }
    public void Save(ReaderState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public static class TextFiles
{
    public static string Decode(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return new UTF32Encoding(true, true).GetString(bytes, 4, bytes.Length - 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
    }
    public static string Read(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("文件超过 32MB，请拆分后阅读。");
        return Decode(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    }
}
public static class Reading
{
    public static int ClampOffset(int offset, int length) => Math.Clamp(offset, 0, Math.Max(0, length - 1));
}
public static class HoverPolicy
{
    // 摸鱼模式不再响应悬停：只有被 Ctrl+双击唤出（revealed）后，鼠标在窗口内或正在操作时保持显示。
    public static bool ShouldShow(bool enabled, bool inside, bool suspended, bool revealed) => !enabled || (revealed && (inside || suspended));
}
public static class WindowPlacement
{
    public static System.Windows.Rect Fit(System.Windows.Rect window, System.Windows.Rect workArea)
    {
        double width = Math.Min(window.Width, workArea.Width), height = Math.Min(window.Height, workArea.Height);
        return new System.Windows.Rect(Math.Clamp(window.Left, workArea.Left, workArea.Right - width),
            Math.Clamp(window.Top, workArea.Top, workArea.Bottom - height), width, height);
    }
}
