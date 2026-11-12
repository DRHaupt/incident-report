// Hazmat: the ERG 2024 guidebook (from the Field Toolkit) searched on the phone, and initial isolation and
// protective-action zones drawn on the map. The data is downloaded once and kept for offline use.

import { app, fitTo, INTERACTIVE, myPositionOrCentre } from './app.js';
import { getJson, postJson } from './api.js';
import * as C from './coords.js';
import { h, row, button, note, heading, segmented, toast, add, set, icon } from './ui.js';

let data = null, loading = null;

/** Loads the ERG data (once; the service worker keeps a copy for offline use). */
export async function loadErg() {
  if (data) return data;
  loading ??= getJson('/api/erg').then((d) => {
    data = d;
    data.byId = new Map();
    for (const m of d.materials) if (m.id) data.byId.set(m.id, [...(data.byId.get(m.id) ?? []), m]);
    data.guideByNumber = new Map(d.guides.map((g) => [g.number, g]));
    return data;
  }).finally(() => { loading = null; });
  return loading;
}

// ------------------------------------------------------------------ search (same rules as the Field Toolkit)

const norm = (s) => s.toLowerCase().normalize('NFKD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9 ]+/g, ' ').replace(/\s+/g, ' ').trim();

/** "1203", "UN1203", "NA 1993" → materials with that ID; "guide 128" → its materials; otherwise name words. */
export function searchErg(q, max = 60) {
  if (!data) return [];
  const t = q.trim();
  const id = t.match(/^(?:un|na)?\s*(\d{4})$/i);
  if (id) return data.byId.get(id[1]) ?? [];
  const guide = t.match(/^(?:guide|erg)\s*(\d{3})p?$/i);
  if (guide) return data.materials.filter((m) => m.guide === guide[1]).slice(0, max);
  const words = norm(t).split(' ').filter(Boolean);
  if (!words.length) return [];
  const hits = [];
  for (const m of data.materials) {
    const n = norm(m.name);
    if (!words.every((w) => n.includes(w))) continue;
    hits.push({ m, score: (n === words.join(' ') ? 3 : 0) + (n.startsWith(words[0]) ? 2 : 0) + (m.highlighted ? 0.5 : 0) });
  }
  return hits.sort((a, b) => b.score - a.score || a.m.name.localeCompare(b.m.name)).slice(0, max).map((x) => x.m);
}

/** True when a search-box query looks like a UN/NA number, so the normal search can offer the hazmat match. */
export const looksLikeUnNumber = (q) => /^(?:un|na)\s*\d{4}$|^\d{4}$/i.test(q.trim());

// ------------------------------------------------------------------ distances

/** "30 m (100 ft)" → 30; "0.2 km (0.1 mi)" → 200; anything else (e.g. "Refer to Table 3") → null. */
export function meters(text) {
  const m = (text ?? '').match(/([\d.,]+)\s*(km|m)\b/i);
  if (!m) return null;
  const v = parseFloat(m[1].replace(',', ''));
  return Number.isFinite(v) ? Math.round(m[2].toLowerCase() === 'km' ? v * 1000 : v) : null;
}

/** Distances a guide's EVACUATION section gives (for materials not in Table 1). */
export function guideDistances(guide) {
  const out = {};
  for (const s of guide?.sections ?? [])
    for (const ss of s.subsections)
      if (/evac/i.test(ss.heading))
        ss.items.forEach((item, i) => {
          const next = ss.items[i + 1] ?? '';
          const m = next.match(/([\d,]+)\s*meters/i);
          if (!m) return;
          const v = parseInt(m[1].replace(',', ''), 10);
          if (/^immediate precautionary/i.test(item)) out.isolate ??= v;
          else if (/^large spill/i.test(item)) out.largeDownwind ??= v;
          else if (/^fire/i.test(item)) out.fire ??= v;
        });
  return out;
}

// Table 1 rows for a material's ID (several when the distances depend on the case, e.g. "when spilled in water").
function tableRows(m) {
  if (!m.id) return [];
  // The same material is often listed under several names with identical distances: show those once.
  const seen = new Set();
  return data.isolationTable.filter((t) => {
    if (t.id !== m.id) return false;
    const key = JSON.stringify([t.small, t.large, t.note]);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

// ------------------------------------------------------------------ views

const flags = (m) => [m.highlighted ? 'TIH – Table 1' : null, m.waterReactive ? `Water-reactive${m.waterReactiveGases ? ' (' + m.waterReactiveGases + ')' : ''}` : null, m.polymerizable ? 'May polymerize (P)' : null].filter(Boolean);
const idLabel = (m) => (m.id ? `UN${m.id}` : '—');
const guideLabel = (m) => m.guide + (m.polymerizable ? 'P' : '');

export function materialRow(m) {
  return row({
    title: m.name, detail: [idLabel(m), `Guide ${guideLabel(m)}`, ...flags(m)].join(' · '),
    badge: m.highlighted ? '!' : 'ERG', color: m.highlighted ? '#c92a2a' : '#e67700',
    onClick: () => app.panel.show(materialView(m), { push: true })
  });
}

export function hazmatView() {
  let timer;
  return {
    tab: null, title: 'Hazmat (ERG 2024)', dispose: () => clearTimeout(timer),
    render(body) {
      const input = h('input', { type: 'search', class: 'search-input', placeholder: 'UN number, name or “guide 128”', autocomplete: 'off', autocorrect: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': 'Search hazardous materials' });
      const results = h('div', { class: 'list' });
      const run = () => {
        const q = input.value.trim();
        if (!data) return;
        if (!q) return set(results, placards());
        const hits = searchErg(q);
        set(results, ...(hits.length ? hits.map(materialRow) : [note(`No material matches “${q}”. Try the UN number from the placard or shipping paper.`)]));
      };
      input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(run, 150); });
      add(body, h('div', { class: 'search-box' }, icon('search'), input), results);
      if (!data) {
        set(results, note('Loading the guidebook…', 'muted'));
        loadErg().then(run).catch((e) => set(results, note(e.message, 'warn')));
      } else run();
      if (zone) add(body, h('div', { class: 'actions' }, button('Show the zones on the map', () => openZone(), 'primary'), button('Clear zones', () => clearZone(), 'danger', 'trash')));
      setTimeout(() => input.focus(), 60);
    }
  };
}

function placards() {
  return h('div', {},
    note('Unknown material? Pick the placard to get its initial response guide. Use the UN number from the placard, shipping paper or package when you can.', 'muted'),
    heading('Placards'),
    h('div', { class: 'grid' }, data.placards.map((p) =>
      h('button', { type: 'button', class: 'tile', onclick: () => app.panel.show(guideView(p.guide, `Placard ${p.label || p.code}`), { push: true }) },
        h('span', { class: 'badge big', style: { background: '#e67700' } }, p.code.length > 4 ? '!' : p.code), h('span', {}, p.className || p.label)))),
    note(`ERG ${data.edition.replace('ERG', '')} · ${data.materials.length.toLocaleString()} materials · works offline once loaded`, 'muted'));
}

function distancesTable(t, i, all) {
  const cell = (s) => h('td', {}, s || '–');
  return h('div', { class: 'erg-table' },
    all.length > 1 ? h('p', { class: 'erg-sub' }, t.name) : null,
    t.note ? h('p', { class: 'line muted' }, t.note) : null,
    h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, ''), h('th', {}, 'Isolate'), h('th', {}, 'Protect – day'), h('th', {}, 'Protect – night'))),
      h('tbody', {},
        h('tr', {}, h('th', {}, 'Small spill'), cell(t.small.isolate), cell(t.small.protectDay), cell(t.small.protectNight)),
        h('tr', {}, h('th', {}, 'Large spill'), cell(t.large.isolate), cell(t.large.protectDay), cell(t.large.protectNight)))));
}

