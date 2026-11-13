using System.Globalization;
using LinkTest.Core;

namespace LinkTest.Mobile;

/// <summary>Server, traffic and tunnel settings. Locked while a test runs.</summary>
sealed class SettingsPage : ContentPage
{
    const string SimulateItem = "Simulate (enterprise mix)";
    static readonly string[] DscpItems = { "0 - Best effort", "46 - EF (voice)", "34 - AF41 (video)", "26 - AF31", "18 - AF21",
                                           "10 - AF11", "48 - CS6 (network control)", "8 - CS1 (scavenger)", SimulateItem };

    readonly AppController _app = AppController.Instance;
    readonly Entry _host = new() { Placeholder = "IP address or FQDN", Keyboard = Keyboard.Url };
    readonly Entry _port = Num();
    readonly Entry _key = new() { Placeholder = "(optional)", IsPassword = true };
    readonly Switch _ipLookup = new();
    readonly Picker _proto = new() { ItemsSource = new[] { "UDP", "TCP", "Mixed (TCP + UDP)" } };
    readonly Entry _udpShare = Num();
    readonly Entry _up = Num(true), _down = Num(true), _size = Num(), _burstOn = Num(), _burstOff = Num(), _streams = Num(), _duration = Num();
    readonly Picker _profile = new() { ItemsSource = Enum.GetNames<TrafficProfile>() };
    readonly Picker _dscp = new() { ItemsSource = DscpItems };
    readonly Switch _keepOn = new(), _updates = new(), _mtuSw = new(), _traceSw = new(), _locationSw = new(), _tlsSw = new(), _capacitySw = new();
    static readonly string[] FamilyItems = { "Auto (IPv4 first)", "IPv4 only", "IPv6 only", "IPv4, then IPv6 (compare)" };
    readonly Picker _family = new() { ItemsSource = FamilyItems };
    readonly Entry _capacityPct = Num();
    readonly Label _batteryState = new() { FontSize = 13, VerticalOptions = LayoutOptions.Center };
    readonly Button _batteryBtn = new() { Text = "Allow", BackgroundColor = Ui.Accent, TextColor = Colors.White, FontSize = 13, HeightRequest = 40, Padding = new Thickness(12, 0) };
    readonly Entry _baseline = Num();
    readonly List<(CheckBox box, int index)> _tunnels = new();
    readonly VerticalStackLayout _form;

