using System.Globalization;
using LinkTest.Core.Tunnels;

namespace LinkTest.Mobile;

/// <summary>Edits the timers of one tunnel simulation.</summary>
sealed class TunnelEditPage : ContentPage
{
    readonly TunnelProfile _p;
    readonly Action<TunnelProfile> _onSave;
    readonly Entry _name = new();
    readonly Picker _transport = new() { ItemsSource = new[] { "UDP", "TCP" } };
    readonly Dictionary<string, Entry> _num = new();
    readonly Label _detect = new() { FontSize = 12, TextColor = Ui.Muted };

    static readonly (string key, string label, string hint)[] Fields =
    {
        ("KeepaliveIntervalMs", "Keepalive interval (ms)", "Idle time after a successful exchange before the next keepalive / DPD probe."),
        ("ProbeTimeoutMs", "Probe timeout (ms)", "Wait for a reply before counting the probe as missed and retransmitting."),
        ("RetryBackoff", "Timeout backoff (×)", "Multiplier on the probe timeout after each miss (1 = fixed, 2 = doubling)."),
        ("MaxMissed", "Max missed probes", "Consecutive unanswered probes before the tunnel is declared DOWN."),
        ("KeepaliveBytes", "Keepalive size (bytes)", ""),
        ("HandshakeRoundTrips", "Handshake round trips", "Exchanges needed to bring the tunnel up (IKEv2 2, WireGuard 1, OpenVPN TLS ≈6)."),
        ("HandshakeTimeoutMs", "Handshake timeout (ms)", ""),
        ("HandshakeBackoff", "Handshake backoff (×)", ""),
        ("HandshakeRetries", "Handshake transmissions (0 = forever)", "Transmissions of one handshake message before the attempt is abandoned."),
        ("ReconnectDelayMs", "Reconnect delay (ms)", "Wait after a drop or failed attempt before a new handshake."),
        ("HandshakeBytes", "Handshake size (bytes)", "Large handshakes (certificates) suffer more from loss / small MTUs."),
        ("StableHoldSec", "Stable hold time (s)", "Dropping again sooner than this after recovering counts as a flap."),
    };

    public TunnelEditPage(TunnelProfile profile, Action<TunnelProfile> onSave)
    {
        _p = profile.Clone();
        _onSave = onSave;
        Title = "Edit tunnel";
        BackgroundColor = Ui.PageBg;

        var form = new VerticalStackLayout { Padding = new Thickness(12, 4, 12, 24), Spacing = 2 };
        form.Add(Ui.Header(profile.Name));
        if (!string.IsNullOrEmpty(profile.Notes)) form.Add(Ui.Hint(profile.Notes));
        Add(form, "Name", _name);
        _name.Text = _p.Name;
        Add(form, "Transport", _transport);
        _transport.SelectedIndex = _p.Transport == TunnelTransport.Tcp ? 1 : 0;
        foreach (var (key, label, hint) in Fields)
        {
            var e = new Entry { Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.End, Text = Get(key) };
            e.TextChanged += (_, _) => UpdateDetect();
            _num[key] = e;
            Add(form, label, e);
            if (hint.Length > 0) form.Add(Ui.Hint(hint));
        }
        form.Add(_detect);

        var save = new Button { Text = "Save", BackgroundColor = Ui.Accent, TextColor = Colors.White, Margin = new Thickness(0, 16, 0, 0) };
        save.Clicked += async (_, _) => { Apply(); _onSave(_p); await Navigation.PopAsync(); };
        var defaults = new Button { Text = "Restore preset values", Margin = new Thickness(0, 8, 0, 0) };
        defaults.Clicked += (_, _) =>
        {
            var preset = TunnelPresets.CreateDefaults().FirstOrDefault(t => t.Name == profile.Name);
            if (preset == null) return;
            foreach (var (key, _, _) in Fields) _num[key].Text = Get(key, preset);
            _transport.SelectedIndex = preset.Transport == TunnelTransport.Tcp ? 1 : 0;
        };
        form.Add(save);
        if (TunnelPresets.CreateDefaults().Any(t => t.Name == profile.Name)) form.Add(defaults);
        Content = new ScrollView { Content = form };
        UpdateDetect();
    }

    static void Add(VerticalStackLayout form, string label, View v)
    {
        form.Add(new Label { Text = label, FontSize = 13, TextColor = Ui.Muted, Margin = new Thickness(0, 6, 0, 0) });
        form.Add(v);
    }

    string Get(string key, TunnelProfile? p = null)
    {
        var v = typeof(TunnelProfile).GetProperty(key)!.GetValue(p ?? _p)!;
        return Convert.ToString(v, CultureInfo.CurrentCulture)!;
    }

    void Apply()
    {
        _p.Name = string.IsNullOrWhiteSpace(_name.Text) ? _p.Name : _name.Text.Trim();
        _p.Transport = _transport.SelectedIndex == 1 ? TunnelTransport.Tcp : TunnelTransport.Udp;
        foreach (var (key, _, _) in Fields)
        {
            var prop = typeof(TunnelProfile).GetProperty(key)!;
            if (!double.TryParse(_num[key].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d)) continue;
            prop.SetValue(_p, prop.PropertyType == typeof(int) ? (object)(int)Math.Round(d) : d);
        }
        _p.Normalize();
    }

    void UpdateDetect()
    {
        var copy = _p.Clone();
        foreach (var (key, _, _) in Fields)
        {
            var prop = typeof(TunnelProfile).GetProperty(key)!;
            if (double.TryParse(_num[key].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d))
                prop.SetValue(copy, prop.PropertyType == typeof(int) ? (object)(int)Math.Round(d) : d);
        }
        copy.Normalize();
        _detect.Text = $"With these timers a dead link is detected after about {copy.DetectionSec:0.#} s (plus up to one keepalive interval).";
    }
}
