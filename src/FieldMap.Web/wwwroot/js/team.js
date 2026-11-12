// Team: other units' positions (each phone opts in, with the last hour's trail) and shared incident pins that every
// phone sees and anyone can edit. Needs an access code on the server; without one the server answers "team-off" and
// the app explains that instead. Positions only go out while Field Map is open: a web app can't use GPS in the background.

import { app, save, focus, fitTo, INTERACTIVE, badgeImage, myPositionOrCentre, clearSelection } from './app.js';
import { placeView } from './panels.js';
import * as C from './coords.js';
import { getJson, postJson } from './api.js';
import { h, row, toggle, button, note, heading, toast, add } from './ui.js';

export const PIN_KINDS = {
  hazard: { label: 'Hazard', glyph: '!', color: '#e53935' },
  staging: { label: 'Staging', glyph: 'S', color: '#1e88e5' },
  closure: { label: 'Road closed', glyph: '✕', color: '#8e24aa' },
  command: { label: 'Command post', glyph: 'C', color: '#f9a825', dark: true },
  medical: { label: 'Medical', glyph: '+', color: '#2e7d32' },
  note: { label: 'Note', glyph: 'i', color: '#546e7a' },
  perimeter: { label: 'Perimeter', glyph: 'P', color: '#d6336c' },
  route: { label: 'Route', glyph: 'R', color: '#1c7ed6' }
};
const UNIT_COLOR = '#00a389';
const UNIT_STALE = '#8a8f96';
const POLL_MS = 5000;
const EMPTY = { type: 'FeatureCollection', features: [] };

// Kept here and attached as app.team in startTeam(): app.js and this file import each other, so app isn't ready yet
// while this file loads.
const state = { available: null, units: [], pins: [], revision: 0, error: null };

export function deviceId() {
  if (!app.settings.teamDevice) {
    const r = crypto.randomUUID?.() ?? Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
    app.settings.teamDevice = 'fm-' + r.replace(/[^a-zA-Z0-9-]/g, '');
    save();
  }
  return app.settings.teamDevice;
}

const myName = () => (app.settings.teamName ?? '').trim();

// ------------------------------------------------------------------ syncing with the server

let pollTimer = null;
let lastSent = { time: 0, lat: 0, lon: 0 };

export function startTeam() {
  app.team = state;
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') poll(); });
  poll();
}

async function poll() {
  clearTimeout(pollTimer);
  if (document.visibilityState === 'hidden') return; // resumes on visibilitychange
  let next = POLL_MS;
  try {
    const t = await getJson('/api/team');
    state.available = true;
    state.error = null;
    state.units = t?.units ?? [];
    state.pins = t?.pins ?? [];
    if (t?.revision !== state.revision) {
      state.revision = t?.revision;
      draw();
      refreshTeamView();
    }
    heartbeat();
  } catch (e) {
    if (e.message === 'team-off') { state.available = false; next = 60000; }
    else if (e.name !== 'AuthRequired') { state.error = e.message; next = 15000; }
    else return;
    refreshTeamView();
  }
  pollTimer = setTimeout(poll, next);
}

/** Called with every GPS fix. Sends it when sharing, at most every 5 s, and only every 10 s unless the unit moved. */
export function teamOnPosition(pos) {
  if (!app.settings.shareTeam || !myName() || state.available === false) return;
  const now = Date.now();
  const moved = C.distance([lastSent.lon, lastSent.lat], [pos.lon, pos.lat]);
  if (now - lastSent.time < 5000 || (now - lastSent.time < 10000 && moved < 25)) return;
  sendPosition(pos);
}

// A parked phone may not get new fixes for a while; keep its position fresh (with the last fix) while it's sharing.
function heartbeat() {
  const p = app.pos;
  if (app.settings.shareTeam && myName() && p && Date.now() - p.time < 60000 && Date.now() - lastSent.time > 30000) sendPosition(p);
}