export function materialView(m) {
  return {
    tab: null, title: idLabel(m),
    render(body) {
      const g = data.guideByNumber.get(m.guide);
      const rows = m.highlighted ? tableRows(m) : [];
      add(body,
        h('div', { class: 'place-head' }, h('h2', {}, m.name), h('p', { class: 'kind' }, `${idLabel(m)} · Guide ${guideLabel(m)}${g ? ' – ' + g.title : ''}`)),
        m.highlighted ? note('Toxic inhalation hazard: use the Table 1 distances below for the initial isolation and protective-action zones.', 'warn') : null,
        m.waterReactive ? note(`Produces toxic gas when spilled in water${m.waterReactiveGases ? ': ' + m.waterReactiveGases : ''}.`, 'warn') : null,
        m.polymerizable ? note('May polymerize violently when heated or contaminated (guide shown with “P”).', 'warn') : null,
        rows.length ? heading('Table 1 – initial isolation and protective action') : null,
        rows.map(distancesTable),
        h('div', { class: 'actions' },
          button(`Guide ${guideLabel(m)}`, () => app.panel.show(guideView(m.guide), { push: true }), 'primary'),
          button('Draw zones on the map', () => app.panel.show(zoneEditor(m), { push: true }), '', 'pin')),
        note(`ERG ${data.edition.replace('ERG', '')}, page ${m.page || '?'}. Follow your agency’s procedures and CANUTEC (1-888-226-8832, *666) advice.`, 'muted'));
    }
  };
}

