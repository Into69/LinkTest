# LinkTest

Link quality monitor for Windows. A **server** and a **client** exchange paced traffic in both directions
and measure throughput, loss, reordering, jitter, RTT and stalls in near real time. They also simulate how
different **VPN tunnel types** would behave on the link: would they drop, and would they come back up and stay up?

| Program | What it is |
|---|---|
| `LinkTestServer.exe` | Console server. Run it on the far end of the link. |
| `LinkTestClient.exe` | Windows GUI client. Choose the server, the rates and the traffic type, then watch live stats. |
| `LinkTestCli.exe` | Headless client with the same engine, for scripting and long-term monitoring. |

## Build

Requires the .NET 8 SDK.

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
  (kernel retransmissions and RTO timeouts are read with `SIO_TCP_INFO`).
- **Upload / TX and Download / RX rates** are set separately. A rate of 0 sends heartbeats only, about 1 kbps
  (monitoring mode).
- **Packet size**, **TCP parallel streams** and **duration** (0 = until stopped).
- **Traffic profile**: Constant, IMIX, Bursty (on/off), Random (Poisson), VoIP (G.711 frames), Video (30 fps with I-frames).
- **DSCP marking** through Windows qWAVE. This requires running as Administrator.
- **Artificial loss %**: a testing aid. The client randomly drops this share of packets in both directions.
- **CSV logging**: writes one row per second.

**CLI**: `LinkTestCli.exe server.example.com --udp --up 20 --down 50 --profile voip --time 0 --tunnels all --csv log.csv`

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
