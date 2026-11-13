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
        foreach (var item in items) _list.Add(Card(item));
    }

    View Card(SavedResult item)
    {
        var r = item.Summary;
        int drops = r.Tunnels.Sum(t => t.Drops);
        var title = new Label { Text = $"{r.StartedAt:yyyy-MM-dd HH:mm} · {r.Mode} · {Clock.FormatDuration(r.DurationSec)}", FontAttributes = FontAttributes.Bold, FontSize = 14 };
        var server = new Label { Text = r.Server, FontSize = 12, TextColor = Ui.Muted };
        var figures = new Label
        {
            FontSize = 13,
            Text = $"↑ {Ui.Fmt(r.UpMbpsAvg, "0.0")}  ↓ {Ui.Fmt(r.DownMbpsAvg, "0.0")} Mbps · loss {Ui.Fmt(r.UpLossPct, "0.##")}/{Ui.Fmt(r.DownLossPct, "0.##")}% · RTT {Ui.Fmt(r.RttAvgMs, "0.0")} ms" +
                   (r.BloatGrade != null ? $" · bloat {r.BloatGrade}" : "") + (r.Tunnels.Count > 0 ? $" · tunnel drops {drops}" : ""),
        };

        var view = Btn("View", async () => await ViewAsync(item));
        var share = Btn("Share report", async () => await ShareAsync(item, "html"));
        var csv = Btn("Share CSV", async () => await ShareAsync(item, "csv"));
        var pdf = Btn("Share PDF", async () =>
        {
            if (_app.Busy) { await DisplayAlertAsync("Test running", "PDF reports can be made once the test has finished.", "OK"); return; }
            await ShareAsync(item, "pdf");
        });
        pdf.IsEnabled = !_app.Busy;
        var del = Btn("Delete", async () =>
        {
            if (await DisplayAlertAsync("Delete result", $"Delete the test from {r.StartedAt:g}?", "Delete", "Cancel"))
            {
                ResultStore.Delete(item.Path);
                _app.NotifyHistoryChanged();
            }
        });
        del.TextColor = Ui.Bad;
        var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start, AlignContent = Microsoft.Maui.Layouts.FlexAlignContent.Start, Children = { view, share, pdf, csv, del } };
        return Ui.Card(new VerticalStackLayout { Spacing = 2, Children = { title, server, figures, buttons } });
    }

    static Button Btn(string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, FontSize = 13, Padding = new Thickness(12, 0), HeightRequest = 40, MinimumHeightRequest = 40, Margin = new Thickness(0, 6, 6, 0), BackgroundColor = Color.FromArgb("#E8EEF6"), TextColor = Ui.Accent };
        b.Clicked += async (_, _) => await onClick();
        return b;
    }

    /// <summary>Loads the full record and writes the report or CSV off the UI thread.</summary>
    static Task<string> WriteFileAsync(SavedResult item, string ext) =>
        Task.Run(() => ReportBuilder.WriteFile(item.Load(), Path.Combine(FileSystem.CacheDirectory, "reports"), ext));

    async Task ShareAsync(SavedResult item, string ext)
    {
        var r = item.Summary;
        try
        {
            string path = await WriteFileAsync(item, ext);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = $"LinkTest report {r.StartedAt:g}",
                File = new ShareFile(path, ext switch { "csv" => "text/csv", "pdf" => "application/pdf", _ => "text/html" }),
            });
        }
        catch (Exception e) { await DisplayAlertAsync("Share failed", e.Message, "OK"); }
    }

    async Task ViewAsync(SavedResult item)
    {
        try
        {
            string path = await WriteFileAsync(item, "html");
            await Launcher.Default.OpenAsync(new OpenFileRequest("LinkTest report", new ReadOnlyFile(path, "text/html")));
        }
        catch (Exception e) { await DisplayAlertAsync("Cannot open the report", e.Message + "\nUse Share report instead.", "OK"); }
    }
}
