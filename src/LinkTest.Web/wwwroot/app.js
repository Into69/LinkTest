// LinkTest browser test. Talks to the LinkTest server over a control WebSocket (/ws/control); TCP traffic runs
// on bulk WebSockets (/ws/bulk), UDP-style traffic on an unreliable, unordered WebRTC data channel.
// Packet format and pacing rules match LinkTest.Web (UdpMeter.cs, Pacing.cs).
'use strict';

const $ = id => document.getElementById(id);
const PKT_DATA = 1, PKT_ECHO_REQ = 2, PKT_ECHO_REPLY = 3, HEADER = 13;

// ───────────── Storage (best effort) ─────────────
const store = {
  get(k, area = 'local') { try { return (area === 'local' ? localStorage : sessionStorage).getItem('linktest.' + k); } catch { return null; } },
  set(k, v, area = 'local') { try { (area === 'local' ? localStorage : sessionStorage).setItem('linktest.' + k, v); } catch { } },
};

// ───────────── Presets ─────────────
const PRESETS = {
  speedtest: { label: 'Speed test', protocol: 'speed', streams: 6, downSec: 10, upSec: 10, baselineSec: 2,
            hint: 'Finds your maximum download and upload: idle latency first, then 10 s of download and 10 s of upload over 6 TCP connections, with latency measured under load.' },
  tcp:    { label: 'TCP load', protocol: 'tcp', profile: 'constant', upMbps: 20, downMbps: 50, durationSec: 20, streams: 4,
            hint: 'Steady TCP traffic both ways at the rates you set, over 4 connections.' },
  video:  { label: 'Video call', protocol: 'udp', profile: 'video', upMbps: 2.5, downMbps: 2.5, durationSec: 60, packetSize: 1200,
            hint: 'A 2.5 Mbps HD video call each way: 30 frames/s with a larger key frame every second.' },
  voip:   { label: 'VoIP call', protocol: 'udp', profile: 'voip', upMbps: 0.08, downMbps: 0.08, durationSec: 60,
            hint: 'One G.711 voice call each way (a 200-byte packet every 20 ms). Watch loss, jitter and MOS.' },
  game:   { label: 'Gaming', protocol: 'udp', profile: 'constant', upMbps: 0.25, downMbps: 0.5, durationSec: 60, packetSize: 200,
            hint: 'Small, frequent packets like an online game. Latency and jitter matter more than throughput.' },
  mixed:  { label: 'Mixed', protocol: 'mixed', profile: 'constant', upMbps: 10, downMbps: 20, durationSec: 30, udpSharePct: 30, streams: 2,
            hint: '30% UDP-style real-time traffic sharing the link with 70% TCP bulk traffic.' },
  custom: { label: 'Custom', hint: 'Your own settings.' },
};
const DEFAULTS = { protocol: 'udp', profile: 'constant', upMbps: 5, downMbps: 10, durationSec: 30, baselineSec: 3, packetSize: 1200,
                   streams: 1, udpSharePct: 30, burstOnMs: 200, burstOffMs: 800, downSec: 10, upSec: 10 };
const FIELDS = Object.keys(DEFAULTS);

function readForm() {
  const c = {};
  for (const f of FIELDS) {
    const el = $(f);
    c[f] = el.tagName === 'SELECT' ? el.value : Number(el.value);
    if (el.tagName !== 'SELECT' && !Number.isFinite(c[f])) c[f] = DEFAULTS[f];
  }
  return c;
}
function writeForm(c) { for (const f of FIELDS) if (c[f] !== undefined) $(f).value = c[f]; updateVisibility(); }

function updateVisibility() {
  const p = $('protocol').value, prof = $('profile').value, speed = p === 'speed';
  document.querySelectorAll('[data-for]').forEach(l => {
    const f = l.dataset.for;
    l.hidden = f === 'speed' ? !speed : f === 'rates' ? speed
      : f === 'udp' ? (speed || p === 'tcp' || prof === 'voip') : f === 'tcp' ? p === 'udp' : f === 'mixed' ? p !== 'mixed'
      : f === 'bursty' ? (speed || prof !== 'bursty') : false;
  });
  if (!test) { buildTiles(speed ? 'speed' : 'normal'); $('lossCard').hidden = speed; }
}

function applyPreset(name, save = true) {
  const p = PRESETS[name];
  if (!p) return;
  if (name !== 'custom') writeForm({ ...DEFAULTS, ...p });
  document.querySelectorAll('#presets button').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.preset === name)));
  $('presetHint').textContent = p.hint;
  if (save) store.set('preset', name);
}

// ───────────── Help (ⓘ) ─────────────
let help = {};
function showHelp(key) {
  const h = help[key];
  if (!h) return;
  $('helpTitle').textContent = h.title;
  $('helpWhat').textContent = h.what;
  $('helpIdeal').textContent = h.ideal ? 'Ideal: ' + h.ideal : '';
  const ul = $('helpRanges');
  ul.replaceChildren(...h.ranges.map(r => {
    const li = document.createElement('li');
    li.className = r.level;
    li.textContent = (r.level === 'info' ? 'Note' : r.level[0].toUpperCase() + r.level.slice(1)) + ': ' + r.text;
    return li;
  }));
  $('helpDialog').showModal();
}
document.addEventListener('click', e => {
  const b = e.target.closest('button.info');
  if (b) showHelp(b.dataset.help);
});