async function sendPosition(p) {
  lastSent = { time: Date.now(), lat: p.lat, lon: p.lon };
  try {
    await postJson('/api/team/position', {
      device: deviceId(), name: myName(), lat: p.lat, lon: p.lon, accuracy: p.acc, heading: p.heading, speed: p.speed
    });
  } catch (e) {
    if (e.message === 'team-off') state.available = false;
  }
}

export async function setSharing(on) {
  if (on && !myName()) { toast('Set your name or callsign first.'); return false; }
  app.settings.shareTeam = on;
  save();
  if (on && app.pos) sendPosition(app.pos);
  if (!on) {
    lastSent = { time: 0, lat: 0, lon: 0 };
    await fetch(`/api/team/position/${encodeURIComponent(deviceId())}`, { method: 'DELETE', credentials: 'same-origin' }).catch(() => {});
    poll();
  }
  return true;
}

// ------------------------------------------------------------------ the map

/** Adds the team layers; called whenever the map style (re)loads. */
export function addTeamLayers(m) {
  const t = app.settings.theme;
  for (const [id, k] of Object.entries(PIN_KINDS))
    if (!m.hasImage(`tp-${id}`)) m.addImage(`tp-${id}`, badgeImage(k.glyph, k.color, k.dark ? '#111' : '#fff', 34), { pixelRatio: 2 });
  const vis = app.settings.showTeam === false ? 'none' : 'visible';
  const halo = t === 'daylight' ? '#fff' : '#000';
  const text = t === 'daylight' ? '#000' : t === 'night' ? '#b8a07a' : '#fff';

  m.addSource('team-trails', { type: 'geojson', data: EMPTY });
  m.addLayer({
    id: 'team-trails', type: 'line', source: 'team-trails', layout: { visibility: vis, 'line-join': 'round', 'line-cap': 'round' },
    paint: { 'line-color': ['case', ['get', 'stale'], UNIT_STALE, UNIT_COLOR], 'line-width': 3, 'line-opacity': 0.55, 'line-dasharray': [2, 1.5] }
  });
  m.addSource('team-units', { type: 'geojson', data: EMPTY });
  m.addLayer({
    id: 'team-units', type: 'circle', source: 'team-units', layout: { visibility: vis },
    paint: {
      'circle-radius': ['interpolate', ['linear'], ['zoom'], 8, 6, 15, 10], 'circle-color': ['case', ['get', 'stale'], UNIT_STALE, UNIT_COLOR],
      'circle-stroke-color': '#fff', 'circle-stroke-width': 2.5, 'circle-opacity': t === 'night' ? 0.7 : 1
    }
  });
  m.addLayer({
    id: 'team-unit-labels', type: 'symbol', source: 'team-units', layout: {
      visibility: vis, 'text-field': ['get', 'name'], 'text-font': ['Noto Sans Regular'], 'text-size': 13,
      'text-offset': [0, 1.2], 'text-anchor': 'top', 'text-allow-overlap': false, 'text-optional': true
    },
    paint: { 'text-color': text, 'text-halo-color': halo, 'text-halo-width': 1.8 }
  });
  // Shared lines and areas, under the badges that carry their labels.
  const kindColor = ['match', ['get', 'kind'], ...Object.entries(PIN_KINDS).flatMap(([id, k]) => [id, k.color]), '#546e7a'];
  m.addSource('team-shapes', { type: 'geojson', data: EMPTY });
  m.addLayer({ id: 'team-area-fill', type: 'fill', source: 'team-shapes', filter: ['==', ['geometry-type'], 'Polygon'], layout: { visibility: vis }, paint: { 'fill-color': kindColor, 'fill-opacity': t === 'night' ? 0.12 : 0.2 } });
  m.addLayer({ id: 'team-area-line', type: 'line', source: 'team-shapes', filter: ['==', ['geometry-type'], 'Polygon'], layout: { visibility: vis, 'line-join': 'round' }, paint: { 'line-color': kindColor, 'line-width': 3 } });
  m.addLayer({ id: 'team-line', type: 'line', source: 'team-shapes', filter: ['==', ['geometry-type'], 'LineString'], layout: { visibility: vis, 'line-join': 'round', 'line-cap': 'round' }, paint: { 'line-color': kindColor, 'line-width': ['interpolate', ['linear'], ['zoom'], 10, 3, 16, 6] } });
  // The line or area being drawn.
  m.addSource('team-draw', { type: 'geojson', data: EMPTY });
  m.addLayer({ id: 'team-draw-fill', type: 'fill', source: 'team-draw', filter: ['==', ['geometry-type'], 'Polygon'], paint: { 'fill-color': '#ffd43b', 'fill-opacity': 0.18 } });
  m.addLayer({ id: 'team-draw-line', type: 'line', source: 'team-draw', filter: ['!=', ['geometry-type'], 'Point'], paint: { 'line-color': '#ffd43b', 'line-width': 3, 'line-dasharray': [2, 1] } });
  m.addLayer({ id: 'team-draw-pt', type: 'circle', source: 'team-draw', filter: ['==', ['geometry-type'], 'Point'], paint: { 'circle-radius': 6, 'circle-color': '#ffd43b', 'circle-stroke-color': '#000', 'circle-stroke-width': 2 } });
  for (const id of ['team-line', 'team-area-fill']) INTERACTIVE.add(id);
  m.addSource('team-pins', { type: 'geojson', data: EMPTY });
  m.addLayer({
    id: 'team-pins', type: 'symbol', source: 'team-pins', layout: {
      visibility: vis, 'icon-image': ['concat', 'tp-', ['get', 'kind']], 'icon-allow-overlap': true, 'icon-ignore-placement': true,
      'text-field': ['step', ['zoom'], '', 12, ['get', 'label']], 'text-font': ['Noto Sans Regular'], 'text-size': 13,
      'text-offset': [0, 1.5], 'text-anchor': 'top', 'text-optional': true, 'text-max-width': 10
    },
    paint: { 'text-color': text, 'text-halo-color': halo, 'text-halo-width': 1.8 }
  });
  INTERACTIVE.add('team-pins');
  INTERACTIVE.add('team-units');
  draw();
}

