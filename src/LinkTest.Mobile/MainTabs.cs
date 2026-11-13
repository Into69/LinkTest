using LinkTest.Core;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;

namespace LinkTest.Mobile;

/// <summary>Bottom tabs; Start/Stop live in the top bar so they are visible on every tab.</summary>
sealed class MainTabs : Microsoft.Maui.Controls.TabbedPage
{
    readonly AppController _app = AppController.Instance;
    readonly ToolbarItem _startStop = new() { Text = "START", Order = ToolbarItemOrder.Primary };
    readonly SettingsPage _settings = new();
    bool _checkedUpdates;

    public MainTabs()
    {
        Title = $"LinkTest v{Updater.CurrentVersion}";
        BarBackgroundColor = Colors.White;
        SelectedTabColor = Ui.Accent;
        UnselectedTabColor = Ui.Muted;
        On<Microsoft.Maui.Controls.PlatformConfiguration.Android>().SetToolbarPlacement(ToolbarPlacement.Bottom);

        Children.Add(new LivePage());
        Children.Add(_settings);
        Children.Add(new DetailsPage());
        Children.Add(new HistoryPage());
        Children.Add(new LogPage());

        _startStop.Clicked += async (_, _) => await StartStopAsync();
        ToolbarItems.Add(_startStop);
        var update = new ToolbarItem { Text = "Check for updates", Order = ToolbarItemOrder.Secondary };
        update.Clicked += async (_, _) => await _app.CheckForUpdatesAsync(this, manual: true);
        ToolbarItems.Add(update);
        var site = new ToolbarItem { Text = "Project page", Order = ToolbarItemOrder.Secondary };
        site.Clicked += async (_, _) => await Launcher.Default.OpenAsync(Updater.ProjectUrl);
        ToolbarItems.Add(site);

        _app.StateChanged += UpdateButton;
        UpdateButton();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_checkedUpdates) return;
        _checkedUpdates = true;
        if (_app.Settings.CheckForUpdates) await _app.CheckForUpdatesAsync(this, manual: false);
    }

    void UpdateButton() => _startStop.Text = _app.Busy ? "STOP" : "START";

    async Task StartStopAsync()
    {
        if (_app.Busy) { await _app.StopAsync(); return; }
        _settings.SaveToSettings();
        CurrentPage = Children[0];
        string? err = await _app.StartAsync();
        if (err != null)
        {
            await DisplayAlertAsync("Could not start", err, "OK");
            if (string.IsNullOrWhiteSpace(_app.Settings.Host)) CurrentPage = _settings;
        }
    }
}
