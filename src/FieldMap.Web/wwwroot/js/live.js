// Live hazards fetched by the server: road events (DriveBC, Alberta 511), wildfire perimeters, evacuation orders and
// alerts, weather alerts. Each kind can be hidden on the phone. Polled every 2 minutes while the app is visible.

import { app, save, INTERACTIVE } from './app.js';
import { placeView } from './panels.js';
import { getJson } from './api.js';
import { h, toggle, note, heading, toast } from './ui.js';

export const LIVE_KINDS = {
  road: { label: 'Road events', detail: 'DriveBC and Alberta 511: closures, incidents, construction', color: '#f76707' },
  fire: { label: 'Wildfires', detail: 'Fire perimeters; fires that are out are drawn faintly', color: '#e8590c' },
  evacuation: { label: 'Evacuation orders and alerts', detail: 'Red: order. Amber: alert.', color: '#e03131' },
  weather: { label: 'Weather alerts', detail: 'Environment Canada warnings, watches and statements', color: '#fab005' }
};
const POLL_MS = 120000;
const EMPTY = { type: 'FeatureCollection', features: [] };

// Attached as app.live in startLive(): app.js imports this file, so app isn't ready while it loads.
const state = { enabled: false, version: null, data: EMPTY, feeds: [], byId: new Map(), error: null };
let timer = null;

const hidden = () => app.settings.liveHidden ?? [];
const visible = (kind) => !hidden().includes(kind);

export function startLive() {
  app.live = state;
  state.enabled = !!app.info?.live;
  if (!state.enabled) return;
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') poll(); });
  poll();
}

async function poll() {
  clearTimeout(timer);
  if (!state.enabled || document.visibilityState === 'hidden') return;
  try {
    const d = await getJson(`/api/live${state.version != null ? `?v=${state.version}` : ''}`);
    state.error = null;
    if (!d?.enabled) { state.enabled = false; return; }
    state.feeds = d.feeds ?? state.feeds;
    if (!d.unchanged) {
      state.version = d.version;
      state.data = { type: 'FeatureCollection', features: d.features ?? [] };
      state.byId = new Map(state.data.features.map((f) => [f.properties.id, f]));
      draw();
    }
  } catch (e) {
    if (e.name === 'AuthRequired') return;
    state.error = e.message;
  }
  timer = setTimeout(poll, POLL_MS);
}

// ------------------------------------------------------------------ the map

const isOut = ['==', ['downcase', ['get', 'severity']], 'out'];
const evacColor = ['case', ['==', ['downcase', ['get', 'severity']], 'order'], '#e03131', '#f59f00'];
const weatherColor = ['match', ['downcase', ['get', 'severity']], 'red', '#e03131', 'orange', '#fd7e14', 'yellow', '#fab005', '#868e96'];
const roadColor = ['match', ['upcase', ['get', 'severity']], ['MAJOR', 'FULL CLOSURE'], '#e03131', '#f76707'];

const LAYERS = {
  weather: ['live-weather-fill', 'live-weather-line'],
  fire: ['live-fire-fill', 'live-fire-line'],
  evacuation: ['live-evac-fill', 'live-evac-line'],
  road: ['live-road-line', 'live-road-pt']
};