// ───────────── Tiles ─────────────
const TILE_SETS = {
  normal: [
    ['up', 'Upload', 'tile.upload'], ['down', 'Download', 'tile.download'], ['rtt', 'Round-trip time', 'tile.rtt'],
    ['jitter', 'Jitter ↑ / ↓', 'tile.jitter'], ['loss', 'Loss ↑ / ↓', 'tile.loss'], ['gap', 'Longest gap ↑ / ↓', 'tile.gap'],
    ['mos', 'VoIP MOS', 'tile.mos'], ['bloat', 'Bufferbloat', 'tile.bloat'], ['elapsed', 'Elapsed', 'tile.elapsed'],
  ],
  speed: [
    ['down', 'Download', 'tile.download'], ['up', 'Upload', 'tile.upload'], ['rttIdle', 'Latency idle', 'tile.rtt'],
    ['rttDown', 'Latency downloading', 'tile.bloat'], ['rttUp', 'Latency uploading', 'tile.bloat'], ['bloat', 'Bufferbloat', 'tile.bloat'],
    ['elapsed', 'Elapsed', 'tile.elapsed'],
  ],
};
let TILES = TILE_SETS.normal, tileMode = '';
const tiles = {};
function buildTiles(mode = 'normal') {
  if (mode === tileMode) return;
  tileMode = mode;
  TILES = TILE_SETS[mode];
  const box = $('tiles');
  box.replaceChildren();
  for (const k of Object.keys(tiles)) delete tiles[k];
  for (const [id, title, key] of TILES) {
    const t = document.createElement('div');
    t.className = 'tile';
    t.innerHTML = `<div class="head"><span></span><button class="info" aria-label="About this figure">i</button></div><div class="value">–</div><div class="sub">&nbsp;</div>`;
    t.querySelector('.head span').textContent = title;
    t.querySelector('button').dataset.help = key;
    box.append(t);
    tiles[id] = t;
  }
}
function setTile(id, value, sub = '', level = '') {
  const t = tiles[id];
  if (!t) return;
  t.querySelector('.value').textContent = value;
  t.querySelector('.sub').textContent = sub || ' ';
  t.className = 'tile' + (level ? ' ' + level : '');
}
const fin = v => typeof v === 'number' && Number.isFinite(v);
const median = v => { if (!v.length) return NaN; const a = [...v].sort((x, y) => x - y); return a[Math.floor(a.length / 2)]; };
const mean = v => v.length ? v.reduce((a, b) => a + b, 0) / v.length : NaN;
const fmt = (v, d = 1) => fin(v) ? v.toFixed(d) : '–';
const mbps = v => !fin(v) ? '–' : v >= 100 ? v.toFixed(0) + ' Mbps' : v >= 1 ? v.toFixed(1) + ' Mbps' : (v * 1000).toFixed(0) + ' kbps';
const level = (v, fair, poor) => !fin(v) ? '' : v >= poor ? 'poor' : v >= fair ? 'fair' : 'good';
const pair = (a, b, d) => `${fmt(a, d)} / ${fmt(b, d)}`;
function clock(sec) {
  sec = Math.floor(sec);
  const h = Math.floor(sec / 3600), m = Math.floor(sec / 60) % 60, s = sec % 60;
  return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}
function resetTiles() { for (const [id] of TILES) setTile(id, '–'); }