function draw() {
  const m = app.map;
  if (!m?.getSource('team-units')) return;
  const me = app.settings.teamDevice;
  const others = state.units.filter((u) => u.device !== me);
  m.getSource('team-units').setData({
    type: 'FeatureCollection',
    features: others.map((u) => ({ type: 'Feature', geometry: { type: 'Point', coordinates: [u.lon, u.lat] }, properties: { device: u.device, name: u.name, stale: u.stale } }))
  });
  m.getSource('team-trails').setData({
    type: 'FeatureCollection',
    features: others.filter((u) => u.trail.length > 1).map((u) => ({ type: 'Feature', geometry: { type: 'LineString', coordinates: [...u.trail, [u.lon, u.lat]] }, properties: { stale: u.stale } }))
  });
  m.getSource('team-pins').setData({
    type: 'FeatureCollection',
    features: state.pins.map((p) => ({ type: 'Feature', geometry: { type: 'Point', coordinates: [p.lon, p.lat] }, properties: { id: p.id, kind: p.kind, label: p.label } }))
  });
  m.getSource('team-shapes').setData({
    type: 'FeatureCollection',
    features: state.pins.filter((p) => p.shape === 'line' || p.shape === 'area').map((p) => ({
      type: 'Feature', properties: { id: p.id, kind: p.kind },
      geometry: p.shape === 'area' ? { type: 'Polygon', coordinates: [[...p.points, p.points[0]]] } : { type: 'LineString', coordinates: p.points }
    }))
  });
}