/** Adds the live layers under the app's own layers; called whenever the map style (re)loads. */
export function addLiveLayers(m) {
  const vis = (k) => (visible(k) ? 'visible' : 'none');
  const night = app.settings.theme === 'night' ? 0.6 : 1;
  m.addSource('live', { type: 'geojson', data: state.data });
  const kind = (k) => ['==', ['get', 'kind'], k];
  const area = ['in', ['geometry-type'], ['literal', ['Polygon', 'MultiPolygon']]];
  m.addLayer({ id: 'live-weather-fill', type: 'fill', source: 'live', filter: ['all', kind('weather'), area], layout: { visibility: vis('weather') }, paint: { 'fill-color': weatherColor, 'fill-opacity': 0.1 * night } });
  m.addLayer({ id: 'live-weather-line', type: 'line', source: 'live', filter: ['all', kind('weather'), area], layout: { visibility: vis('weather') }, paint: { 'line-color': weatherColor, 'line-width': 1.5, 'line-dasharray': [4, 3], 'line-opacity': 0.8 * night } });
  m.addLayer({ id: 'live-fire-fill', type: 'fill', source: 'live', filter: ['all', kind('fire'), area], layout: { visibility: vis('fire') }, paint: { 'fill-color': ['case', isOut, '#868e96', '#e8590c'], 'fill-opacity': ['case', isOut, 0.05, 0.3 * night] } });
  m.addLayer({ id: 'live-fire-line', type: 'line', source: 'live', filter: ['all', kind('fire'), area], layout: { visibility: vis('fire') }, paint: { 'line-color': ['case', isOut, '#868e96', '#d9480f'], 'line-width': ['case', isOut, 0.8, 2], 'line-opacity': ['case', isOut, 0.5, night] } });
  m.addLayer({ id: 'live-evac-fill', type: 'fill', source: 'live', filter: ['all', kind('evacuation'), area], layout: { visibility: vis('evacuation') }, paint: { 'fill-color': evacColor, 'fill-opacity': 0.22 * night } });
  m.addLayer({ id: 'live-evac-line', type: 'line', source: 'live', filter: ['all', kind('evacuation'), area], layout: { visibility: vis('evacuation') }, paint: { 'line-color': evacColor, 'line-width': 2.5, 'line-opacity': night } });
  m.addLayer({ id: 'live-road-line', type: 'line', source: 'live', minzoom: 6, filter: ['all', kind('road'), ['in', ['geometry-type'], ['literal', ['LineString', 'MultiLineString']]]], layout: { visibility: vis('road'), 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': roadColor, 'line-width': ['interpolate', ['linear'], ['zoom'], 6, 2.5, 14, 6], 'line-opacity': 0.85 * night } });
  m.addLayer({
    id: 'live-road-pt', type: 'circle', source: 'live', minzoom: 6, filter: ['all', kind('road'), ['in', ['geometry-type'], ['literal', ['Point', 'MultiPoint']]]], layout: { visibility: vis('road') },
    paint: { 'circle-radius': ['interpolate', ['linear'], ['zoom'], 6, 4, 14, 8], 'circle-color': roadColor, 'circle-stroke-color': '#fff', 'circle-stroke-width': 1.5, 'circle-opacity': night }
  });
  for (const id of ['live-road-pt', 'live-road-line', 'live-evac-fill', 'live-fire-fill', 'live-weather-fill']) INTERACTIVE.add(id);
}

function draw() {
  app.map?.getSource('live')?.setData(state.data);
}

export function setLiveVisible(kind, on) {
  const set = new Set(hidden());
  if (on) set.delete(kind); else set.add(kind);
  app.settings.liveHidden = [...set];
  save();
  for (const id of LAYERS[kind]) if (app.map?.getLayer(id)) app.map.setLayoutProperty(id, 'visibility', on ? 'visible' : 'none');
}

/** True for the map layers this file handles taps for. */
export const isLiveLayer = (id) => id.startsWith('live-');

// ------------------------------------------------------------------ views

const time = (iso) => (iso ? new Date(iso).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : null);

function feedLine(f) {
  if (f.needsKey) return `${f.name}: needs a developer key on the server`;
  if (!f.fetched) return `${f.name}: not available yet${f.error ? ` (${f.error})` : ''}`;
  return `${f.name}: ${f.live ? 'live' : 'last copy'}, ${time(f.fetched)}${f.error ? ` (${f.error})` : ''}`;
}

/** The Live hazards section for the Layers panel (null when the server has no feeds). */
export function liveLayerSection() {
  if (!state.enabled) return null;
  const counts = {};
  for (const f of state.data.features) counts[f.properties.kind] = (counts[f.properties.kind] ?? 0) + 1;
  const fires = state.data.features.filter((f) => f.properties.kind === 'fire');
  const activeFires = fires.filter((f) => f.properties.severity.toLowerCase() !== 'out').length;
  return h('div', {},
    heading('Live hazards'),
    state.error ? note(state.error, 'warn') : null,
    Object.entries(LIVE_KINDS).map(([k, d]) => toggle({
      label: d.label,
      detail: k === 'fire' ? `${activeFires} burning, ${fires.length - activeFires} out · ${d.detail}` : `${counts[k] ?? 0} now · ${d.detail}`,
      badge: '!', color: d.color, checked: visible(k), onChange: (on) => setLiveVisible(k, on)
    })),
    h('div', { class: 'feed-status' }, state.feeds.map((f) => h('p', { class: 'note muted' }, feedLine(f)))),
    note('Fetched by the Field Map server. Confirm closures, fires and evacuations with dispatch or the issuing agency.', 'muted'));
}

/** Opens the details for a tapped live item. `at` is where it was tapped (for lines and areas). */
export function openLiveItem(id, at) {
  const f = state.byId.get(id);
  if (!f) return toast('That item is no longer in the feed.');
  const p = f.properties;
  const feed = state.feeds.find((x) => x.id === p.feed);
  const g = f.geometry;
  const [lon, lat] = g.type === 'Point' ? g.coordinates : at;
  const k = LIVE_KINDS[p.kind];
  app.panel.show(placeView({
    title: p.title || k.label,
    kind: `${k.label.replace(/s$/, '')}${p.severity ? ' · ' + p.severity : ''}`,
    lat, lon,
    warning: (p.kind === 'evacuation' && p.severity.toLowerCase() === 'order') ? 'Evacuation order in effect for this area.' : null,
    lines: [
      p.description ? h('p', { class: 'line desc' }, p.description) : null,
      p.updated ? h('p', { class: 'line muted' }, `Updated ${time(p.updated)}`) : null,
      g.type !== 'Point' ? h('p', { class: 'line muted' }, `${p.area ? 'Area' : 'Line'} – the position below is where you tapped.`) : null
    ],
    source: feed ? `${feedLine(feed)}${feed.licence ? ' · ' + feed.licence : ''}` : null
  }));
}
