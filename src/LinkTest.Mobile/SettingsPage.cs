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
    readonly Switch _keepOn = new(), _updates = new();
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

        _form.Add(Ui.Header("Traffic"));
        Row("Protocol", _proto);
        Row("UDP share (%) - mixed mode", _udpShare);
        Row("Upload / TX (Mbps)", _up);
        Row("Download / RX (Mbps)", _down);
        Row("Packet size (bytes)", _size);
        Row("Traffic profile", _profile);
        Row("Burst on / off (ms) - bursty profile", BurstRow());
        Row("TCP streams", _streams);
        Row("DSCP marking", _dscp);
        _form.Add(Ui.Hint("On Android DSCP marking needs no special permission. \"Simulate\" runs a 10-class enterprise mix (voice EF, video AF41 … scavenger CS1) - see the Details tab while running."));
        Row("Duration (s, 0 = until stopped)", _duration);

        _form.Add(Ui.Header("VPN / tunnel simulations"));
        _form.Add(Ui.Hint("Each checked tunnel sends real keepalive and handshake probes with its own timers; it drops only if the link loses enough of them."));
        for (int i = 0; i < st.Tunnels.Count; i++)
        {
            var box = new CheckBox { IsChecked = st.Tunnels[i].Enabled, Color = Ui.Accent };
            _tunnels.Add((box, i));
            _form.Add(new HorizontalStackLayout { Children = { box, new Label { Text = st.Tunnels[i].Name, VerticalOptions = LayoutOptions.Center, FontSize = 14 } } });
        }

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