// ───────────── Charts ─────────────
class Chart {
  constructor(canvas, series) {
    this.c = canvas; this.series = series.map(s => ({ ...s, data: [] })); this.max = 300;
    new ResizeObserver(() => this.draw()).observe(canvas);
  }
  clear() { this.series.forEach(s => s.data = []); this.draw(); }
  push(values) {
    this.series.forEach((s, i) => { s.data.push(values[i]); if (s.data.length > this.max) s.data.shift(); });
    this.draw();
  }
  draw() {
    const c = this.c, dpr = window.devicePixelRatio || 1, w = c.clientWidth, h = c.clientHeight || 180;
    if (!w) return;
    if (c.width !== Math.round(w * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    const css = getComputedStyle(document.documentElement), col = n => css.getPropertyValue(n).trim();
    const left = 44, right = 8, top = 22, bottom = 18, pw = w - left - right, ph = h - top - bottom;
    let ymax = 0;
    for (const s of this.series) for (const v of s.data) if (fin(v)) ymax = Math.max(ymax, v);
    ymax = niceMax(ymax || 1);
    g.font = '11px "Segoe UI", system-ui, sans-serif';
    g.fillStyle = col('--muted'); g.strokeStyle = col('--grid'); g.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
      const y = top + ph - ph * i / 4;
      g.beginPath(); g.moveTo(left, y + .5); g.lineTo(left + pw, y + .5); g.stroke();
      g.textAlign = 'right'; g.textBaseline = 'middle';
      g.fillText(shortNum(ymax * i / 4), left - 6, y);
    }
    const n = Math.max(2, ...this.series.map(s => s.data.length));
    let lx = left;
    for (const s of this.series) {
      g.strokeStyle = col(s.color); g.lineWidth = s.width || 1.6; g.beginPath();
      let pen = false;
      s.data.forEach((v, i) => {
        if (!fin(v)) { pen = false; return; }
        const x = left + pw * i / (n - 1), y = top + ph - ph * Math.min(v, ymax) / ymax;
        pen ? g.lineTo(x, y) : g.moveTo(x, y); pen = true;
      });
      g.stroke();
      g.fillStyle = col(s.color); g.fillRect(lx, 6, 10, 3);
      g.fillStyle = col('--muted'); g.textAlign = 'left'; g.textBaseline = 'middle';
      g.fillText(s.name, lx + 14, 8); lx += 22 + g.measureText(s.name).width;
    }
  }
}
function niceMax(v) { const p = Math.pow(10, Math.floor(Math.log10(v))); for (const m of [1, 2, 2.5, 5, 10]) if (v <= m * p) return m * p; return 10 * p; }
function shortNum(v) { return v >= 1000 ? (v / 1000).toFixed(v >= 10000 ? 0 : 1) + 'k' : v >= 10 ? v.toFixed(0) : v >= 1 ? v.toFixed(1) : v.toFixed(2); }

let charts;
function buildCharts() {
  charts = {
    tput: new Chart($('chartTput'), [{ name: 'Upload', color: '--up' }, { name: 'Download', color: '--down' }, { name: 'Sent up', color: '--tx', width: 1 }]),
    lat: new Chart($('chartLat'), [{ name: 'RTT', color: '--rtt' }, { name: 'RTT max', color: '--rttmax', width: 1 }, { name: 'Jitter ↑', color: '--up' }, { name: 'Jitter ↓', color: '--down' }]),
    loss: new Chart($('chartLoss'), [{ name: 'Loss ↑', color: '--up' }, { name: 'Loss ↓', color: '--bad' }]),
  };
}

// ───────────── Pacing (same rules as Pacing.cs) ─────────────
function pacer(profile, mbps, packetSize, onMs, offMs) {
  const bpm = Math.max(0, mbps) * 1e6 / 8 / 1000;
  onMs = Math.max(10, onMs); offMs = Math.max(0, offMs);
  const size = profile === 'voip' ? 200 : packetSize;
  const calls = Math.max(1, Math.round(bpm * 20 / 200));
  const due = ms => {
    if (ms < 0 || bpm <= 0) return 0;
    switch (profile) {
      case 'bursty': { const cyc = onMs + offMs, k = Math.floor(ms / cyc), rem = ms - k * cyc; return bpm * cyc / onMs * (k * onMs + Math.min(rem, onMs)); }
      case 'voip': return (Math.floor(ms / 20) + 1) * calls * 200;
      case 'video': { const p = bpm * 1000 / (30 - 1 + 5), frames = Math.floor(ms * 30 / 1000) + 1, iFrames = Math.floor((frames - 1) / 30) + 1; return frames * p + 4 * p * iFrames; }
      default: return bpm * ms;
    }
  };
  return { size, due };
}

// ───────────── Connection ─────────────
let info = null, ctrl = null, ready = false, test = null;
// Round-trip times of the control connection while idle (sent with the test so the server can plan for latency).
const idleRtt = [];
setInterval(() => { if (ready && !test) send({ t: 'ping', id: performance.now() }); }, 2000);

function send(o) { if (ctrl && ctrl.readyState === WebSocket.OPEN) ctrl.send(JSON.stringify(o)); }
function status(text, err = false) { const s = $('status'); s.textContent = text; s.classList.toggle('err', err); }
const wsUrl = path => (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + path;

function connect() {
  ready = false;
  ctrl = new WebSocket(wsUrl('/ws/control'));
  ctrl.onopen = () => send({ t: 'hello', key: $('key').value });
  ctrl.onmessage = e => { try { onMessage(JSON.parse(e.data)); } catch (err) { console.error(err); } };
  ctrl.onclose = () => {
    ready = false;
    if (test) finishTest('Connection to the server lost');
    status('Disconnected from the server - reconnecting…', true);
    setTimeout(connect, 3000);
  };
}

function onMessage(m) {
  switch (m.t) {
    case 'welcome':
      ready = true;
      send({ t: 'ping', id: performance.now() });
      if (!test) status(`Ready · server ${m.version}` + (m.udp ? '' : ' · UDP-style tests unavailable on this server'));
      if ($('key').value) store.set('key', $('key').value, 'session');
      updateStart();
      break;
    case 'error':
      status(m.msg, true);
      if (test && !test.go) cleanupTest();
      if (/key/i.test(m.msg)) { $('keyRow').hidden = false; $('key').focus(); }
      updateStart();
      break;
    case 'started': setupTest(m).catch(err => { status(err.message, true); send({ t: 'stop' }); cleanupTest(); }); break;
    case 'answer': test?.onAnswer?.(m.sdp); break;
    case 'cand': test?.onCandidate?.(m.c, m.mid); break;
    case 'go': beginLoad(); break;
    case 'pong':
      if (!fin(m.id)) break;
      if (test) test.rtt.push(performance.now() - m.id);
      else { idleRtt.push(performance.now() - m.id); if (idleRtt.length > 5) idleRtt.shift(); }
      break;
    case 'sample': onSample(m); break;
    case 'done': onDone(m); break;
  }
}

// ───────────── Running a test ─────────────
function startTest() {
  if (!ready || test) return;
  const cfg = readForm();
  const maxUdp = info?.maxUdpMbps ?? 25;
  if (cfg.protocol === 'udp' && (cfg.upMbps > maxUdp || cfg.downMbps > maxUdp)) {
    status(`UDP-style tests go up to ${maxUdp} Mbps each way in a browser. Use a TCP or mixed test for higher rates.`, true);
    return;
  }
  store.set('settings', JSON.stringify(cfg));
  $('result').hidden = true;
  buildTiles(cfg.protocol === 'speed' ? 'speed' : 'normal');
  resetTiles();
  Object.values(charts).forEach(c => c.clear());
  test = { cfg, go: false, rtt: [], bulk: [], cands: [], remoteSet: false, stopRequested: false };
  updateStart();
  status('Setting up…');
  const rtt = median(idleRtt);
  send({ t: 'start', config: { ...cfg, rttMs: fin(rtt) ? Math.round(rtt) : null } });
}

async function setupTest(st) {
  const t = test;
  if (!t) return;
  Object.assign(t, { token: st.token, st });
  const jobs = [];
  if (st.udp) jobs.push(setupRtc(t));
  const tcp = st.upTcp + st.downTcp > 0;
  for (let i = 0; tcp && i < st.streams; i++) {
    if (st.upTcp > 0) jobs.push(openBulk(t, 'up'));
    if (st.downTcp > 0) jobs.push(openBulk(t, 'down'));
  }
  status(st.udp ? 'Opening the WebRTC connection…' : 'Connecting…');
  await Promise.all(jobs);
  if (test !== t) return;
  send({ t: 'go' });
}

function setupRtc(t) {
  return new Promise((resolve, reject) => {
    const pc = t.pc = new RTCPeerConnection({ iceServers: [] });
    const dc = t.dc = pc.createDataChannel('udp', { ordered: false, maxRetransmits: 0 });
    dc.binaryType = 'arraybuffer';
    const timer = setTimeout(() => reject(new Error(
      `The WebRTC connection to the server didn't open. UDP port ${info?.httpPort ?? ''} must be reachable from this device (firewall / port forwarding). Try a TCP test instead.`)), 30000);   // high-latency links need several round trips to connect
    dc.onopen = () => { clearTimeout(timer); resolve(); };
    dc.onmessage = e => onPacket(t, e.data);
    dc.onclose = () => { if (test === t && t.go && !t.stopRequested) status('The WebRTC connection closed', true); };
    pc.onicecandidate = e => { if (e.candidate && e.candidate.candidate) send({ t: 'cand', c: e.candidate.candidate, mid: e.candidate.sdpMid }); };
    t.onAnswer = async sdp => {
      try {
        await pc.setRemoteDescription({ type: 'answer', sdp });
        t.remoteSet = true;
        for (const [c, mid] of t.cands) pc.addIceCandidate({ candidate: c, sdpMid: mid ?? '0' }).catch(() => { });
        t.cands = [];
      } catch (err) { reject(err); }
    };
    t.onCandidate = (c, mid) => {
      if (t.remoteSet) pc.addIceCandidate({ candidate: c, sdpMid: mid ?? '0' }).catch(() => { });
      else t.cands.push([c, mid]);
    };
    pc.createOffer().then(o => pc.setLocalDescription(o)).then(() => send({ t: 'offer', sdp: pc.localDescription.sdp })).catch(reject);
  });
}

function openBulk(t, dir) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl(`/ws/bulk?token=${encodeURIComponent(t.token)}&dir=${dir}`));
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => resolve();
    ws.onerror = () => reject(new Error('Could not open the TCP test connection.'));
    if (dir === 'down') ws.onmessage = e => { t.rxTcp += e.data.byteLength; };
    t.bulk.push({ ws, dir, sent: 0 });
  });
}