    public SettingsPage()
    {
        Title = "Settings";
        BackgroundColor = Ui.PageBg;
        var st = _app.Settings;

        _form = new VerticalStackLayout { Padding = new Thickness(12, 4, 12, 24), Spacing = 2 };
        _form.Add(Ui.Header("Server"));
        Row("Server IP / FQDN", _host);
        Row("Port (TCP + UDP)", _port);
        Row("Key", _key);
        SwitchRow("Look up my public IP details (ipinfo.io)", _ipLookup);
        Row("Address family", _family);
        _form.Add(Ui.Hint("\"IPv4, then IPv6\" runs the test twice, one after the other, and makes a side-by-side comparison report (set a duration)."));
        SwitchRow("Require an encrypted connection", _tlsSw);
        _form.Add(Ui.Hint("The control connection (key, settings, results) is encrypted whenever the server supports it (1.9+). The server's certificate is remembered the first time; you're asked if it changes."));

        _form.Add(Ui.Header("Traffic"));
        Row("Protocol", _proto);
        Row("UDP share (%) - mixed mode", _udpShare);
        Row("Upload / TX (Mbps)", _up);
        Row("Download / RX (Mbps)", _down);
        SwitchRow("Find the capacity first", _capacitySw);
        Row("…then test at (% of capacity)", _capacityPct);
        _form.Add(Ui.Hint("Capacity test: ramps up from a few Mbps until the link saturates or reaches the upload / download rates above, then tests at this share of what it found. Needs a 1.9+ server."));
        Row("Packet size (bytes)", _size);
        Row("Traffic profile", _profile);
        Row("Burst on / off (ms) - bursty profile", BurstRow());
        Row("TCP streams", _streams);
        Row("DSCP marking", _dscp);
        _form.Add(Ui.Hint("On Android DSCP marking needs no special permission. \"Simulate\" runs a 10-class enterprise mix (voice EF, video AF41 … scavenger CS1) - see the Details tab while running."));
        Row("Duration (s, 0 = until stopped)", _duration);

        _form.Add(Ui.Header("Diagnostics"));
        Row("Idle baseline (s) - for the bufferbloat grade, 0 = off", _baseline);
        SwitchRow("Measure path MTU when a test starts", _mtuSw);
        SwitchRow("Traceroute at start and on trouble", _traceSw);
        SwitchRow("Report my location with the test", _locationSw);
        _form.Add(Ui.Hint("Off by default. When on, the phone's position (GPS / network) is recorded every 10 s with the test results and reports, " +
                          "and sent to the server. Android asks for location permission at the next test."));

        _form.Add(Ui.Header("VPN / tunnel simulations"));
        _form.Add(Ui.Hint("Each checked tunnel sends real keepalive and handshake probes with its own timers; it drops only if the link loses enough of them."));
        for (int i = 0; i < st.Tunnels.Count; i++)
        {
            int idx = i;
            var box = new CheckBox { IsChecked = st.Tunnels[i].Enabled, Color = Ui.Accent };
            var name = new Label { Text = st.Tunnels[i].Name, VerticalOptions = LayoutOptions.Center, FontSize = 14, LineBreakMode = LineBreakMode.TailTruncation };
            var edit = new Button { Text = "Edit", FontSize = 13, Padding = new Thickness(12, 0), HeightRequest = 40, VerticalOptions = LayoutOptions.Center, BackgroundColor = Color.FromArgb("#E8EEF6"), TextColor = Ui.Accent };
            edit.Clicked += async (_, _) =>
            {
                if (_app.Busy) return;
                var current = st.Tunnels[idx];
                await Navigation.PushAsync(new TunnelEditPage(current, edited =>
                {
                    edited.Enabled = box.IsChecked;
                    st.Tunnels[idx] = edited;
                    name.Text = edited.Name;
                    st.Save();
                }));
            };
            _tunnels.Add((box, i));
            var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
            row.Add(box, 0); row.Add(name, 1); row.Add(edit, 2);
            _form.Add(row);
        }

        _form.Add(Ui.Header("Background testing"));
        _form.Add(Ui.Hint("Tests keep running with the screen off, the phone asleep or another app in front: LinkTest runs as a foreground " +
                          "service with a notification and keeps the CPU and Wi-Fi awake while a test runs (only then)."));
        var batteryRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, Margin = new Thickness(0, 6, 0, 0) };
        batteryRow.Add(_batteryState, 0);
        batteryRow.Add(_batteryBtn, 1);
        _form.Add(batteryRow);
        _batteryBtn.Clicked += (_, _) =>
        {
            TestService.Battery.RequestExemption();
            // The answer comes from a system screen: re-check for a minute so the status updates when it closes.
            int checks = 0;
            Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () => { RefreshBattery(); return ++checks < 60 && !TestService.Battery.IsExempt; });
        };
        var oemHint = Ui.Hint("Some phones (Samsung, Xiaomi, Huawei, OnePlus…) add their own battery managers: also set LinkTest to \"Unrestricted\" " +
                              "in the app's battery settings. Tap here for instructions for your phone (dontkillmyapp.com).");
        oemHint.TextColor = Ui.Accent;
        Ui.OnTap(oemHint, () => _ = Launcher.Default.OpenAsync("https://dontkillmyapp.com"));
        _form.Add(oemHint);
        var appBattery = new Button { Text = "Open LinkTest's app settings", BackgroundColor = Color.FromArgb("#E8EEF6"), TextColor = Ui.Accent, FontSize = 13, HeightRequest = 40, HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(0, 4, 0, 0) };
        appBattery.Clicked += (_, _) => AppInfo.Current.ShowSettingsUI();
        _form.Add(appBattery);

        _form.Add(Ui.Header("App"));
        SwitchRow("Keep the screen on during a test", _keepOn);
        SwitchRow("Check for updates at startup", _updates);
        _form.Add(Ui.Hint($"LinkTest {Updater.CurrentVersion} · {Updater.ProjectUrl}"));

        Content = new ScrollView { Content = _form };
        Load();
        _proto.SelectedIndexChanged += (_, _) => UpdateEnabled();
        _dscp.SelectedIndexChanged += (_, _) => UpdateEnabled();
        _profile.SelectedIndexChanged += (_, _) => UpdateEnabled();
        _app.StateChanged += UpdateEnabled;
        UpdateEnabled();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_app.Busy) SaveToSettings();
    }

    static Entry Num(bool decimals = false) => new() { Keyboard = decimals ? Keyboard.Numeric : Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.End };

    View BurstRow()
    {
        var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        g.Add(_burstOn, 0);
        g.Add(_burstOff, 1);
        return g;
    }

    void Row(string label, View control)
    {
        _form.Add(new Label { Text = label, FontSize = 13, TextColor = Ui.Muted, Margin = new Thickness(0, 6, 0, 0) });
        _form.Add(control);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshBattery();
    }

    void RefreshBattery()
    {
        bool ok = TestService.Battery.IsExempt;
        _batteryState.Text = ok ? "Battery optimisation: LinkTest is exempt ✓" : "Battery optimisation: Android may pause long tests";
        _batteryState.TextColor = ok ? Ui.Good : Ui.Warn;
        _batteryBtn.IsVisible = !ok;
    }

    void SwitchRow(string label, Switch sw)
    {
        var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, Margin = new Thickness(0, 4) };
        g.Add(new Label { Text = label, FontSize = 14, VerticalOptions = LayoutOptions.Center }, 0);
        g.Add(sw, 1);
        sw.OnColor = Ui.Accent;
        _form.Add(g);
    }

    void Load()
    {
        var st = _app.Settings;
        var c = st.Config;
        _host.Text = st.Host;
        _port.Text = st.Port.ToString(CultureInfo.InvariantCulture);
        _key.Text = st.Key;
        _ipLookup.IsToggled = st.IpLookup;
        _proto.SelectedIndex = Math.Clamp((int)c.Protocol, 0, 2);
        _udpShare.Text = c.UdpSharePct.ToString("0", CultureInfo.CurrentCulture);
        _up.Text = c.UpMbps.ToString("0.###", CultureInfo.CurrentCulture);
        _down.Text = c.DownMbps.ToString("0.###", CultureInfo.CurrentCulture);
        _size.Text = c.PacketSize.ToString(CultureInfo.InvariantCulture);
        _profile.SelectedIndex = (int)c.Profile;
        _burstOn.Text = c.BurstOnMs.ToString(CultureInfo.InvariantCulture);
        _burstOff.Text = c.BurstOffMs.ToString(CultureInfo.InvariantCulture);
        _streams.Text = c.Streams.ToString(CultureInfo.InvariantCulture);
        _dscp.SelectedIndex = c.DscpSimulate ? DscpItems.Length - 1
            : Math.Max(0, Array.FindIndex(DscpItems, d => int.TryParse(d.Split(' ')[0], out var v) && v == c.Dscp));
        _duration.Text = c.DurationSec.ToString(CultureInfo.InvariantCulture);
        _keepOn.IsToggled = st.KeepScreenOn;
        _baseline.Text = c.BaselineSec.ToString(CultureInfo.InvariantCulture);
        _mtuSw.IsToggled = st.MeasureMtu;
        _traceSw.IsToggled = st.AutoTraceroute;
        _locationSw.IsToggled = st.ShareLocation;
        _family.SelectedIndex = st.Family switch { "4" => 1, "6" => 2, "both" => 3, _ => 0 };
        _tlsSw.IsToggled = st.RequireTls;
        _capacitySw.IsToggled = st.CapacityTest;
        _capacityPct.Text = st.CapacityPct.ToString("0", CultureInfo.CurrentCulture);
        _updates.IsToggled = st.CheckForUpdates;
    }

    public void SaveToSettings()
    {
        var st = _app.Settings;
        var c = st.Config;
        st.Host = (_host.Text ?? "").Trim();
        st.Port = Int(_port, st.Port, 1, 65535);
        st.Key = _key.Text ?? "";
        st.IpLookup = _ipLookup.IsToggled;
        c.Protocol = (TransportKind)Math.Max(0, _proto.SelectedIndex);
        c.UdpSharePct = Dbl(_udpShare, c.UdpSharePct, 0, 100);
        c.UpMbps = Dbl(_up, c.UpMbps, 0, 10000);
        c.DownMbps = Dbl(_down, c.DownMbps, 0, 10000);
        c.PacketSize = Int(_size, c.PacketSize, Protocol.HeaderSize, Protocol.MaxPacket);
        c.Profile = (TrafficProfile)Math.Max(0, _profile.SelectedIndex);
        c.BurstOnMs = Int(_burstOn, c.BurstOnMs, 1, 60000);
        c.BurstOffMs = Int(_burstOff, c.BurstOffMs, 0, 60000);
        c.Streams = Int(_streams, c.Streams, 1, 16);
        bool sim = _dscp.SelectedItem as string == SimulateItem;
        c.DscpSimulate = sim;
        c.Dscp = sim || _dscp.SelectedItem is not string d ? 0 : int.TryParse(d.Split(' ')[0], out var v) ? v : 0;
        c.DurationSec = Int(_duration, c.DurationSec, 0, 864000);
        foreach (var (box, i) in _tunnels) st.Tunnels[i].Enabled = box.IsChecked;
        st.KeepScreenOn = _keepOn.IsToggled;
        c.BaselineSec = Int(_baseline, c.BaselineSec, 0, 60);
        st.MeasureMtu = _mtuSw.IsToggled;
        st.AutoTraceroute = _traceSw.IsToggled;
        st.ShareLocation = _locationSw.IsToggled;
        st.Family = _family.SelectedIndex switch { 1 => "4", 2 => "6", 3 => "both", _ => "auto" };
        st.RequireTls = _tlsSw.IsToggled;
        st.CapacityTest = _capacitySw.IsToggled;
        st.CapacityPct = Dbl(_capacityPct, st.CapacityPct, 10, 100);
        st.CheckForUpdates = _updates.IsToggled;
        st.Save();
    }

    static int Int(Entry e, int fallback, int min, int max) =>
        int.TryParse(e.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ? Math.Clamp(v, min, max) : fallback;

    static double Dbl(Entry e, double fallback, double min, double max) =>
        double.TryParse(e.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    void UpdateEnabled()
    {
        bool idle = !_app.Busy;
        _form.IsEnabled = idle;
        if (!idle) return;
        bool sim = _dscp.SelectedItem as string == SimulateItem;
        _proto.IsEnabled = _profile.IsEnabled = !sim;
        _streams.IsEnabled = !sim && _proto.SelectedIndex >= 1;
        _udpShare.IsEnabled = !sim && _proto.SelectedIndex == 2;
        _burstOn.IsEnabled = _burstOff.IsEnabled = !sim && _profile.SelectedIndex == (int)TrafficProfile.Bursty;
    }
}
