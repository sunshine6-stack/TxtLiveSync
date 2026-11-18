using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace TxtLiveSync;

/// <summary>
/// 同名单词的色块：和查找高亮一样画在正文背景层（<see cref="KnownLayer.Background"/>），
/// 文字永远在它上面，所以不会盖字，也不影响选区。
///
/// 颜色比选中态（#99C9EF）弱得多：双击的那个词由系统选区交代，其余同名处只淡淡铺一层，
/// 眼睛扫过去知道"还有这些地方"，又不抢正文。
/// </summary>
internal sealed class OccurrenceHighlightRenderer : IBackgroundRenderer
{
    private static readonly Brush OtherBrush = Freeze(Color.FromRgb(0xE1, 0xEE, 0xFC));

    private List<HighlightMark> _marks = new();

    /// <summary>当前要标的词（空 = 没在标）。</summary>
    public string Word { get; private set; } = "";

    public int Count => _marks.Count;

    public KnownLayer Layer => KnownLayer.Background;

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Clear()
    {
        Word = "";
        _marks = new List<HighlightMark>();
    }

    /// <summary>换词（或换区间）时更新：词为空即等于清空。</summary>
    public void Set(string? word, List<HighlightMark> marks)
    {
        Word = word ?? "";
        _marks = marks;
    }

    public void SetEmpty() => _marks = new List<HighlightMark>();

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

            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                drawingContext.DrawRectangle(OtherBrush, null, rect);
            }
        }
    }
}
