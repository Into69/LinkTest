# LinkTest

Link quality monitor for Windows. A **server** and a **client** exchange paced traffic in both directions
and measure throughput, loss, reordering, jitter, RTT and stalls in near real time. They also simulate how
different **VPN tunnel types** would behave on the link: would they drop, and would they come back up and stay up?

| Program | What it is |
|---|---|
| `LinkTestServer.exe` | Console server. Run it on the far end of the link. |
| `LinkTestServerGui.exe` | The same server with a window. It has settings, start/stop, live test sessions, the log, a button that adds firewall rules, and updates. |
| `LinkTestClient.exe` | Windows GUI client. Choose the server, the rates and the traffic type, then watch live stats. |
| `LinkTestCli.exe` | Headless client with the same engine, for scripting and long-term monitoring. |
| `LinkTest-Client-android.apk` | Android client (Android 7.0+): the same tests, tunnel simulations, traffic classes and IP lookup on a phone. |

## Build

Requires the .NET 10 SDK. To build the Android app (`src/LinkTest.Mobile`) you also need:
- the MAUI Android workload: `dotnet workload install maui-android`
- JDK 17
- the Android SDK with platform 36

Without them, build the Windows projects individually instead of the whole solution.

```powershell
dotnet build -c Release
# self-contained single-file exes (no .NET runtime needed on target machines):
dotnet publish src\LinkTest.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\server
dotnet publish src\LinkTest.Client -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\client
dotnet publish src\LinkTest.Cli    -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\cli
```

## Run

**Server** (listens on TCP **and** UDP port 7447 by default):

```powershell
LinkTestServer.exe [--port 7447] [--key SECRET] [--max-mbps 1000] [--max-sessions 8] [--bind ADDR]

# allow it through Windows Firewall (Administrator, once):
netsh advfirewall firewall add rule name="LinkTest TCP" dir=in action=allow protocol=TCP localport=7447
netsh advfirewall firewall add rule name="LinkTest UDP" dir=in action=allow protocol=UDP localport=7447
```

**Client GUI**: enter the server IP or FQDN, then pick the options:

- **Protocol**: UDP shows the network's real loss and reordering. TCP shows what applications experience
  (kernel retransmissions and RTO timeouts are read with `SIO_TCP_INFO`). **Mixed** runs both at once, like
  real traffic: **UDP share %** sets how much of each direction's rate is UDP, and the rest goes over the
  TCP streams. Each transport is measured separately. The throughput tiles show the UDP/TCP split, and
  loss, jitter and RTT come from the UDP part, with the TCP RTT shown alongside.
- **Upload / TX and Download / RX rates** are set separately. A rate of 0 sends heartbeats only, about 1 kbps
  (monitoring mode).
- **Packet size**, **TCP parallel streams** and **duration** (0 = until stopped).
- **Traffic profile**: Constant, IMIX, Bursty (on/off), Random (Poisson), VoIP (G.711 frames), Video (30 fps with I-frames).
- **DSCP marking** through Windows qWAVE. This requires running as Administrator, and it only works between
  two machines: Windows cannot mark traffic to its own addresses.
- **DSCP Simulate** runs a typical enterprise traffic mix of 10 classes at once. Each class has its own
  socket(s), DSCP value, transport, traffic pattern and statistics, shown on the *Traffic classes* tab.
  This shows whether the network's QoS protects voice and video while bulk and scavenger traffic take the
  loss:

  | Class | DSCP | Transport | Share | Pattern |
  |---|---|---|---|---|
  | Voice | EF (46) | UDP | 10% | G.711 VoIP |
  | Video conferencing | AF41 (34) | UDP | 18% | 30 fps video |
  | Network control | CS6 (48) | UDP | 2% | small constant |
  | OAM / management | CS2 (16) | UDP | 1% | small random |
  | Call signaling | CS3 (24) | TCP | 2% | small random |
  | Streaming video | AF31 (26) | TCP | 12% | video |
  | Transactional data | AF21 (18) | TCP | 12% | random (web/apps) |
  | Bulk data | AF11 (10) | TCP | 12% | constant |
  | Best effort | BE (0) | TCP | 30% | IMIX |
  | Scavenger | CS1 (8) | TCP | 1% | constant |

  Class names and DSCP values follow RFC 4594 and the Cisco QoS baseline. The shares follow common design
  guidance: real-time traffic at most about 33% of the link, best effort at least 25%, scavenger about 1%.
  The individual percentages are representative, not a standard.