function beginLoad() {
  const t = test;
  if (!t || t.go) return;
  const st = t.st, now = performance.now();
  Object.assign(t, {
    go: true, t0: now, loadStart: now + st.baselineSec * 1000,
    txUp: 0, rxTcp: 0, rxUdp: 0, heldBack: 0, seq: 0, udpSent: 0, echoId: 0,
    // download meter
    highest: -1, intervalStart: -1, intervalPk: 0, lostTot: 0, expTot: 0, jit: 0, lastTransit: NaN, lastArr: NaN, maxGap: 0,
    prev: { tcp: 0, udp: 0, tx: 0 }, second: 0,
  });
  t.upPace = pacer(st.profile, st.upUdp, st.packetSize, st.burstOnMs, st.burstOffMs);
  t.upBuf = new ArrayBuffer(t.upPace.size);
  new Uint8Array(t.upBuf)[0] = PKT_DATA;
  t.tcpPace = pacer(st.profile === 'bursty' ? 'bursty' : 'constant', st.upTcp / Math.max(1, st.streams), 1400, st.burstOnMs, st.burstOffMs);
  // Speed test: upload only in its phase, as fast as the link takes it (bounded by the server's cap).
  t.upStart = st.speed ? now + (st.baselineSec + st.downSec) * 1000 : t.loadStart;
  t.sp = { down: [], up: [], idle: [], dlRtt: [], ulRtt: [] };
  t.chunk = new Uint8Array(st.speed ? 64 * 1024 : 16 * 1024);
  crypto.getRandomValues(t.chunk);
  t.timers = [setInterval(() => sendTick(t), 4), setInterval(() => echoTick(t), 100)];
  scheduleSecond(t);
  $('start').disabled = false;
  const slow = st.speed && st.rampSec > 2 ? ` · high latency: ${st.downSec} s each way, the first ${st.rampSec} s left out as TCP ramp-up`
    : st.warmupSec > 0 ? ` · high latency: the first ${st.warmupSec} s are a warm-up` : '';
  t.slowNote = slow;
  status(st.speed ? 'Measuring idle latency…' + slow : `Running · ${st.describe}${slow}` + (st.baselineSec > 0 ? ` · measuring idle latency for ${st.baselineSec} s` : ''));
  if ($('shareLocation').checked && window.isSecureContext && navigator.geolocation) startLocation(t);
  document.addEventListener('visibilitychange', t.onVis = () => { if (document.hidden) t.hiddenAt = performance.now(); });
}

