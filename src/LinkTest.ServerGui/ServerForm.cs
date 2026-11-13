using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LinkTest.Core;
using LinkTest.Server;

namespace LinkTest.ServerGui;

/// <summary>Windows GUI for the LinkTest server: settings, start/stop, live sessions and log.</summary>
sealed class ServerForm : Form
{
    readonly ServerSettings _settings = ServerSettings.Load();
    readonly ToolTip _tips = new() { AutoPopDelay = 20000 };

    readonly NumericUpDown _port = Num(1, 65535);
    readonly ComboBox _bind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox _key = new() { UseSystemPasswordChar = true, PlaceholderText = "(none - any client may connect)" };
    readonly NumericUpDown _maxMbps = Num(1, 100000);
    readonly NumericUpDown _maxSessions = Num(1, 64);
    readonly CheckBox _startOnLaunch = new() { Text = "Start the server when this window opens", AutoSize = true };
    readonly CheckBox _lookupClients = new() { Text = "Look up client IP details (ISP, location via ipinfo.io)", AutoSize = true };
    readonly CheckBox _autoInstall = new() { Text = "Install updates automatically when no tests are running", AutoSize = true };
    readonly Button _startBtn = new() { Text = "Start server", Height = 36, Width = 150 };
    readonly Button _stopBtn = new() { Text = "Stop server", Height = 36, Width = 120, Enabled = false };
    readonly Button _firewallBtn = new() { Text = "Add firewall rules…", Height = 36, AutoSize = true };
    readonly Label _state = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Text = "Stopped", ForeColor = Color.DimGray };
    readonly Label _addresses = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(900, 0) };
    readonly ListView _sessions = new() { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.White };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _updateLink = new() { IsLink = true, Visible = false };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 3600 * 1000 };
    readonly List<Control> _inputs = new();
    readonly Dictionary<uint, (long rx, long tx, long lost, long exp, DateTime at)> _prev = new();

    ServerHost? _host;
    Task? _hostTask;
    CancellationTokenSource? _hostCts;
    UpdateInfo? _update;
    bool _updating;

    public ServerForm(bool autoStart)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "LinkTest Server";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 246, 248);
        BuildLayout();
        LoadSettings();
        ResumeLayout(false);
        PerformLayout();

        ServerHost.ConsoleOutput = false;
        ServerHost.LogMessage += OnLog;
        _timer.Tick += (_, _) => RefreshSessions();
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        _startBtn.Click += (_, _) => StartServer();
        _stopBtn.Click += async (_, _) => await StopServerAsync();
        _firewallBtn.Click += (_, _) => AddFirewallRules();
        _updateLink.Click += async (_, _) => { if (_update != null) await InstallUpdateAsync(_update, ask: true); };
        FormClosing += OnClosing;
        Shown += async (_, _) =>
        {
            if (autoStart || _settings.StartOnLaunch) StartServer();
            if (_settings.CheckForUpdates)
            {
                _updateTimer.Start();
                await Task.Delay(3000);
                await CheckForUpdatesAsync(false);
            }
        };
    }

    // ------------------------------------------------------------------ layout

    void BuildLayout()
    {
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(12, 8, 12, 4), BackColor = Color.White };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));

        AddField(settings, "Port (TCP + UDP)", _port, 0, 0, "Clients connect to this port. Both TCP and UDP must be allowed through firewalls.");
        AddField(settings, "Listen on", _bind, 2, 0, "All interfaces, or one specific local address.");
        AddField(settings, "Shared key", _key, 0, 1, "If set, clients must enter the same key.");
        AddField(settings, "Max Mbps per direction", _maxMbps, 2, 1, "Rate cap applied to each test (protects this server's link).");
        AddField(settings, "Max concurrent tests", _maxSessions, 0, 2, "Further clients are refused while this many tests run.");
        var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        checks.Controls.Add(_startOnLaunch);
        checks.Controls.Add(_lookupClients);
        _tips.SetToolTip(_lookupClients, "Public client addresses are looked up at ipinfo.io; private/LAN addresses get reverse DNS only.");
        checks.Controls.Add(_autoInstall);
        settings.Controls.Add(checks, 2, 2);
        settings.SetColumnSpan(checks, 2);
        _inputs.AddRange(new Control[] { _startOnLaunch, _lookupClients });

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 6, 12, 6), BackColor = Color.FromArgb(235, 238, 242), WrapContents = true };
        _startBtn.Font = new Font(Font, FontStyle.Bold);
        _startBtn.BackColor = Color.FromArgb(0, 120, 212); _startBtn.ForeColor = Color.White; _startBtn.FlatStyle = FlatStyle.Flat;
        _startBtn.EnabledChanged += (_, _) => _startBtn.BackColor = _startBtn.Enabled ? Color.FromArgb(0, 120, 212) : Color.FromArgb(200, 205, 212);
        _state.Margin = new Padding(16, 8, 8, 0);
        actions.Controls.AddRange(new Control[] { _startBtn, _stopBtn, _firewallBtn, _state });
        _tips.SetToolTip(_firewallBtn, "Adds inbound Windows Firewall rules for the port (asks for Administrator rights).");

        var addrPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14, 4, 12, 6) };
        addrPanel.Controls.Add(_addresses);

        foreach (var (name, w) in new[] { ("Session", 80), ("Client", 130), ("Host (reverse DNS)", 170), ("ISP / ASN", 180), ("Location", 160), ("Version", 60),
                                          ("Mode", 110), ("Duration", 70), ("Rx / upload Mbps", 110), ("Tx / download Mbps", 120), ("Loss %", 60), ("RTT ms", 60), ("Classes", 55) })
            _sessions.Columns.Add(name, (int)(w * DeviceDpi / 96f));

        var sessionsBox = new GroupBox { Text = "Active tests", Dock = DockStyle.Fill, Padding = new Padding(6) };
        sessionsBox.Controls.Add(_sessions);
        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(6) };
        logBox.Controls.Add(_log);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(sessionsBox);
        split.Panel2.Controls.Add(logBox);
        Load += (_, _) => split.SplitterDistance = (int)(split.Height * 0.4);

        var strip = new StatusStrip();
        strip.Items.Add(_status);
        strip.Items.Add(_updateLink);
        strip.Items.Add(BuildVersionMenu());

        Controls.Add(split);
        Controls.Add(addrPanel);
        Controls.Add(actions);
        Controls.Add(settings);
        Controls.Add(strip);
    }

    void AddField(TableLayoutPanel t, string label, Control c, int col, int row, string tip)
    {
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 7, 8, 3) };
        c.Dock = DockStyle.Fill;
        c.Margin = new Padding(0, 3, 24, 3);
        t.Controls.Add(l, col, row);
        t.Controls.Add(c, col + 1, row);
        _tips.SetToolTip(l, tip);
        _tips.SetToolTip(c, tip);
        _inputs.Add(c);
    }

    static NumericUpDown Num(decimal min, decimal max) => new() { Minimum = min, Maximum = max, ThousandsSeparator = false };

    ToolStripItem BuildVersionMenu()
    {
        var menu = new ToolStripDropDownButton($"v{Updater.CurrentVersion}") { Alignment = ToolStripItemAlignment.Right };
        var check = new ToolStripMenuItem("Check for updates now");
        check.Click += async (_, _) => await CheckForUpdatesAsync(true);
        var auto = new ToolStripMenuItem("Check for updates automatically") { CheckOnClick = true, Checked = _settings.CheckForUpdates };
        auto.CheckedChanged += (_, _) =>
        {
            _settings.CheckForUpdates = auto.Checked;
            _settings.Save();
            if (auto.Checked) _updateTimer.Start(); else _updateTimer.Stop();
        };
        var page = new ToolStripMenuItem("Project page on GitHub");
        page.Click += (_, _) => OpenUrl(Updater.ProjectUrl);
        menu.DropDownItems.AddRange(new ToolStripItem[] { check, auto, new ToolStripSeparator(), page });
        return menu;
    }

    // ------------------------------------------------------------------ settings

    void LoadSettings()
    {
        _port.Value = Math.Clamp(_settings.Port, 1, 65535);
        _bind.Items.Add("All interfaces (IPv4 + IPv6)");
        foreach (var a in LocalAddresses()) _bind.Items.Add(a.ToString());
        int idx = _settings.Bind.Length > 0 ? _bind.Items.IndexOf(_settings.Bind) : 0;
        _bind.SelectedIndex = Math.Max(0, idx);
        _key.Text = _settings.Key;
        _maxMbps.Value = (decimal)Math.Clamp(_settings.MaxMbps, 1, 100000);
        _maxSessions.Value = Math.Clamp(_settings.MaxSessions, 1, 64);
        _startOnLaunch.Checked = _settings.StartOnLaunch;
        _lookupClients.Checked = _settings.LookupClients;
        _autoInstall.Checked = _settings.AutoInstallUpdates;
        _status.Text = "Server stopped";
    }

    void SaveSettings()
    {
        _settings.Port = (int)_port.Value;
        _settings.Bind = _bind.SelectedIndex > 0 ? _bind.SelectedItem!.ToString()! : "";
        _settings.Key = _key.Text;
        _settings.MaxMbps = (double)_maxMbps.Value;
        _settings.MaxSessions = (int)_maxSessions.Value;
        _settings.StartOnLaunch = _startOnLaunch.Checked;
        _settings.LookupClients = _lookupClients.Checked;
        _settings.AutoInstallUpdates = _autoInstall.Checked;
        _settings.Save();
    }

    static List<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                .Where(a => !a.IsIPv6LinkLocal)
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                .ToList();
        }
        catch { return new(); }
    }

    // ------------------------------------------------------------------ server lifecycle

    bool Running => _hostTask != null && !_hostTask.IsCompleted;

    void StartServer()
    {
        if (Running) return;
        SaveSettings();
        var opts = new ServerOptions
        {
            Port = (int)_port.Value,
            Bind = _settings.Bind.Length > 0 && IPAddress.TryParse(_settings.Bind, out var b) ? b : null,
            Key = string.IsNullOrEmpty(_key.Text) ? null : _key.Text,
            MaxMbps = (double)_maxMbps.Value,
            MaxSessions = (int)_maxSessions.Value,
            NoIpLookup = !_lookupClients.Checked,
        };
        _host = new ServerHost(opts) { PeriodicStatus = false };
        _hostCts = new CancellationTokenSource();
        var host = _host;
        _hostTask = Task.Run(() => host.RunAsync(_hostCts.Token));
        _ = _hostTask.ContinueWith(t => BeginInvoke(() => OnServerStopped(t)));
        _prev.Clear();
        SetRunningUi(true);
        _state.Text = $"Running on port {opts.Port}";
        _state.ForeColor = Color.ForestGreen;
        var addrs = opts.Bind != null ? new List<IPAddress> { opts.Bind } : LocalAddresses();
        _addresses.Text = "Clients can connect to: " + (addrs.Count > 0 ? string.Join("   ", addrs.Select(a => a.ToString())) : "(no network addresses found)");
        _status.Text = $"Listening on TCP+UDP port {opts.Port}";
        _timer.Start();
    }

    async Task StopServerAsync()
    {
        if (!Running) return;
        int n = _host?.SessionCount ?? 0;
        if (n > 0 && MessageBox.Show(this, $"{n} test(s) are running. Stop the server anyway?", "LinkTest Server",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _stopBtn.Enabled = false;
        _hostCts?.Cancel();
        try { await _hostTask!; } catch { }
    }

    void OnServerStopped(Task t)
    {
        _timer.Stop();
        if (t.Exception?.GetBaseException() is Exception e)
        {
            string msg = e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? $"Port {_port.Value} is already in use (is another LinkTest server running?)"
                : e.Message;
            OnLog($"{DateTime.Now:HH:mm:ss} Server failed: {msg}");
            MessageBox.Show(this, msg, "LinkTest Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _host = null;
        _hostTask = null;
        SetRunningUi(false);
        _state.Text = "Stopped";
        _state.ForeColor = Color.DimGray;
        _addresses.Text = "";
        _status.Text = "Server stopped";
        _sessions.Items.Clear();
    }

    void SetRunningUi(bool running)
    {
        foreach (var c in _inputs) c.Enabled = !running;
        _startBtn.Enabled = !running;
        _stopBtn.Enabled = running;
    }

    void RefreshSessions()
    {
        var host = _host;
        if (host == null) return;
        var list = host.GetSessions();
        var now = DateTime.Now;
        _sessions.BeginUpdate();
        _sessions.Items.Clear();
        foreach (var s in list)
        {
            _prev.TryGetValue(s.Id, out var p);
            double dt = p.at == default ? 0 : (now - p.at).TotalSeconds;
            string rx = "–", tx = "–", loss = "–";
            if (dt > 0.2)
            {
                rx = ((s.RxBytes - p.rx) * 8 / dt / 1e6).ToString("0.00", CultureInfo.CurrentCulture);
                tx = ((s.TxBytes - p.tx) * 8 / dt / 1e6).ToString("0.00", CultureInfo.CurrentCulture);
                long de = s.Expected - p.exp, dl = Math.Max(0, s.Lost - p.lost);
                loss = de > 0 ? (100.0 * dl / de).ToString("0.##", CultureInfo.CurrentCulture) : "0";
            }
            _prev[s.Id] = (s.RxBytes, s.TxBytes, s.Lost, s.Expected, now);
            var dur = now - s.Started;
            var item = new ListViewItem(new[]
            {
                s.Id.ToString("X8"), s.Client,
                s.ClientInfo == null ? "looking up…" : s.ClientInfo.Hostname ?? "-",
                s.ClientInfo == null ? "" : s.ClientInfo.Org ?? (s.ClientInfo.IsPublic ? "-" : s.ClientInfo.Scope),
                s.ClientInfo == null ? "" : s.ClientInfo.Location.Length > 0 ? s.ClientInfo.Location : "-",
                s.ClientVersion ?? "?", s.Mode,
                s.Running ? $"{(int)dur.TotalHours}:{dur.Minutes:00}:{dur.Seconds:00}" : "starting",
                rx, tx, loss, double.IsFinite(s.RttMs) ? s.RttMs.ToString("0.0", CultureInfo.CurrentCulture) : "–", s.Classes.ToString(),
            });
            _sessions.Items.Add(item);
        }
        _sessions.EndUpdate();
        foreach (var id in _prev.Keys.Except(list.Select(s => s.Id)).ToList()) _prev.Remove(id);
        _status.Text = $"Listening on TCP+UDP port {_port.Value} · {list.Count} active test(s)";

        if (_update != null && _autoInstall.Checked && list.Count == 0 && !_updating)
            _ = InstallUpdateAsync(_update, ask: false);
    }

    void AddFirewallRules()
    {
        int port = (int)_port.Value;
        string exe = Environment.ProcessPath ?? "";
        string args = $"/c netsh advfirewall firewall add rule name=\"LinkTest TCP {port}\" dir=in action=allow protocol=TCP localport={port} & " +
                      $"netsh advfirewall firewall add rule name=\"LinkTest UDP {port}\" dir=in action=allow protocol=UDP localport={port}";
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
            OnLog($"{DateTime.Now:HH:mm:ss} " + (p?.ExitCode == 0 ? $"Firewall rules added for TCP/UDP port {port}." : "Adding firewall rules may have failed (exit code " + p?.ExitCode + ")."));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            OnLog($"{DateTime.Now:HH:mm:ss} Firewall rules not added (Administrator permission was declined).");
        }
    }

    // ------------------------------------------------------------------ updates

    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        try
        {
            var info = await Updater.CheckAsync(UpdateChannel.ServerGui);
            if (info == null)
            {
                if (manual) MessageBox.Show(this, $"You are running the latest version ({Updater.CurrentVersion}).", "LinkTest Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _update = info;
            _updateLink.Text = $"Update {info.Tag} available - install";
            _updateLink.Visible = true;
            OnLog($"{DateTime.Now:HH:mm:ss} Update available: {info.Tag} (running {Updater.CurrentVersion})");
            if (manual) await InstallUpdateAsync(info, ask: true);
            else if (_autoInstall.Checked && (_host?.SessionCount ?? 0) == 0) await InstallUpdateAsync(info, ask: false);
        }
        catch (Exception e)
        {
            if (manual) MessageBox.Show(this, "Could not check for updates: " + e.Message, "LinkTest Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else OnLog($"{DateTime.Now:HH:mm:ss} Update check failed: {e.Message}");
        }
    }

    async Task InstallUpdateAsync(UpdateInfo info, bool ask)
    {
        if (_updating) return;
        int n = _host?.SessionCount ?? 0;
        if (ask)
        {
            string q = $"LinkTest Server {info.Tag} is available (you have {Updater.CurrentVersion}).\n\n" +
                       (n > 0 ? $"{n} test(s) are running and will be interrupted. " : "") + "Install it and restart now?";
            if (MessageBox.Show(this, q, "LinkTest Server update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        else if (n > 0) return;
        if (!Updater.CanSelfUpdate(UpdateChannel.ServerGui, out var why))
        {
            OnLog($"{DateTime.Now:HH:mm:ss} Cannot install update: {why}");
            if (ask) MessageBox.Show(this, $"This copy can't update itself: {why}.", "LinkTest Server update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _updating = true;
        try
        {
            OnLog($"{DateTime.Now:HH:mm:ss} Downloading {info.Tag}…");
            var progress = new Progress<double>(p => _updateLink.Text = $"Downloading {info.Tag}… {p:P0}");
            string zip = await Updater.DownloadAsync(info, progress);
            bool wasRunning = Running;
            if (wasRunning) { _hostCts?.Cancel(); try { await _hostTask!; } catch { } }
            SaveSettings();
            await Task.Run(() => Updater.Apply(zip, UpdateChannel.ServerGui));
            OnLog($"{DateTime.Now:HH:mm:ss} Update installed; restarting.");
            Updater.Restart(wasRunning ? new[] { "--autostart" } : Array.Empty<string>());
            _forceClose = true;
            Close();
        }
        catch (Exception e)
        {
            OnLog($"{DateTime.Now:HH:mm:ss} Update failed: {e.Message}");
            _updateLink.Text = $"Update {info.Tag} failed - retry";
            if (ask) MessageBox.Show(this, "Update failed: " + e.Message, "LinkTest Server update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _updating = false; }
    }

    // ------------------------------------------------------------------ misc

    bool _forceClose;

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        int n = _host?.SessionCount ?? 0;
        if (!_forceClose && n > 0 && MessageBox.Show(this, $"{n} test(s) are running. Stop the server and exit?", "LinkTest Server",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SaveSettings();
        ServerHost.LogMessage -= OnLog;
        _hostCts?.Cancel();
        try { _hostTask?.Wait(3000); } catch { }
    }

    void OnLog(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => OnLog(line)); } catch { } return; }
        _log.AppendText(line + Environment.NewLine);
        if (_log.TextLength > 400_000) _log.Text = _log.Text[^200_000..];
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
