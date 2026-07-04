namespace MegatonHammer.Forms;

/// <summary>
/// A vertical stack of <see cref="CollapsibleSection"/>s (the Hammer-style right dock). Collapsed
/// sections shrink to their header; expanded sections share the remaining height by weight, and the
/// gaps between adjacent expanded sections act as draggable splitters that re-balance those weights.
/// </summary>
public sealed class DockStack : Panel
{
    private const int SplitterH = 5;

    private readonly List<CollapsibleSection> _sections = [];
    private readonly List<float> _weights = [];

    // Splitter hit-zones from the last layout: (y, index of the expanded section above the gap).
    private readonly List<(int y, int above, int below)> _gaps = [];
    private int _dragAbove = -1, _dragBelow = -1, _dragStartY;
    private float _dragW0, _dragW1;

    public DockStack()
    {
        BackColor = Color.FromArgb(10, 10, 10);
        DoubleBuffered = true;
    }

    public void AddSection(CollapsibleSection section, float weight = 1f)
    {
        section.CollapseToggled += DoLayoutStack;
        _sections.Add(section);
        _weights.Add(weight);
        Controls.Add(section);
        DoLayoutStack();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DoLayoutStack();
    }

    private void DoLayoutStack()
    {
        _gaps.Clear();
        if (_sections.Count == 0) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        var expanded = new List<int>();
        for (int i = 0; i < _sections.Count; i++)
            if (!_sections[i].IsCollapsed) expanded.Add(i);

        int collapsed = _sections.Count - expanded.Count;
        int splitters = Math.Max(0, expanded.Count - 1);
        int avail = h - collapsed * CollapsibleSection.HeaderHeight - splitters * SplitterH;
        if (avail < 0) avail = 0;
        float wsum = 0f;
        foreach (int i in expanded) wsum += _weights[i];
        if (wsum <= 0f) wsum = 1f;

        int y = 0, given = 0, lastExpanded = expanded.Count > 0 ? expanded[^1] : -1;
        for (int idx = 0; idx < _sections.Count; idx++)
        {
            var s = _sections[idx];
            int sh;
            if (s.IsCollapsed)
            {
                sh = CollapsibleSection.HeaderHeight;
            }
            else if (idx == lastExpanded)
            {
                sh = Math.Max(CollapsibleSection.HeaderHeight, avail - given);   // last expanded absorbs rounding
            }
            else
            {
                sh = (int)(avail * (_weights[idx] / wsum));
                sh = Math.Max(CollapsibleSection.HeaderHeight, sh);
                given += sh;
            }
            s.SetBounds(0, y, w, sh);
            y += sh;

            // A draggable gap follows an expanded section that is not the last expanded one.
            if (!s.IsCollapsed && idx != lastExpanded)
            {
                int below = expanded[expanded.IndexOf(idx) + 1];
                _gaps.Add((y, idx, below));
                y += SplitterH;
            }
        }
    }

    // ── Splitter dragging ──────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        foreach (var (gy, above, below) in _gaps)
            if (e.Y >= gy - 2 && e.Y <= gy + SplitterH + 2)
            {
                _dragAbove = above; _dragBelow = below; _dragStartY = e.Y;
                _dragW0 = _weights[above]; _dragW1 = _weights[below];
                Capture = true;
                return;
            }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragAbove < 0)
        {
            bool onGap = false;
            foreach (var (gy, _, _) in _gaps)
                if (e.Y >= gy - 2 && e.Y <= gy + SplitterH + 2) { onGap = true; break; }
            Cursor = onGap ? Cursors.HSplit : Cursors.Default;
            return;
        }

        // Transfer weight between the two sections proportionally to the pixel drag.
        float pairW = _dragW0 + _dragW1;
        int aboveH = _sections[_dragAbove].Height, belowH = _sections[_dragBelow].Height;
        int pairPx = Math.Max(1, aboveH + belowH);
        float dw = (e.Y - _dragStartY) / (float)pairPx * pairW;
        float wAbove = Math.Clamp(_dragW0 + dw, 0.08f * pairW, 0.92f * pairW);
        _weights[_dragAbove] = wAbove;
        _weights[_dragBelow] = pairW - wAbove;
        DoLayoutStack();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragAbove = _dragBelow = -1;
        Capture = false;
    }
}
