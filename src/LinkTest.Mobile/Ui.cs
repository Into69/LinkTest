using System.Globalization;

namespace LinkTest.Mobile;

/// <summary>Colours, formatting and small control factories shared by the pages.</summary>
static class Ui
{
    public static readonly Color Brand = Color.FromArgb("#0B1E3A");
    public static readonly Color Accent = Color.FromArgb("#0078D4");
    public static readonly Color PageBg = Color.FromArgb("#F5F6F8");
    public static readonly Color Muted = Color.FromArgb("#5A5A5A");
    public static readonly Color Good = Color.FromArgb("#228B22");
    public static readonly Color Warn = Color.FromArgb("#D2691E");
    public static readonly Color Bad = Color.FromArgb("#B22222");
    public static readonly Color GoodBg = Color.FromArgb("#DFF6DD");
    public static readonly Color WarnBg = Color.FromArgb("#FFF4CE");
    public static readonly Color BadBg = Color.FromArgb("#FDE7E9");

    public static string Fmt(double v, string f) => double.IsFinite(v) ? v.ToString(f, CultureInfo.CurrentCulture) : "–";
    public static double Z(double v) => double.IsFinite(v) ? v : 0;
    public static string Mbps(double v) => !double.IsFinite(v) ? "–" : v >= 100 ? $"{v:0} Mbps" : v >= 1 ? $"{v:0.0} Mbps" : $"{v * 1000:0} kbps";
    public static string Ms(double v) => double.IsFinite(v) ? (v >= 10 ? $"{v:0} ms" : $"{v:0.0} ms") : "–";
    public static Color? Level(double v, double warn, double bad) => !double.IsFinite(v) ? null : v >= bad ? Bad : v >= warn ? Warn : Good;

    public static Label Header(string text) => new()
    {
        Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Accent, Margin = new Thickness(0, 14, 0, 4),
    };

    public static Label Hint(string text) => new() { Text = text, FontSize = 12, TextColor = Muted };

    public static Border Card(View content) => new()
    {
        Content = content, BackgroundColor = Colors.White, Stroke = Color.FromArgb("#DDDDDD"), StrokeThickness = 1,
        Padding = new Thickness(10, 8), Margin = new Thickness(0, 4),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
    };
}

/// <summary>"Big number" tile.</summary>
sealed class Tile : Border
{
    readonly Label _value = new() { Text = "–", FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Colors.Black };
    readonly Label _sub = new() { FontSize = 11, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.TailTruncation };

    public Tile(string title)
    {
        BackgroundColor = Colors.White;
        Stroke = Color.FromArgb("#DDDDDD");
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 };
        Padding = new Thickness(10, 6);
        Content = new VerticalStackLayout
        {
            Spacing = 0,
            Children = { new Label { Text = title, FontSize = 11, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.TailTruncation }, _value, _sub },
        };
    }

    public void Set(string value, string sub = "", Color? color = null)
    {
        _value.Text = value;
        _value.TextColor = color ?? Colors.Black;
        _sub.Text = sub;
    }
}

/// <summary>Real-time line chart (one point per second, newest on the right) drawn with Microsoft.Maui.Graphics.</summary>
sealed class ChartDrawable : IDrawable
{
    readonly List<(string name, Color color, List<double> data)> _series = new();

    public string Title { get; init; } = "";
    public string Unit { get; init; } = "";
    public int Capacity { get; init; } = 180;
    public double MinYMax { get; init; } = 1;

    public void AddSeries(string name, Color color) => _series.Add((name, color, new List<double>()));

    public void Push(params double[] values)
    {
        for (int i = 0; i < _series.Count; i++)
        {
            var d = _series[i].data;
            d.Add(i < values.Length ? values[i] : double.NaN);
            if (d.Count > Capacity) d.RemoveAt(0);
        }
    }

    public void Clear() { foreach (var s in _series) s.data.Clear(); }

    public void Draw(ICanvas canvas, RectF r)
    {
        canvas.FillColor = Colors.White;
        canvas.FillRectangle(r);
        const float left = 38, top = 36, right = 8, bottom = 16;
        var plot = new RectF(r.X + left, r.Y + top, r.Width - left - right, r.Height - top - bottom);
        if (plot.Width < 20 || plot.Height < 20) return;

        double max = MinYMax;
        foreach (var s in _series) foreach (var v in s.data) if (double.IsFinite(v) && v > max) max = v;
        max = NiceCeiling(max * 1.1);

        canvas.FontSize = 12;
        canvas.FontColor = Colors.Black;
        canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
        canvas.DrawString(Title + (Unit.Length > 0 ? $" ({Unit})" : ""), r.X + 4, r.Y + 1, r.Width - 8, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
        canvas.Font = Microsoft.Maui.Graphics.Font.Default;

        // Legend (latest values) on the second line.
        canvas.FontSize = 10;
        float lx = r.X + 4;
        foreach (var (name, color, data) in _series)
        {
            double last = data.Count > 0 ? data[^1] : double.NaN;
            string txt = $"{name}: {(double.IsFinite(last) ? last.ToString("0.##", CultureInfo.CurrentCulture) : "–")}";
            float w = txt.Length * 5.6f + 4;
            if (lx + 12 + w > r.Right) break;
            canvas.FillColor = color;
            canvas.FillRectangle(lx, r.Y + 21, 8, 8);
            canvas.FontColor = Colors.Black;
            canvas.DrawString(txt, lx + 11, r.Y + 17, w, 14, HorizontalAlignment.Left, VerticalAlignment.Top);
            lx += 12 + w + 6;
        }

        canvas.StrokeColor = Color.FromArgb("#E6E6E6");
        canvas.StrokeSize = 1;
        canvas.FontColor = Ui.Muted;
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Bottom - plot.Height * i / 4;
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            canvas.DrawString((max * i / 4).ToString(max < 10 ? "0.##" : "0", CultureInfo.CurrentCulture),
                r.X, y - 7, left - 4, 14, HorizontalAlignment.Right, VerticalAlignment.Center);
        }
        canvas.StrokeColor = Colors.Silver;
        canvas.DrawRectangle(plot);
        canvas.DrawString($"-{Capacity / 60}:{Capacity % 60:00}", plot.Left, plot.Bottom + 1, 60, 14, HorizontalAlignment.Left, VerticalAlignment.Top);
        canvas.DrawString("now", plot.Right - 40, plot.Bottom + 1, 40, 14, HorizontalAlignment.Right, VerticalAlignment.Top);

        float step = plot.Width / Math.Max(1, Capacity - 1);
        foreach (var (_, color, data) in _series)
        {
            canvas.StrokeColor = color;
            canvas.StrokeSize = 1.8f;
            PathF? path = null;
            int offset = Capacity - data.Count;
            for (int i = 0; i < data.Count; i++)
            {
                double v = data[i];
                if (!double.IsFinite(v)) { if (path != null) canvas.DrawPath(path); path = null; continue; }
                float x = plot.Left + (offset + i) * step;
                float y = plot.Bottom - (float)(Math.Clamp(v / max, 0, 1) * plot.Height);
                if (path == null) { path = new PathF(); path.MoveTo(x, y); }
                else path.LineTo(x, y);
            }
            if (path != null) canvas.DrawPath(path);
        }
    }

    static double NiceCeiling(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp;
    }
}
