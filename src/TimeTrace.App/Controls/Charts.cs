using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TimeTrace.App.Services;

namespace TimeTrace.App.Controls;

public sealed record BarItem(string Label, double Value, double Value2, string Tooltip, bool ShowLabel = true);
public sealed record SliceItem(string Label, double Value, string ColorHex);
public sealed record TimelineBlock(long SessionId, DateTime StartUtc, DateTime EndUtc, string Name, string ColorHex, bool IsIdle, string? Title, int? AppId);
public sealed record TimelineRow(string Label, DateTime DayStartUtc, IReadOnlyList<TimelineBlock> Blocks, bool IsToday);

/// <summary>Shared helpers for custom-drawn, theme-aware charts.</summary>
public abstract class ChartBase : FrameworkElement
{
    protected ChartBase()
    {
        Loaded += (_, _) => { if (AppServices.Instance != null) AppServices.Instance.Theme.Changed += OnTheme; };
        Unloaded += (_, _) => { if (AppServices.Instance != null) AppServices.Instance.Theme.Changed -= OnTheme; };
        SnapsToDevicePixels = true;
    }

    private void OnTheme() => Dispatcher.BeginInvoke(InvalidateVisual);

    protected static Brush B(string key) => ThemeService.Brush(key);

    protected static Brush Hex(string hex)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch (Exception) { return Brushes.Gray; }
    }

    protected FormattedText Text(string s, double size, Brush brush, bool bold = false, double maxWidth = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    protected void SetTip(string? text)
    {
        if (!Equals(ToolTip, text)) ToolTip = text;
        ToolTipService.SetInitialShowDelay(this, 150);
    }

    protected static string Axis(double seconds, double max) =>
        max >= 3600 * 2 ? $"{seconds / 3600:0.#}h" : max >= 600 ? $"{seconds / 60:0}m" : $"{seconds:0}s";
}

// ------------------------------------------------------------------------------------------------ bar chart
public sealed class BarChart : ChartBase
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<BarItem>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondaryColorProperty = DependencyProperty.Register(nameof(SecondaryColor), typeof(string), typeof(BarChart),
        new FrameworkPropertyMetadata("IdleBrush", FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<BarItem>? Items { get => (IReadOnlyList<BarItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public string SecondaryColor { get => (string)GetValue(SecondaryColorProperty); set => SetValue(SecondaryColorProperty, value); }

    private int _hover = -1;
    private const double Left = 40, Bottom = 22, Top = 8;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var items = Items;
        if (items == null || items.Count == 0 || items.All(i => i.Value + i.Value2 <= 0))
        {
            var t = Text("No tracked activity in this period", 12, B("MutedBrush"));
            dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            return;
        }
        double max = items.Max(i => i.Value + i.Value2);
        double step = NiceStep(max);
        max = Math.Ceiling(max / step) * step;
        double plotH = h - Bottom - Top, plotW = w - Left - 6;
        var grid = new Pen(B("LineBrush"), 1);
        for (double v = 0; v <= max + 0.001; v += step)
        {
            double y = Top + plotH - v / max * plotH;
            dc.DrawLine(grid, new Point(Left, y), new Point(w - 6, y));
            var lt = Text(Axis(v, max), 10, B("FaintBrush"));
            dc.DrawText(lt, new Point(Left - 6 - lt.Width, y - lt.Height / 2));
        }
        double slot = plotW / items.Count, bw = Math.Max(2, Math.Min(36, slot * 0.68));
        int labelEvery = Math.Max(1, (int)Math.Ceiling(34 / slot));
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            double x = Left + i * slot + (slot - bw) / 2;
            double h1 = it.Value / max * plotH, h2 = it.Value2 / max * plotH;
            var accent = i == _hover ? B("TextBrush") : B("AccentBrush");
            if (h1 > 0) dc.DrawRoundedRectangle(accent, null, new Rect(x, Top + plotH - h1 - h2, bw, h1), 3, 3);
            if (h2 > 0) dc.DrawRoundedRectangle(B(SecondaryColor), null, new Rect(x, Top + plotH - h2, bw, h2), 3, 3);
            if (it.ShowLabel && i % labelEvery == 0)
            {
                var lt = Text(it.Label, 10, B("MutedBrush"));
                dc.DrawText(lt, new Point(x + bw / 2 - lt.Width / 2, h - Bottom + 5));
            }
        }
    }

    private static double NiceStep(double max)
    {
        if (max <= 0) return 1;
        double unit = max >= 7200 ? 3600 : max >= 600 ? 60 : 1;      // hours / minutes / seconds
        double raw = max / unit / 4;
        double[] nice = { 1, 2, 5, 10, 15, 30, 60, 120, 240 };
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1))));
        double pick = nice.FirstOrDefault(n => n * (mag >= 10 && n < 10 ? 1 : 1) >= raw);
        if (pick == 0) pick = Math.Ceiling(raw);
        return Math.Max(pick, 1) * unit;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var items = Items; if (items == null || items.Count == 0) return;
        double slot = (ActualWidth - Left - 6) / items.Count;
        int i = (int)((e.GetPosition(this).X - Left) / slot);
        if (i < 0 || i >= items.Count) i = -1;
        if (i != _hover) { _hover = i; InvalidateVisual(); }
        SetTip(i >= 0 ? items[i].Tooltip : null);
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }
}

