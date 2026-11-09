using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TxtLiveSync;

/// <summary>一处高亮：矩形 + 是否为「当前命中」。</summary>
public readonly struct HighlightMark
{
    public readonly Rect Rect;
    public readonly bool Current;

    public HighlightMark(Rect rect, bool current)
    {
        Rect = rect;
        Current = current;
    }
}

/// <summary>
/// 查找高亮层：垫在编辑区下面、在文字底下铺色块（跟 Visual Studio 一样），不接收鼠标事件。
/// 不用 TextBox 的选区来做高亮，是因为选区颜色只在"编辑区有焦点"时醒目，
/// 焦点在查找框里时用的是系统淡色，白底上几乎看不出来——看起来就像"有时高亮了有时没有"。
/// 配色照 VS：当前命中 #99C9EF，其余命中 #E6E6E6，不透明，与焦点在哪无关。
/// </summary>
public sealed class MatchHighlightOverlay : FrameworkElement
{
    private readonly List<HighlightMark> _marks = new();
    private Rect _clip = Rect.Empty;

    // 不透明填充（和 VS 一致）。因为这一层在文字下面，色块不会糊字，也不会两处重叠叠深。
    private static readonly Brush MatchFill = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly Brush CurrentFill = Frozen(Color.FromRgb(0x99, 0xC9, 0xEF));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>只在正文可视区内画，别糊到滚动条上。</summary>
    public void SetClip(Rect clip)
    {
        if (clip == _clip) return;
        _clip = clip;
        InvalidateVisual();
    }

    public void SetMarks(List<HighlightMark> marks)
    {
        _marks.Clear();
        if (marks.Count > 0) _marks.AddRange(marks);
        InvalidateVisual();
    }

    public void Clear()
    {
        if (_marks.Count == 0) return;
        _marks.Clear();
        InvalidateVisual();
    }

    public int MarkCount => _marks.Count;

    protected override void OnRender(DrawingContext dc)
    {
        if (_marks.Count == 0) return;

        var clipped = false;
        if (!_clip.IsEmpty && _clip.Width > 0 && _clip.Height > 0)
        {
            dc.PushClip(new RectangleGeometry(_clip));
            clipped = true;
        }

        foreach (var m in _marks)
        {
            var r = m.Rect;
            if (r.Width <= 0 || r.Height <= 0) continue;
            dc.DrawRectangle(m.Current ? CurrentFill : MatchFill, null, r);
        }

        if (clipped) dc.Pop();
    }
}
