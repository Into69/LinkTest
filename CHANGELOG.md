# Changelog

Each release's notes on GitHub are taken from its section below.

## [1.8.0]

### New
- **Detailed statistics, both directions, UDP and TCP.** The server now sends the client a full report on what
  it receives once a second. Together with the client's own measurements this gives up to four columns:
  UDP upload, UDP download, TCP upload and TCP download.
  - Traffic: packets sent and received, data, average throughput and packet rate, packet sizes, sender lag.
  - Loss (UDP): lost packets, loss events, longest and average burst, burst-length histogram.
  - Order (UDP): out-of-order packets and how far they were reordered, duplicates, very late packets.
  - Timing: jitter (average and peak), one-way delay variation (average, p50 / p95 / p99, max), longest
    silence, stalls of 100 ms and 1 s or more.
  - TCP, from the sending side's kernel: RTT and minimum RTT, congestion window, bytes in flight,
    retransmitted bytes, fast retransmits, retransmission timeouts, duplicate ACKs, reordering, MSS.
- Shown on a new *Statistics* tab (desktop client) and under *Details* (Android), printed by the CLI at the
  end of a test, saved with each result, and included in the HTML report.

- **PDF traffic report.** A *PDF report…* button on the client, enabled only between tests, makes a PDF of the last
  finished test. *Save PDF report…* in History does the same for any saved test; Android has *Share PDF*, and the CLI
  has `--pdf FILE`. The report focuses on how well the link carried each type of simulated traffic:
  - a verdict for each traffic type (maintained, marginal or not maintained), judged against what that kind of
    traffic needs: voice and video ≤ 1% loss, ≤ 30 ms jitter and ≤ 300 ms RTT; network control ≤ 0.5% loss;
    bulk and best effort ≥ 90% of their rate; and so on (ITU-T G.114 / Y.1541, RFC 4594);
  - how much of the target rate was delivered, loss (or TCP retransmissions), jitter and RTT per traffic type;
  - "within limits" over time: the share of seconds each type met its requirements, with a timeline strip for
    each and a list of what fell short and for how long;
  - VPN tunnel stability, throughput / latency / loss charts, the detailed statistics, and the test setup.
- Saved results now keep per-second figures for each traffic class.

### Compatibility
- Needs a 1.8 server for the upload columns; with an older server they show "n/a". Older clients are
  unaffected (the server only sends the new report to 1.8+ clients).

## [1.7.0]

### New
- **Saved results.** Every finished test is saved automatically. Each saved test holds:
  - settings and per-second figures;
  - the summary, tunnel and traffic-class results;
  - connection and IP details, MTU and traceroutes;
  - the event log.

  Desktop results go to `Documents\LinkTest\Results`.
- **Shareable HTML report.** One self-contained file that opens in any browser and is easy to email to an
  ISP or vendor. It has no scripts or external resources, and works in light and dark mode. It contains:
  - key figures, and throughput, latency and loss charts;
  - settings and connection details;
  - traffic-class, tunnel, MTU and traceroute tables;
  - the full event log.
- **Desktop client:**
  - a *History* window (next to Start/Stop) to open or save reports, export CSV, open the folder, and delete;
  - an *Open report* link in the status bar after each test.
- **Android:**
  - a *History* tab to view a report, share it, share CSV, or delete a result;
  - **background testing:** a foreground service with a live status notification keeps long tests running
    with the screen off or in other apps. It holds the CPU and Wi-Fi awake while the test runs and can be
    turned off in Settings. The screen-on option now defaults to off;
  - **tunnel timers can be edited** (Settings > tunnel > Edit), with a "detected after about N s" preview and
    a restore-preset button.
- **CLI:** `--report FILE` (HTML report) and `--json FILE` (full record).

### Fixed
- Android: traceroute could crash the app. .NET started many `ping` processes in parallel, which its runtime
  can't handle on Android; it now uses Android's process API, six hops at a time.
- Android: traceroute stops once it reaches the server instead of probing all 30 hops (about 3 s instead of 15 s).
  IPv6 traces no longer start `ping` twice per hop.
