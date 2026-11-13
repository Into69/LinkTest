using System.Globalization;
using LinkTest.Core;
using LinkTest.Core.Tunnels;

namespace LinkTest.Client;

sealed class MainForm : Form
{
    readonly ClientSettings _settings = ClientSettings.Load();
    readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400 };

    // settings controls
    readonly TextBox _host = new() { PlaceholderText = "IP address or FQDN" };
    readonly NumericUpDown _port = Num(1, 65535, 0);
    readonly TextBox _key = new() { UseSystemPasswordChar = true, PlaceholderText = "(optional)" };
    readonly ComboBox _proto = Combo("UDP", "TCP");
    readonly NumericUpDown _up = Num(0, 10000, 3);
    readonly NumericUpDown _down = Num(0, 10000, 3);
    readonly NumericUpDown _size = Num(Protocol.HeaderSize, Protocol.MaxPacket, 0);
    readonly ComboBox _profile = Combo(Enum.GetNames<TrafficProfile>());
    readonly NumericUpDown _burstOn = Num(1, 60000, 0);
    readonly NumericUpDown _burstOff = Num(0, 60000, 0);
    readonly NumericUpDown _streams = Num(1, 16, 0);
    readonly ComboBox _dscp = Combo("0 - Best effort", "46 - EF (voice)", "34 - AF41 (video)", "26 - AF31", "18 - AF21",
                                     "10 - AF11", "48 - CS6 (network control)", "8 - CS1 (scavenger)");
    readonly NumericUpDown _duration = Num(0, 864000, 0);
    readonly NumericUpDown _loss = Num(0, 100, 1);
    readonly CheckedListBox _tunnelList = new() { CheckOnClick = true, Height = 150, IntegralHeight = false };
    readonly CheckBox _csv = new() { Text = "Write CSV log to:", AutoSize = true };
    readonly TextBox _csvFolder = new();
    readonly Button _start = new() { Text = "Start test", Height = 36, Dock = DockStyle.Fill };
    readonly Button _stop = new() { Text = "Stop", Height = 36, Dock = DockStyle.Fill, Enabled = false };
    readonly List<Control> _inputs = new();

    // live view
    readonly StatTile _tUp = new("Upload (client→server)");
    readonly StatTile _tDown = new("Download (server→client)");
    readonly StatTile _tRtt = new("Round-trip time");
    readonly StatTile _tJitter = new("Jitter ↑ / ↓");
    readonly StatTile _tLoss = new("Loss ↑ / ↓ (interval)");
    readonly StatTile _tGap = new("Longest gap ↑ / ↓");
    readonly StatTile _tRetrans = new("TCP retransmits ↑ / ↓");
    readonly StatTile _tMos = new("VoIP MOS (est.)");
    readonly StatTile _tTime = new("Elapsed");
    readonly LineChart _chartTput = new() { Title = "Throughput", Unit = "Mbps", Dock = DockStyle.Fill };
    readonly LineChart _chartLat = new() { Title = "Latency", Unit = "ms", Dock = DockStyle.Fill };
    readonly LineChart _chartLoss = new() { Title = "Packet loss", Unit = "%", Dock = DockStyle.Fill, MinYMax = 1 };
    readonly ListView _tunnelView = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = true };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.White };
    readonly ToolStripStatusLabel _status = new() { Text = "Idle", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    /// <summary>The icon embedded in the exe (from app.ico), reused for windows.</summary>
    internal static readonly Icon? AppIcon = LoadAppIcon();

    static Icon? LoadAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); }
        catch { return null; }
    }

    ClientSession? _session;
    CsvLogger? _csvLog;
    CancellationTokenSource? _connectCts;

    public MainForm()
    {
        // Controls are laid out in 96-DPI units; WinForms scales them when the layout resumes.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "LinkTest Client - link quality & tunnel stability";
        Icon = AppIcon;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1400, 880);
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 246, 248);
        BuildLayout();
        LoadSettingsToUi();
        ResumeLayout(false);
        PerformLayout();

        Load += (_, _) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            if (Width > wa.Width || Height > wa.Height)
            {
                Bounds = new Rectangle(wa.X, wa.Y, Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
                if (Width >= wa.Width - 10 || Height >= wa.Height - 10) WindowState = FormWindowState.Maximized;
            }
        };

        _timer.Tick += (_, _) => OnTick();
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += async (_, _) => await StopAsync();
        _proto.SelectedIndexChanged += (_, _) => OnProtocolChanged();
        _profile.SelectedIndexChanged += (_, _) => UpdateEnabledInputs();
        FormClosing += (_, _) => { SaveUiToSettings(); _session?.Dispose(); _csvLog?.Dispose(); };
    }

    // ------------------------------------------------------------------ layout

    void BuildLayout()
    {
        var left = new Panel { Dock = DockStyle.Left, Width = 340, AutoScroll = true, Padding = new Padding(10, 6, 10, 6), BackColor = Color.White };
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));

        Header(form, "Server");
        Row(form, "Server IP / FQDN", _host, "Address or DNS name of the machine running LinkTestServer.exe");
        Row(form, "Port", _port, "TCP and UDP port of the server (default 7447)");
        Row(form, "Key", _key, "Shared key, if the server was started with --key");

        Header(form, "Traffic");
        Row(form, "Protocol", _proto, "UDP shows real packet loss / reordering. TCP shows what an application sees: retransmissions, stalls and throughput collapse.");
        Row(form, "Upload / TX (Mbps)", _up, "Client → server payload rate. 0 = heartbeats only (monitoring mode).");
        Row(form, "Download / RX (Mbps)", _down, "Server → client payload rate. 0 = heartbeats only (monitoring mode).");
        Row(form, "Packet size (bytes)", _size, "UDP payload per datagram (keep ≤1472 to avoid IP fragmentation, ≤1400 for VPN paths) or TCP write size.");
        Row(form, "Traffic profile", _profile,
            "Constant: evenly paced fixed-size packets\nImix: 64/576/1500 B mix (7:4:1)\nBursty: on/off bursts with the same average rate\n" +
            "Random: Poisson arrivals, random sizes (web/office)\nVoip: 172 B every 20 ms per call (rate sets call count)\nVideo: 30 fps frame bursts with an I-frame each second");
        Row(form, "Burst on (ms)", _burstOn, "Bursty profile: transmit period");
        Row(form, "Burst off (ms)", _burstOff, "Bursty profile: silent period");
        Row(form, "TCP streams", _streams, "Parallel TCP connections (rate is split between them)");
        Row(form, "DSCP marking", _dscp, "QoS marking via Windows qWAVE. Requires running as Administrator; the network must also honour it.");
        Row(form, "Duration (s, 0 = ∞)", _duration, "Stop automatically after this many seconds. 0 runs until you press Stop - useful for long-term monitoring.");
        Row(form, "Artificial loss (%)", _loss, "Testing aid: the client randomly discards this percentage of packets in both directions, to see how the tunnels react.");

        Header(form, "VPN / tunnel simulations");
        var tunnelHelp = new Label
        {
            Text = "Each checked tunnel sends real keepalive and handshake probes with its own timers over this link. A tunnel drops only if the link loses enough of them.",
            AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 4),
        };
        form.Controls.Add(tunnelHelp); form.SetColumnSpan(tunnelHelp, 2);
        _tunnelList.Dock = DockStyle.Fill;
        form.Controls.Add(_tunnelList); form.SetColumnSpan(_tunnelList, 2);
        _inputs.Add(_tunnelList);
        var tb = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        tb.Controls.Add(SmallButton("Edit…", EditTunnel));
        tb.Controls.Add(SmallButton("Copy", CopyTunnel));
        tb.Controls.Add(SmallButton("Remove", RemoveTunnel));
        tb.Controls.Add(SmallButton("Defaults", ResetTunnels));
        form.Controls.Add(tb); form.SetColumnSpan(tb, 2);
        _tunnelList.DoubleClick += (_, _) => EditTunnel();

        Header(form, "Logging");
        form.Controls.Add(_csv); form.SetColumnSpan(_csv, 2);
        _inputs.Add(_csv);
        var csvRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _csvFolder.Dock = DockStyle.Fill;
        csvRow.Controls.Add(_csvFolder);
        var browse = SmallButton("…", BrowseCsv); browse.Width = 32;
        csvRow.Controls.Add(browse);
        form.Controls.Add(csvRow); form.SetColumnSpan(csvRow, 2);
        _inputs.Add(_csvFolder);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 2, Padding = new Padding(0, 8, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        _start.Font = new Font(Font, FontStyle.Bold);
        _start.BackColor = Color.FromArgb(0, 120, 212); _start.ForeColor = Color.White; _start.FlatStyle = FlatStyle.Flat;
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_stop);

        left.Controls.Add(buttons);
        left.Controls.Add(form);

        // Right side
        var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), WrapContents = true };
        foreach (var t in new[] { _tUp, _tDown, _tRtt, _tJitter, _tLoss, _tGap, _tRetrans, _tMos, _tTime }) tiles.Controls.Add(t);
        _tUp.Width = _tDown.Width = 175;

        _chartTput.AddSeries("Upload (server rx)", Color.FromArgb(0, 120, 212));
        _chartTput.AddSeries("Download (client rx)", Color.FromArgb(16, 137, 62));
        _chartTput.AddSeries("Client tx", Color.FromArgb(170, 170, 170));
        _chartLat.AddSeries("RTT", Color.FromArgb(202, 80, 16));
        _chartLat.AddSeries("RTT max", Color.FromArgb(240, 180, 140));
        _chartLat.AddSeries("Jitter ↑", Color.FromArgb(0, 120, 212));
        _chartLat.AddSeries("Jitter ↓", Color.FromArgb(16, 137, 62));
        _chartLoss.AddSeries("Loss ↑", Color.FromArgb(0, 120, 212));
        _chartLoss.AddSeries("Loss ↓", Color.FromArgb(196, 43, 28));

        var charts = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(4) };
        for (int i = 0; i < 3; i++) charts.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        charts.Controls.Add(_chartTput, 0, 0);
        charts.Controls.Add(_chartLat, 0, 1);
        charts.Controls.Add(_chartLoss, 0, 2);

        foreach (var (name, w) in new[] { ("Tunnel", 210), ("State", 150), ("Verdict", 210), ("Avail. %", 65), ("Drops", 50), ("Flaps", 50),
                                          ("Downtime", 70), ("Longest out", 80), ("Detect", 60), ("Recover", 60), ("Probe RTT", 70), ("Last event", 330) })
            _tunnelView.Columns.Add(name, (int)(w * DeviceDpi / 96f));   // column widths are not auto-scaled
        _tips.SetToolTip(_tunnelView, "Detect = avg time from last good probe to DOWN. Recover = avg outage duration until UP again.");

        var bottom = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        bottom.Panel1.Controls.Add(_tunnelView);
        bottom.Panel2.Controls.Add(_log);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(charts);
        split.Panel2.Controls.Add(bottom);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(split);
        right.Controls.Add(tiles);

        var strip = new StatusStrip();
        strip.Items.Add(_status);

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(strip);

        Load += (_, _) =>
        {
            split.SplitterDistance = (int)(split.Height * 0.58);
            bottom.SplitterDistance = (int)(bottom.Height * 0.5);
        };
    }

    void Header(TableLayoutPanel t, string text)
    {
        var l = new Label { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, 10f, FontStyle.Bold), ForeColor = Color.FromArgb(0, 90, 160), Margin = new Padding(0, 10, 0, 4) };
        t.Controls.Add(l);
        t.SetColumnSpan(l, 2);
    }

    void Row(TableLayoutPanel t, string label, Control c, string tip)
    {
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 3, 3) };
        c.Dock = DockStyle.Fill;
        c.Margin = new Padding(3, 3, 0, 3);
        t.Controls.Add(l);
        t.Controls.Add(c);
        _tips.SetToolTip(l, tip);
        _tips.SetToolTip(c, tip);
        _inputs.Add(c);
    }

    Button SmallButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 2, 4, 2) };
        b.Click += (_, _) => onClick();
        _inputs.Add(b);
        return b;
    }

    static NumericUpDown Num(decimal min, decimal max, int decimals) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, ThousandsSeparator = decimals == 0 && max > 9999, Increment = decimals > 0 ? 1 : 1 };

    static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        c.Items.AddRange(items);
        return c;
    }

    // ------------------------------------------------------------------ settings

    void LoadSettingsToUi()
    {
        var c = _settings.Config;
        _host.Text = _settings.Host;
        _port.Value = Math.Clamp(_settings.Port, 1, 65535);
        _key.Text = _settings.Key;
        _proto.SelectedIndex = c.Protocol == TransportKind.Tcp ? 1 : 0;
        _up.Value = (decimal)Math.Clamp(c.UpMbps, 0, 10000);
        _down.Value = (decimal)Math.Clamp(c.DownMbps, 0, 10000);
        _size.Value = Math.Clamp(c.PacketSize, Protocol.HeaderSize, Protocol.MaxPacket);
        _profile.SelectedIndex = (int)c.Profile;
        _burstOn.Value = Math.Clamp(c.BurstOnMs, 1, 60000);
        _burstOff.Value = Math.Clamp(c.BurstOffMs, 0, 60000);
        _streams.Value = Math.Clamp(c.Streams, 1, 16);
        _dscp.SelectedIndex = Math.Max(0, Enumerable.Range(0, _dscp.Items.Count).FirstOrDefault(i => ParseDscp(_dscp.Items[i]!.ToString()!) == c.Dscp));
        _duration.Value = Math.Clamp(c.DurationSec, 0, 864000);
        _loss.Value = (decimal)Math.Clamp(_settings.ArtificialLossPct, 0, 100);
        _csv.Checked = _settings.CsvEnabled;
        _csvFolder.Text = _settings.CsvFolder;
        RefreshTunnelList();
        UpdateEnabledInputs();
    }

    void SaveUiToSettings()
    {
        _settings.Host = _host.Text.Trim();
        _settings.Port = (int)_port.Value;
        _settings.Key = _key.Text;
        _settings.Config = ReadConfig();
        _settings.ArtificialLossPct = (double)_loss.Value;
        _settings.CsvEnabled = _csv.Checked;
        _settings.CsvFolder = _csvFolder.Text.Trim();
        for (int i = 0; i < _settings.Tunnels.Count && i < _tunnelList.Items.Count; i++)
            _settings.Tunnels[i].Enabled = _tunnelList.GetItemChecked(i);
        _settings.Save();
    }

    TestConfig ReadConfig() => new()
    {
        Protocol = _proto.SelectedIndex == 1 ? TransportKind.Tcp : TransportKind.Udp,
        UpMbps = (double)_up.Value,
        DownMbps = (double)_down.Value,
        PacketSize = (int)_size.Value,
        Profile = (TrafficProfile)Math.Max(0, _profile.SelectedIndex),
        BurstOnMs = (int)_burstOn.Value,
        BurstOffMs = (int)_burstOff.Value,
        Streams = (int)_streams.Value,
        Dscp = ParseDscp(_dscp.SelectedItem?.ToString() ?? "0"),
        DurationSec = (int)_duration.Value,
    };

    static int ParseDscp(string s) => int.TryParse(s.Split(' ')[0], out var v) ? v : 0;

    void OnProtocolChanged()
    {
        bool tcp = _proto.SelectedIndex == 1;
        if (tcp && _size.Value == 1400) _size.Value = 16000;
        else if (!tcp && _size.Value == 16000) _size.Value = 1400;
        UpdateEnabledInputs();
    }

    void UpdateEnabledInputs()
    {
        bool idle = _session == null;
        foreach (var c in _inputs) c.Enabled = idle;
        if (!idle) return;
        _streams.Enabled = _proto.SelectedIndex == 1;
        bool bursty = _profile.SelectedIndex == (int)TrafficProfile.Bursty;
        _burstOn.Enabled = _burstOff.Enabled = bursty;
        _size.Enabled = _profile.SelectedIndex != (int)TrafficProfile.Voip;
    }

    void RefreshTunnelList()
    {
        _tunnelList.Items.Clear();
        foreach (var t in _settings.Tunnels) _tunnelList.Items.Add(t.Name, t.Enabled);
    }

    void SyncTunnelChecks()
    {
        for (int i = 0; i < _settings.Tunnels.Count && i < _tunnelList.Items.Count; i++)
            _settings.Tunnels[i].Enabled = _tunnelList.GetItemChecked(i);
    }

    void EditTunnel()
    {
        int i = _tunnelList.SelectedIndex;
        if (i < 0 || _session != null) return;
        SyncTunnelChecks();
        using var f = new TunnelEditForm(_settings.Tunnels[i]);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Tunnels[i] = f.Profile;
            RefreshTunnelList();
            _tunnelList.SelectedIndex = i;
        }
    }

    void CopyTunnel()
    {
        int i = _tunnelList.SelectedIndex;
        if (i < 0) return;
        SyncTunnelChecks();
        var copy = _settings.Tunnels[i].Clone();
        copy.Name += " (copy)";
        _settings.Tunnels.Insert(i + 1, copy);
        RefreshTunnelList();
        _tunnelList.SelectedIndex = i + 1;
        EditTunnel();
    }

    void RemoveTunnel()
    {
        int i = _tunnelList.SelectedIndex;
        if (i < 0) return;
        SyncTunnelChecks();
        _settings.Tunnels.RemoveAt(i);
        RefreshTunnelList();
    }

    void ResetTunnels()
    {
        if (MessageBox.Show(this, "Replace all tunnel profiles with the built-in defaults?", "LinkTest", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _settings.Tunnels = TunnelPresets.CreateDefaults();
        RefreshTunnelList();
    }

    void BrowseCsv()
    {
        using var d = new FolderBrowserDialog { SelectedPath = _csvFolder.Text, Description = "Folder for CSV logs" };
        if (d.ShowDialog(this) == DialogResult.OK) { _csvFolder.Text = d.SelectedPath; _csv.Checked = true; }
    }

    // ------------------------------------------------------------------ run

    async Task StartAsync()
    {
        if (_session != null) return;
        string host = _host.Text.Trim();
        if (host.Length == 0)
        {
            MessageBox.Show(this, "Enter the server IP address or FQDN.", "LinkTest", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _host.Focus();
            return;
        }
        SaveUiToSettings();
        var cfg = ReadConfig();
        var tunnels = _settings.Tunnels.Where(t => t.Enabled).Select(t => t.Clone()).ToList();

        var session = new ClientSession();
        session.Log += m => Ui(() => AppendLog(m));
        session.Ended += r => Ui(() => OnEnded(session, r));
        _session = session;
        UpdateEnabledInputs();
        _start.Enabled = false;
        _stop.Enabled = true;
        _chartTput.Clear(); _chartLat.Clear(); _chartLoss.Clear();
        _tunnelView.Items.Clear();
        foreach (var t in new[] { _tUp, _tDown, _tRtt, _tJitter, _tLoss, _tGap, _tRetrans, _tMos, _tTime }) t.Set("–");
        _status.Text = $"Connecting to {host}…";

        _connectCts = new CancellationTokenSource();
        try
        {
            await session.StartAsync(host, (int)_port.Value, _key.Text, cfg, tunnels, (double)_loss.Value, _connectCts.Token);
        }
        catch (Exception e)
        {
            string msg = e is OperationCanceledException ? "Cancelled" : e.Message;
            AppendLog("Could not start: " + msg);
            session.Dispose();
            _session = null;
            _start.Enabled = true;
            _stop.Enabled = false;
            UpdateEnabledInputs();
            _status.Text = "Idle";
            if (e is not OperationCanceledException)
                MessageBox.Show(this, msg, "LinkTest - connection failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            _connectCts.Dispose();
            _connectCts = null;
        }

        if (_csv.Checked)
        {
            try
            {
                string path = Path.Combine(_csvFolder.Text.Trim(), $"LinkTest_{SafeName(host)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _csvLog = new CsvLogger(path);
                AppendLog("Logging to " + path);
            }
            catch (Exception e) { AppendLog("CSV log disabled: " + e.Message); }
        }

        foreach (var t in session.Tunnels) _tunnelView.Items.Add(new ListViewItem(new string[_tunnelView.Columns.Count]) { Text = t.Profile.Name });
        _status.Text = $"Connected to {session.ServerEndPoint} - {session.Config}";
        _timer.Start();
    }

    async Task StopAsync()
    {
        if (_connectCts != null) { _connectCts.Cancel(); return; }
        if (_session == null) return;
        _stop.Enabled = false;
        _status.Text = "Stopping…";
        await _session.StopAsync();
    }

    void OnEnded(ClientSession session, string reason)
    {
        if (_session != session) return;
        _timer.Stop();
        _csvLog?.Dispose();
        _csvLog = null;
        OnTickTunnels();   // final tunnel state
        AppendLog("Test ended: " + reason);
        session.Dispose();
        _session = null;
        _start.Enabled = true;
        _stop.Enabled = false;
        UpdateEnabledInputs();
        _status.Text = "Idle - " + reason;
    }

    void OnTick()
    {
        var session = _session;
        if (session == null || !session.IsRunning) return;
        var s = session.TakeSample();

        _tUp.Set(Mbps(s.UpMbps), s.UpReportStale ? $"no report for {Fmt(s.UpReportAgeSec, "0.0")} s" : $"sent {Mbps(s.TxMbps)}", s.UpReportStale ? Color.Firebrick : null);
        _tDown.Set(Mbps(s.DownMbps), double.IsFinite(s.ServerTxMbps) ? $"server sent {Mbps(s.ServerTxMbps)}" : "");
        _tRtt.Set(Ms(s.RttMs), $"min {Fmt(s.RttMinMs, "0.0")} / max {Fmt(s.RttMaxMs, "0.0")}", LevelColor(s.RttMs, 100, 250));
        _tJitter.Set($"{Fmt(s.UpJitterMs, "0.0")} / {Fmt(s.DownJitterMs, "0.0")}", "ms", LevelColor(Math.Max(Z(s.UpJitterMs), s.DownJitterMs), 10, 30));
        _tLoss.Set($"{Fmt(s.UpLossPct, "0.##")} / {Fmt(s.DownLossPct, "0.##")}",
            $"total {Fmt(s.UpLossTotalPct, "0.###")}% / {Fmt(s.DownLossTotalPct, "0.###")}%",
            LevelColor(Math.Max(Z(s.UpLossPct), s.DownLossPct), 0.5, 3));
        _tGap.Set($"{Fmt(s.UpMaxGapMs, "0")} / {Fmt(s.DownMaxGapMs, "0")}", "ms (last ~2 s)", LevelColor(Math.Max(Z(s.UpMaxGapMs), s.DownMaxGapMs), 250, 1000));
        _tRetrans.Set(session.Config.Protocol == TransportKind.Tcp ? $"{Kb(s.UpRetransBytes)} / {Kb(s.DownRetransBytes)}" : "n/a (UDP)",
            session.Config.Protocol == TransportKind.Tcp ? $"KB/s · RTOs {s.UpTimeoutsTotal} / {s.DownTimeoutsTotal}" : "");
        _tMos.Set(Fmt(s.Mos, "0.0"), double.IsFinite(s.Mos) ? MosText(s.Mos) : "", s.Mos >= 4 ? Color.ForestGreen : s.Mos >= 3.6 ? Color.DarkOrange : Color.Firebrick);
        var el = TimeSpan.FromSeconds(s.ElapsedSec);
        _tTime.Set($"{(int)el.TotalHours}:{el.Minutes:00}:{el.Seconds:00}", session.Config.DurationSec > 0 ? $"of {session.Config.DurationSec} s" : "until stopped");

        _chartTput.Push(s.UpMbps, s.DownMbps, s.TxMbps);
        _chartLat.Push(s.RttMs, s.RttMaxMs, s.UpJitterMs, s.DownJitterMs);
        _chartLoss.Push(s.UpLossPct, s.DownLossPct);

        OnTickTunnels();
        try { _csvLog?.Write(s, session.TunnelSummaryLine()); } catch { }

        if (session.Config.DurationSec > 0 && s.ElapsedSec >= session.Config.DurationSec)
            _ = session.StopAsync("Duration reached");
    }

    void OnTickTunnels()
    {
        var session = _session;
        if (session == null) return;
        long now = Clock.NowUs;
        _tunnelView.BeginUpdate();
        for (int i = 0; i < session.Tunnels.Count && i < _tunnelView.Items.Count; i++)
        {
            var st = session.Tunnels[i].GetStatus(now);
            var it = _tunnelView.Items[i];
            string[] cells =
            {
                st.Name, st.StateText, st.Verdict, st.AvailabilityPct.ToString("0.##"), st.Drops.ToString(), st.Flaps.ToString(),
                $"{st.DownSec:0.0} s", $"{st.LongestOutageSec:0.0} s", Fmt(st.AvgDetectSec, "0.0") + (double.IsFinite(st.AvgDetectSec) ? " s" : ""),
                Fmt(st.AvgRecoverSec, "0.0") + (double.IsFinite(st.AvgRecoverSec) ? " s" : ""), Fmt(st.LastRttMs, "0.0") + " ms", st.LastEvent,
            };
            for (int c = 0; c < cells.Length; c++)
            {
                if (c == 0) it.Text = cells[0];
                else if (it.SubItems.Count > c) it.SubItems[c].Text = cells[c];
                else it.SubItems.Add(cells[c]);
            }
            it.UseItemStyleForSubItems = true;
            it.BackColor = st.State switch
            {
                TunnelState.Up when st.Drops == 0 => Color.FromArgb(223, 246, 221),
                TunnelState.Up => Color.FromArgb(255, 244, 206),
                TunnelState.Down => Color.FromArgb(253, 231, 233),
                _ => Color.FromArgb(240, 240, 240),
            };
        }
        _tunnelView.EndUpdate();
    }

    // ------------------------------------------------------------------ helpers

    void AppendLog(string msg)
    {
        foreach (var line in msg.Replace("\r", "").Split('\n'))
            _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        if (_log.TextLength > 400_000) _log.Text = _log.Text[^200_000..];
    }

    void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    static string SafeName(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
    static double Z(double v) => double.IsFinite(v) ? v : 0;
    static string Fmt(double v, string f) => double.IsFinite(v) ? v.ToString(f, CultureInfo.CurrentCulture) : "–";
    static string Mbps(double v) => !double.IsFinite(v) ? "–" : v >= 100 ? $"{v:0} Mbps" : v >= 1 ? $"{v:0.0} Mbps" : $"{v * 1000:0} kbps";
    static string Ms(double v) => double.IsFinite(v) ? (v >= 10 ? $"{v:0} ms" : $"{v:0.0} ms") : "–";
    static string Kb(long bytes) => (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture);
    static Color? LevelColor(double v, double warn, double bad) => !double.IsFinite(v) ? null : v >= bad ? Color.Firebrick : v >= warn ? Color.DarkOrange : Color.ForestGreen;
    static string MosText(double m) => m >= 4.3 ? "excellent" : m >= 4.0 ? "good" : m >= 3.6 ? "fair" : m >= 3.1 ? "poor" : "bad";
}