function sendTick(t) {
  if (test !== t || !t.go) return;
  const now = performance.now(), ms = now - t.loadStart;
  if (ms < 0 || (t.st.durationSec > 0 && now - t.t0 >= t.st.durationSec * 1000)) return;   // baseline / test over
  // UDP-style upload over the data channel
  if (t.dc && t.st.upUdp > 0 && t.dc.readyState === 'open') {
    const due = t.upPace.due(ms), size = t.upPace.size, view = new DataView(t.upBuf);
    const maxQueue = Math.max(16 * 1024, t.st.upUdp * 1e6 / 8 * 0.02);   // ~20 ms: see WebTestSession.UdpSendLoop
    if (due - t.udpSent > size * 2000) t.udpSent = due - size * 200;       // after a stall (tab hidden): don't flood
    while (t.udpSent + size <= due) {
      t.udpSent += size;
      if (t.dc.bufferedAmount > maxQueue) { t.heldBack += size; continue; }   // congestion control holding back
      view.setUint32(1, t.seq++, true);
      view.setFloat64(5, performance.now(), true);
      try { t.dc.send(t.upBuf); t.txUp += size; } catch { break; }
    }
  }
  // TCP upload over the bulk WebSockets
  const upMs = now - t.upStart;
  if (t.st.upTcp > 0 && upMs >= 0) {
    const due = t.tcpPace.due(upMs), cap = t.st.upTcp / t.st.streams * 1e6 / 8 * 0.25, len = t.chunk.length;
    const queue = t.st.speed ? 64 * len : 4 * len;       // speed test: keep long, fast paths full (4 MB per connection)
    for (const b of t.bulk) {
      if (b.dir !== 'up' || b.ws.readyState !== WebSocket.OPEN) continue;
      if (due - b.sent > cap + len) b.sent = due - cap;
      while (b.sent + len <= due && b.ws.bufferedAmount < queue) { b.ws.send(t.chunk); b.sent += len; t.txUp += len; }
    }
  }
}

function echoTick(t) {
  if (test !== t || !t.go) return;
  if (t.dc && t.dc.readyState === 'open') {
    const b = new ArrayBuffer(HEADER), v = new DataView(b);
    v.setUint8(0, PKT_ECHO_REQ); v.setUint32(1, t.echoId++, true); v.setFloat64(5, performance.now(), true);
    try { t.dc.send(b); } catch { }
  } else send({ t: 'ping', id: performance.now() });
}

