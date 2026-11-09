const { invoke } = window.__TAURI__.core;
const content = document.getElementById('content');
const ranges = document.getElementById('ranges');

const MEASURES = {
  breaks: { title: 'Breaks per day', unit: 'breaks', value: (d) => d.breaks },
  snoozes: { title: 'Snoozes per day', unit: 'snoozes', value: (d) => d.snoozes },
  minutes: { title: 'Break minutes per day', unit: 'minutes', value: (d) => d.break_seconds / 60 },
};
const VIEWS = { breaks: ['breaks', 'minutes'], snoozes: ['snoozes'] };
const WIDTH = 684;
const HEIGHT = 430;
const LEFT = 38;
const RIGHT = 14;
const AXIS = 24;

let byDate = new Map();
let view = 'breaks';
let span = 14;

const dateKey = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const number = (v) => String(Math.round(v * 10) / 10);
const shortDate = (d) => d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
const escapeText = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

function days() {
  const today = new Date();
  return Array.from({ length: span }, (_, i) => {
    const date = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (span - 1 - i));
    const stats = byDate.get(dateKey(date)) || { breaks: 0, snoozes: 0, break_seconds: 0 };
    return { date, breaks: stats.breaks, snoozes: stats.snoozes, break_seconds: stats.break_seconds };
  });
}

function niceMax(value) {
  if (value <= 1) return 1;
  for (const step of [1, 2, 5, 10, 20, 50, 100, 200, 500]) {
    if (Math.ceil(value / step) <= 5) return Math.ceil(value / step) * step;
  }
  return Math.ceil(value / 1000) * 1000;
}

function roundedBar(x, y, width, height) {
  const r = Math.min(4, width / 2, height);
  return `M${x},${y + height}V${y + r}Q${x},${y} ${x + r},${y}H${x + width - r}Q${x + width},${y} ${x + width},${y + r}V${y + height}Z`;
}

function drawMeasure(measure, top, height, data, band) {
  const values = data.map(measure.value);
  const total = values.reduce((a, b) => a + b, 0);
  const max = niceMax(Math.max(...values));
  const plotTop = top + 34;
  const plotBottom = top + height - 6;
  const width = Math.min(24, Math.max(2, band - 2));

  const bars = values.map((v, i) => {
    if (v <= 0) return '';
    const h = Math.max(2, (v / max) * (plotBottom - plotTop));
    return `<path d="${roundedBar(LEFT + i * band + (band - width) / 2, plotBottom - h, width, h)}" fill="var(--bar)"/>`;
  }).join('');

  return `<text class="title" x="6" y="${top + 18}">${measure.title}</text>
    <text class="note" x="${WIDTH - RIGHT}" y="${top + 18}" text-anchor="end">${number(total)} ${measure.unit} in the last ${span} days</text>
    <rect class="band" x="0" y="${plotTop}" width="${band}" height="${plotBottom - plotTop}" fill="var(--hover)" visibility="hidden"/>
    <line x1="${LEFT}" x2="${WIDTH - RIGHT}" y1="${plotTop}" y2="${plotTop}" stroke="var(--grid)"/>
    <line x1="${LEFT}" x2="${WIDTH - RIGHT}" y1="${plotBottom}" y2="${plotBottom}" stroke="var(--baseline)"/>
    <text class="tick" x="${LEFT - 5}" y="${plotTop + 4}" text-anchor="end">${number(max)}</text>
    <text class="tick" x="${LEFT - 5}" y="${plotBottom + 4}" text-anchor="end">0</text>${bars}`;
}

function drawChart() {
  const data = days();
  const band = (WIDTH - LEFT - RIGHT) / span;
  const measures = VIEWS[view].map((m) => MEASURES[m]);
  const panel = (HEIGHT - AXIS) / measures.length;
  const step = span <= 14 ? 2 : span <= 30 ? 5 : 15;

  let dates = '';
  for (let i = span - 1; i >= 0; i -= step) {
    const center = LEFT + (i + 0.5) * band;
    dates += `<text class="tick" x="${center}" y="${HEIGHT - 7}" text-anchor="middle">${shortDate(data[i].date)}</text>`;
  }

  content.innerHTML = `<svg width="${WIDTH}" height="${HEIGHT}" role="img" aria-label="Chart of ${measures.map((m) => m.title).join(' and ')}. The same numbers are under Raw data.">
    ${measures.map((m, i) => drawMeasure(m, i * panel, panel, data, band)).join('')}${dates}</svg><div id="tip"></div>`;

  const svg = content.querySelector('svg');
  const tip = document.getElementById('tip');
  svg.addEventListener('mousemove', (event) => {
    const rect = svg.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const index = Math.floor((x - LEFT) / band);
    const valid = index >= 0 && index < span && event.clientY - rect.top < HEIGHT - AXIS;
    svg.querySelectorAll('.band').forEach((b) => {
      b.setAttribute('visibility', valid ? 'visible' : 'hidden');
      if (valid) b.setAttribute('x', LEFT + index * band);
    });
    tip.style.display = valid ? 'block' : 'none';
    if (!valid) return;
    const d = data[index];
    tip.innerHTML = `<b>${d.date.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })}</b>
      Breaks: ${d.breaks}<br>Snoozes: ${d.snoozes}<br>Minutes: ${number(d.break_seconds / 60)}`;
    const after = LEFT + (index + 1) * band + 6;
    tip.style.left = `${after + tip.offsetWidth > WIDTH ? LEFT + index * band - tip.offsetWidth - 6 : after}px`;
    tip.style.top = '34px';
  });
  svg.addEventListener('mouseleave', () => {
    tip.style.display = 'none';
    svg.querySelectorAll('.band').forEach((b) => b.setAttribute('visibility', 'hidden'));
  });
}

function drawTable() {
  const rows = [...byDate.values()].reverse().map((d) => {
    const label = new Date(`${d.date}T12:00:00`).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
    return `<tr><td>${escapeText(label)}</td><td>${d.breaks}</td><td>${d.snoozes}</td><td>${number(d.break_seconds / 60)}</td></tr>`;
  }).join('');
  content.innerHTML = `<table><thead><tr><th>Day</th><th>Breaks</th><th>Snoozes</th><th>Minutes</th></tr></thead><tbody>${rows}</tbody></table>`;
}

function draw() {
  ranges.style.visibility = view === 'raw' ? 'hidden' : 'visible';
  if (view === 'raw') drawTable();
  else drawChart();
}

function select(container, button) {
  container.querySelectorAll('button').forEach((b) => b.classList.toggle('on', b === button));
}

document.getElementById('views').addEventListener('click', (event) => {
  if (!event.target.dataset.view) return;
  view = event.target.dataset.view;
  select(event.currentTarget, event.target);
  draw();
});

ranges.addEventListener('click', (event) => {
  if (!event.target.dataset.days) return;
  span = Number(event.target.dataset.days);
  select(event.currentTarget, event.target);
  draw();
});

invoke('history_days').then((list) => {
  byDate = new Map(list.map((d) => [d.date, d]));
  draw();
});