export function setTeamVisible(on) {
  app.settings.showTeam = on;
  save();
  for (const id of ['team-trails', 'team-units', 'team-unit-labels', 'team-pins', 'team-area-fill', 'team-area-line', 'team-line'])
    if (app.map?.getLayer(id)) app.map.setLayoutProperty(id, 'visibility', on ? 'visible' : 'none');
}

// ------------------------------------------------------------------ views

const agoSeconds = (s) => {
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  return `${Math.round(s / 3600)} h ago`;
};
const ago = (iso) => {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return new Date(iso).toLocaleDateString();
};

function refreshTeamView() {
  // Don't redraw under someone who is typing their name.
  if (app.panel?.current?.teamView && !(document.activeElement?.closest?.('#panel') && document.activeElement.matches('input, textarea'))) app.panel.refresh();
}

function nameField() {
  const input = h('input', { type: 'text', class: 'field', placeholder: 'e.g. Medic 12', maxlength: 40, value: app.settings.teamName ?? '', autocomplete: 'off', 'aria-label': 'Your name or callsign' });
  input.addEventListener('change', () => {
    app.settings.teamName = input.value.trim();
    save();
    if (!app.settings.teamName && app.settings.shareTeam) setSharing(false).then(() => app.panel.refresh());
  });
  return h('label', { class: 'field-row' }, h('span', { class: 'field-label' }, 'Your name or callsign'), input);
}

export function teamView() {
  return {
    tab: null, title: 'Team', teamView: true,
    render(body) {
      if (state.available === false) {
        add(body, note('Team positions and shared pins are off on this server. They need an access code, so only your crews can see where everyone is. On the server, set AccessCode in /etc/fieldmap-web/appsettings.json and restart Field Map.', 'warn'));
        return;
      }
      if (state.error) add(body, note(state.error, 'warn'));
      add(body, nameField(),
        toggle({
          label: 'Share my position', detail: 'Other units see where you are, with your last hour’s trail. Only while Field Map is open on screen.',
          checked: !!app.settings.shareTeam, onChange: async (on) => { await setSharing(on); app.panel.refresh(); }
        }),
        toggle({ label: 'Show team on the map', detail: 'Units, their trails and shared pins', checked: app.settings.showTeam !== false, onChange: (on) => setTeamVisible(on) }));

      const me = app.settings.teamDevice;
      const units = state.units;
      add(body, heading(`Units sharing (${units.length})`));
      if (!units.length) add(body, note('Nobody is sharing their position right now.', 'muted'));
      for (const u of units)
        add(body, row({
          title: u.device === me ? `${u.name} (you)` : u.name,
          detail: `${u.stale ? 'Last update ' : 'Updated '}${agoSeconds(u.ageSeconds)}${app.pos ? ' · ' + C.formatDistance(C.distance([app.pos.lon, app.pos.lat], [u.lon, u.lat])) + ' away' : ''}`,
          badge: '●', color: u.stale ? UNIT_STALE : UNIT_COLOR, onClick: () => openTeamUnit(u.device, { push: true })
        }));

      const pins = state.pins;
      add(body, heading(`Shared pins (${pins.length})`));
      if (!pins.length) add(body, note('No shared pins yet. Touch and hold the map to drop a pin, then choose “Share as pin”.', 'muted'));
      for (const p of [...pins].sort((a, b) => b.updatedUtc.localeCompare(a.updatedUtc))) {
        const k = PIN_KINDS[p.kind] ?? PIN_KINDS.note;
        add(body, row({ title: p.label, detail: `${k.label} · ${p.updatedBy}, ${ago(p.updatedUtc)}`, badge: k.glyph, color: k.color, onClick: () => openTeamPin(p.id, { push: true }) }));
      }
      add(body, h('div', { class: 'actions' },
        button('Draw a line', () => startDrawing('line'), '', 'route'),
        button('Draw an area', () => startDrawing('area')),
        button('Add a pin here', () => {
          const at = myPositionOrCentre();
          app.panel.show(pinEditor({ lat: at.lat, lon: at.lon }), { push: true });
        }, 'primary', 'pin'),
        pins.length ? button('Clear all pins', async () => {
          if (!confirm(`Remove all ${pins.length} shared pins for everyone? Do this after an incident is over.`)) return;
          try { await getJson('/api/team/pins', { method: 'DELETE' }); toast('All shared pins removed'); poll(); } catch (e) { toast(e.message); }
        }, 'danger', 'trash') : null),
        note('“Add a pin here” uses your position, or the middle of the map without GPS.', 'muted'));
    }
  };
}