function onPacket(t, buf) {
  if (!(buf instanceof ArrayBuffer) || buf.byteLength < HEADER) return;
  const v = new DataView(buf), now = performance.now(), type = v.getUint8(0);
  if (type === PKT_ECHO_REPLY) { t.rtt.push(now - v.getFloat64(5, true)); return; }
  if (type !== PKT_DATA || !t.go) return;
  const seq = v.getUint32(1, true), transit = now - v.getFloat64(5, true);
  t.rxUdp += buf.byteLength; t.intervalPk++;
  if (seq > t.highest) t.highest = seq;
  if (Number.isFinite(t.lastTransit)) t.jit += (Math.abs(transit - t.lastTransit) - t.jit) / 16;
  t.lastTransit = transit;
  if (Number.isFinite(t.lastArr)) t.maxGap = Math.max(t.maxGap, now - t.lastArr);
  t.lastArr = now;
}

function scheduleSecond(t) {
  const due = t.t0 + (t.second + 1) * 1000;
  t.secTimer = setTimeout(() => {
    if (test !== t) return;
    const exp = Math.max(0, t.highest - t.intervalStart), lost = Math.max(0, exp - t.intervalPk);
    t.lostTot += lost; t.expTot += exp;
    const r = t.rtt.splice(0);
    const m = {
      t: 'sec', i: t.second, txUp: t.txUp - t.prev.tx, rxTcp: t.rxTcp - t.prev.tcp, rxUdp: t.rxUdp - t.prev.udp,
      dLost: lost, dExp: exp, dJit: t.highest >= 0 ? t.jit : null, dGap: t.intervalPk > 0 ? t.maxGap : null,
      dLostTot: t.lostTot, dExpTot: t.expTot,
      rtt: r.length ? r.reduce((a, b) => a + b, 0) / r.length : null, rttMin: r.length ? Math.min(...r) : null,
      rttMax: r.length ? Math.max(...r) : null, rttN: r.length,
    };
    t.prev = { tcp: t.rxTcp, udp: t.rxUdp, tx: t.txUp };
    t.intervalStart = t.highest; t.intervalPk = 0; t.maxGap = 0;
    send(m);
    t.second++;
    scheduleSecond(t);
  }, Math.max(0, due - performance.now()));
}

function startLocation(t) {
  let last = 0;
  t.geo = navigator.geolocation.watchPosition(p => {
    const now = Date.now();
    if (now - last < 10000) return;
    last = now;
    const c = p.coords;
    send({ t: 'loc', lat: c.latitude, lon: c.longitude, acc: c.accuracy, alt: c.altitude, speed: c.speed });
  }, err => status('Location unavailable: ' + err.message, true), { enableHighAccuracy: true, maximumAge: 10000 });
}

function onSample(s) {
  const st = test?.st;
  if (st?.speed) { onSpeedSample(test, s); return; }
  const udp = !!st?.udp;
  setTile('up', mbps(s.up), s.tx != null ? `sent ${mbps(s.tx)}` + (s.upUdp != null && st?.upTcp > 0 && st?.upUdp > 0 ? ` · UDP ${mbps(s.upUdp)} · TCP ${mbps(s.upTcp)}` : '') : '');
  setTile('down', mbps(s.down), s.serverTx != null ? `server sent ${mbps(s.serverTx)}` + (s.heldBack ? ` · ${s.heldBack} Mb held back` : '')
    + (st?.downTcp > 0 && st?.downUdp > 0 ? ` · UDP ${mbps(s.downUdp)} · TCP ${mbps(s.downTcp)}` : '') : '');
  setTile('rtt', fin(s.rtt) ? `${fmt(s.rtt)} ms` : '–', fin(s.rttMin) ? `min ${fmt(s.rttMin)} / max ${fmt(s.rttMax)}` : '', level(s.rtt, 100, 250));
  setTile('jitter', udp ? pair(s.jitUp, s.jitDown) : 'n/a', udp ? 'ms' : 'UDP tests only', udp ? level(Math.max(s.jitUp ?? 0, s.jitDown ?? 0), 10, 30) : '');
  setTile('loss', udp ? pair(s.lossUp, s.lossDown, 2) : 'n/a', udp ? `total ${pair(s.lossUpTotal, s.lossDownTotal, 2)} %` : 'TCP hides loss', udp ? level(Math.max(s.lossUp ?? 0, s.lossDown ?? 0), 0.5, 3) : '');
  setTile('gap', udp ? pair(s.gapUp, s.gapDown, 0) : 'n/a', udp ? 'ms' : '', udp ? level(Math.max(s.gapUp ?? 0, s.gapDown ?? 0), 250, 1000) : '');
  setTile('mos', fin(s.mos) ? fmt(s.mos) : '–', udp ? '' : 'UDP tests only', fin(s.mos) ? (s.mos >= 4 ? 'good' : s.mos >= 3.6 ? 'fair' : 'poor') : '');
  if (s.baseline) setTile('bloat', '…', 'measuring idle RTT');
  else if (s.bloat) setTile('bloat', s.bloat, fin(s.bloatMs) ? `+${fmt(s.bloatMs)} ms under load` : '', /^A/.test(s.bloat) ? 'good' : /^[BC]/.test(s.bloat) ? 'fair' : 'poor');
  else setTile('bloat', '–', st?.baselineSec ? 'measuring…' : 'baseline off');
  setTile('elapsed', clock(s.sec), st?.durationSec > 0 ? `of ${st.durationSec} s` : '');
  charts.tput.push([s.up, s.down, s.tx]);
  charts.lat.push([s.rtt, s.rttMax, s.jitUp, s.jitDown]);
  charts.loss.push([s.lossUp, s.lossDown]);
}