export function guideView(number, from) {
  return {
    tab: null, title: `Guide ${number}`,
    render(body) {
      const g = data.guideByNumber.get(number);
      if (!g) return add(body, note(`Guide ${number} isn’t in this edition.`, 'warn'));
      add(body,
        h('div', { class: 'place-head' }, h('h2', {}, g.title), h('p', { class: 'kind' }, `ERG guide ${g.number}${from ? ' · ' + from : ''} · page ${g.pages}`)),
        g.sections.map((s) => h('section', { class: 'erg-section' },
          heading(s.heading),
          s.subsections.map((ss) => h('div', {},
            ss.heading ? h('p', { class: 'erg-sub' }, ss.heading) : null,
            h('ul', { class: 'erg-items' }, ss.items.map((i) => h('li', {}, i))))))),
        (g.notes ?? []).map((n) => note(n, 'muted')),
        data.generalFirstAid ? h('div', { class: 'actions' }, button('General first aid', () => app.panel.show(firstAidView(), { push: true }))) : null,
        h('div', { class: 'actions' }, button('Materials using this guide', () => app.panel.show(listView(`Guide ${number}`, data.materials.filter((m) => m.guide === number)), { push: true }))));
    }
  };
}

function firstAidView() {
  return {
    tab: null, title: 'General first aid',
    render(body) {
      const s = data.generalFirstAid;
      add(body, s.subsections.map((ss) => h('div', {}, ss.heading ? h('p', { class: 'erg-sub' }, ss.heading) : null, h('ul', { class: 'erg-items' }, ss.items.map((i) => h('li', {}, i))))),
        note(`ERG ${data.edition.replace('ERG', '')}, page ${data.generalFirstAidPage}`, 'muted'));
    }
  };
}

const listView = (title, list) => ({ tab: null, title, render(body) { add(body, h('div', { class: 'list' }, list.map(materialRow))); } });

// ------------------------------------------------------------------ zones on the map

let zone = null; // { material, center: {lat, lon}, isolate, protect, windFrom, shape, label }
const EMPTY = { type: 'FeatureCollection', features: [] };

const R = 6371008.8;
function dest(lat, lon, bearingDeg, dist) {
  const d = dist / R, b = bearingDeg * Math.PI / 180, p1 = lat * Math.PI / 180, l1 = lon * Math.PI / 180;
  const p2 = Math.asin(Math.sin(p1) * Math.cos(d) + Math.cos(p1) * Math.sin(d) * Math.cos(b));
  const l2 = l1 + Math.atan2(Math.sin(b) * Math.sin(d) * Math.cos(p1), Math.cos(d) - Math.sin(p1) * Math.sin(p2));
  return [((l2 * 180 / Math.PI + 540) % 360) - 180, p2 * 180 / Math.PI];
}
export const circleRing = (c, r, n = 64) => { const pts = Array.from({ length: n }, (_, i) => dest(c.lat, c.lon, i * 360 / n, r)); return [...pts, pts[0]]; };
export function sectorRing(c, bearing, width, r, n = 24) {
  const pts = [[c.lon, c.lat]];
  for (let i = 0; i <= n; i++) pts.push(dest(c.lat, c.lon, bearing - width / 2 + i * width / n, r));
  pts.push([c.lon, c.lat]);
  return pts;
}
/** The ERG square: as wide as it is long, centred on the downwind line, starting at the spill. */
export function squareRing(c, bearing, d) {
  const [l1, p1] = dest(c.lat, c.lon, bearing - 90, d / 2), [r1, q1] = dest(c.lat, c.lon, bearing + 90, d / 2);
  const [fl, fp] = dest(c.lat, c.lon, bearing, d);
  const [l2, p2] = dest(fp, fl, bearing - 90, d / 2), [r2, q2] = dest(fp, fl, bearing + 90, d / 2);
  return [[l1, p1], [l2, p2], [r2, q2], [r1, q1], [l1, p1]];
}

