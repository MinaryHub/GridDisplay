// Tiny chart helpers for the demo dashboards. Deterministic data so every capture looks the same.
function rng(seed) { return () => (seed = (seed * 16807) % 2147483647) / 2147483647; }

function series(seed, n, base, spread) {
  const r = rng(seed); let v = base; const out = [];
  for (let i = 0; i < n; i++) { v += (r() - 0.5) * spread; v = Math.max(base - spread * 3, Math.min(base + spread * 3, v)); out.push(v); }
  return out;
}

function lineChart(el, lines, opts = {}) {
  const W = 1000, H = opts.h || 360, P = 30;
  const all = lines.flatMap(l => l.data), min = opts.min ?? Math.min(...all), max = opts.max ?? Math.max(...all);
  const x = i => P + (W - 2 * P) * i / (lines[0].data.length - 1);
  const y = v => H - P - (H - 2 * P) * (v - min) / (max - min || 1);
  let s = `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">`;
  for (let g = 0; g <= 4; g++) { const gy = P + (H - 2 * P) * g / 4; s += `<line x1="${P}" x2="${W - P}" y1="${gy}" y2="${gy}" stroke="#26304a" stroke-width="1"/>`; }
  for (const l of lines) {
    const pts = l.data.map((v, i) => `${x(i)},${y(v)}`).join(' ');
    if (l.fill) s += `<polygon points="${x(0)},${H - P} ${pts} ${x(l.data.length - 1)},${H - P}" fill="${l.color}" opacity=".15"/>`;
    s += `<polyline points="${pts}" fill="none" stroke="${l.color}" stroke-width="4" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>`;
  }
  el.innerHTML = s + '</svg>';
}

function barChart(el, data, color, opts = {}) {
  const W = 1000, H = opts.h || 360, P = 30, max = Math.max(...data) * 1.1, bw = (W - 2 * P) / data.length;
  let s = `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">`;
  data.forEach((v, i) => {
    const h = (H - 2 * P) * v / max;
    const c = Array.isArray(color) ? color[i % color.length] : color;
    s += `<rect x="${P + i * bw + bw * 0.15}" y="${H - P - h}" width="${bw * 0.7}" height="${h}" rx="6" fill="${c}"/>`;
  });
  el.innerHTML = s + '</svg>';
}

function donut(el, parts) {
  const total = parts.reduce((a, p) => a + p.v, 0); let a0 = -Math.PI / 2, s = '<svg viewBox="0 0 200 200">';
  for (const p of parts) {
    const a1 = a0 + 2 * Math.PI * p.v / total, big = a1 - a0 > Math.PI ? 1 : 0;
    const r = 80, cx = 100, cy = 100;
    s += `<path d="M${cx + r * Math.cos(a0)} ${cy + r * Math.sin(a0)} A${r} ${r} 0 ${big} 1 ${cx + r * Math.cos(a1)} ${cy + r * Math.sin(a1)}" fill="none" stroke="${p.c}" stroke-width="28"/>`;
    a0 = a1;
  }
  el.innerHTML = s + '</svg>';
}

function tick() {
  const c = document.querySelector('.clock'); if (!c) return;
  const d = new Date(), p = n => String(n).padStart(2, '0');
  c.textContent = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}
setInterval(tick, 1000); window.addEventListener('DOMContentLoaded', tick);