export function openTeamUnit(device, { push = false } = {}) {
  const u = state.units.find((x) => x.device === device);
  if (!u) return toast('That unit stopped sharing.');
  focus(u.lat, u.lon);
  const extra = [];
  if (u.trail.length > 1) {
    const xs = u.trail.map((p) => p[0]), ys = u.trail.map((p) => p[1]);
    extra.push(button('Show trail', () => fitTo([Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)])));
  }
  app.panel.show(placeView({
    title: u.name, kind: `Team · ${u.stale ? 'last update' : 'updated'} ${agoSeconds(u.ageSeconds)}`,
    lat: u.lat, lon: u.lon, warning: u.stale ? 'This position is old: the unit may have closed Field Map or lost signal.' : null,
    lines: [u.speed != null && u.speed > 1 ? `Moving at ${Math.round(u.speed * 3.6)} km/h` : null, u.accuracy != null ? `GPS accuracy ±${Math.round(u.accuracy)} m` : null],
    extraButtons: extra
  }), { push });
}

export function openTeamPin(id, { push = false } = {}) {
  const p = state.pins.find((x) => x.id === id);
  if (!p) return toast('That pin was removed.');
  const k = PIN_KINDS[p.kind] ?? PIN_KINDS.note;
  const isShape = p.shape === 'line' || p.shape === 'area';
  if (isShape) { clearSelection(); fitTo(bboxOf(p.points)); } else focus(p.lat, p.lon);
  app.panel.show(placeView({
    title: p.label, kind: `Shared ${p.shape === 'area' ? 'area' : p.shape === 'line' ? 'line' : 'pin'} · ${k.label}`, lat: p.lat, lon: p.lon,
    lines: [
      isShape ? h('p', { class: 'line' }, p.shape === 'area' ? `Area ${formatArea(areaOf(p.points))} · perimeter ${C.formatDistance(lengthOf([...p.points, p.points[0]]))}` : `Length ${C.formatDistance(lengthOf(p.points))}`) : null,
      p.note ? h('p', { class: 'line desc' }, p.note) : null,
      h('p', { class: 'line muted' }, `Added by ${p.createdBy}, ${ago(p.createdUtc)}${p.updatedUtc !== p.createdUtc ? ` · changed by ${p.updatedBy}, ${ago(p.updatedUtc)}` : ''}`)
    ],
    extraButtons: [
      button('Edit', () => app.panel.show(pinEditor(p), { push: true }), '', 'pin'),
      isShape ? button('Zoom to', () => fitTo(bboxOf(p.points))) : null,
      button('Remove', async () => {
        if (!confirm(`Remove “${p.label}” for everyone?`)) return;
        try { await getJson(`/api/team/pins/${p.id}`, { method: 'DELETE' }); toast('Pin removed'); app.panel.back(); poll(); } catch (e) { toast(e.message); }
      }, 'danger', 'trash')
    ]
  }), { push });
}