function zoneGeoJson() {
  if (!zone) return EMPTY;
  const f = [];
  const downwind = zone.windFrom != null ? (zone.windFrom + 180) % 360 : null;
  if (zone.protect && downwind != null)
    f.push({ type: 'Feature', properties: { part: 'protect' }, geometry: { type: 'Polygon', coordinates: [zone.shape === 'square' ? squareRing(zone.center, downwind, zone.protect) : sectorRing(zone.center, downwind, 90, zone.protect)] } });
  if (zone.isolate) f.push({ type: 'Feature', properties: { part: 'isolate' }, geometry: { type: 'Polygon', coordinates: [circleRing(zone.center, zone.isolate)] } });
  f.push({ type: 'Feature', properties: { part: 'spill' }, geometry: { type: 'Point', coordinates: [zone.center.lon, zone.center.lat] } });
  return { type: 'FeatureCollection', features: f };
}

export function addHazmatLayers(m) {
  m.addSource('hazmat', { type: 'geojson', data: zoneGeoJson() });
  const part = (p) => ['==', ['get', 'part'], p];
  m.addLayer({ id: 'hazmat-protect', type: 'fill', source: 'hazmat', filter: part('protect'), paint: { 'fill-color': '#fd7e14', 'fill-opacity': 0.22 } });
  m.addLayer({ id: 'hazmat-protect-line', type: 'line', source: 'hazmat', filter: part('protect'), paint: { 'line-color': '#e8590c', 'line-width': 2.5, 'line-dasharray': [3, 2] } });
  m.addLayer({ id: 'hazmat-isolate', type: 'fill', source: 'hazmat', filter: part('isolate'), paint: { 'fill-color': '#e03131', 'fill-opacity': 0.3 } });
  m.addLayer({ id: 'hazmat-isolate-line', type: 'line', source: 'hazmat', filter: part('isolate'), paint: { 'line-color': '#c92a2a', 'line-width': 2.5 } });
  m.addLayer({ id: 'hazmat-spill', type: 'circle', source: 'hazmat', filter: part('spill'), paint: { 'circle-radius': 6, 'circle-color': '#111', 'circle-stroke-color': '#ffd43b', 'circle-stroke-width': 3 } });
  for (const id of ['hazmat-spill', 'hazmat-isolate', 'hazmat-protect']) INTERACTIVE.add(id);
}

const drawZone = () => app.map?.getSource('hazmat')?.setData(zoneGeoJson());
export const isHazmatLayer = (id) => id.startsWith('hazmat-');
export function clearZone() { zone = null; drawZone(); toast('Hazmat zones cleared'); app.panel.refresh(); }

function zoneBounds() {
  const pts = zoneGeoJson().features.flatMap((f) => (f.geometry.type === 'Point' ? [f.geometry.coordinates] : f.geometry.coordinates[0]));
  const xs = pts.map((p) => p[0]), ys = pts.map((p) => p[1]);
  return [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)];
}

const COMPASS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
const fmt = (m) => (m >= 1000 ? `${(m / 1000).toFixed(m >= 10000 ? 0 : 1)} km` : `${m} m`);