// ------------------------------------------------------------------------------------------------ donut
public sealed class DonutChart : ChartBase
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<SliceItem>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CenterTitleProperty = DependencyProperty.Register(nameof(CenterTitle), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CenterSubtitleProperty = DependencyProperty.Register(nameof(CenterSubtitle), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<SliceItem>? Items { get => (IReadOnlyList<SliceItem>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public string CenterTitle { get => (string)GetValue(CenterTitleProperty); set => SetValue(CenterTitleProperty, value); }
    public string CenterSubtitle { get => (string)GetValue(CenterSubtitleProperty); set => SetValue(CenterSubtitleProperty, value); }
    private int _hover = -1;

    private (Point c, double r) Geometry => (new Point(ActualWidth / 2, ActualHeight / 2), Math.Max(10, Math.Min(ActualWidth, ActualHeight) / 2 - 4));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var (c, r) = Geometry;
        double thick = r * 0.34;
        var items = Items?.Where(i => i.Value > 0).ToList();
        double total = items?.Sum(i => i.Value) ?? 0;
        if (items == null || total <= 0)
        {
            dc.DrawEllipse(null, new Pen(B("Surface2Brush"), thick), c, r - thick / 2, r - thick / 2);
            var t = Text("No data", 12, B("MutedBrush"));
            dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
            return;
        }
        double angle = -90;
        for (int i = 0; i < items.Count; i++)
        {
            double sweep = items[i].Value / total * 360;
            var brush = Hex(items[i].ColorHex);
            double ro = i == _hover ? r + 3 : r, ri = ro - thick - (i == _hover ? 3 : 0);
            if (sweep >= 359.99)
            {
                dc.DrawEllipse(null, new Pen(brush, ro - ri), c, (ro + ri) / 2, (ro + ri) / 2);
            }
            else
            {
                double gap = items.Count > 1 ? 1.2 : 0;
                var g = ArcGeometry(c, ro, ri, angle + gap / 2, Math.Max(0.5, sweep - gap));
                dc.DrawGeometry(brush, null, g);
            }
            angle += sweep;
        }
        if (!string.IsNullOrEmpty(CenterTitle))
        {
            var t1 = Text(CenterTitle, Math.Max(12, r * 0.28), B("TextBrush"), bold: true, maxWidth: (r - thick) * 1.8);
            var t2 = Text(CenterSubtitle ?? "", 11, B("MutedBrush"), maxWidth: (r - thick) * 1.8);
            double total2 = t1.Height + (string.IsNullOrEmpty(CenterSubtitle) ? 0 : t2.Height);
            dc.DrawText(t1, new Point(c.X - t1.Width / 2, c.Y - total2 / 2));
            if (!string.IsNullOrEmpty(CenterSubtitle)) dc.DrawText(t2, new Point(c.X - t2.Width / 2, c.Y - total2 / 2 + t1.Height));
        }
    }

    private static Geometry ArcGeometry(Point c, double ro, double ri, double startDeg, double sweepDeg)
    {
        Point P(double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool large = sweepDeg > 180;
            ctx.BeginFigure(P(ro, startDeg), true, true);
            ctx.ArcTo(P(ro, startDeg + sweepDeg), new Size(ro, ro), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(ri, startDeg + sweepDeg), true, false);
            ctx.ArcTo(P(ri, startDeg), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var items = Items?.Where(i => i.Value > 0).ToList();
        if (items == null || items.Count == 0) return;
        var (c, r) = Geometry;
        var p = e.GetPosition(this);
        double dx = p.X - c.X, dy = p.Y - c.Y, dist = Math.Sqrt(dx * dx + dy * dy);
        int idx = -1;
        if (dist >= r * 0.5 && dist <= r + 4)
        {
            double deg = Math.Atan2(dy, dx) * 180 / Math.PI + 90; if (deg < 0) deg += 360;
            double total = items.Sum(i => i.Value), acc = 0;
            for (int i = 0; i < items.Count; i++) { acc += items[i].Value / total * 360; if (deg <= acc) { idx = i; break; } }
        }
        if (idx != _hover) { _hover = idx; InvalidateVisual(); }
        double tot = items.Sum(i => i.Value);
        SetTip(idx >= 0 ? $"{items[idx].Label}\n{Fmt.Duration(items[idx].Value)} · {Fmt.Percent(items[idx].Value / tot * 100)}" : null);
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }
}

// ------------------------------------------------------------------------------------------------ heatmap
public sealed class HeatmapChart : ChartBase
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(double[,]), typeof(HeatmapChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[,]? Data { get => (double[,]?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    private static readonly DayOfWeek[] Order = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
    private const double Left = 34, Bottom = 18;
    private (int row, int col) _hover = (-1, -1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var data = Data;
        double max = 0;
        if (data != null) foreach (var v in data) max = Math.Max(max, v);
        double cw = (w - Left) / 24, ch = (h - Bottom) / 7;
        var accentBrush = (SolidColorBrush)B("AccentBrush");
        var empty = B("Surface2Brush");
        for (int r = 0; r < 7; r++)
        {
            var label = Text(Order[r].ToString()[..3], 10, B("MutedBrush"));
            dc.DrawText(label, new Point(0, r * ch + (ch - label.Height) / 2));
            for (int c = 0; c < 24; c++)
            {
                double v = data == null ? 0 : data[(int)Order[r], c];
                Brush fill = empty;
                if (v > 0 && max > 0)
                {
                    double t = Math.Pow(v / max, 0.6);
                    var col = accentBrush.Color; col.A = (byte)(60 + 195 * t);
                    fill = new SolidColorBrush(col);
                }
                var rect = new Rect(Left + c * cw + 1, r * ch + 1, Math.Max(1, cw - 2), Math.Max(1, ch - 2));
                dc.DrawRoundedRectangle(fill, (_hover == (r, c)) ? new Pen(B("TextBrush"), 1.2) : null, rect, 3, 3);
            }
        }
        for (int c = 0; c < 24; c += 3)
        {
            var t = Text(Fmt.Hour(c), 10, B("FaintBrush"));
            dc.DrawText(t, new Point(Left + c * cw + (cw - t.Width) / 2, h - Bottom + 3));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        double cw = (ActualWidth - Left) / 24, ch = (ActualHeight - Bottom) / 7;
        int c = (int)((p.X - Left) / cw), r = (int)(p.Y / ch);
        if (c < 0 || c > 23 || r < 0 || r > 6 || Data == null) { _hover = (-1, -1); SetTip(null); InvalidateVisual(); return; }
        if (_hover != (r, c)) { _hover = (r, c); InvalidateVisual(); }
        SetTip($"{Order[r]} {c:00}:00 – {Fmt.Duration(Data[(int)Order[r], c])}");
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = (-1, -1); InvalidateVisual(); }
}

// ------------------------------------------------------------------------------------------------ timeline
public sealed class TimelineChart : ChartBase
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(IReadOnlyList<TimelineRow>), typeof(TimelineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty PixelsPerHourProperty = DependencyProperty.Register(nameof(PixelsPerHour), typeof(double), typeof(TimelineChart),
        new FrameworkPropertyMetadata(48.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SelectedSessionIdProperty = DependencyProperty.Register(nameof(SelectedSessionId), typeof(long), typeof(TimelineChart),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TimelineRow>? Rows { get => (IReadOnlyList<TimelineRow>?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public double PixelsPerHour { get => (double)GetValue(PixelsPerHourProperty); set => SetValue(PixelsPerHourProperty, value); }
    public long SelectedSessionId { get => (long)GetValue(SelectedSessionIdProperty); set => SetValue(SelectedSessionIdProperty, value); }
    public event Action<TimelineBlock>? BlockClicked;

    private const double LabelW = 78, HeaderH = 26, Pad = 16;
    private double RowH => (Rows?.Count ?? 1) <= 1 ? 74 : 38;
    private readonly List<(Rect rect, TimelineBlock block)> _hits = new();
    private TimelineBlock? _hover;

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Math.Max(1, Rows?.Count ?? 1);
        return new Size(LabelW + 24 * PixelsPerHour + Pad, HeaderH + rows * RowH + 8);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        double pph = PixelsPerHour, w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var rows = Rows;
        if (rows == null || rows.Count == 0) return;

        var line = new Pen(B("LineBrush"), 1);
        int labelEvery = pph >= 60 ? 1 : pph >= 32 ? 2 : pph >= 20 ? 3 : 6;
        for (int hr = 0; hr <= 24; hr++)
        {
            double x = LabelW + hr * pph;
            dc.DrawLine(line, new Point(x, HeaderH - 4), new Point(x, h - 4));
            if (hr < 24 && hr % labelEvery == 0)
            {
                var t = Text(Fmt.Hour(hr) + (Fmt.Hour(hr).Length <= 2 ? ":00" : ""), 10, B("MutedBrush"));
                dc.DrawText(t, new Point(x + 3, 4));
            }
        }

        double rh = RowH;
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            double y = HeaderH + r * rh;
            var rl = Text(row.Label, 11, row.IsToday ? B("AccentBrush") : B("MutedBrush"), bold: row.IsToday);
            dc.DrawText(rl, new Point(6, y + (rh - rl.Height) / 2));
            dc.DrawLine(line, new Point(LabelW, y + rh), new Point(w - Pad / 2, y + rh));

            foreach (var b in row.Blocks)
            {
                double x1 = LabelW + (b.StartUtc - row.DayStartUtc).TotalHours * pph;
                double x2 = LabelW + (b.EndUtc - row.DayStartUtc).TotalHours * pph;
                x1 = Math.Max(LabelW, x1); x2 = Math.Min(LabelW + 24 * pph, x2);
                if (x2 <= x1) continue;
                double bw = Math.Max(2, x2 - x1);
                double by = y + 6, bh = rh - 12;
                if (b.IsIdle) { by = y + rh * 0.34; bh = rh * 0.32; }
                var rect = new Rect(x1, by, bw, bh);
                Brush fill = b.IsIdle ? B("IdleBrush") : Hex(b.ColorHex);
                bool sel = b.SessionId == SelectedSessionId && b.SessionId != 0;
                dc.DrawRoundedRectangle(fill, (sel || b == _hover) ? new Pen(B("TextBrush"), 2) : null, rect, 4, 4);
                _hits.Add((rect, b));
                if (!b.IsIdle && bw > 46 && rh > 50)
                {
                    var t = Text(b.Name, 11, Brushes.Black, bold: true, maxWidth: bw - 8);
                    var clip = new RectangleGeometry(rect);
                    dc.PushClip(clip);
                    dc.DrawText(t, new Point(rect.X + 5, rect.Y + (bh - t.Height) / 2));
                    dc.Pop();
                }
            }
            if (row.IsToday)
            {
                double nx = LabelW + (DateTime.UtcNow - row.DayStartUtc).TotalHours * pph;
                if (nx >= LabelW && nx <= LabelW + 24 * pph)
                    dc.DrawLine(new Pen(B("DangerBrush"), 1.5) { DashStyle = DashStyles.Dash }, new Point(nx, y), new Point(nx, y + rh));
            }
        }
    }

    private TimelineBlock? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var (rect, block) = _hits[i];
            var inflated = new Rect(rect.X - 1, rect.Y, rect.Width + 2, rect.Height);
            if (inflated.Contains(p)) return block;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(hit, _hover)) { _hover = hit; InvalidateVisual(); }
        Cursor = hit != null ? Cursors.Hand : Cursors.Arrow;
        if (hit == null) { SetTip(null); return; }
        var dur = (hit.EndUtc - hit.StartUtc).TotalSeconds;
        var tip = $"{(hit.IsIdle ? "Idle" : hit.Name)}\n{Fmt.TimeOfDay(hit.StartUtc, true)} – {Fmt.TimeOfDay(hit.EndUtc, true)}  ({Fmt.Duration(dur)})";
        if (!string.IsNullOrWhiteSpace(hit.Title)) tip += "\n" + (hit.Title!.Length > 90 ? hit.Title[..90] + "…" : hit.Title);
        SetTip(tip);
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (hit != null) BlockClicked?.Invoke(hit);
    }
}
