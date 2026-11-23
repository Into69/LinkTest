using System.Globalization;
using LinkTest.Core;

namespace LinkTest.Mobile;

/// <summary>Colours, formatting and small control factories shared by the pages.</summary>
static class Ui
{
    /// <summary>
    /// Dark theme in effect (the app's Theme setting, or Android's when it is "System"). The pages read the colours
    /// below when they are built: <see cref="App.ApplyTheme"/> rebuilds them after a change.
    /// </summary>
    public static bool Dark => Application.Current?.RequestedTheme == AppTheme.Dark;
    static Color C(string light, string dark) => Color.FromArgb(Dark ? dark : light);

    public static Color Brand => C("#0B1E3A", "#E8EAED");
    public static Color Accent => C("#0078D4", "#4FA8F0");
    /// <summary>Filled buttons (white text): the same blue in both themes.</summary>
    public static readonly Color ButtonBg = Color.FromArgb("#0078D4");
    /// <summary>Light buttons with <see cref="Accent"/> text.</summary>
    public static Color SoftButtonBg => C("#E8EEF6", "#22324A");
    public static Color PageBg => C("#F5F6F8", "#111419");
    public static Color CardBg => C("#FFFFFF", "#1B1F27");
    public static Color CardStroke => C("#DDDDDD", "#2C323D");
    public static Color Text => C("#000000", "#E8EAED");
    public static Color Muted => C("#5A5A5A", "#A0A6B0");
    public static Color Good => C("#228B22", "#5BC46B");
    public static Color Warn => C("#D2691E", "#E8A15A");
    public static Color Bad => C("#B22222", "#F06A6A");
    public static Color GoodBg => C("#DFF6DD", "#173322");
    public static Color WarnBg => C("#FFF4CE", "#3A2C16");
    public static Color BadBg => C("#FDE7E9", "#3D1C1F");
    public static Color NeutralBg => C("#F0F0F0", "#2A2F38");
    public static Color ChartGrid => C("#E6E6E6", "#252A33");
    public static Color ChartFrame => C("#C0C0C0", "#3A404B");
    // Chart series.
    public static Color Up => C("#0078D4", "#4FA8F0");
    public static Color Down => C("#10893E", "#4CC27A");
    public static Color Rtt => C("#CA5010", "#F08A4B");
    public static Color LossDown => C("#C42B1C", "#F06A6A");

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

    public const string InfoGlyph = "ⓘ";

    /// <summary>Shows what a statistic means and its ideal values.</summary>
    public static void ShowHelp(string key)
    {
        if (StatHelp.Get(key) is { } i) ShowHelp(i.Title, i.ToText());
    }

    public static async void ShowHelp(string title, string text)
    {
        try
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null) await page.DisplayAlertAsync(title, text, "OK");
        }
        catch { }
    }

    public static void OnTap(View v, Action a)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => a();
        v.GestureRecognizers.Add(tap);
    }

    /// <summary>Section header with an ⓘ that explains every statistic of the section.</summary>
    public static View HelpHeader(string text, string group, string helpTitle)
    {
        var info = new Label { Text = InfoGlyph, FontSize = 20, TextColor = Accent, VerticalOptions = LayoutOptions.End, Padding = new Thickness(8, 0, 8, 2) };
        OnTap(info, () => ShowHelp(helpTitle, StatHelp.GroupText(group)));
        var h = Header(text);
        var row = new HorizontalStackLayout { Children = { h, info } };
        OnTap(h, () => ShowHelp(helpTitle, StatHelp.GroupText(group)));
        return row;
    }

    public static Border Card(View content) => new()
    {
        Content = content, BackgroundColor = Ui.CardBg, Stroke = Ui.CardStroke, StrokeThickness = 1,
        Padding = new Thickness(10, 8), Margin = new Thickness(0, 4),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
    };
}

/// <summary>"Big number" tile.</summary>
sealed class Tile : Border
{
    readonly Label _value = new() { Text = "–", FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Ui.Text };
    readonly Label _sub = new() { FontSize = 11, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.TailTruncation };

    /// <param name="helpKey">StatHelp key: the title gets an ⓘ and tapping the tile explains the statistic.</param>
    public Tile(string title, string? helpKey = null)
    {
        if (helpKey != null)
        {
            title += " " + Ui.InfoGlyph;
            Ui.OnTap(this, () => Ui.ShowHelp(helpKey));
        }
        BackgroundColor = Ui.CardBg;
        Stroke = Ui.CardStroke;
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
        _value.TextColor = color ?? Ui.Text;
        _sub.Text = sub;
    }
}

/// <summary>Real-time line chart (one point per second, newest on the right) drawn with Microsoft.Maui.Graphics.</summary>
sealed class ChartDrawable : IDrawable
{
    readonly List<(string name, Color color, List<double> data)> _series = new();

    public string Title { get; init; } = "";
    /// <summary>Draws an ⓘ after the title (the view handles the tap).</summary>
    public bool ShowInfo { get; init; }
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
        canvas.FillColor = Ui.CardBg;
        canvas.FillRectangle(r);
        const float left = 38, top = 36, right = 8, bottom = 16;
        var plot = new RectF(r.X + left, r.Y + top, r.Width - left - right, r.Height - top - bottom);
        if (plot.Width < 20 || plot.Height < 20) return;

        double max = MinYMax;
        foreach (var s in _series) foreach (var v in s.data) if (double.IsFinite(v) && v > max) max = v;
        max = NiceCeiling(max * 1.1);

        canvas.FontSize = 12;
        canvas.FontColor = Ui.Text;
        canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
        canvas.DrawString(Title + (Unit.Length > 0 ? $" ({Unit})" : "") + (ShowInfo ? " " + Ui.InfoGlyph : ""), r.X + 4, r.Y + 1, r.Width - 8, 16, HorizontalAlignment.Left, VerticalAlignment.Top);
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
            canvas.FontColor = Ui.Text;
            canvas.DrawString(txt, lx + 11, r.Y + 17, w, 14, HorizontalAlignment.Left, VerticalAlignment.Top);
            lx += 12 + w + 6;
        }

        canvas.StrokeColor = Ui.ChartGrid;
        canvas.StrokeSize = 1;
        canvas.FontColor = Ui.Muted;
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Bottom - plot.Height * i / 4;
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            canvas.DrawString((max * i / 4).ToString(max < 10 ? "0.##" : "0", CultureInfo.CurrentCulture),
                r.X, y - 7, left - 4, 14, HorizontalAlignment.Right, VerticalAlignment.Center);
        }
        canvas.StrokeColor = Ui.ChartFrame;
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