- **Artificial loss %**: a testing aid. The client randomly drops this share of UDP packets in both
  directions. It can be changed while a test runs, for example raised to watch tunnels drop and then
  lowered to see whether they recover. In the CLI, type `loss 20` and press Enter.
- **IP lookup** of the client, done by both sides:
  - The client's *Connection* tab shows the local address, the address the server sees, whether NAT is in
    between, and the client's public IP details: reverse DNS, ISP/ASN, approximate location and timezone.
  - The server shows each client's reverse DNS, ISP and location in its log and sessions table.
  - Details for public addresses come from `ipinfo.io`. Private and LAN addresses are classified locally
    and never sent out.
  - Online lookups can be turned off: a checkbox in both GUIs, `--no-ip-lookup` for the CLI and the
    console server.
- **CSV logging**: writes one row per second.

**CLI**: `LinkTestCli.exe server.example.com --udp --up 20 --down 50 --profile voip --time 0 --tunnels all --csv log.csv`

## Diagnostics

- **Path MTU:** when a test starts, the client finds the largest packet that gets through unfragmented in
  each direction, using don't-fragment probes.
  - It recommends a tunnel MTU and TCP MSS for common VPN types: WireGuard, IPsec ESP (AES-GCM and
    AES-CBC/SHA1, with NAT-T), OpenVPN UDP and GRE.
  - Results appear on the *Path* tab.
  - Turn it off in Settings, or with `--no-mtu` in the CLI.
- **Bufferbloat:** the first few seconds of a test carry heartbeats only (*Idle baseline*, 3 s by default).
  - The idle round-trip time is then compared with the round-trip time under load.
  - The grade runs from A+ (under 5 ms extra) through A, B, C and D to F (400 ms or more extra).
- **Traceroute:** an MTR-style trace to the server, with loss and RTT per hop. It runs at the start and again
  when loss of 2% or more, a stall of 1 s or more, or a tunnel drop appears (at most once a minute).
  - The trace names the hop where loss starts and persists to the end of the path.
  - Loss on a single hop that doesn't continue to later hops is usually ICMP rate limiting, not real loss.
  - Many servers block ping, so the last hop may not answer.

## Development

- `dotnet test tests/LinkTest.Tests` runs about 50 unit and end-to-end tests. The end-to-end tests start a
  real server in-process.
- `.github/workflows/ci.yml` builds and runs the tests on every push and pull request.
- The release workflow runs the tests before it publishes anything.
- To release: add a section for the new version to `CHANGELOG.md` (it becomes the release notes), bump
  `<Version>` in `Directory.Build.props`, then push a `vX.Y.Z` tag.

## Android

Install `LinkTest-Client-android.apk` from the latest release. Android asks you to allow installing apps
from your browser or file manager the first time. Then:

- **Live** tab: tiles, charts, and an artificial-loss slider that works while a test runs.
- **Settings** tab: server, traffic, DSCP (including Simulate) and tunnel options.
- **Details** tab: tunnels, traffic classes, and connection and IP information.
- **START / STOP** is in the top bar, so it is available from every tab.

DSCP marking works without special permissions on Android. The screen stays on during a test (this can be
turned off in Settings), because Android may pause apps that are in the background.

**Updates:** the app checks GitHub, downloads the new APK and opens the Android installer; you confirm the
install. Every release APK is signed with the same release key, which is stored in the repository secrets
`ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD` and `ANDROID_KEY_ALIAS`. Without that key, installed
copies cannot be updated.

## Updates

The programs update themselves from this repository's **GitHub Releases**.

- **Client**: checks at startup (this can be turned off in the version menu at the bottom right). When a
  newer release exists, a banner offers *Install and restart*.
- **Server GUI**: shows an update link in the status bar. It can install updates automatically when no
  tests are running, and it restarts the server afterwards.