/** Pick the spill point, size, day/night, wind and shape; draws the zones as the choices change. */
export function zoneEditor(m, at) {
  const g = data.guideByNumber.get(m.guide);
  const rows = m.highlighted ? tableRows(m) : [];
  const fromGuide = guideDistances(g);
  const s = { size: 'small', time: app.pos ? (new Date().getHours() >= 7 && new Date().getHours() < 19 ? 'day' : 'night') : 'day', windFrom: zone?.windFrom ?? null, shape: zone?.shape ?? 'sector', row: 0 };
  let center = at ?? (app.selected ? { ...app.selected } : (() => { const p = myPositionOrCentre(); return { lat: p.lat, lon: p.lon }; })());
  return {
    tab: null, title: 'Hazmat zones',
    render(body) {
      const t = rows[s.row];
      let isolate, protect, source;
      if (t) {
        const d = s.size === 'small' ? t.small : t.large;
        isolate = meters(d.isolate);
        protect = meters(s.time === 'day' ? d.protectDay : d.protectNight);
        source = `Table 1, ${s.size} spill, ${s.time}`;
      } else {
        isolate = fromGuide.isolate ?? null;
        protect = s.size === 'large' ? fromGuide.largeDownwind ?? null : null;
        source = `Guide ${m.guide} evacuation distances`;
      }
      zone = { material: m, center, isolate, protect, windFrom: s.windFrom, shape: s.shape, label: `${idLabel(m)} ${m.name}` };
      drawZone();
      const missing = t && (isolate == null || protect == null);
      add(body,
        h('div', { class: 'place-head' }, h('h2', {}, m.name), h('p', { class: 'kind' }, `${idLabel(m)} · Guide ${guideLabel(m)}`)),
        rows.length > 1 ? segmented(rows.map((r, i) => ({ id: i, label: (r.note || r.name).slice(0, 40) })), s.row, (i) => { s.row = i; app.panel.refresh(); }) : null,
        heading('Spill'),
        segmented([{ id: 'small', label: 'Small' }, { id: 'large', label: 'Large' }], s.size, (v) => { s.size = v; app.panel.refresh(); }),
        t ? segmented([{ id: 'day', label: 'Day' }, { id: 'night', label: 'Night' }], s.time, (v) => { s.time = v; app.panel.refresh(); }) : null,
        heading('Wind blowing from'),
        h('div', { class: 'chips wrap' }, COMPASS.map((c, i) => {
          const b = h('button', { type: 'button', class: s.windFrom === i * 45 ? 'on' : '' }, c);
          b.addEventListener('click', () => { s.windFrom = i * 45; app.panel.refresh(); });
          return b;
        })),
        protect && s.windFrom == null ? note('Choose the wind direction to draw the downwind protective-action zone.', 'muted') : null,
        protect ? segmented([{ id: 'sector', label: '90° sector' }, { id: 'square', label: 'ERG square' }], s.shape, (v) => { s.shape = v; app.panel.refresh(); }) : null,
        heading('Zones'),
        h('dl', { class: 'attrs' },
          h('dt', {}, 'Isolate'), h('dd', {}, isolate ? `${fmt(isolate)} in all directions` : '–'),
          h('dt', {}, 'Protect'), h('dd', {}, protect ? `${fmt(protect)} downwind${s.windFrom != null ? ` (toward ${COMPASS[Math.round(((s.windFrom + 180) % 360) / 45) % 8]})` : ''}` : s.size === 'small' && !t ? 'Small spill: isolate only' : '–'),
          h('dt', {}, 'From'), h('dd', {}, source),
          h('dt', {}, 'Spill at'), h('dd', {}, C.all(center.lat, center.lon).dec)),
        missing ? note('Table 1 says “Refer to Table 3” for this case. Use Table 3 in the printed ERG and your agency’s procedures.', 'warn') : null,
        fromGuide.fire ? note(`If a tank, rail car or highway tank is involved in a fire: isolate ${fmt(fromGuide.fire)} in all directions (guide ${m.guide}).`, 'muted') : null,
        h('div', { class: 'actions small' },
          app.pos ? button('Spill at my position', () => { center = { lat: app.pos.lat, lon: app.pos.lon }; app.panel.refresh(); }) : null,
          button('Spill at map centre', () => { const c = app.map.getCenter(); center = { lat: c.lat, lon: c.lng }; app.panel.refresh(); })),
        h('div', { class: 'actions' },
          button('Show on map', () => fitTo(zoneBounds()), 'primary'),
          app.team?.available ? button('Share with team', () => shareZone(isolate, protect, source)) : null,
          button('Clear', () => { clearZone(); app.panel.back(); }, 'danger', 'trash')),
        note('Initial zones only. Adjust for the actual situation, terrain and wind, and follow CANUTEC and your agency’s procedures.', 'muted'));
    }
  };
}

function openZone() {
  if (!zone) return;
  app.panel.show(zoneEditor(zone.material, zone.center), { push: true });
  fitTo(zoneBounds());
}

/** Opens the zone editor when the zones on the map are tapped. */
export function onZoneTap() { if (zone) { loadErg().then(() => app.panel.show(zoneEditor(zone.material, zone.center))); } }

async function shareZone(isolate, protect, source) {
  const name = (app.settings.teamName ?? '').trim();
  if (!name) return toast('Set your name or callsign in More → Team first.');
  const wind = zone.windFrom != null ? ` Wind from ${COMPASS[zone.windFrom / 45]}.` : '';
  try {
    await postJson('/api/team/pins', {
      device: app.settings.teamDevice, name, kind: 'hazard', label: `${zone.label}`.slice(0, 60),
      note: `Hazmat: isolate ${isolate ? fmt(isolate) : '?'}${protect ? `, protect ${fmt(protect)} downwind` : ''} (${source}).${wind}`,
      lat: zone.center.lat, lon: zone.center.lon
    });
    toast('Shared with the team as a hazard pin');
  } catch (e) { toast(e.message); }
}
