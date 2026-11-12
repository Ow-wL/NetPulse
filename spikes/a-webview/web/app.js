// M0 스파이크 A 시험 장면 스크립트.
'use strict';
const params = new URLSearchParams(location.search);
const root = document.documentElement;
root.dataset.theme = params.get('theme') === 'light' ? 'light' : 'dark';
if (params.get('reduceMotion')) root.classList.add('reduce-motion');
if (params.get('reduceTransparency')) root.classList.add('reduce-transparency');
if (params.get('glassTest')) root.classList.add('glass-test');
const host = window.chrome && window.chrome.webview;
const send = (text) => host && host.postMessage(text);

// ---- 실시간 그래프: 매초 값이 들어오고, 프레임마다 부드럽게 흘러간다 ----
const canvas = document.getElementById('chart');
const ctx = canvas.getContext('2d');
const WINDOW = 60;
const series = [[], [], []];
let lastArrival = performance.now();
const colors = () => root.dataset.theme === 'light' ? ['#0e7490', '#7c3aed', '#be123c'] : ['#5eead4', '#a78bfa', '#fb7185'];

function resize() {
  const r = canvas.getBoundingClientRect(), dpr = devicePixelRatio || 1;
  canvas.width = Math.round(r.width * dpr); canvas.height = Math.round(r.height * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}
addEventListener('resize', resize);

function draw(now) {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  ctx.clearRect(0, 0, w, h);
  const step = w / (WINDOW - 2);
  // 움직임 줄이기에서는 그래프를 흘리지 않고 새 값이 오면 한 번에 바꾼다.
  const shift = root.classList.contains('reduce-motion') ? 0 : Math.min(1, (now - lastArrival) / 1000) * step;
  ctx.strokeStyle = root.dataset.theme === 'light' ? 'rgba(17,24,39,.08)' : 'rgba(255,255,255,.07)';
  ctx.lineWidth = 1;
  for (let i = 1; i < 5; i++) { const y = h * i / 5; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke(); }
  const c = colors();
  series.forEach((s, k) => {
    if (s.length < 2) return;
    ctx.beginPath();
    s.forEach((v, i) => {
      const x = w - (s.length - 1 - i) * step - shift;
      const y = h - 30 - (v / 110) * (h - 60);
      i ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
    });
    ctx.strokeStyle = c[k]; ctx.lineWidth = 2.2; ctx.lineJoin = 'round'; ctx.stroke();
    const g = ctx.createLinearGradient(0, 0, 0, h);
    g.addColorStop(0, c[k] + '33'); g.addColorStop(1, c[k] + '00');
    ctx.lineTo(w - shift, h); ctx.lineTo(w - (s.length - 1) * step - shift, h); ctx.closePath();
    ctx.fillStyle = g; ctx.fill();
  });
}

// ---- 프레임 간격 기록 (P2) ----
let frameTimes = [], lastFrame = 0, recording = null;
function loop(now) {
  if (lastFrame && recording) frameTimes.push(now - lastFrame);
  lastFrame = now;
  draw(now);
  requestAnimationFrame(loop);
}
function startRecording(label, ms) {
  return new Promise(resolve => {
    frameTimes = []; recording = label;
    setTimeout(() => {
      const t = frameTimes.slice().sort((a, b) => a - b);
      const avg = t.length ? 1000 / (t.reduce((a, b) => a + b, 0) / t.length) : 0;
      const p95 = t.length ? t[Math.floor(t.length * 0.95)] : 0;
      send(`frames|${label}|${avg.toFixed(1)}|${p95.toFixed(1)}|${t.length}`);
      recording = null; resolve();
    }, ms);
  });
}

// ---- 본체에서 오는 메시지 ----
if (host) host.addEventListener('message', (e) => {
  const m = e.data;
  if (!m || typeof m !== 'object') return;
  if (m.type === 'round') {
    m.series.forEach((v, k) => { series[k].push(v); if (series[k].length > WINDOW) series[k].shift(); });
    lastArrival = performance.now();
    document.getElementById('round').textContent = String(m.index);
    document.getElementById('latency').textContent = `${m.series[2].toFixed(0)} ms`;
    document.getElementById('late').textContent = `예정 대비 ${m.late} ms`;
    document.getElementById('status-text').textContent = m.series[2] > 80 ? '지연 급증' : '안정적';
  } else if (m.type === 'probes') {
    document.getElementById('probes').textContent = m.text;
  }
});

// ---- 포인터를 따라가는 하이라이트 ----
document.addEventListener('pointermove', (e) => {
  if (root.classList.contains('reduce-motion')) return;
  for (const el of document.querySelectorAll('.glass')) {
    const r = el.getBoundingClientRect();
    el.style.setProperty('--mx', `${e.clientX - r.left}px`);
    el.style.setProperty('--my', `${e.clientY - r.top}px`);
  }
});

// ---- 화면 전환 ----
function showPage(name) {
  const swap = () => {
    for (const p of document.querySelectorAll('.page')) p.classList.toggle('hidden', p.id !== `page-${name}`);
    for (const b of document.querySelectorAll('.nav-item[data-page]')) b.classList.toggle('active', b.dataset.page === name);
  };
  if (!document.startViewTransition || root.classList.contains('reduce-motion')) { swap(); return Promise.resolve(); }
  root.classList.add('page-switching');
  const t = document.startViewTransition(swap);
  return t.finished.finally(() => root.classList.remove('page-switching'));
}
for (const b of document.querySelectorAll('.nav-item[data-page]')) b.addEventListener('click', () => showPage(b.dataset.page));

// ---- 테마 전환: 누른 지점에서 원형으로 퍼진다 ----
function toggleTheme(x, y) {
  const flip = () => { root.dataset.theme = root.dataset.theme === 'light' ? 'dark' : 'light'; };
  if (!document.startViewTransition || root.classList.contains('reduce-motion')) { flip(); return Promise.resolve(); }
  root.style.setProperty('--rx', `${x}px`); root.style.setProperty('--ry', `${y}px`);
  root.classList.add('theme-switching');
  const t = document.startViewTransition(flip);
  return t.finished.finally(() => root.classList.remove('theme-switching'));
}
document.getElementById('theme-toggle').addEventListener('click', (e) => toggleTheme(e.clientX, e.clientY));
document.getElementById('mark').addEventListener('click', () => send('mark|user'));

// ---- 시작 ----
resize();
requestAnimationFrame((t) => {
  lastFrame = t; requestAnimationFrame(loop);
  document.getElementById('ready').setAttribute('data-ok', '1');
  send('ready');
});

// ---- 벤치마크 순서: 정지 장면 → 테마 반복 전환 → 화면 전환 ----
if (params.get('bench')) {
  (async () => {
    await new Promise(r => setTimeout(r, 4000));
    await startRecording('scene', 10000);
    const themeRun = startRecording('theme-toggle', 6000);
    for (let i = 0; i < 6; i++) { await toggleTheme(innerWidth - 120, innerHeight - 60); await new Promise(r => setTimeout(r, 400)); }
    await themeRun;
    const pageRun = startRecording('page-switch', 6000);
    for (let i = 0; i < 6; i++) { await showPage(i % 2 ? 'monitor' : 'diagnose'); await new Promise(r => setTimeout(r, 500)); }
    await pageRun;
    await showPage('monitor');
    send('mark|bench-done');
  })();
}
