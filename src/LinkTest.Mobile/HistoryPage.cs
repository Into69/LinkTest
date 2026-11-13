using System.Globalization;
using LinkTest.Core;

namespace LinkTest.Mobile;

/// <summary>Saved test results with share / view actions for the HTML report and CSV.</summary>
sealed class HistoryPage : ContentPage
{
    readonly AppController _app = AppController.Instance;
    readonly VerticalStackLayout _list = new() { Spacing = 6, Padding = new Thickness(10) };

    public HistoryPage()
    {
        Title = "History";
        BackgroundColor = Ui.PageBg;
        Content = new ScrollView { Content = _list };
        _app.HistoryChanged += Reload;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Reload();
    }

    void Reload()
    {
        _list.Clear();
        var items = _app.Results.List();
        if (items.Count == 0)
        {
            _list.Add(Ui.Hint("Finished tests are saved here. Each can be shared as an HTML report (charts, tables, traceroutes) or as CSV."));
            return;
        }
        foreach (var (path, r) in items) _list.Add(Card(path, r));
    }

    View Card(string path, TestRecord r)
    {
        static string F(double v, string f) => double.IsFinite(v) ? v.ToString(f, CultureInfo.CurrentCulture) : "–";
        int drops = r.Tunnels.Sum(t => t.Drops);
        var title = new Label { Text = $"{r.StartedAt:yyyy-MM-dd HH:mm} · {r.Mode} · {TimeSpan.FromSeconds(r.DurationSec):h\\:mm\\:ss}", FontAttributes = FontAttributes.Bold, FontSize = 14 };
        var server = new Label { Text = r.Server, FontSize = 12, TextColor = Ui.Muted };
        var figures = new Label
        {
            FontSize = 13,
            Text = $"↑ {F(r.UpMbpsAvg, "0.0")}  ↓ {F(r.DownMbpsAvg, "0.0")} Mbps · loss {F(r.UpLossPct, "0.##")}/{F(r.DownLossPct, "0.##")}% · RTT {F(r.RttAvgMs, "0.0")} ms" +
                   (r.BloatGrade != null ? $" · bloat {r.BloatGrade}" : "") + (r.Tunnels.Count > 0 ? $" · tunnel drops {drops}" : ""),
        };

        var view = Btn("View", async () => await ViewAsync(r));
        var share = Btn("Share report", async () => await ShareAsync(r, "html"));
        var csv = Btn("Share CSV", async () => await ShareAsync(r, "csv"));
        var del = Btn("Delete", async () =>
        {
            if (await DisplayAlertAsync("Delete result", $"Delete the test from {r.StartedAt:g}?", "Delete", "Cancel"))
            {
                ResultStore.Delete(path);
                _app.NotifyHistoryChanged();
            }
        });
        del.TextColor = Ui.Bad;
        var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start, AlignContent = Microsoft.Maui.Layouts.FlexAlignContent.Start, Children = { view, share, csv, del } };
        return Ui.Card(new VerticalStackLayout { Spacing = 2, Children = { title, server, figures, buttons } });
    }

    static Button Btn(string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, FontSize = 13, Padding = new Thickness(12, 0), HeightRequest = 40, MinimumHeightRequest = 40, Margin = new Thickness(0, 6, 6, 0), BackgroundColor = Color.FromArgb("#E8EEF6"), TextColor = Ui.Accent };
        b.Clicked += async (_, _) => await onClick();
        return b;
    }

    static string WriteFile(TestRecord r, string ext)
    {
        string dir = Path.Combine(FileSystem.CacheDirectory, "reports");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, r.SuggestedFileName(ext));
        File.WriteAllText(path, ext == "csv" ? r.ToCsv() : ReportBuilder.Build(r));
        return path;
    }

    async Task ShareAsync(TestRecord r, string ext)
    {
        try
        {
            string path = WriteFile(r, ext);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = $"LinkTest report {r.StartedAt:g}",
                File = new ShareFile(path, ext == "csv" ? "text/csv" : "text/html"),
            });
        }
        catch (Exception e) { await DisplayAlertAsync("Share failed", e.Message, "OK"); }
    }

    async Task ViewAsync(TestRecord r)
    {
        try
        {
            string path = WriteFile(r, "html");
            await Launcher.Default.OpenAsync(new OpenFileRequest("LinkTest report", new ReadOnlyFile(path, "text/html")));
        }
        catch (Exception e) { await DisplayAlertAsync("Cannot open the report", e.Message + "\nUse Share report instead.", "OK"); }
    }
}
