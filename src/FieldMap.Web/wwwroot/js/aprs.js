// APRS stations heard on APRS-IS by the server (receive-only): other hams, digipeaters, weather stations, objects.
// The server keeps the stations; phones poll /api/aprs while the layer is on and draw them with their recent tracks.

import { app, save, focus, fitTo, INTERACTIVE } from './app.js';
import { placeView } from './panels.js';
import * as C from './coords.js';
import { getJson } from './api.js';
import { h, button, toggle, toast } from './ui.js';
import { updateAprsAlerts } from './alerts.js';

// Colour by kind of station, kept apart from the team (green) and pin colours.
export const APRS_GROUPS = {
  emergency: { label: 'Emergency', color: '#e03131' },
  vehicle: { label: 'Vehicle', color: '#e8590c' },
  person: { label: 'Person', color: '#d6336c' },
  aircraft: { label: 'Aircraft', color: '#1c7ed6' },
  boat: { label: 'Boat', color: '#0c8599' },
  weather: { label: 'Weather', color: '#7048e8' },
  infrastructure: { label: 'Digipeater / I-gate', color: '#5c677d' },
  facility: { label: 'Facility', color: '#a61e4d' },
  other: { label: 'Other', color: '#868e96' }
};
const POLL_MS = 20000;
const EMPTY = { type: 'FeatureCollection', features: [] };
const FONT = ['Noto Sans Regular'];

// Attached as app.aprs in startAprs(): app.js imports this file, so app isn't ready while it loads.
const state = { enabled: false, version: null, stations: [], status: null, error: null };
let timer = null;

const on = () => state.enabled && app.settings.showAprs !== false;

export function startAprs() {
  app.aprs = state;
  state.enabled = !!app.info?.aprs;
  if (!state.enabled) return;
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') poll(); });
  poll();
}

async function poll() {
  clearTimeout(timer);
  if (!on() || document.visibilityState === 'hidden') return; // restarts when shown again or the layer is turned on
  try {
    const d = await getJson(`/api/aprs${state.version != null ? `?v=${state.version}` : ''}`);
    state.error = null;
    if (!d?.enabled) { state.enabled = false; draw(); return; }
    state.status = d.status;
    if (!d.unchanged) {
      state.version = d.version;
      state.stations = d.stations ?? [];
      draw();
      updateAprsAlerts(state.stations);
    }
  } catch (e) {
    if (e.name === 'AuthRequired') return;
    state.error = e.message;
  }
  timer = setTimeout(poll, POLL_MS);
}

// ------------------------------------------------------------------ the map

const groupColor = ['match', ['get', 'group'], ...Object.entries(APRS_GROUPS).flatMap(([id, g]) => [id, g.color]), APRS_GROUPS.other.color];

export function addAprsLayers(m) {
  const t = app.settings.theme;
  const vis = on() ? 'visible' : 'none';
  const halo = t === 'daylight' ? '#fff' : '#000';
  const text = t === 'daylight' ? '#000' : t === 'night' ? '#b8a07a' : '#fff';
  m.addSource('aprs-tracks', { type: 'geojson', data: EMPTY });
  m.addLayer({
    id: 'aprs-tracks', type: 'line', source: 'aprs-tracks', minzoom: 7, layout: { visibility: vis, 'line-join': 'round', 'line-cap': 'round' },
    paint: { 'line-color': groupColor, 'line-width': 2, 'line-opacity': 0.5 }
  });
  m.addSource('aprs', { type: 'geojson', data: EMPTY });
  m.addLayer({
    id: 'aprs', type: 'circle', source: 'aprs', minzoom: 5, layout: { visibility: vis, 'circle-sort-key': ['case', ['get', 'emergency'], 1, 0] },
    paint: {
      'circle-radius': ['interpolate', ['linear'], ['zoom'], 6, ['case', ['get', 'emergency'], 7, 3.5], 12, ['case', ['get', 'emergency'], 12, 8], 15, 10],
      'circle-color': ['case', ['get', 'emergency'], APRS_GROUPS.emergency.color, groupColor],
      'circle-stroke-color': ['case', ['get', 'emergency'], '#ffd8a8', '#fff'], 'circle-stroke-width': ['case', ['get', 'emergency'], 3, 1.5],
      'circle-opacity': t === 'night' ? 0.7 : 0.95
    }
  });
  m.addLayer({
    id: 'aprs-labels', type: 'symbol', source: 'aprs', minzoom: 11, layout: {
      visibility: vis, 'text-field': ['get', 'key'], 'text-font': FONT, 'text-size': 11, 'text-offset': [0, 1.1], 'text-anchor': 'top',
      'text-optional': true, 'text-allow-overlap': false
    },
    paint: { 'text-color': text, 'text-halo-color': halo, 'text-halo-width': 1.6 }
  });
  INTERACTIVE.add('aprs');
  draw();
}

