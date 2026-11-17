using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace TxtLiveSync;

/// <summary>一处要标色的命中：起点、长度、是不是当前这一处。</summary>
public readonly struct HighlightMark
{
    public HighlightMark(int start, int length, bool current)
    {
        Start = start;
        Length = length;
        IsCurrent = current;
    }

    public int Start { get; }
    public int Length { get; }
    public bool IsCurrent { get; }
}

/// <summary>
/// 查找命中的色块：作为 TextView 的背景层画在文字下面（同 VS / VS Code），
/// 所以文字永远不会被色块盖住，普通选区也不受影响。
/// 颜色沿用之前定的：当前命中 #99C9EF，其余 #E6E6E6。
/// </summary>
internal sealed class SearchHighlightRenderer : IBackgroundRenderer
{
    private static readonly Brush MatchBrush = Freeze(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly Brush CurrentBrush = Freeze(Color.FromRgb(0x99, 0xC9, 0xEF));

    private List<HighlightMark> _marks = new();

    public int Count => _marks.Count;

    /// <summary>画在正文背景层：文字和选区都会盖在它上面。</summary>
    public KnownLayer Layer => KnownLayer.Background;

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Clear() => _marks = new List<HighlightMark>();

    public void SetMarks(List<HighlightMark> marks) => _marks = marks;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_marks.Count == 0 || textView.Document is null) return;
        if (!textView.VisualLinesValid) return;

        var length = textView.Document.TextLength;

        foreach (var mark in _marks)
        {
            if (mark.Length <= 0 || mark.Start < 0 || mark.Start >= length) continue;

            var len = Math.Min(mark.Length, length - mark.Start);
            var segment = new TextSegment { StartOffset = mark.Start, Length = len };

            var brush = mark.IsCurrent ? CurrentBrush : MatchBrush;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                drawingContext.DrawRectangle(brush, null, rect);
            }
        }
    }
}
