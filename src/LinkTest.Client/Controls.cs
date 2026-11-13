using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace LinkTest.Client;

/// <summary>Minimal real-time line chart: newest point on the right, one point per second.</summary>
sealed class LineChart : Control
{
    sealed class Series
    {
        public required string Name;
        public required Color Color;
        public readonly List<double> Data = new();
    }

    readonly List<Series> _series = new();

    public LineChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 8.25f);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Unit { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Capacity { get; set; } = 300;
    /// <summary>The y axis never scales below this maximum.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double MinYMax { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ValueFormat { get; set; } = "0.##";

    public void AddSeries(string name, Color color) => _series.Add(new Series { Name = name, Color = color });

    /// <summary>One value per series, in AddSeries order. Use NaN for "no data".</summary>
    public void Push(params double[] values)
    {
        for (int i = 0; i < _series.Count; i++)
        {
            var d = _series[i].Data;
            d.Add(i < values.Length ? values[i] : double.NaN);
            if (d.Count > Capacity) d.RemoveAt(0);
        }
        Invalidate();
    }

    public void Clear()
    {
        foreach (var s in _series) s.Data.Clear();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        float k = DeviceDpi / 96f;
        var plot = new RectangleF(48 * k, 22 * k, Width - 58 * k, Height - 40 * k);
        if (plot.Width < 20 || plot.Height < 20) return;

        double max = MinYMax;
        foreach (var s in _series)
            foreach (var v in s.Data)
                if (double.IsFinite(v) && v > max) max = v;
        max = NiceCeiling(max * 1.1);

        using var gridPen = new Pen(Color.FromArgb(230, 230, 230));
        using var axisBrush = new SolidBrush(Color.FromArgb(110, 110, 110));
        using var titleFont = new Font(Font, FontStyle.Bold);
        g.DrawString(Title + (Unit.Length > 0 ? $" ({Unit})" : ""), titleFont, Brushes.Black, 4 * k, 3 * k);

        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Bottom - plot.Height * i / 4;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            string lbl = (max * i / 4).ToString(max < 10 ? "0.##" : "0", CultureInfo.CurrentCulture);
            var sz = g.MeasureString(lbl, Font);
            g.DrawString(lbl, Font, axisBrush, plot.Left - sz.Width - 2, y - sz.Height / 2);
        }
        g.DrawRectangle(Pens.Silver, plot.X, plot.Y, plot.Width, plot.Height);
        g.DrawString($"-{Capacity / 60}:{Capacity % 60:00}", Font, axisBrush, plot.Left, plot.Bottom + 1);
        var nowSz = g.MeasureString("now", Font);
        g.DrawString("now", Font, axisBrush, plot.Right - nowSz.Width, plot.Bottom + 1);

        float step = plot.Width / Math.Max(1, Capacity - 1);
        foreach (var s in _series)
        {
            using var pen = new Pen(s.Color, 1.6f * k);
            var pts = new List<PointF>();
            int offset = Capacity - s.Data.Count;
            for (int i = 0; i < s.Data.Count; i++)
            {
                double v = s.Data[i];
                if (!double.IsFinite(v)) { Flush(); continue; }
                float x = plot.Left + (offset + i) * step;
                float y = plot.Bottom - (float)(Math.Clamp(v / max, 0, 1) * plot.Height);
                pts.Add(new PointF(x, y));
            }
            Flush();

            void Flush()
            {
                if (pts.Count == 1) g.FillEllipse(new SolidBrush(s.Color), pts[0].X - 2, pts[0].Y - 2, 4, 4);
                else if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                pts.Clear();
            }
        }

        // Legend with latest values, right-aligned in the title row.
        float lx = Width - 6 * k;
        for (int i = _series.Count - 1; i >= 0; i--)
        {
            var s = _series[i];
            double last = s.Data.Count > 0 ? s.Data[^1] : double.NaN;
            string txt = $"{s.Name}: {(double.IsFinite(last) ? last.ToString(ValueFormat, CultureInfo.CurrentCulture) : "–")}";
            var sz = g.MeasureString(txt, Font);
            lx -= sz.Width;
            g.DrawString(txt, Font, Brushes.Black, lx, 4 * k);
            lx -= 12 * k;
            using var b = new SolidBrush(s.Color);
            g.FillRectangle(b, lx, 7 * k, 9 * k, 9 * k);
            lx -= 10 * k;
        }
    }

    static double NiceCeiling(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nf * exp;
    }
}

/// <summary>Small "big number" panel.</summary>
sealed class StatTile : Panel
{
    readonly Label _title = new() { Dock = DockStyle.Top, AutoSize = false, Height = 18, ForeColor = Color.FromArgb(90, 90, 90) };
    readonly Label _value = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _sub = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 18, ForeColor = Color.FromArgb(90, 90, 90) };

    public StatTile(string title)
    {
        Width = 150;
        Height = 78;
        Padding = new Padding(8, 4, 8, 4);
        Margin = new Padding(4);
        BackColor = Color.White;
        BorderStyle = BorderStyle.FixedSingle;
        _title.Text = title;
        _value.Font = new Font("Segoe UI Semibold", 15f);
        _value.Text = "–";
        Controls.Add(_value);
        Controls.Add(_sub);
        Controls.Add(_title);
    }

    public void Set(string value, string sub = "", Color? color = null)
    {
        _value.Text = value;
        _value.ForeColor = color ?? Color.Black;
        _sub.Text = sub;
    }
}
