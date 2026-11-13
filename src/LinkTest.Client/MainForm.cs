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
    readonly ComboBox _proto = Combo("UDP", "TCP", "Mixed (TCP + UDP)");
    readonly NumericUpDown _udpShare = Num(0, 100, 0);
    readonly NumericUpDown _up = Num(0, 10000, 3);
    readonly NumericUpDown _down = Num(0, 10000, 3);
    readonly NumericUpDown _size = Num(Protocol.HeaderSize, Protocol.MaxPacket, 0);
    readonly ComboBox _profile = Combo(Enum.GetNames<TrafficProfile>());
    readonly NumericUpDown _burstOn = Num(1, 60000, 0);
    readonly NumericUpDown _burstOff = Num(0, 60000, 0);
    readonly NumericUpDown _streams = Num(1, 16, 0);
    const string DscpSimulateItem = "Simulate (enterprise mix)";
    readonly ComboBox _dscp = Combo("0 - Best effort", "46 - EF (voice)", "34 - AF41 (video)", "26 - AF31", "18 - AF21",
                                     "10 - AF11", "48 - CS6 (network control)", "8 - CS1 (scavenger)", DscpSimulateItem);
    readonly NumericUpDown _duration = Num(0, 864000, 0);
    readonly NumericUpDown _loss = Num(0, 100, 1);
    readonly CheckedListBox _tunnelList = new() { CheckOnClick = true, Height = 150, IntegralHeight = false };
    readonly CheckBox _csv = new() { Text = "Write CSV log to:", AutoSize = true };
    readonly TextBox _csvFolder = new();
    readonly Button _start = new() { Text = "Start test", Height = 36, Dock = DockStyle.Fill };
    readonly Button _stop = new() { Text = "Stop", Height = 36, Dock = DockStyle.Fill, Enabled = false };
    readonly Button _history = new() { Text = "History", Height = 36, Dock = DockStyle.Fill };
    readonly Button _pdf = new() { Text = "PDF report…", Height = 36, Dock = DockStyle.Fill };
    readonly ToolStripStatusLabel _reportLink = new() { IsLink = true, Visible = false, Text = "Open report" };
    ResultStore Results => new(_settings.ResultsFolder);
    TestRecord? _lastRecord;
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
    readonly StatTile _tBloat = new("Bufferbloat");
    readonly TextBox _pathView = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.White };
    readonly CheckBox _mtu = new() { Text = "Measure path MTU when a test starts", AutoSize = true };
    readonly CheckBox _trace = new() { Text = "Traceroute at start and when trouble appears", AutoSize = true };
    readonly NumericUpDown _baseline = Num(0, 60, 0);
    readonly LineChart _chartTput = new() { Title = "Throughput", Unit = "Mbps", Dock = DockStyle.Fill };
    readonly LineChart _chartLat = new() { Title = "Latency", Unit = "ms", Dock = DockStyle.Fill };
    readonly LineChart _chartLoss = new() { Title = "Packet loss", Unit = "%", Dock = DockStyle.Fill, MinYMax = 1 };
    readonly ListView _tunnelView = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = true };
    readonly ListView _statsView = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true };
    readonly ListView _classView = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = true };
    readonly TabControl _bottomTabs = new() { Dock = DockStyle.Fill };
    readonly ListView _connView = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly CheckBox _ipLookup = new() { Text = "Look up my public IP details (ipinfo.io)", AutoSize = true };
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
        Text = $"LinkTest Client v{Updater.CurrentVersion} - link quality & tunnel stability";
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
        _dscp.SelectedIndexChanged += (_, _) => UpdateEnabledInputs();
        _loss.ValueChanged += (_, _) =>
        {
            if (_session is { IsRunning: true } s && Math.Abs(s.ArtificialLossPct - (double)_loss.Value) > 1e-9)
            {
                s.ArtificialLossPct = (double)_loss.Value;
                AppendLog($"Artificial loss changed to {_loss.Value}%");
            }
        };
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
        form.Controls.Add(_ipLookup); form.SetColumnSpan(_ipLookup, 2);
        _inputs.Add(_ipLookup);
        _tips.SetToolTip(_ipLookup, "When a test starts, look up this computer's public IP address, ISP (ASN), approximate location and reverse DNS via ipinfo.io. " +
            "Shown on the Connection tab. Private / LAN addresses are never sent out. The server does the same lookup of the client's address.");

        Header(form, "Traffic");
        Row(form, "Protocol", _proto, "UDP shows real packet loss / reordering. TCP shows what an application sees: retransmissions, stalls and throughput collapse.\n" +
            "Mixed runs both at once, like real traffic (web/file transfer over TCP alongside voice/video over UDP); loss, jitter and RTT figures then come from the UDP part.");
        Row(form, "UDP share (%)", _udpShare, "Mixed mode: percentage of each direction's rate sent as UDP; the rest goes over the TCP streams.");
        Row(form, "Upload / TX (Mbps)", _up, "Client → server payload rate. 0 = heartbeats only (monitoring mode).");
        Row(form, "Download / RX (Mbps)", _down, "Server → client payload rate. 0 = heartbeats only (monitoring mode).");
        Row(form, "Packet size (bytes)", _size, "UDP payload per datagram (keep ≤1472 to avoid IP fragmentation, ≤1400 for VPN paths) or TCP write size.");
        Row(form, "Traffic profile", _profile,
            "Constant: evenly paced fixed-size packets\nImix: 64/576/1500 B mix (7:4:1)\nBursty: on/off bursts with the same average rate\n" +
            "Random: Poisson arrivals, random sizes (web/office)\nVoip: 172 B every 20 ms per call (rate sets call count)\nVideo: 30 fps frame bursts with an I-frame each second");
        Row(form, "Burst on (ms)", _burstOn, "Bursty profile: transmit period");
        Row(form, "Burst off (ms)", _burstOff, "Bursty profile: silent period");
        Row(form, "TCP streams", _streams, "Parallel TCP connections (rate is split between them)");
        Row(form, "DSCP marking", _dscp, "QoS marking via Windows qWAVE. Requires running as Administrator, and only works between two machines (not to this PC itself).\n\n" +
            "Simulate: runs a typical enterprise mix of 10 classes at once, each with its own DSCP, transport, traffic pattern and statistics:\n" +
            "Voice EF 10% · Video conferencing AF41 18% · Network control CS6 2% · OAM CS2 1% (UDP)\n" +
            "Call signaling CS3 2% · Streaming video AF31 12% · Transactional AF21 12% · Bulk AF11 12% · Best effort 30% · Scavenger CS1 1% (TCP)\n" +
            "Protocol, profile, UDP share and TCP streams are set per class in this mode. See the Traffic classes tab while running.");
        Row(form, "Duration (s, 0 = ∞)", _duration, "Stop automatically after this many seconds. 0 runs until you press Stop - useful for long-term monitoring.");
        Row(form, "Artificial loss (%)", _loss, "Testing aid: the client randomly discards this percentage of UDP packets in both directions, to see how the tunnels react.\n" +
            "Can be changed while a test is running - e.g. raise it to watch tunnels drop, then lower it to see whether they recover.");

        Header(form, "Diagnostics");
        Row(form, "Idle baseline (s)", _baseline, "Seconds at the start with no load, to measure the idle round-trip time for the bufferbloat grade. 0 disables the grade.");
        form.Controls.Add(_mtu); form.SetColumnSpan(_mtu, 2); _inputs.Add(_mtu);
        form.Controls.Add(_trace); form.SetColumnSpan(_trace, 2); _inputs.Add(_trace);
        _tips.SetToolTip(_mtu, "Finds the largest packet that crosses the path unfragmented in each direction and recommends tunnel MTUs (needs a 1.6+ server). Results on the Path tab.");
        _tips.SetToolTip(_trace, "Traces the route to the server at the start, and again (at most once a minute) when loss of 2% or more, a stall of 1 s or more, or a tunnel drop appears. Results on the Path tab.");

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

        left.Controls.Add(form);

        // Start / Stop live in their own panel under the scrolling settings, so they are always visible.
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _start.Font = new Font(Font, FontStyle.Bold);
        _start.BackColor = Color.FromArgb(0, 120, 212); _start.ForeColor = Color.White; _start.FlatStyle = FlatStyle.Flat;
        _start.EnabledChanged += (_, _) => _start.BackColor = _start.Enabled ? Color.FromArgb(0, 120, 212) : Color.FromArgb(200, 205, 212);
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_stop);
        buttons.Controls.Add(_history);
        buttons.Controls.Add(_pdf);
        _history.Click += (_, _) => { using var h = new HistoryForm(Results); h.ShowDialog(this); };
        _tips.SetToolTip(_history, "Saved test results: open or export the HTML or PDF report, export CSV.");
        _pdf.Click += (_, _) => SavePdfReport();
        _tips.SetToolTip(_pdf, "PDF report on how well the link carried each type of simulated traffic, for the last finished test " +
                               "(any saved test: History). Available when no test is running.");
        // Only between tests: Start is enabled exactly when no test is running (or connecting) and no update is installing.
        _start.EnabledChanged += (_, _) => _pdf.Enabled = _start.Enabled;
        var actionBar = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(10, 8, 10, 8), BackColor = Color.FromArgb(235, 238, 242) };
        actionBar.Controls.Add(buttons);

        var leftColumn = new Panel { Dock = DockStyle.Left, Width = 340 };
        left.Dock = DockStyle.Fill;
        leftColumn.Controls.Add(left);
        leftColumn.Controls.Add(actionBar);

        // Right side
        var tiles = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), WrapContents = true };
        foreach (var t in new[] { _tUp, _tDown, _tRtt, _tJitter, _tLoss, _tGap, _tRetrans, _tMos, _tBloat, _tTime }) tiles.Controls.Add(t);
        _tUp.Width = _tDown.Width = 175;

        _chartTput.AddSeries("Upload (server rx)", Color.FromArgb(0, 120, 212));
        _chartTput.AddSeries("Download (client rx)", Color.FromArgb(16, 137, 62));
        _chartTput.AddSeries("Client tx", Color.FromArgb(170, 170, 170));
        _chartLat.AddSeries("RTT", Color.FromArgb(202, 80, 16));
        _chartLat.AddSeries("RTT max", Color.FromArgb(240, 180, 140));
        _chartLat.AddSeries("Jitter ↑", Color.FromArgb(0, 120, 212));
        _chartLat.AddSeries("Jitter ↓", Color.FromArgb(16, 137, 62));
        _chartLat.AddSeries("RTT (TCP)", Color.FromArgb(135, 100, 184));
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

        foreach (var (name, w) in new[] { ("Class", 170), ("DSCP", 85), ("Proto", 50), ("Share", 50), ("Up Mbps", 70), ("Up loss %", 70), ("Up jitter", 70),
                                          ("Down Mbps", 75), ("Down loss %", 80), ("Down jitter", 75), ("RTT", 65), ("Lost ↑/↓", 90), ("TCP retrans KB ↑/↓", 120) })
            _classView.Columns.Add(name, (int)(w * DeviceDpi / 96f));
        _tips.SetToolTip(_classView, "Per traffic class. With DSCP marking honoured by the network, high-priority classes (EF, AF4x, CS6) should keep low loss and jitter under congestion while best effort / scavenger degrade first.");

        var tunnelsTab = new TabPage("VPN tunnels");
        tunnelsTab.Controls.Add(_tunnelView);
        var classesTab = new TabPage("Traffic classes");
        classesTab.Controls.Add(_classView);
        _connView.Columns.Add("Item", (int)(190 * DeviceDpi / 96f));
        _connView.Columns.Add("Value", (int)(720 * DeviceDpi / 96f));
        var statsTab = new TabPage("Statistics");
        statsTab.Controls.Add(_statsView);
        _tips.SetToolTip(_statsView, "Cumulative since the start. Upload columns are measured by the server and sent to the client; download columns are measured here. TCP rows come from the sending side's kernel.");
        var connTab = new TabPage("Connection");
        connTab.Controls.Add(_connView);
        _bottomTabs.TabPages.Add(tunnelsTab);
        _bottomTabs.TabPages.Add(classesTab);
        _bottomTabs.TabPages.Add(statsTab);
        _bottomTabs.TabPages.Add(connTab);
        var pathTab = new TabPage("Path (MTU / traceroute)");
        pathTab.Controls.Add(_pathView);
        _bottomTabs.TabPages.Add(pathTab);

        var bottom = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        bottom.Panel1.Controls.Add(_bottomTabs);
        bottom.Panel2.Controls.Add(_log);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(charts);
        split.Panel2.Controls.Add(bottom);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(split);
        right.Controls.Add(tiles);

        var strip = new StatusStrip();
        strip.Items.Add(_status);
        strip.Items.Add(_reportLink);
        _reportLink.Click += (_, _) =>
        {
            if (_lastRecord != null && HistoryForm.OpenReport(_lastRecord) is { } err)
                MessageBox.Show(this, "Could not open the report:\n" + err, "LinkTest", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        strip.Items.Add(BuildVersionMenu());

        Controls.Add(right);
        Controls.Add(leftColumn);
        Controls.Add(strip);
        Controls.Add(BuildUpdateBanner());

        InitSplit(this, split, 0.58);
        InitSplit(this, bottom, 0.5);
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

    /// <summary>
    /// Sets a splitter to a proportion of its height once it has a real size. Never throws: an exception in
    /// a Load handler is silently swallowed by WinForms and skips the remaining Load handlers (e.g. when
    /// the window starts minimized and has no height yet).
    /// </summary>
    static void InitSplit(Form form, SplitContainer split, double ratio)
    {
        bool done = false;
        void Apply()
        {
            if (done || split.Height < 100) return;
            try
            {
                int max = split.Height - split.Panel2MinSize - split.SplitterWidth;
                split.SplitterDistance = Math.Clamp((int)(split.Height * ratio), split.Panel1MinSize, Math.Max(split.Panel1MinSize, max));
                done = true;
            }
            catch { }
        }
        form.Load += (_, _) => Apply();
        split.SizeChanged += (_, _) => Apply();
    }

    static NumericUpDown Num(decimal min, decimal max, int decimals) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, ThousandsSeparator = false, Increment = 1 };

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
        _proto.SelectedIndex = Math.Clamp((int)c.Protocol, 0, 2);
        _udpShare.Value = (decimal)Math.Clamp(c.UdpSharePct, 0, 100);
        _up.Value = (decimal)Math.Clamp(c.UpMbps, 0, 10000);
        _down.Value = (decimal)Math.Clamp(c.DownMbps, 0, 10000);
        _size.Value = Math.Clamp(c.PacketSize, Protocol.HeaderSize, Protocol.MaxPacket);
        _profile.SelectedIndex = (int)c.Profile;
        _burstOn.Value = Math.Clamp(c.BurstOnMs, 1, 60000);
        _burstOff.Value = Math.Clamp(c.BurstOffMs, 0, 60000);
        _streams.Value = Math.Clamp(c.Streams, 1, 16);
        _dscp.SelectedIndex = c.DscpSimulate ? _dscp.Items.IndexOf(DscpSimulateItem)
            : Math.Max(0, Enumerable.Range(0, _dscp.Items.Count).FirstOrDefault(i => ParseDscp(_dscp.Items[i]!.ToString()!) == c.Dscp));
        _duration.Value = Math.Clamp(c.DurationSec, 0, 864000);
        _loss.Value = (decimal)Math.Clamp(_settings.ArtificialLossPct, 0, 100);
        _csv.Checked = _settings.CsvEnabled;
        _ipLookup.Checked = _settings.IpLookup;
        _mtu.Checked = _settings.MeasureMtu;
        _trace.Checked = _settings.AutoTraceroute;
        _baseline.Value = Math.Clamp(c.BaselineSec, 0, 60);
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
        _settings.IpLookup = _ipLookup.Checked;
        _settings.MeasureMtu = _mtu.Checked;
        _settings.AutoTraceroute = _trace.Checked;
        _settings.CsvFolder = _csvFolder.Text.Trim();
        for (int i = 0; i < _settings.Tunnels.Count && i < _tunnelList.Items.Count; i++)
            _settings.Tunnels[i].Enabled = _tunnelList.GetItemChecked(i);
        _settings.Save();
    }

    TestConfig ReadConfig() => new()
    {
        Protocol = (TransportKind)Math.Max(0, _proto.SelectedIndex),
        UdpSharePct = (double)_udpShare.Value,
        UpMbps = (double)_up.Value,
        DownMbps = (double)_down.Value,
        PacketSize = (int)_size.Value,
        Profile = (TrafficProfile)Math.Max(0, _profile.SelectedIndex),
        BurstOnMs = (int)_burstOn.Value,
        BurstOffMs = (int)_burstOff.Value,
        Streams = (int)_streams.Value,
        Dscp = DscpSimulateSelected ? 0 : ParseDscp(_dscp.SelectedItem?.ToString() ?? "0"),
        DscpSimulate = DscpSimulateSelected,
        DurationSec = (int)_duration.Value,
        BaselineSec = (int)_baseline.Value,
    };

    bool DscpSimulateSelected => _dscp.SelectedItem as string == DscpSimulateItem;

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
        if (!idle)
        {
            _loss.Enabled = true;   // adjustable while a test runs
            return;
        }
        bool sim = DscpSimulateSelected;   // classes define transport / profile / streams
        _proto.Enabled = _profile.Enabled = !sim;
        _streams.Enabled = !sim && _proto.SelectedIndex >= 1;
        _udpShare.Enabled = !sim && _proto.SelectedIndex == 2;
        bool bursty = !sim && _profile.SelectedIndex == (int)TrafficProfile.Bursty;
        _burstOn.Enabled = _burstOff.Enabled = bursty;
        _size.Enabled = sim || _profile.SelectedIndex != (int)TrafficProfile.Voip;
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
        session.ConnectionChanged += c => Ui(() => ShowConnection(c));
        session.MtuMeasured += _ => Ui(() => ShowPath(session));
        session.TraceCompleted += _ => Ui(() => ShowPath(session));
        session.MeasureMtu = _mtu.Checked;
        session.AutoTraceroute = _trace.Checked;
        _session = session;
        UpdateEnabledInputs();
        _start.Enabled = false;
        _stop.Enabled = true;
        _chartTput.Clear(); _chartLat.Clear(); _chartLoss.Clear();
        _tunnelView.Items.Clear();
        _classView.Items.Clear();
        ShowDetail(null);
        _connView.Items.Clear();
        foreach (var t in new[] { _tUp, _tDown, _tRtt, _tJitter, _tLoss, _tGap, _tRetrans, _tMos, _tBloat, _tTime }) t.Set("–");
        _pathView.Text = _mtu.Checked || _trace.Checked ? "Waiting for the test to start…" : "Path MTU and traceroute are turned off (Diagnostics settings).";
        _status.Text = $"Connecting to {host}…";

        _connectCts = new CancellationTokenSource();
        try
        {
            await session.StartAsync(host, (int)_port.Value, _key.Text, cfg, tunnels, (double)_loss.Value, _connectCts.Token, _ipLookup.Checked);
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
        foreach (var k in session.Classes) _classView.Items.Add(new ListViewItem(new string[_classView.Columns.Count]) { Text = k.Name });
        if (session.Classes.Count > 2) _bottomTabs.SelectedIndex = 1;
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
        if (session.Record?.Detail is { } finalDetail) ShowDetail(finalDetail);
        AppendLog("Test ended: " + reason);
        if (session.Record is { } rec)
        {
            try
            {
                string path = Results.Save(rec);
                _lastRecord = rec;
                _reportLink.Text = $"Open report ({rec.StartedAt:HH:mm})";
                _reportLink.Visible = true;
                AppendLog($"Result saved to {path} - open it from History or the status bar.");
            }
            catch (Exception e) { AppendLog("Could not save the result: " + e.Message); }
        }
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

        _tUp.Set(Mbps(s.UpMbps), s.UpReportStale ? $"no report for {Fmt(s.UpReportAgeSec, "0.0")} s"
            : s.Mixed ? $"UDP {Mbps(s.UpMbpsUdp)} · TCP {Mbps(s.UpMbpsTcp)}" : $"sent {Mbps(s.TxMbps)}", s.UpReportStale ? Color.Firebrick : null);
        _tDown.Set(Mbps(s.DownMbps), s.Mixed ? $"UDP {Mbps(s.DownMbpsUdp)} · TCP {Mbps(s.DownMbpsTcp)}"
            : double.IsFinite(s.ServerTxMbps) ? $"server sent {Mbps(s.ServerTxMbps)}" : "");
        _tRtt.Set(Ms(s.RttMs), s.Mixed ? $"UDP · TCP {Ms(s.RttTcpMs)}" : $"min {Fmt(s.RttMinMs, "0.0")} / max {Fmt(s.RttMaxMs, "0.0")}",
            LevelColor(s.RttMs, 100, 250));
        _tJitter.Set($"{Fmt(s.UpJitterMs, "0.0")} / {Fmt(s.DownJitterMs, "0.0")}", "ms", LevelColor(Math.Max(Z(s.UpJitterMs), s.DownJitterMs), 10, 30));
        _tLoss.Set($"{Fmt(s.UpLossPct, "0.##")} / {Fmt(s.DownLossPct, "0.##")}",
            $"total {Fmt(s.UpLossTotalPct, "0.###")}% / {Fmt(s.DownLossTotalPct, "0.###")}%",
            LevelColor(Math.Max(Z(s.UpLossPct), s.DownLossPct), 0.5, 3));
        _tGap.Set($"{Fmt(s.UpMaxGapMs, "0")} / {Fmt(s.DownMaxGapMs, "0")}", "ms (last ~2 s)", LevelColor(Math.Max(Z(s.UpMaxGapMs), s.DownMaxGapMs), 250, 1000));
        _tRetrans.Set(session.Config.UsesTcp ? $"{Kb(s.UpRetransBytes)} / {Kb(s.DownRetransBytes)}" : "n/a (UDP)",
            session.Config.UsesTcp ? $"KB/s · RTOs {s.UpTimeoutsTotal} / {s.DownTimeoutsTotal}" : "");
        _tMos.Set(Fmt(s.Mos, "0.0"), double.IsFinite(s.Mos) ? MosText(s.Mos) : "", s.Mos >= 4 ? Color.ForestGreen : s.Mos >= 3.6 ? Color.DarkOrange : Color.Firebrick);
        if (s.BaselinePhase) _tBloat.Set("…", $"measuring idle RTT ({session.Config.BaselineSec} s)");
        else if (s.BloatGrade != null)
            _tBloat.Set(s.BloatGrade, $"+{s.BloatIncreaseMs:0.0} ms under load (idle {s.IdleRttMs:0.0})",
                s.BloatGrade is "A+" or "A" ? Color.ForestGreen : s.BloatGrade is "B" or "C" ? Color.DarkOrange : Color.Firebrick);
        else _tBloat.Set("–", session.Config.BaselineSec == 0 ? "baseline off / server < 1.6" : session.Config.UpMbps + session.Config.DownMbps > 0 ? "measuring under load…" : "no load");
        var el = TimeSpan.FromSeconds(s.ElapsedSec);
        _tTime.Set($"{(int)el.TotalHours}:{el.Minutes:00}:{el.Seconds:00}", session.Config.DurationSec > 0 ? $"of {session.Config.DurationSec} s" : "until stopped");

        _chartTput.Push(s.UpMbps, s.DownMbps, s.TxMbps);
        _chartLat.Push(s.RttMs, s.RttMaxMs, s.UpJitterMs, s.DownJitterMs, s.RttTcpMs);
        _chartLoss.Push(s.UpLossPct, s.DownLossPct);

        OnTickTunnels();
        UpdateClassView(s);
        ShowDetail(s.Detail);
        try { _csvLog?.Write(s, session.TunnelSummaryLine()); } catch { }

        if (session.Config.DurationSec > 0 && s.ElapsedSec >= session.Config.DurationSec)
            _ = session.StopAsync("Duration reached");
    }

    /// <summary>Saves the PDF report of the last finished test (or the newest saved one) and opens it.</summary>
    void SavePdfReport()
    {
        if (_session != null) return;   // only between tests
        TestRecord? rec = _lastRecord;
        if (rec == null)
        {
            try { rec = Results.List().FirstOrDefault()?.Load(); } catch { }
        }
        if (rec == null)
        {
            MessageBox.Show(this, "There is no finished test yet. Run a test first, or pick a saved test in History.", "LinkTest",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        HistoryForm.SavePdf(this, rec, Results.Folder);
    }

    /// <summary>Fills the Statistics tab (rows = figures, columns = UDP/TCP × upload/download), updating in place.</summary>
    void ShowDetail(LinkDetail? d)
    {
        if (d == null || d.Columns.Count == 0)
        {
            _statsView.Items.Clear();
            _statsView.Groups.Clear();
            return;
        }
        var rows = d.Rows();
        string[] titles = d.Columns.Select(c => c.Title + (c.Available ? "" : " (server < 1.8)")).ToArray();
        bool rebuild = _statsView.Columns.Count != titles.Length + 1 || _statsView.Items.Count != rows.Count
            || !titles.Select((t, i) => _statsView.Columns[i + 1].Text == t).All(x => x);
        _statsView.BeginUpdate();
        if (rebuild)
        {
            _statsView.Items.Clear();
            _statsView.Groups.Clear();
            _statsView.Columns.Clear();
            _statsView.Columns.Add("Statistic", (int)(250 * DeviceDpi / 96f));
            foreach (var t in titles) _statsView.Columns.Add(t, (int)(190 * DeviceDpi / 96f), HorizontalAlignment.Right);
            foreach (var (section, name, values, hint) in rows)
            {
                var g = _statsView.Groups.Cast<ListViewGroup>().FirstOrDefault(x => x.Header == section)
                        ?? _statsView.Groups.Add(section, section);
                var it = new ListViewItem(new[] { name }.Concat(values).ToArray(), g) { ToolTipText = hint };
                _statsView.Items.Add(it);
            }
        }
        else
        {
            for (int r = 0; r < rows.Count; r++)
            {
                var it = _statsView.Items[r];
                for (int c = 0; c < rows[r].values.Length; c++)
                    if (it.SubItems[c + 1].Text != rows[r].values[c]) it.SubItems[c + 1].Text = rows[r].values[c];
            }
        }
        _statsView.EndUpdate();
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

    void ShowConnection(ConnectionInfo c)
    {
        var rows = new List<(string, string)>
        {
            ("Server", c.Server),
            ("Local address (this PC)", c.LocalAddress),
            ("Address seen by server", c.SeenByServer ?? "(server older than 1.4)"),
            ("NAT", c.NatStatus),
        };
        if (c.SeenByServerInfo is IpInfo seen && (c.PublicInfo == null || c.PublicInfo.Ip != seen.Ip))
            rows.Add(("Seen-by-server details", seen.Summary));
        var p = c.PublicInfo;
        if (p != null)
        {
            rows.Add(("Public IP", p.Ip + (p.IsPublic ? "" : $"  ({p.Scope})")));
            rows.Add(("Reverse DNS", p.Hostname ?? "(none)"));
            rows.Add(("ISP / ASN", p.Org ?? "-"));
            rows.Add(("Location (approx.)", p.Location.Length > 0 ? p.Location : "-"));
            rows.Add(("Timezone", p.Timezone ?? "-"));
        }
        rows.Add(("Lookup", !_ipLookup.Checked ? "Online lookup disabled (reverse DNS only)"
            : !c.LookupComplete ? "Looking up…"
            : p?.Error != null ? $"{IpLookup.Service}: {p.Error}" : $"via {IpLookup.Service}"));

        _connView.BeginUpdate();
        _connView.Items.Clear();
        foreach (var (k, v) in rows) _connView.Items.Add(new ListViewItem(new[] { k, v }));
        _connView.EndUpdate();
    }

    void ShowPath(ClientSession session)
    {
        var sb = new System.Text.StringBuilder();
        var m = session.Mtu;
        sb.AppendLine("PATH MTU");
        if (m == null) sb.AppendLine(_mtu.Checked ? "  measuring…" : "  (off)");
        else
        {
            sb.AppendLine("  " + m.Summary);
            if (m.Ok)
            {
                sb.AppendLine($"  Payload limits: up {m.UpPayload} B, down {m.DownPayload} B (UDP); outer packets are {(m.Ipv6 ? "IPv6" : "IPv4")}.");
                sb.AppendLine();
                sb.AppendLine($"  {"Tunnel type",-34} {"Overhead",9} {"Tunnel MTU",11} {"TCP MSS",8}");
                foreach (var v in VpnOverhead.All)
                    sb.AppendLine($"  {v.Name,-34} {v.Overhead(m.Ipv6),7} B {v.TunnelMtu(m.PathMtu, m.Ipv6),11} {v.TcpMss(m.PathMtu, m.Ipv6),8}");
                sb.AppendLine("  Set the tunnel interface MTU (or MSS clamping) to at most these values to avoid fragmentation.");
            }
        }
        sb.AppendLine();
        sb.AppendLine("TRACEROUTES (newest first)");
        var traces = session.Traces;
        if (traces.Count == 0) sb.AppendLine(_trace.Checked ? "  none yet" : "  (off)");
        foreach (var t in traces.Reverse())
        {
            sb.Append(t.Format());
            sb.AppendLine();
        }
        sb.AppendLine("Note: loss on a single hop that does not continue to later hops is usually ICMP rate limiting, not real loss.");
        _pathView.Text = sb.ToString();
        _pathView.Select(0, 0);
    }

    void UpdateClassView(StatSample s)
    {
        _classView.BeginUpdate();
        for (int i = 0; i < s.Classes.Count && i < _classView.Items.Count; i++)
        {
            var k = s.Classes[i];
            bool udp = k.Transport == TransportKind.Udp;
            string[] cells =
            {
                k.Name, DscpNames.Name(k.Dscp), udp ? "UDP" : "TCP", $"{k.SharePct:0.#}%",
                Fmt(k.UpMbps, "0.00"), udp ? Fmt(k.UpLossPct, "0.00") : "–", Fmt(k.UpJitterMs, "0.00") + " ms",
                Fmt(k.DownMbps, "0.00"), udp ? Fmt(k.DownLossPct, "0.00") : "–", Fmt(k.DownJitterMs, "0.00") + " ms",
                Ms(k.RttMs), udp ? $"{k.UpLostTotal} / {k.DownLostTotal}" : "–",
                udp ? "–" : $"{Kb(k.UpRetransBytes)} / {Kb(k.DownRetransBytes)}",
            };
            var it = _classView.Items[i];
            for (int c = 0; c < cells.Length; c++)
            {
                if (c == 0) it.Text = cells[0];
                else if (it.SubItems.Count > c) it.SubItems[c].Text = cells[c];
                else it.SubItems.Add(cells[c]);
            }
            double loss = Math.Max(Z(k.UpLossPct), Z(k.DownLossPct));
            it.BackColor = !udp ? (k.UpRetransBytes + k.DownRetransBytes > 0 ? Color.FromArgb(255, 244, 206) : Color.White)
                : loss >= 3 ? Color.FromArgb(253, 231, 233) : loss >= 0.5 ? Color.FromArgb(255, 244, 206) : Color.FromArgb(223, 246, 221);
        }
        _classView.EndUpdate();
    }

    // ------------------------------------------------------------------ updates

    // Auto-sized so the buttons are never clipped, whatever the DPI scaling.
    readonly FlowLayoutPanel _banner = new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
        Visible = false, BackColor = Color.FromArgb(255, 244, 206), Padding = new Padding(8, 4, 8, 4),
    };
    readonly Label _bannerText = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
    readonly Button _bannerInstall = new() { Text = "Install and restart", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2) };
    readonly ToolStripMenuItem _autoCheckItem = new("Check for updates at startup") { CheckOnClick = true };
    UpdateInfo? _pendingUpdate;
    bool _updating;

    Control BuildUpdateBanner()
    {
        var notes = new LinkLabel { Text = "What's new", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 12, 0) };
        var later = new Button { Text = "Later", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2) };
        notes.LinkClicked += (_, _) => OpenUrl(_pendingUpdate?.PageUrl ?? Updater.ProjectUrl);
        later.Click += (_, _) => _banner.Visible = false;
        _bannerInstall.Click += async (_, _) => { if (_pendingUpdate != null) await InstallUpdateAsync(_pendingUpdate); };
        _banner.Controls.AddRange(new Control[] { _bannerText, _bannerInstall, notes, later });
        return _banner;
    }

    ToolStripItem BuildVersionMenu()
    {
        var menu = new ToolStripDropDownButton($"v{Updater.CurrentVersion}") { Alignment = ToolStripItemAlignment.Right };
        var check = new ToolStripMenuItem("Check for updates now");
        check.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        _autoCheckItem.Checked = _settings.CheckForUpdates;
        _autoCheckItem.CheckedChanged += (_, _) => { _settings.CheckForUpdates = _autoCheckItem.Checked; _settings.Save(); };
        var page = new ToolStripMenuItem("Project page on GitHub");
        page.Click += (_, _) => OpenUrl(Updater.ProjectUrl);
        menu.DropDownItems.AddRange(new ToolStripItem[] { check, _autoCheckItem, new ToolStripSeparator(), page });
        Load += async (_, _) => { if (_settings.CheckForUpdates) await CheckForUpdatesAsync(manual: false); };   // Load: also fires when started minimized
        return menu;
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        try
        {
            var info = await Updater.CheckAsync(UpdateChannel.Client);
            if (info == null)
            {
                if (manual) MessageBox.Show(this, $"You are running the latest version ({Updater.CurrentVersion}).", "LinkTest", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _pendingUpdate = info;
            _bannerText.Text = $"LinkTest {info.Tag} is available (you have {Updater.CurrentVersion}).";
            _bannerInstall.Enabled = true;
            _banner.Visible = true;
            if (manual) await InstallUpdateAsync(info, ask: true);
        }
        catch (Exception e)
        {
            if (manual) MessageBox.Show(this, "Could not check for updates: " + e.Message, "LinkTest", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else AppendLog("Update check failed: " + e.Message);
        }
    }

    async Task InstallUpdateAsync(UpdateInfo info, bool ask = false)
    {
        if (_updating) return;
        if (ask && MessageBox.Show(this, $"LinkTest {info.Tag} is available (you have {Updater.CurrentVersion}).\n\nInstall it and restart now?",
                "LinkTest update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (_session != null)
        {
            MessageBox.Show(this, "Stop the running test before updating.", "LinkTest update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Updater.CanSelfUpdate(UpdateChannel.Client, out var why))
        {
            if (MessageBox.Show(this, $"This copy can't update itself: {why}.\n\nOpen the download page instead?", "LinkTest update",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                OpenUrl(info.PageUrl);
            return;
        }

        _updating = true;
        _bannerInstall.Enabled = false;
        _start.Enabled = false;
        _banner.Visible = true;
        try
        {
            var progress = new Progress<double>(p => _bannerText.Text = $"Downloading LinkTest {info.Tag}… {p:P0}");
            string zip = await Updater.DownloadAsync(info, progress);
            _bannerText.Text = "Installing…";
            SaveUiToSettings();
            _timer.Stop();
            await Task.Run(() => Updater.ApplyAndRestart(zip, UpdateChannel.Client, Array.Empty<string>()));
            // The exe has been replaced: exit at once - running more code could need assemblies that
            // can no longer be loaded from the (swapped) single-file bundle.
            Environment.Exit(0);
        }
        catch (Exception e)
        {
            _bannerText.Text = $"Update to {info.Tag} failed.";
            _bannerInstall.Enabled = true;
            _start.Enabled = _session == null;
            Updater.Log("Client update failed: " + e);
            AppendLog("Update failed: " + Updater.Describe(e) + $" (details in {Updater.LogPath})");
            MessageBox.Show(this, "Update failed: " + Updater.Describe(e) + $"\n\nDetails: {Updater.LogPath}", "LinkTest update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _updating = false;
        }
    }

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
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