- **Server**: logs a notice when an update is available. `LinkTestServer --update` installs it now.
  `--auto-update` checks every 6 hours, installs when no tests are running, and restarts with the same
  arguments. `--no-update-check` disables checking.
- **CLI**: `LinkTestCli --update`.
- **Offline**: `--update-from LinkTest-Server-win-x64.zip` (server and CLI) installs from a downloaded
  release zip.

Every update step is logged to `%LOCALAPPDATA%\LinkTest\update.log`.

Downloads are verified against the SHA-256 digest that GitHub publishes for each release asset. Files in use
are renamed to `*.old` and removed the next time the program starts. If something fails, the update is
rolled back. A program can only update itself when its folder is writable; under `Program Files`, run it as
Administrator.

**Publishing a release:** bump `<Version>` in `Directory.Build.props`, commit, then push a matching tag:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

`.github/workflows/release.yml` builds self-contained single-file exes and attaches
`LinkTest-Client-win-x64.zip`, `LinkTest-Server-win-x64.zip` and `LinkTest-Cli-win-x64.zip` to the
release. The tag sets the version, so running apps see it as new.

## How it works

```
client                                         server
  │ TCP control (JSON lines): hello/config ─────▶ │  session id, rate caps
  │ ◀──────────────────────────── welcome         │
  │ data: UDP datagrams or N TCP streams  ◀─────▶ │  both directions at the configured rates
  │ UDP tunnel probes (keepalive/handshake) ────▶ │  echoed back unchanged
```

Every data packet has a 48-byte header: sequence number, send timestamp, and an echo of the peer's latest
timestamp plus the time it was held. RTT therefore needs no clock synchronisation. Every 100 ms a packet also
carries a **stats report**: the sender's cumulative view of what it has *received* (packets, bytes, loss,
reordering, duplicates, jitter, longest gap, TCP retransmits). This report is the real payload. The rest of
each packet is random filler that brings the stream up to the requested rate. Random filler also defeats WAN
compression.

- The **upload** figures (client → server) are measured by the server and reported back inside the download
  stream. The **download** figures are measured by the client.
- If no data flows for 50 ms, a heartbeat is sent, so the stats and RTT keep updating even at very low rates.
- If the reports stop arriving, the upload tile turns red. This itself means the link is in trouble.
- **Longest gap** is the longest time with no arrivals. It reveals micro-outages that averages hide.
- **MOS** is an E-model (G.107) estimate for VoIP calls, based on RTT, jitter and loss.

### Tunnel simulations

Each enabled tunnel profile runs a state machine that sends **real probes over the link under test**, using
the timers of that technology:

- **Up**: after *keepalive interval* idle, a DPD/keepalive probe is sent. Each unanswered probe is
  retransmitted after *probe timeout* × *backoff*ⁿ. After *max missed* consecutive misses, the tunnel goes **DOWN**.
- **Down**: after *reconnect delay*, the handshake starts. It needs *N* request/response round trips, in
  order. Each message is retransmitted with its own timeout and backoff, and the attempt fails after
  *transmissions*. Handshake messages use the configured size: large IKE_AUTH or TLS packets suffer more
  from loss and fragmentation.
- **Stable**: the tunnel stayed up for the *stable hold time*. Dropping sooner counts as a **flap**.

Presets cover IKEv2 (Cisco-like and strongSwan defaults), WireGuard, OpenVPN UDP and TCP, GRE keepalives,
BFD and SD-WAN BFD. All parameters can be edited in the client (*Edit…*). TCP-transport tunnels send their
probes over the control connection, so loss appears as TCP retransmission delay rather than as lost probes.

These are timer-faithful models, not real VPN stacks. They show whether the loss pattern on this link would
trip each technology's failure detection and whether re-establishment succeeds. They do not reproduce
vendor-specific behaviour such as on-demand DPD being suppressed by data traffic.

## Security notes

- The server sends UDP only to the IP address that owns the session's TCP control connection, so it cannot
  be used to reflect traffic at third parties.
- Use `--key` and `--max-mbps` on servers reachable from the internet. Traffic is not encrypted, but it
  contains nothing except test data and statistics.