/** Add (no id) or edit a shared pin. */
export function pinEditor(pin) {
  const editing = !!pin.id;
  let kind = pin.kind ?? 'hazard';
  return {
    tab: null, title: editing ? 'Edit' : pin.shape === 'area' ? 'Share an area' : pin.shape === 'line' ? 'Share a line' : 'Share a pin',
    render(body) {
      const label = h('input', { type: 'text', class: 'field', maxlength: 60, placeholder: 'What is here? e.g. Downed power line', value: pin.label ?? '', 'aria-label': 'Label' });
      const text = h('textarea', { class: 'field', rows: 3, maxlength: 500, placeholder: 'Details (optional)', 'aria-label': 'Details' }, pin.note ?? '');
      const chips = h('div', { class: 'chips wrap' }, Object.entries(PIN_KINDS).map(([id, k]) => {
        const b = h('button', { type: 'button', class: id === kind ? 'on' : '' }, h('span', { class: 'chip-dot', style: { background: k.color } }), k.label);
        b.addEventListener('click', () => { kind = id; chips.querySelectorAll('button').forEach((x) => x.classList.toggle('on', x === b)); });
        return b;
      }));
      let at = { lat: pin.lat, lon: pin.lon };
      const where = h('p', { class: 'line muted' }, C.all(at.lat, at.lon).dec);
      const go = async () => {
        if (!myName()) return toast('Set your name or callsign first.');
        if (!label.value.trim()) { label.focus(); return toast('Give the pin a label.'); }
        const data = { device: deviceId(), name: myName(), kind, label: label.value, note: text.value, lat: at.lat, lon: at.lon };
        if (!editing && (pin.shape === 'line' || pin.shape === 'area')) Object.assign(data, { shape: pin.shape, points: pin.points });
        try {
          const saved = editing
            ? await getJson(`/api/team/pins/${pin.id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) })
            : await postJson('/api/team/pins', data);
          toast(editing ? 'Pin updated for everyone' : 'Pin shared with everyone');
          await poll();
          // Show the pin as everyone now sees it (replacing the dropped-pin view and its red marker).
          if (saved?.id) openTeamPin(saved.id);
          else app.panel.back();
        } catch (e) { toast(e.message); }
      };
      add(body,
        !myName() ? nameField() : null,
        heading('Kind'), chips,
        heading('Label'), label,
        heading('Details'), text,
        pin.shape === 'line' || pin.shape === 'area'
          ? h('p', { class: 'line muted' }, pin.shape === 'area' ? `Area ${formatArea(areaOf(pin.points))} · ${pin.points.length} points` : `Line ${C.formatDistance(lengthOf(pin.points))} · ${pin.points.length} points`)
          : h('div', {}, heading('Position'), where),
        editing && app.pos && (pin.shape ?? 'point') === 'point' ? h('div', { class: 'actions small' }, button('Move to my position', () => { at = { lat: app.pos.lat, lon: app.pos.lon }; where.textContent = C.all(at.lat, at.lon).dec + ' (your position)'; })) : null,
        h('div', { class: 'actions' }, button(editing ? 'Save' : 'Share with everyone', go, 'primary', 'share'), button('Cancel', () => app.panel.back())),
        note('Everyone signed in to this Field Map server sees shared pins and can change or remove them.', 'muted'));
      if (!editing) setTimeout(() => label.focus(), 60);
    }
  };
}

/** The "Share as pin" button for a place view, when team features are on. */
export const sharePinButton = (lat, lon, label = '') =>
  state.available ? button('Share as pin', () => app.panel.show(pinEditor({ lat, lon, label }), { push: true }), '', 'pin') : null;

// ------------------------------------------------------------------ drawing shared lines and areas

const R = 6371008.8;
const rad = (d) => d * Math.PI / 180;
function haversine(a, b) {
  const dLat = rad(b[1] - a[1]), dLon = rad(b[0] - a[0]);
  const x = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a[1])) * Math.cos(rad(b[1])) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.sqrt(x));
}
export const lengthOf = (pts) => pts.slice(1).reduce((s, p, i) => s + haversine(pts[i], p), 0);
/** Area in m² (planar projection around the shape: fine at incident scale). */
export function areaOf(pts) {
  if (pts.length < 3) return 0;
  const lat0 = pts.reduce((s, p) => s + p[1], 0) / pts.length, k = Math.cos(rad(lat0)) * R * Math.PI / 180, m = R * Math.PI / 180;
  let a = 0;
  for (let i = 0; i < pts.length; i++) { const [x1, y1] = pts[i], [x2, y2] = pts[(i + 1) % pts.length]; a += x1 * k * y2 * m - x2 * k * y1 * m; }
  return Math.abs(a) / 2;
}
export const formatArea = (m2) => (m2 >= 1e6 ? `${(m2 / 1e6).toFixed(m2 >= 1e7 ? 0 : 2)} km²` : m2 >= 1e4 ? `${(m2 / 1e4).toFixed(1)} ha` : `${Math.round(m2)} m²`);
function bboxOf(pts) {
  const xs = pts.map((p) => p[0]), ys = pts.map((p) => p[1]);
  return [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)];
}

let drawing = null; // { shape: 'line'|'area', points: [[lon, lat]] }

export const isDrawing = () => !!drawing;

export function startDrawing(shape) {
  if (state.available === false) return toast('Team features are off on this server.');
  if (!myName()) return toast('Set your name or callsign in More → Team first.');
  drawing = { shape, points: [] };
  app.panel.close();
  drawBar();
  drawPreview();
}

/** A map tap while drawing adds a point (called from app.js before normal tap handling). */
export function drawTap(lngLat) {
  drawing.points.push([+lngLat.lng.toFixed(6), +lngLat.lat.toFixed(6)]);
  navigator.vibrate?.(15);
  drawPreview();
  drawBar();
}

function drawPreview() {
  const src = app.map?.getSource('team-draw');
  if (!src) return;
  if (!drawing) return src.setData(EMPTY);
  const p = drawing.points, f = p.map((c) => ({ type: 'Feature', geometry: { type: 'Point', coordinates: c }, properties: {} }));
  if (drawing.shape === 'area' && p.length >= 3) f.unshift({ type: 'Feature', geometry: { type: 'Polygon', coordinates: [[...p, p[0]]] }, properties: {} });
  else if (p.length >= 2) f.unshift({ type: 'Feature', geometry: { type: 'LineString', coordinates: p }, properties: {} });
  src.setData({ type: 'FeatureCollection', features: f });
}

function drawBar() {
  let bar = document.getElementById('draw-bar');
  if (!drawing) { bar?.remove(); return; }
  if (!bar) { bar = h('div', { id: 'draw-bar', role: 'toolbar' }); document.body.append(bar); }
  const p = drawing.points, need = drawing.shape === 'area' ? 3 : 2;
  const measure = drawing.shape === 'area' ? (p.length >= 3 ? formatArea(areaOf(p)) : '') : (p.length >= 2 ? C.formatDistance(lengthOf(p)) : '');
  bar.replaceChildren(
    h('div', { class: 'draw-text' },
      h('b', {}, drawing.shape === 'area' ? 'Drawing an area' : 'Drawing a line'),
      h('span', {}, p.length < need ? `Tap the map to add points (${p.length}/${need})` : `${p.length} points · ${measure}`)),
    button('Undo', () => { drawing.points.pop(); drawPreview(); drawBar(); }, 'small'),
    button('Done', () => finishDrawing(), p.length >= need ? 'small primary' : 'small'),
    button('Cancel', () => { drawing = null; drawPreview(); drawBar(); }, 'small'));
}

function finishDrawing() {
  const need = drawing.shape === 'area' ? 3 : 2;
  if (drawing.points.length < need) return toast(drawing.shape === 'area' ? 'An area needs at least 3 points.' : 'A line needs at least 2 points.');
  const d = drawing;
  drawing = null;
  drawBar();
  drawPreview();
  app.panel.show(pinEditor({ shape: d.shape, points: d.points, kind: d.shape === 'area' ? 'perimeter' : 'route', lat: d.points[0][1], lon: d.points[0][0] }));
}
