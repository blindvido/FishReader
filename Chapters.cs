using System;
using System.Collections.Generic;
using System.Windows;
using System.Text.RegularExpressions;

namespace FishReader;
public sealed record Chapter(string Title, int Start, int Length);
public static class Chapters
{
    static readonly Regex Heading = new(@"^[ \t\u3000]*(?:第[0-9０-９零〇一二三四五六七八九十百千万两壹贰叁肆伍陆柒捌玖拾佰仟]+[章回节卷部篇][^\r\n]{0,70}|Chapter\s+(?:[0-9]+|[IVXLCDM]+)\b[^\r\n]{0,70}|(?:序章|序言|前言|楔子|引子|尾声|终章|后记|番外)(?:[ \t\u3000:：、].{0,60})?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(3));
    public static IReadOnlyList<Chapter> Parse(string text)
    {
        var headings = new List<(string Title, int Start)>();
        foreach (Match match in Heading.Matches(text)) headings.Add((match.Value.Trim(), match.Index));
        if (headings.Count == 0) return [new Chapter("全文", 0, text.Length)];
        if (headings[0].Start > 0)
        {
            if (!string.IsNullOrWhiteSpace(text[..headings[0].Start])) headings.Insert(0, ("前言", 0));
            else headings[0] = (headings[0].Title, 0);
        }
        var result = new List<Chapter>();
        for (int i = 0; i < headings.Count; i++)
            result.Add(new Chapter(headings[i].Title, headings[i].Start, (i + 1 < headings.Count ? headings[i + 1].Start : text.Length) - headings[i].Start));
        return result;
    }
    public static int At(IReadOnlyList<Chapter> chapters, int offset)
    {
        int lo = 0, hi = chapters.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (chapters[mid].Start <= offset) lo = mid; else hi = mid - 1; }
        return lo;
    }
}
[Flags]
public enum ResizeEdge { Left = 1, Top = 2, Right = 4, Bottom = 8 }
public static class WindowResize
{
    public static Rect Apply(Rect bounds, ResizeEdge edge, double dx, double dy, double minWidth, double minHeight)
    {
        double left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom;
        if (edge.HasFlag(ResizeEdge.Left)) left = Math.Min(left + dx, right - minWidth);
        if (edge.HasFlag(ResizeEdge.Top)) top = Math.Min(top + dy, bottom - minHeight);
        if (edge.HasFlag(ResizeEdge.Right)) right = Math.Max(right + dx, left + minWidth);
        if (edge.HasFlag(ResizeEdge.Bottom)) bottom = Math.Max(bottom + dy, top + minHeight);
        return new Rect(left, top, right - left, bottom - top);
    }
}