function draw() {
  const m = app.map;
  if (!m?.getSource('aprs')) return;
  const list = state.enabled ? state.stations : [];
  m.getSource('aprs').setData({
    type: 'FeatureCollection',
    features: list.map((s) => ({ type: 'Feature', geometry: { type: 'Point', coordinates: [s.lon, s.lat] }, properties: { key: s.key, group: s.group, emergency: s.emergency } }))
  });
  m.getSource('aprs-tracks').setData({
    type: 'FeatureCollection',
    features: list.filter((s) => s.track.length > 1).map((s) => ({ type: 'Feature', geometry: { type: 'LineString', coordinates: [...s.track, [s.lon, s.lat]] }, properties: { group: s.group } }))
  });
}

export function setAprsVisible(visible) {
  app.settings.showAprs = visible;
  save();
  for (const id of ['aprs-tracks', 'aprs', 'aprs-labels'])
    if (app.map?.getLayer(id)) app.map.setLayoutProperty(id, 'visibility', visible ? 'visible' : 'none');
  if (visible) poll();
}

// ------------------------------------------------------------------ views

const ago = (s) => (s < 60 ? 'just now' : s < 3600 ? `${Math.round(s / 60)} min ago` : `${Math.round(s / 3600)} h ago`);
const ageNow = (st) => Math.max(0, (Date.now() - new Date(st.heard).getTime()) / 1000);

/** The APRS entry for the Layers panel (null when the server doesn't have APRS-IS on). */
export function aprsLayerToggle() {
  if (!state.enabled) return null;
  const s = state.status;
  const detail = state.error ? state.error
    : !s ? 'Connecting…'
    : s.state === 'connected' ? `${state.stations.length.toLocaleString()} stations heard in the last hour · APRS-IS, receive only`
    : s.text;
  return h('div', {},
    toggle({ label: 'APRS stations', detail, badge: 'A', color: APRS_GROUPS.vehicle.color, checked: on(), onChange: (v) => setAprsVisible(v) }),
    h('div', { class: 'legend' }, ['vehicle', 'person', 'weather', 'infrastructure', 'aircraft', 'emergency'].map((g) =>
      [h('span', { class: 'sw', style: { background: APRS_GROUPS[g].color, opacity: 1, borderRadius: '6px', width: '12px' } }), APRS_GROUPS[g].label + ' '])));
}

export function openAprsStation(key, { push = false } = {}) {
  const s = state.stations.find((x) => x.key === key);
  if (!s) return toast('That station is no longer on the map.');
  focus(s.lat, s.lon);
  const g = APRS_GROUPS[s.group] ?? APRS_GROUPS.other;
  const moving = s.speedKmh != null && s.speedKmh >= 4;
  const extra = [
    h('a', { class: 'btn', href: `https://aprs.fi/#!call=a%2F${encodeURIComponent(s.key)}`, target: '_blank', rel: 'noopener' }, 'aprs.fi')
  ];
  if (s.track.length > 1) {
    const xs = s.track.map((p) => p[0]), ys = s.track.map((p) => p[1]);
    extra.unshift(button('Show track', () => fitTo([Math.min(...xs, s.lon), Math.min(...ys, s.lat), Math.max(...xs, s.lon), Math.max(...ys, s.lat)])));
  }
  app.panel.show(placeView({
    title: s.key,
    kind: `${s.symbol} · APRS, heard ${ago(ageNow(s))}`,
    lat: s.lat, lon: s.lon,
    warning: s.emergency ? `EMERGENCY${s.micE ? ' – ' + s.micE : ''}. Confirm with dispatch before acting.` : s.ambiguity > 0 ? 'The sender blurred this position on purpose, so it is only approximate.' : null,
    lines: [
      s.isObject ? `Object placed by ${s.source}` : null,
      s.comment ? h('p', { class: 'line desc' }, s.comment) : null,
      s.status ? `Status: ${s.status}` : null,
      s.micE && !s.emergency ? `Mic-E: ${s.micE}` : null,
      moving ? `Moving ${Math.round(s.speedKmh)} km/h${s.course != null ? ' ' + C.compass(s.course) : ''}` : null,
      s.altitude != null ? `Altitude ${Math.round(s.altitude).toLocaleString()} m` : null,
      s.weather ? `Weather: ${s.weather}` : null,
      h('p', { class: 'line muted' }, `${g.label}${s.path ? ' · via ' + s.path : ''}`)
    ],
    extraButtons: extra,
    source: 'From APRS-IS: positions are sent by the stations themselves and can be old or wrong.'
  }), { push });
}