function onSpeedSample(t, s) {
  const st = t.st, sp = t.sp, ramp = st.rampSec ?? 2;
  const inDown = s.sec - st.baselineSec, inUp = s.sec - st.baselineSec - st.downSec;
  if (s.phase === 'idle' && fin(s.rtt)) sp.idle.push(s.rtt);
  if (s.phase === 'download') { if (inDown > ramp && fin(s.down)) sp.down.push(s.down); if (inDown > 1 && fin(s.rtt)) sp.dlRtt.push(s.rtt); }
  if (s.phase === 'upload') { if (inUp > ramp && fin(s.up)) sp.up.push(s.up); if (inUp > 1 && fin(s.rtt)) sp.ulRtt.push(s.rtt); }
  const idle = median(sp.idle);
  if (s.phase === 'download') setTile('down', mbps(s.down), `testing · ${Math.min(inDown, st.downSec)} / ${st.downSec} s`);
  else if (sp.down.length) setTile('down', mbps(mean(sp.down)), 'average');
  if (s.phase === 'upload') setTile('up', mbps(s.up), `testing · ${Math.min(inUp, st.upSec)} / ${st.upSec} s`);
  else if (s.phase === 'download') setTile('up', '…', 'after the download');
  if (sp.up.length && s.phase !== 'upload') setTile('up', mbps(mean(sp.up)), 'average');
  setTile('rttIdle', fin(idle) ? `${fmt(idle)} ms` : '…', s.phase === 'idle' ? 'measuring…' : '', level(idle, 100, 250));
  const dl = mean(sp.dlRtt), ul = mean(sp.ulRtt);
  setTile('rttDown', fin(dl) ? `${fmt(dl)} ms` : '…', fin(dl) && fin(idle) ? `+${fmt(Math.max(0, dl - idle))} ms` : '', level(dl - (idle || 0), 30, 100));
  setTile('rttUp', fin(ul) ? `${fmt(ul)} ms` : '…', fin(ul) && fin(idle) ? `+${fmt(Math.max(0, ul - idle))} ms` : '', level(ul - (idle || 0), 30, 100));
  if (s.bloat) setTile('bloat', s.bloat, fin(s.bloatMs) ? `+${fmt(s.bloatMs)} ms under load` : '', /^A/.test(s.bloat) ? 'good' : /^[BC]/.test(s.bloat) ? 'fair' : 'poor');
  setTile('elapsed', clock(s.sec), `of ${st.durationSec} s`);
  status((s.phase === 'idle' ? 'Measuring idle latency…' : s.phase === 'download' ? `Download test · ${Math.min(inDown, st.downSec)} / ${st.downSec} s`
    : `Upload test · ${Math.min(inUp, st.upSec)} / ${st.upSec} s`) + (t.slowNote || ''));
  charts.tput.push([s.up, s.down, s.tx]);
  charts.lat.push([s.rtt, s.rttMax, null, null]);
  charts.loss.push([null, null]);
}

function stopTest() {
  if (!test) return;
  test.stopRequested = true;
  status('Stopping…');
  send({ t: 'stop' });
  if (!test.go) cleanupTest();
}

function onDone(m) {
  const t = test;
  cleanupTest();
  status('Idle · ' + m.reason + (t?.hiddenAt ? ' · the tab was in the background during the test, which can lower the figures' : ''));
  if (m.file) showResult(m);
  loadHistory();
}

function finishTest(reason) { cleanupTest(); status('Idle · ' + reason, true); }

function cleanupTest() {
  const t = test;
  test = null;
  if (t) {
    (t.timers || []).forEach(clearInterval);
    clearTimeout(t.secTimer);
    if (t.geo != null) navigator.geolocation.clearWatch(t.geo);
    if (t.onVis) document.removeEventListener('visibilitychange', t.onVis);
    for (const b of t.bulk) try { b.ws.close(); } catch { }
    try { t.dc?.close(); } catch { }
    try { t.pc?.close(); } catch { }
  }
  updateStart();
}

function updateStart() {
  const b = $('start');
  b.textContent = test ? 'Stop' : 'Start test';
  b.classList.toggle('stop', !!test);
  b.disabled = !ready && !test;
}

// ───────────── Results ─────────────
const keyQuery = () => $('key').value ? '?key=' + encodeURIComponent($('key').value) : '';
function reportLinks(file, box) {
  const base = `/api/results/${encodeURIComponent(file)}/`;
  for (const [kind, label] of [['report.html', 'Open report'], ['report.pdf', 'PDF report'], ['data.csv', 'CSV']]) {
    const a = document.createElement('a');
    a.href = base + kind + keyQuery();
    a.textContent = label;
    if (kind === 'report.html') a.target = '_blank';
    a.rel = 'noopener';
    box.append(a);
  }
}

