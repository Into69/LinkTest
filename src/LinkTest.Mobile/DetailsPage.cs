using LinkTest.Core;
using LinkTest.Core.Tunnels;

namespace LinkTest.Mobile;

/// <summary>VPN tunnel simulations, per traffic class figures and connection / IP details.</summary>
sealed class DetailsPage : ContentPage
{
    readonly AppController _app = AppController.Instance;
    readonly VerticalStackLayout _tunnels = new() { Spacing = 4 };
    readonly VerticalStackLayout _classes = new() { Spacing = 4 };
    readonly VerticalStackLayout _conn = new() { Spacing = 2 };
    readonly Label _mtu = new() { FontSize = 12 };
    readonly Label _trace = new() { FontFamily = "monospace", FontSize = 10, LineBreakMode = LineBreakMode.NoWrap };

    public DetailsPage()
    {
        Title = "Details";
        BackgroundColor = Ui.PageBg;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(10, 0, 10, 20),
                Children =
                {
                    Ui.Header("VPN tunnels"), _tunnels,
                    Ui.Header("Traffic classes"), _classes,
                    Ui.Header("Connection"), Ui.Card(_conn),
                    Ui.Header("Path MTU"), Ui.Card(_mtu),
                    Ui.Header("Traceroute"), Ui.Card(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _trace }),
                },
            },
        };
        _app.Sampled += _ => Refresh();
        _app.StateChanged += Refresh;
        _app.ConnectionChanged += ShowConnection;
        _app.PathChanged += ShowPath;
        ShowPath();
        Refresh();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    void Refresh()
    {
        RefreshTunnels();
        RefreshClasses(_app.LastSample);
        if (_app.Connection != null) ShowConnection(_app.Connection);
        else if (_conn.Count == 0) _conn.Add(Ui.Hint("Start a test to see addresses, NAT and public-IP details."));
    }

    void RefreshTunnels()
    {
        var list = _app.TunnelStatuses();
        if (list.Count == 0)
        {
            if (_tunnels.Count != 1 || _tunnels[0] is not Label)
            {
                _tunnels.Clear();
                _tunnels.Add(Ui.Hint("No tunnel simulations running."));
            }
            return;
        }
        while (_tunnels.Count < list.Count || (_tunnels.Count > 0 && _tunnels[0] is Label)) { if (_tunnels.Count > 0 && _tunnels[0] is Label) _tunnels.Clear(); else _tunnels.Add(new TunnelCard()); }
        while (_tunnels.Count > list.Count) _tunnels.RemoveAt(_tunnels.Count - 1);
        for (int i = 0; i < list.Count; i++) ((TunnelCard)_tunnels[i]).Set(list[i]);
    }

    void RefreshClasses(StatSample? s)
    {
        if (s == null || s.Classes.Count == 0)
        {
            if (_classes.Count != 1 || _classes[0] is not Label)
            {
                _classes.Clear();
                _classes.Add(Ui.Hint("Per-class figures appear while a test runs."));
            }
            return;
        }
        if (_classes.Count != s.Classes.Count || _classes[0] is Label)
        {
            _classes.Clear();
            foreach (var _ in s.Classes) _classes.Add(new ClassCard());
        }
        for (int i = 0; i < s.Classes.Count; i++) ((ClassCard)_classes[i]).Set(s.Classes[i]);
    }

    void ShowPath()
    {
        var m = _app.Mtu;
        if (m == null) _mtu.Text = _app.Busy ? (_app.Settings.MeasureMtu ? "Measuring…" : "Off (Settings)") : "Measured when a test starts.";
        else
        {
            var sb = new System.Text.StringBuilder(m.Summary);
            if (m.Ok)
            {
                sb.Append("\n\nRecommended tunnel MTU / TCP MSS:");
                foreach (var v in VpnOverhead.All) sb.Append($"\n• {v.Name}: {v.TunnelMtu(m.PathMtu, m.Ipv6)} / {v.TcpMss(m.PathMtu, m.Ipv6)}");
            }
            _mtu.Text = sb.ToString();
        }
        var traces = _app.Traces;
        _trace.Text = traces.Count == 0
            ? (_app.Settings.AutoTraceroute ? "Runs at the start and when loss, stalls or tunnel drops appear." : "Off (Settings)")
            : string.Join("\n", traces.Reverse().Select(t => t.Format())) + "\nLoss on one hop that does not continue is usually ICMP rate limiting.";
    }

    void ShowConnection(ConnectionInfo c)
    {
        _conn.Clear();
        void Row(string k, string v) => _conn.Add(new Label
        {
            FormattedText = new FormattedString { Spans = { new Span { Text = k + ": ", TextColor = Ui.Muted, FontSize = 13 }, new Span { Text = v, FontSize = 13 } } },
        });
        Row("Server", c.Server);
        Row("This device", c.LocalAddress);
        Row("Seen by server", c.SeenByServer ?? "(server older than 1.4)");
        Row("NAT", c.NatStatus);
        if (c.SeenByServerInfo is IpInfo seen && (c.PublicInfo == null || c.PublicInfo.Ip != seen.Ip)) Row("Seen-by-server details", seen.Summary);
        if (c.PublicInfo is IpInfo p)
        {
            Row("Public IP", p.Ip + (p.IsPublic ? "" : $" ({p.Scope})"));
            Row("Reverse DNS", p.Hostname ?? "(none)");
            Row("ISP / ASN", p.Org ?? "-");
            Row("Location (approx.)", p.Location.Length > 0 ? p.Location : "-");
            Row("Timezone", p.Timezone ?? "-");
        }
        Row("Lookup", !_app.Settings.IpLookup ? "online lookup disabled" : !c.LookupComplete ? "looking up…"
            : c.PublicInfo?.Error != null ? $"{IpLookup.Service}: {c.PublicInfo.Error}" : $"via {IpLookup.Service}");
    }

    sealed class TunnelCard : Border
    {
        readonly Label _name = new() { FontAttributes = FontAttributes.Bold, FontSize = 14 };
        readonly Label _state = new() { FontSize = 13 };
        readonly Label _stats = new() { FontSize = 12, TextColor = Ui.Muted };
        readonly Label _event = new() { FontSize = 11, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.TailTruncation };

        public TunnelCard()
        {
            Stroke = Color.FromArgb("#DDDDDD");
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 };
            Padding = new Thickness(10, 6);
            Content = new VerticalStackLayout { Spacing = 1, Children = { _name, _state, _stats, _event } };
        }

        public void Set(TunnelStatus st)
        {
            _name.Text = st.Name;
            _state.Text = $"{st.StateText} · {st.Verdict}";
            _stats.Text = $"avail {st.AvailabilityPct:0.##}% · drops {st.Drops} · flaps {st.Flaps} · down {st.DownSec:0.0}s · longest {st.LongestOutageSec:0.0}s · probe {Ui.Fmt(st.LastRttMs, "0.0")} ms";
            _event.Text = st.LastEvent;
            BackgroundColor = st.State switch
            {
                TunnelState.Up when st.Drops == 0 => Ui.GoodBg,
                TunnelState.Up => Ui.WarnBg,
                TunnelState.Down => Ui.BadBg,
                _ => Color.FromArgb("#F0F0F0"),
            };
        }
    }

    sealed class ClassCard : Border
    {
        readonly Label _name = new() { FontAttributes = FontAttributes.Bold, FontSize = 14 };
        readonly Label _line1 = new() { FontSize = 12 };
        readonly Label _line2 = new() { FontSize = 12, TextColor = Ui.Muted };

        public ClassCard()
        {
            Stroke = Color.FromArgb("#DDDDDD");
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 };
            Padding = new Thickness(10, 6);
            Content = new VerticalStackLayout { Spacing = 1, Children = { _name, _line1, _line2 } };
        }

        public void Set(ClassStat k)
        {
            bool udp = k.Transport == TransportKind.Udp;
            _name.Text = $"{k.Name}  ·  {DscpNames.Name(k.Dscp)}  ·  {(udp ? "UDP" : "TCP")} {k.SharePct:0.#}%";
            _line1.Text = $"↑ {Ui.Fmt(k.UpMbps, "0.00")} Mbps" + (udp ? $", loss {Ui.Fmt(k.UpLossPct, "0.##")}%" : "") +
                          $"   ↓ {Ui.Fmt(k.DownMbps, "0.00")} Mbps" + (udp ? $", loss {Ui.Fmt(k.DownLossPct, "0.##")}%" : "");
            _line2.Text = $"jitter {Ui.Fmt(k.UpJitterMs, "0.00")} / {Ui.Fmt(k.DownJitterMs, "0.00")} ms · RTT {Ui.Ms(k.RttMs)}" +
                          (udp ? $" · lost {k.UpLostTotal} / {k.DownLostTotal}" : $" · retrans {k.UpRetransBytes / 1024.0:0.#} / {k.DownRetransBytes / 1024.0:0.#} KB");
            double loss = Math.Max(Ui.Z(k.UpLossPct), Ui.Z(k.DownLossPct));
            BackgroundColor = !udp ? Colors.White : loss >= 3 ? Ui.BadBg : loss >= 0.5 ? Ui.WarnBg : Ui.GoodBg;
        }
    }
}