- Android: if a test ended while the notification-permission prompt was open, background mode could start
  afterwards and keep the phone awake.
- Android tunnel editor: decimal values work on phones set to languages that use a decimal comma, and
  values that can't be read are reported instead of being silently ignored.
- Durations of a day or more are shown in full hours (26:00:00, not 2:00:00) in History and in reports.
- Desktop: failing to save or open a report or CSV shows a message instead of an error dialog.
- History opens quickly with many long tests: the list reads a small summary index, and full results are
  loaded only when a report or CSV is made.
- Download loss is shown as "not measured" (–) when nothing was received, instead of 0%.

## [1.6.0]

### New
- **Path MTU test.** When a test starts, the client finds the largest packet that crosses the path
  unfragmented, upstream and downstream, using don't-fragment probes.
  - It recommends a tunnel MTU and TCP MSS for WireGuard, IPsec (AES-GCM and AES-CBC), OpenVPN and GRE.
  - The results appear on the new *Path* tab (desktop client) and on *Details* (Android).
  - Needs a 1.6 server.
- **Bufferbloat grade.** Each test now starts with a short idle baseline, 3 s by default and configurable.
  - The client measures the idle round-trip time, then compares it with the round-trip time under load.
  - The result is a grade from A+ to F, using the same thresholds as common bufferbloat tests.
  - A new tile shows it, along with the extra delay under load in ms.
- **Traceroute when trouble appears.** An MTR-style traceroute to the server, with loss and RTT per hop:
  - one at the start of each test, as a baseline;
  - again on loss of 2% or more, a stall of 1 s or more, or a tunnel drop (at most once a minute).
  - It reports the hop where loss starts and persists to the end of the path.
- **CLI:** `--baseline SEC`, `--no-mtu` and `--no-trace`; traceroutes are printed as hop tables.
- **Automated tests.** About 50 unit and end-to-end tests, covering:
  - loss and reorder accounting, traffic shaping, tunnel state machines, traffic classes;
  - every traffic mode end to end against a real in-process server;
  - live artificial-loss changes, MTU probing, traceroute and old-client compatibility.

  They run on every push and before every release.

### Changed
- Test averages include the idle baseline, and the summary says so.
- GitHub Actions steps updated to versions that run on the current Node runtime. Release notes now come from this changelog.

### Compatibility
- 1.5 clients work with a 1.6 server, and vice versa.
- The MTU test and bufferbloat grade only run when both ends are 1.6 or newer; older servers are detected
  and the client says so.

## [1.5.0]
- **Android client app** (`LinkTest-Client-android.apk`, Android 7.0+). It has the same tests, tunnel
  simulations, traffic classes and IP lookup as the desktop client, and updates itself through the Android
  installer.
- DSCP marking and TCP retransmit counters on Linux/Android.
- All programs moved to .NET 10.

## [1.4.1]
- Fixed self-update. The new version was installed but did not restart: single-file apps could no longer
  load parts of themselves once their exe had been replaced.
- The update banner no longer cuts off its buttons at high DPI.
- The server GUI now auto-starts and checks for updates when launched minimized.
- The version is shown in the title bars. Update steps are logged to `%LOCALAPPDATA%\LinkTest\update.log`.

## [1.4.0]
- **Client IP lookup on both ends.** The client and server show public IP, reverse DNS, ISP/ASN and location
  (via ipinfo.io), along with NAT detection.
- Artificial loss can be changed while a test is running.

## [1.3.0]
- **DSCP Simulate:** a 10-class enterprise traffic mix (EF voice … CS1 scavenger) with per-class statistics.
- **Server GUI** (`LinkTestServerGui.exe`).
- The client's Start/Stop buttons are always visible.

## [1.2.0]
- **Mixed TCP + UDP** traffic mode.

## [1.1.0]
- Self-update from GitHub Releases, and the release workflow.

## [1.0.0]
- First version:
  - Windows server, client GUI and CLI;
  - UDP/TCP tests with in-band statistics and traffic profiles;
  - VPN tunnel simulations.