function showResult(m) {
  $('result').hidden = false;
  $('resultSummary').textContent = m.summary || '';
  const sr = m.speed;
  $('speedResult').hidden = !sr;
  if (sr) {
    setTile('down', mbps(sr.down), fin(sr.downPeak) ? `average · peak ${mbps(sr.downPeak)}` : 'average');
    setTile('up', mbps(sr.up), fin(sr.upPeak) ? `average · peak ${mbps(sr.upPeak)}` : 'average');
    $('srDown').textContent = fmt(sr.down);
    $('srUp').textContent = fmt(sr.up);
    $('srLat').textContent = `${fmt(sr.idleRtt)} ms idle · ${fmt(sr.downRtt)} ms downloading · ${fmt(sr.upRtt)} ms uploading` + (sr.bloat ? ` · bufferbloat ${sr.bloat}` : '');
  }
  const box = $('resultLinks');
  box.replaceChildren();
  reportLinks(m.file, box);
  box.querySelectorAll('a').forEach(a => a.classList.add('button'));
}

async function loadHistory() {
  const body = document.querySelector('#history tbody'), hint = $('historyHint');
  try {
    const r = await fetch('/api/results', { headers: $('key').value ? { 'X-LinkTest-Key': $('key').value } : {} });
    if (r.status === 403) { body.replaceChildren(); hint.textContent = 'Enter the server key to see saved tests.'; return; }
    const j = await r.json();
    body.replaceChildren(...j.results.map(x => {
      const tr = document.createElement('tr');
      const cells = [x.started, x.client ?? '', x.mode, fmt(x.up), fmt(x.down), x.lossUp != null || x.lossDown != null ? pair(x.lossUp, x.lossDown, 2) : '–',
                     fmt(x.rtt), fmt(x.mos), x.bloat ?? '–'];
      cells.forEach((c, i) => { const td = document.createElement('td'); td.textContent = c; if (i >= 3 && i <= 7) td.className = 'num'; tr.append(td); });
      const td = document.createElement('td');
      reportLinks(x.file, td);
      tr.append(td);
      return tr;
    }));
    hint.textContent = j.results.length ? `${j.results.length} saved test(s), on the server in ${j.folder}` : 'No browser tests saved yet.';
  } catch { hint.textContent = 'Could not load the saved tests.'; }
}

// ───────────── Start-up ─────────────
async function init() {
  buildCharts();
  const pre = $('presets');
  for (const [name, p] of Object.entries(PRESETS)) {
    const b = document.createElement('button');
    b.type = 'button'; b.textContent = p.label; b.dataset.preset = name;
    b.onclick = () => applyPreset(name);
    pre.append(b);
  }
  let saved = null;
  try { saved = JSON.parse(store.get('settings') || 'null'); } catch { }
  const preset = store.get('preset') || 'speedtest';
  if (preset === 'custom' && saved) { writeForm({ ...DEFAULTS, ...saved }); applyPreset('custom', false); }
  else applyPreset(PRESETS[preset] ? preset : 'speedtest', false);
  for (const f of FIELDS) $(f).addEventListener('input', () => { applyPreset('custom'); updateVisibility(); });
  $('key').value = store.get('key', 'session') || '';
  $('key').addEventListener('change', () => {
    // New key: reconnect at once (not through the "connection lost" path).
    if (ctrl && !test) { ctrl.onclose = null; ctrl.close(); status('Connecting…'); connect(); }
    loadHistory();
  });
  $('start').onclick = () => test ? stopTest() : startTest();
  $('refresh').onclick = loadHistory;

  try {
    info = await (await fetch('/api/info')).json();
    $('version').textContent = 'server ' + info.version;
    $('keyRow').hidden = !info.keyRequired;
    $('udpPort').textContent = String(info.httpPort);
    $('maxUdp').textContent = String(info.maxUdpMbps ?? 25);
    const sec = $('security');
    sec.textContent = info.https ? 'HTTPS' : 'HTTP';
    sec.className = 'badge ' + (info.https ? 'secure' : 'plain');
    sec.title = info.https ? 'Encrypted (self-signed server certificate)' : 'Not encrypted';
    if (!info.https && info.httpsPort) {
      const a = document.createElement('a');
      a.href = `https://${location.hostname}:${info.httpsPort}/`;
      a.textContent = 'open the HTTPS page';
      $('httpsLink').append(': ', a);
    }
    if (!info.udp) {
      $('protocol').querySelector('option[value=udp]').disabled = true;
      $('protocol').querySelector('option[value=mixed]').disabled = true;
      applyPreset('speedtest', false);
    }
  } catch { }
  if (!window.isSecureContext) {
    $('shareLocation').disabled = true;
    $('shareLocation').parentElement.title = 'Browsers only allow location on HTTPS pages';
  }
  try { help = await (await fetch('/api/help')).json(); } catch { }
  connect();
  loadHistory();
}
init();
