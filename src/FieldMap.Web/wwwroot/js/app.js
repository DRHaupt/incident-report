// Field Map for phones: map, GPS, layers and the panels. The server (FieldMap.Web) serves the same region data,
// search, nearest and directions as the Windows app.

import { buildStyle, symbolColor, THEMES } from './style.js';
import * as C from './coords.js';
import { getJson, qs, setAuthHandler } from './api.js';
import { loadSettings, saveSettings, listLocalFiles } from './store.js';
import { Panel, h, icon, toast, set } from './ui.js';
import * as V from './panels.js';

const maplibregl = window.maplibregl;
const $ = (s) => document.querySelector(s);

export const app = {
  settings: loadSettings(),
  info: null,
  map: null,
  panel: null,
  pos: null,               // { lat, lon, acc, heading, speed, time }
  follow: true,
  events: new Map(),       // id -> { id, name, summary, source: 'server'|'phone', data, loading }
  route: null,             // last route response + { to, toLabel, cumulative }
  marker: null,
  selected: null,
  compassHeading: null,
  wakeLock: null,
  origin: location.origin
};

export const save = () => saveSettings(app.settings);

// ------------------------------------------------------------------ start-up

async function start() {
  document.body.dataset.ui = THEMES[app.settings.theme]?.ui ?? 'dark';
  const bottom = $('#bottom');
  new ResizeObserver(() => { document.documentElement.style.setProperty('--bottom-h', `${bottom.offsetHeight}px`); queueFit(); }).observe(bottom);
  app.panel = new Panel($('#panel'), { onResize: fitPadding });
  setAuthHandler(() => V.showLogin());
  for (const b of document.querySelectorAll('#tabs button'))
    b.addEventListener('click', () => openTab(b.dataset.tab));
  $('#search-pill').addEventListener('click', () => openTab('search'));
  $('#btn-me').addEventListener('click', () => recenter());
  $('#btn-compass').addEventListener('click', () => toggleHeadingUp());
  $('#status-coords').addEventListener('click', () => cycleCoordFormat());
  $('#route-banner').addEventListener('click', () => app.route && V.openDirections(app.route.to, app.route.toLabel, { push: false }));
  window.addEventListener('resize', queueFit);
  window.visualViewport?.addEventListener('resize', queueFit);
  new ResizeObserver(queueFit).observe($('#panel'));
  // Optional fold reporting (behind feature flags in Safari): the layout already works without it.
  navigator.devicePosture?.addEventListener?.('change', queueFit);
  fitPadding();
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible' && app.settings.keepAwake) keepAwake(true); });

  if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost'))
    navigator.serviceWorker.register('sw.js').catch(() => {});

  try {
    const status = await getJson('/api/status');
    if (status?.codeRequired && !status.signedIn) return V.showLogin();
  } catch { /* offline: carry on with whatever is cached */ }
  await loadInfo();
}

export async function loadInfo() {
  try {
    app.info = await getJson('/api/info');
  } catch (e) {
    if (e.name === 'AuthRequired') return;
    $('#splash-text').textContent = 'Can\'t reach the Field Map server. Check the connection and try again.';
    $('#splash').classList.add('error');
    $('#splash-retry').hidden = false;
    $('#splash-retry').onclick = () => location.reload();
    return;
  }
  $('#splash').hidden = true;
  if (!app.info.hasData) {
    createMap();
    V.showNoData();
    return;
  }
  if (app.settings.layers == null)
    app.settings.layers = app.info.layers.filter((l) => l.defaultOn).map((l) => l.id);
  $('#status-data').textContent = app.info.dataDate ? `Data ${app.info.dataDate}` : '';
  createMap();
  loadEventList();
  startGps();
  if (app.settings.keepAwake) keepAwake(true);
}

// ------------------------------------------------------------------ the map

function initialView() {
  const v = app.settings.view;
  if (v && app.info.regions.some((r) => v.lon >= r.bounds[0] && v.lon <= r.bounds[2] && v.lat >= r.bounds[1] && v.lat <= r.bounds[3]))
    return { center: [v.lon, v.lat], zoom: v.zoom };
  const b = app.info.regions[0]?.bounds;
  return b ? { bounds: [[b[0], b[1]], [b[2], b[3]]] } : { center: [-120, 53], zoom: 4 };
}

function createMap() {
  const view = app.info?.hasData ? initialView() : { center: [-120, 53], zoom: 4 };
  app.map = new maplibregl.Map({
    container: 'map',
    style: buildStyle(app.info ?? { regions: [], version: '0' }, app.settings.theme, app.origin),
    ...view,
    maxPitch: 0,
    pitchWithRotate: false,
    attributionControl: false,
    fadeDuration: 150,
    dragRotate: true,
    touchPitch: false,
    maxZoom: 19
  });
  fitPadding();
  app.map.addControl(new maplibregl.AttributionControl({ compact: true, customAttribution: '© OpenStreetMap contributors' }), 'bottom-left');
  app.map.addControl(new maplibregl.ScaleControl({ unit: 'metric', maxWidth: 90 }), 'bottom-left');
  app.map.on('style.load', () => { addImages(); addOverlays(); });
  app.map.on('moveend', () => {
    const c = app.map.getCenter();
    app.settings.view = { lat: c.lat, lon: c.lng, zoom: app.map.getZoom() };
    save();
    schedulePois();
    if (!app.pos) updateStatus();
  });
  app.map.on('rotate', () => {
    $('#btn-compass .needle').style.transform = `rotate(${-app.map.getBearing()}deg)`;
  });
  const stopFollow = (e) => { if (e.originalEvent && app.follow) { app.follow = false; updateMeButton(); } };
  app.map.on('dragstart', stopFollow);
  app.map.on('click', onTap);
  longPress(app.map, (lngLat) => V.openWhatsHere(lngLat.lat, lngLat.lng));
}

export function setTheme(theme) {
  app.settings.theme = theme;
  save();
  document.body.dataset.ui = THEMES[theme].ui;
  document.querySelector('meta[name=theme-color]').setAttribute('content', THEMES[theme].background);
  app.map.setStyle(buildStyle(app.info, theme, app.origin), { diff: false });
}

/** Keeps the map's centre in the part of the screen the panel doesn't cover. */
function fitPadding() {
  const p = $('#panel');
  const split = getComputedStyle(p).getPropertyValue('--sheet').trim() !== '1';
  document.body.dataset.layout = split ? 'split' : 'sheet';
  const open = app.panel?.isOpen;
  document.body.classList.toggle('panel-open', !!open);
  if (!app.map) return;
  // offset* ignore the slide-in transform, so this is where the panel sits when open, even while it's closed or moving.
  const top = $('#top');
  const padding = { top: split ? 16 : top.offsetTop + top.offsetHeight + 8, left: 0, right: 0, bottom: 0 };
  // Two panes: the map always centres on the right-hand side, so your position never sits on the fold and the map
  // doesn't jump when the panel opens or closes.
  if (split) padding.left = Math.max(0, Math.min(p.offsetLeft + p.offsetWidth, window.innerWidth - 200));
  else if (open) padding.bottom = Math.max(0, Math.min(p.offsetHeight, window.innerHeight * 0.6));
  const same = (a, b) => ['top', 'left', 'right', 'bottom'].every((k) => Math.abs(a[k] - b[k]) < 1);
  if (same(app.map.getPadding(), padding)) return;
  // Changing the padding jumps the camera, which would cut short a fly-to that's under way.
  if (app.map.isMoving()) app.map.once('moveend', () => app.map.setPadding(padding));
  else app.map.setPadding(padding);
}

// Opening or closing a foldable (or rotating, or Split View) changes the size without a reload: re-fit once per frame.
let fitQueued = false;
function queueFit() {
  if (fitQueued) return;
  fitQueued = true;
  requestAnimationFrame(() => { fitQueued = false; fitPadding(); });
}

// ------------------------------------------------------------------ map symbols

function badgeImage(text, fill, textColor, size = 30) {
  const ratio = 2, s = size * ratio;
  const c = document.createElement('canvas');
  c.width = c.height = s;
  const g = c.getContext('2d');
  g.beginPath();
  g.arc(s / 2, s / 2, s / 2 - 3 * ratio, 0, Math.PI * 2);
  g.fillStyle = fill;
  g.fill();
  g.lineWidth = 2.2 * ratio;
  g.strokeStyle = app.settings.theme === 'daylight' ? '#000' : 'rgba(255,255,255,0.85)';
  g.stroke();
  g.fillStyle = textColor;
  g.font = `700 ${(text.length >= 3 ? 9 : text.length === 2 ? 11.5 : 14) * ratio}px system-ui, -apple-system, sans-serif`;
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.fillText(text, s / 2, s / 2 + ratio);
  return { width: s, height: s, data: g.getImageData(0, 0, s, s).data };
}

const luminance = (hex) => {
  const n = parseInt(hex.slice(1), 16);
  return (0.299 * ((n >> 16) & 255) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)) / 255;
};

function meImage(arrow, color) {
  const ratio = 2, s = 48 * ratio;
  const c = document.createElement('canvas');
  c.width = c.height = s;
  const g = c.getContext('2d');
  g.translate(s / 2, s / 2);
  g.lineJoin = 'round';
  if (arrow) {
    g.beginPath();
    g.moveTo(0, -19 * ratio);
    g.lineTo(13 * ratio, 15 * ratio);
    g.lineTo(0, 8 * ratio);
    g.lineTo(-13 * ratio, 15 * ratio);
    g.closePath();
  } else {
    g.beginPath();
    g.arc(0, 0, 10 * ratio, 0, Math.PI * 2);
  }
  g.shadowColor = 'rgba(0,0,0,0.55)';
  g.shadowBlur = 6 * ratio;
  g.fillStyle = color;
  g.fill();
  g.shadowBlur = 0;
  g.lineWidth = 3.5 * ratio;
  g.strokeStyle = '#ffffff';
  g.stroke();
  return { width: s, height: s, data: g.getImageData(0, 0, s, s).data };
}

function addImages() {
  const m = app.map, theme = app.settings.theme;
  for (const l of app.info?.layers ?? []) {
    const fill = symbolColor(l.color, theme);
    m.addImage(`poi-${l.id}`, badgeImage(l.glyph, fill, luminance(l.color) > 0.7 ? '#111' : '#fff'), { pixelRatio: 2 });
  }
  m.addImage('rpt', badgeImage('R', symbolColor('#8e44ad', theme), '#fff', 28), { pixelRatio: 2 });
  const gps = theme === 'night' ? '#3d7cc9' : '#1e88ff';
  m.addImage('me-arrow', meImage(true, gps), { pixelRatio: 2 });
  m.addImage('me-dot', meImage(false, gps), { pixelRatio: 2 });
  m.addImage('me-stale', meImage(false, '#777'), { pixelRatio: 2 });
}

// ------------------------------------------------------------------ overlays (coverage, event files, route, POIs, me)

const EMPTY = { type: 'FeatureCollection', features: [] };
export const INTERACTIVE = new Set();

function addOverlays() {
  const m = app.map, t = THEMES[app.settings.theme];
  INTERACTIVE.clear();
  if (app.info?.coverage) {
    m.addSource('coverage', {
      type: 'vector', maxzoom: app.info.coverage.maxZoom ?? 11,
      tiles: [`${app.origin}/tiles/coverage/{z}/{x}/{y}.pbf?v=${app.info.version}`]
    });
    m.addLayer({
      id: 'coverage', type: 'fill', source: 'coverage', 'source-layer': 'coverage',
      layout: { visibility: app.settings.coverage ? 'visible' : 'none' },
      paint: {
        'fill-color': ['match', ['get', 'kind'], '5g', '#8e3fd6', 'lte', '#1e7ae6', '3g', '#00a0b0', '#888888'],
        'fill-opacity': ['match', ['get', 'kind'], '5g', 0.3, 0.24]
      }
    });
  }
  for (const ev of app.events.values()) addEventLayers(ev);
  m.addSource('route', { type: 'geojson', data: app.route ? routeGeoJson(app.route) : EMPTY });
  m.addLayer({ id: 'route-case', type: 'line', source: 'route', layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': '#000', 'line-width': ['interpolate', ['linear'], ['zoom'], 10, 7, 16, 14], 'line-opacity': 0.6 } });
  m.addLayer({ id: 'route', type: 'line', source: 'route', layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': t.route, 'line-width': ['interpolate', ['linear'], ['zoom'], 10, 4, 16, 9] } });

  m.addSource('pois', { type: 'geojson', data: EMPTY });
  const minZoom = ['match', ['get', 'layer'], ...(app.info?.layers ?? []).flatMap((l) => [l.id, l.minZoom]), 12];
  m.addLayer({
    id: 'pois', type: 'symbol', source: 'pois',
    filter: ['>=', ['zoom'], minZoom],
    layout: {
      'icon-image': ['concat', 'poi-', ['get', 'layer']],
      'icon-size': ['interpolate', ['linear'], ['zoom'], 11, 0.75, 16, 1],
      'icon-allow-overlap': ['step', ['zoom'], false, 16, true],
      'icon-padding': 1,
      // Emergency services first when symbols compete for space.
      'symbol-sort-key': ['match', ['get', 'layer'], 'hospital', 0, 'fire', 1, 'ambulance', 1, 'police', 2, 'aed', 3, 'water', 6, 'access', 7, 5],
      'text-field': ['step', ['zoom'], '', 16, ['get', 'name']],
      'text-font': ['Noto Sans Regular'], 'text-size': 11, 'text-offset': [0, 1.5], 'text-anchor': 'top', 'text-optional': true, 'text-max-width': 9
    },
    paint: { 'text-color': t.label, 'text-halo-color': t.halo, 'text-halo-width': 1.4 }
  });
  INTERACTIVE.add('pois');

  m.addSource('repeaters', { type: 'geojson', data: app.repeaters ?? EMPTY });
  m.addLayer({
    id: 'repeaters', type: 'symbol', source: 'repeaters', minzoom: 7,
    layout: {
      visibility: app.settings.repeaters ? 'visible' : 'none',
      'icon-image': 'rpt', 'icon-allow-overlap': true,
      'text-field': ['step', ['zoom'], '', 11, ['get', 'label']], 'text-font': ['Noto Sans Regular'], 'text-size': 11,
      'text-offset': [0, 1.4], 'text-anchor': 'top', 'text-optional': true
    },
    paint: { 'text-color': t.label, 'text-halo-color': t.halo, 'text-halo-width': 1.4 }
  });
  INTERACTIVE.add('repeaters');

  m.addSource('me-acc', { type: 'geojson', data: EMPTY });
  m.addLayer({ id: 'me-acc', type: 'fill', source: 'me-acc', paint: { 'fill-color': t.gps, 'fill-opacity': 0.12, 'fill-outline-color': t.gps } });
  m.addSource('me', { type: 'geojson', data: EMPTY });
  m.addLayer({
    id: 'me', type: 'symbol', source: 'me',
    layout: {
      'icon-image': ['get', 'icon'], 'icon-rotate': ['get', 'heading'], 'icon-rotation-alignment': 'map',
      'icon-allow-overlap': true, 'icon-ignore-placement': true
    }
  });
  if (app.pos) drawPosition();
  lastPoiKey = '';
  schedulePois();
  if (app.settings.repeaters && !app.repeaters && app.info?.repeaters > 0) loadRepeaters();
}

// ------------------------------------------------------------------ event files

const EVENT_COLOR = '#ff9f1c';

function addEventLayers(ev) {
  const m = app.map;
  if (!ev.data || m.getSource(`ev-${ev.id}`)) return;
  const vis = app.settings.hiddenEvents.includes(ev.id) ? 'none' : 'visible';
  const src = `ev-${ev.id}`;
  m.addSource(src, { type: 'geojson', data: ev.data });
  const t = THEMES[app.settings.theme];
  m.addLayer({
    id: `${src}-fill`, type: 'fill', source: src, filter: ['==', ['geometry-type'], 'Polygon'], layout: { visibility: vis },
    paint: { 'fill-color': ['coalesce', ['get', 'fill'], ['get', 'stroke'], EVENT_COLOR], 'fill-opacity': ['min', 0.35, ['coalesce', ['get', 'fillOpacity'], 0.2]] }
  });
  m.addLayer({
    id: `${src}-line`, type: 'line', source: src, filter: ['in', ['geometry-type'], ['literal', ['LineString', 'Polygon']]], layout: { visibility: vis, 'line-join': 'round', 'line-cap': 'round' },
    paint: { 'line-color': ['coalesce', ['get', 'stroke'], EVENT_COLOR], 'line-width': ['interpolate', ['linear'], ['zoom'], 10, 1.5, 16, ['max', 3, ['coalesce', ['get', 'width'], 3]]] }
  });
  m.addLayer({
    id: `${src}-pt`, type: 'circle', source: src, filter: ['==', ['geometry-type'], 'Point'], layout: { visibility: vis },
    paint: {
      'circle-radius': ['interpolate', ['linear'], ['zoom'], 10, 4, 16, 8], 'circle-color': ['coalesce', ['get', 'stroke'], EVENT_COLOR],
      'circle-stroke-color': app.settings.theme === 'daylight' ? '#000' : '#fff', 'circle-stroke-width': 2,
      'circle-opacity': app.settings.theme === 'night' ? 0.7 : 1
    }
  });
  m.addLayer({
    id: `${src}-label`, type: 'symbol', source: src, minzoom: 13, layout: {
      visibility: vis, 'text-field': ['get', 'name'], 'text-font': ['Noto Sans Regular'], 'text-size': 12,
      'text-offset': [0, 1.1], 'text-anchor': 'top', 'text-optional': true, 'text-max-width': 10,
      'symbol-placement': 'point'
    },
    paint: { 'text-color': t.label, 'text-halo-color': t.halo, 'text-halo-width': 1.6 }
  });
  for (const k of ['pt', 'line', 'fill']) INTERACTIVE.add(`${src}-${k}`);
  // Keep event files under the route, POIs and the position marker.
  for (const top of ['route-case', 'route', 'pois', 'repeaters', 'me-acc', 'me'])
    if (m.getLayer(top)) m.moveLayer(top);
}

export async function loadEventList() {
  const server = await getJson('/api/events').catch(() => []) ?? [];
  const local = await listLocalFiles();
  for (const e of server) {
    const known = app.events.get(e.id);
    app.events.set(e.id, { ...known, ...e, source: 'server' });
  }
  for (const f of local) app.events.set(f.id, { id: f.id, name: f.name, summary: f.summary, source: 'phone', data: f.data });
  for (const [id, ev] of app.events)
    if (ev.source === 'server' && !server.some((s) => s.id === id)) { removeEventLayers(id); app.events.delete(id); }
  await Promise.all([...app.events.values()].map(ensureEventData));
  V.refreshIfShowing('layers', 'items');
}

async function ensureEventData(ev) {
  if (!ev.data && ev.source === 'server') {
    try { ev.data = await getJson(`/api/events/${ev.id}?m=${encodeURIComponent(ev.modified ?? '')}`); } catch { return; }
  }
  if (ev.data && app.map?.isStyleLoaded()) addEventLayers(ev);
  else if (ev.data) app.map?.once('idle', () => addEventLayers(ev));
}

export function addLocalEvent(file) {
  app.events.set(file.id, { id: file.id, name: file.name, summary: file.summary, source: 'phone', data: file.data });
  addEventLayers(app.events.get(file.id));
}

export function removeEventLayers(id) {
  const m = app.map, src = `ev-${id}`;
  for (const k of ['label', 'pt', 'line', 'fill']) {
    if (m.getLayer(`${src}-${k}`)) m.removeLayer(`${src}-${k}`);
    INTERACTIVE.delete(`${src}-${k}`);
  }
  if (m.getSource(src)) m.removeSource(src);
}

export function setEventVisible(id, visible) {
  const hidden = new Set(app.settings.hiddenEvents);
  visible ? hidden.delete(id) : hidden.add(id);
  app.settings.hiddenEvents = [...hidden];
  save();
  for (const k of ['label', 'pt', 'line', 'fill'])
    if (app.map.getLayer(`ev-${id}-${k}`)) app.map.setLayoutProperty(`ev-${id}-${k}`, 'visibility', visible ? 'visible' : 'none');
}

export function setLayerVisible(id, visible) {
  if (id === 'coverage') { app.settings.coverage = visible; if (app.map.getLayer('coverage')) app.map.setLayoutProperty('coverage', 'visibility', visible ? 'visible' : 'none'); }
  else if (id === 'repeaters') { app.settings.repeaters = visible; app.map.setLayoutProperty('repeaters', 'visibility', visible ? 'visible' : 'none'); if (visible && !app.repeaters) loadRepeaters(); }
  else {
    const s = new Set(app.settings.layers);
    visible ? s.add(id) : s.delete(id);
    app.settings.layers = [...s];
    lastPoiKey = '';
    schedulePois();
  }
  save();
}

async function loadRepeaters() {
  const list = await getJson('/api/repeaters').catch(() => []) ?? [];
  app.repeaters = { type: 'FeatureCollection', features: list.map((r, i) => ({ type: 'Feature', id: i, geometry: { type: 'Point', coordinates: [r.lon, r.lat] }, properties: { ...r, i } })) };
  app.map.getSource('repeaters')?.setData(app.repeaters);
}

// ------------------------------------------------------------------ POIs for the visible area

let poiTimer, poiAbort, lastPoiKey = '';
function schedulePois() {
  clearTimeout(poiTimer);
  poiTimer = setTimeout(loadPois, 200);
}

async function loadPois() {
  const m = app.map;
  if (!m?.getSource('pois') || !app.info?.hasData) return;
  const z = m.getZoom();
  const layers = app.info.layers.filter((l) => app.settings.layers.includes(l.id) && l.minZoom <= z + 0.5).map((l) => l.id);
  if (layers.length === 0) { m.getSource('pois').setData(EMPTY); lastPoiKey = ''; return; }
  const b = m.getBounds();
  // Ask for a margin around the screen so small pans don't need another request.
  const dx = (b.getEast() - b.getWest()) * 0.3, dy = (b.getNorth() - b.getSouth()) * 0.3;
  const want = [b.getWest() - dx, b.getSouth() - dy, b.getEast() + dx, b.getNorth() + dy];
  const key = layers.join(',');
  if (lastPoiKey && lastPoiKey.key === key && lastPoiKey.box[0] <= b.getWest() && lastPoiKey.box[1] <= b.getSouth() && lastPoiKey.box[2] >= b.getEast() && lastPoiKey.box[3] >= b.getNorth() && lastPoiKey.z <= z + 1.5) return;
  poiAbort?.abort();
  poiAbort = new AbortController();
  try {
    const data = await getJson(`/api/pois?${qs({ bbox: want.map((v) => v.toFixed(5)).join(','), layers: key })}`, { signal: poiAbort.signal });
    if (!data) return;
    m.getSource('pois')?.setData(data);
    lastPoiKey = { key, box: want, z };
  } catch { /* aborted or offline */ }
}

// ------------------------------------------------------------------ taps

function onTap(e) {
  if (suppressClick) { suppressClick = false; return; }
  const layers = [...INTERACTIVE].filter((l) => app.map.getLayer(l));
  const r = 14;
  const feats = app.map.queryRenderedFeatures([[e.point.x - r, e.point.y - r], [e.point.x + r, e.point.y + r]], { layers });
  if (!feats.length) return;
  // Points beat lines beat areas; within a kind, the one drawn on top wins.
  const rank = (f) => (f.layer.id === 'pois' || f.layer.id === 'repeaters' || f.layer.id.endsWith('-pt') ? 0 : f.layer.id.endsWith('-line') ? 1 : 2);
  const f = feats.sort((a, b) => rank(a) - rank(b))[0];
  if (f.layer.id === 'pois') V.openPoi(f.properties.id);
  else if (f.layer.id === 'repeaters') V.openRepeater(f.properties);
  else {
    const id = f.layer.id.replace(/^ev-/, '').replace(/-(pt|line|fill)$/, '');
    const ev = app.events.get(id);
    const full = ev?.data?.features?.find((x) => x.properties.i === f.properties.i);
    if (ev && full) V.openEventFeature(ev, full, [e.lngLat.lng, e.lngLat.lat]);
  }
}

let suppressClick = false;
function longPress(map, fire) {
  const el = map.getCanvasContainer();
  let timer = null, start = null;
  const cancel = () => { clearTimeout(timer); timer = null; };
  el.addEventListener('touchstart', (e) => {
    if (e.touches.length !== 1) return cancel();
    const t = e.touches[0];
    const rect = el.getBoundingClientRect();
    start = [t.clientX - rect.left, t.clientY - rect.top];
    cancel();
    timer = setTimeout(() => {
      timer = null;
      suppressClick = true;
      setTimeout(() => { suppressClick = false; }, 800);
      navigator.vibrate?.(30);
      fire(map.unproject(start));
    }, 550);
  }, { passive: true });
  el.addEventListener('touchmove', (e) => {
    if (!timer) return;
    const t = e.touches[0], rect = el.getBoundingClientRect();
    if (Math.hypot(t.clientX - rect.left - start[0], t.clientY - rect.top - start[1]) > 10) cancel();
  }, { passive: true });
  el.addEventListener('touchend', cancel);
  el.addEventListener('touchcancel', cancel);
  map.on('contextmenu', (e) => fire(e.lngLat)); // mouse right-click on a computer
}

// ------------------------------------------------------------------ selection marker

export function select(lat, lon, { fly = true, zoom } = {}) {
  app.selected = { lat, lon };
  if (!app.marker) {
    const el = h('div', { class: 'sel-marker' }, icon('pin', 'sel-pin'));
    app.marker = new maplibregl.Marker({ element: el, anchor: 'bottom' });
  }
  app.marker.setLngLat([lon, lat]).addTo(app.map);
  if (fly) {
    app.follow = false;
    updateMeButton();
    // Wait a frame so the panel that's opening has set the map padding first.
    requestAnimationFrame(() => app.map.flyTo({ center: [lon, lat], zoom: Math.max(app.map.getZoom(), zoom ?? 15), speed: 1.6 }));
  }
}

export function clearSelection() {
  app.marker?.remove();
  app.selected = null;
}

export function fitTo(bbox) {
  app.follow = false;
  updateMeButton();
  requestAnimationFrame(() => app.map.fitBounds([[bbox[0], bbox[1]], [bbox[2], bbox[3]]], { padding: 50, maxZoom: 16 }));
}

// ------------------------------------------------------------------ GPS

let watchId = null;
function startGps() {
  if (!('geolocation' in navigator)) { setGpsState('none', 'No GPS'); return; }
  if (watchId !== null) return;
  setGpsState('wait', 'GPS…');
  watchId = navigator.geolocation.watchPosition(onPosition, onGpsError, { enableHighAccuracy: true, maximumAge: 2000, timeout: 30000 });
  setInterval(() => { if (app.pos && Date.now() - app.pos.time > 30000) { setGpsState('stale', 'GPS old'); drawPosition(); } }, 5000);
}

function onGpsError(err) {
  if (err.code === err.PERMISSION_DENIED) {
    setGpsState('none', 'GPS off');
    toast('Location is off for this site. Turn it on in Settings › Privacy › Location Services › Safari Websites.', 6000);
    navigator.geolocation.clearWatch(watchId);
    watchId = null;
  } else if (!app.pos) setGpsState('wait', 'No fix');
}

function onPosition(p) {
  const c = p.coords;
  const prev = app.pos;
  let heading = Number.isFinite(c.heading) && c.speed > 1.2 ? c.heading : null;
  // Keep the last course for a short stop (traffic lights) instead of jumping to a dot.
  if (heading == null && prev?.heading != null && Date.now() - prev.headingTime < 20000) heading = prev.heading;
  app.pos = {
    lat: c.latitude, lon: c.longitude, acc: c.accuracy, speed: c.speed, heading,
    headingTime: Number.isFinite(c.heading) && c.speed > 1.2 ? Date.now() : prev?.headingTime ?? 0,
    time: Date.now()
  };
  setGpsState(c.accuracy <= 30 ? 'good' : 'poor', `±${Math.round(c.accuracy)} m`);
  drawPosition();
  updateStatus();
  if (app.follow) followCamera(!prev);
  if (app.route) updateRouteProgress();
}

function drawPosition() {
  const m = app.map, p = app.pos;
  if (!p || !m?.getSource('me')) return;
  const stale = Date.now() - p.time > 30000;
  const heading = p.heading ?? app.compassHeading;
  m.getSource('me').setData({
    type: 'FeatureCollection',
    features: [{ type: 'Feature', geometry: { type: 'Point', coordinates: [p.lon, p.lat] }, properties: { icon: stale ? 'me-stale' : heading != null ? 'me-arrow' : 'me-dot', heading: heading ?? 0 } }]
  });
  m.getSource('me-acc').setData(p.acc > 8 ? circle([p.lon, p.lat], p.acc) : EMPTY);
}

function circle(center, radius) {
  const pts = [], n = 48, lat = center[1] * Math.PI / 180;
  for (let i = 0; i <= n; i++) {
    const a = (i / n) * 2 * Math.PI;
    pts.push([center[0] + (radius * Math.sin(a)) / (111320 * Math.cos(lat)), center[1] + (radius * Math.cos(a)) / 110540]);
  }
  return { type: 'Feature', geometry: { type: 'Polygon', coordinates: [pts] }, properties: {} };
}

function followCamera(first) {
  const p = app.pos;
  const opts = { center: [p.lon, p.lat], duration: first ? 0 : 600 };
  if (first) opts.zoom = Math.max(app.map.getZoom(), 15);
  const heading = p.heading ?? app.compassHeading;
  if (app.settings.headingUp && heading != null) opts.bearing = heading;
  app.map.easeTo(opts);
}

function recenter() {
  if (!app.pos) {
    startGps();
    toast(watchId === null ? 'Location isn\'t available.' : 'Waiting for a GPS fix…');
    return;
  }
  app.follow = true;
  updateMeButton();
  app.map.easeTo({ center: [app.pos.lon, app.pos.lat], zoom: Math.max(app.map.getZoom(), 15), duration: 500, bearing: app.settings.headingUp ? (app.pos.heading ?? app.compassHeading ?? 0) : 0 });
}

function updateMeButton() {
  $('#btn-me').classList.toggle('on', app.follow && !!app.pos);
}

function setGpsState(state, text) {
  const el = $('#status-gps');
  el.dataset.state = state;
  el.textContent = text;
  updateMeButton();
}

// Heading-up turns the map with the direction of travel; standing still it uses the phone's compass if allowed.
async function toggleHeadingUp() {
  app.settings.headingUp = !app.settings.headingUp;
  save();
  $('#btn-compass').classList.toggle('on', app.settings.headingUp);
  if (app.settings.headingUp) {
    await startCompass();
    toast('Heading up: the map turns with your direction of travel');
    if (app.pos) { app.follow = true; updateMeButton(); followCamera(false); }
  } else {
    app.map.easeTo({ bearing: 0, duration: 400 });
    toast('North up');
  }
}

let compassOn = false;
async function startCompass() {
  if (compassOn || !('DeviceOrientationEvent' in window)) return;
  try {
    if (typeof DeviceOrientationEvent.requestPermission === 'function' && (await DeviceOrientationEvent.requestPermission()) !== 'granted') return;
  } catch { return; }
  compassOn = true;
  let last = 0;
  window.addEventListener('deviceorientation', (e) => {
    const hdg = e.webkitCompassHeading ?? (e.absolute && e.alpha != null ? 360 - e.alpha : null);
    if (hdg == null) return;
    app.compassHeading = hdg;
    const now = Date.now();
    if (now - last < 250) return;
    last = now;
    if (app.pos && app.pos.heading == null) {
      drawPosition();
      if (app.settings.headingUp && app.follow) app.map.easeTo({ bearing: hdg, duration: 250 });
    }
  });
}

// ------------------------------------------------------------------ status strip

function cycleCoordFormat() {
  const i = C.FORMATS.findIndex((f) => f.id === app.settings.coordFormat);
  app.settings.coordFormat = C.FORMATS[(i + 1) % C.FORMATS.length].id;
  save();
  updateStatus();
  toast(C.FORMATS.find((f) => f.id === app.settings.coordFormat).label);
}

export function updateStatus() {
  const f = C.FORMATS.find((x) => x.id === app.settings.coordFormat) ?? C.FORMATS[0];
  let lat, lon, prefix = '';
  if (app.pos) ({ lat, lon } = app.pos);
  else if (app.map) { const c = app.map.getCenter(); lat = c.lat; lon = c.lng; prefix = '⊕ '; }
  else return;
  $('#status-coords').textContent = prefix + f.fn(lat, lon);
  const p = app.pos;
  $('#status-speed').textContent = p && p.speed > 0.8 ? `${Math.round(p.speed * 3.6)} km/h${p.heading != null ? ' ' + C.compass(p.heading) : ''}` : '';
}

export function myPositionOrCentre() {
  if (app.pos) return { lat: app.pos.lat, lon: app.pos.lon, mine: true };
  const c = app.map.getCenter();
  return { lat: c.lat, lon: c.lng, mine: false };
}

// ------------------------------------------------------------------ route

export function routeGeoJson(r) {
  return { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: { type: 'LineString', coordinates: r.geometry }, properties: {} }] };
}

export function showRoute(r, to, toLabel) {
  const cum = [0];
  for (let i = 1; i < r.geometry.length; i++) cum.push(cum[i - 1] + C.distance(r.geometry[i - 1], r.geometry[i]));
  app.route = { ...r, to, toLabel, cumulative: cum };
  app.map.getSource('route')?.setData(routeGeoJson(r));
  const lons = r.geometry.map((p) => p[0]), lats = r.geometry.map((p) => p[1]);
  fitTo([Math.min(...lons), Math.min(...lats), Math.max(...lons), Math.max(...lats)]);
  updateRouteProgress();
}

export function clearRoute() {
  app.route = null;
  app.map.getSource('route')?.setData(EMPTY);
  $('#route-banner').hidden = true;
  document.body.classList.remove('routing');
}

function updateRouteProgress() {
  const r = app.route, p = app.pos, banner = $('#route-banner');
  document.body.classList.add('routing');
  banner.hidden = false;
  if (!p) {
    banner.replaceChildren(h('b', {}, `${r.distanceText} · ${r.durationText}`), h('span', {}, ` to ${r.toLabel}`));
    return;
  }
  // Nearest point on the route line (flat-earth projection is fine over a few km).
  const k = Math.cos(p.lat * Math.PI / 180);
  let best = Infinity, along = 0;
  for (let i = 1; i < r.geometry.length; i++) {
    const [ax, ay] = r.geometry[i - 1], [bx, by] = r.geometry[i];
    const vx = (bx - ax) * k, vy = by - ay, wx = (p.lon - ax) * k, wy = p.lat - ay;
    const len = vx * vx + vy * vy;
    const t = len > 0 ? Math.max(0, Math.min(1, (wx * vx + wy * vy) / len)) : 0;
    const d = Math.hypot(wx - t * vx, wy - t * vy) * 111195;
    if (d < best) { best = d; along = r.cumulative[i - 1] + t * (r.cumulative[i] - r.cumulative[i - 1]); }
  }
  const total = r.cumulative.at(-1);
  const left = Math.max(0, total - along);
  if (left < 30) {
    banner.replaceChildren(h('b', {}, 'Arrived'), h('span', {}, ` · ${r.toLabel}`));
    return;
  }
  if (best > 80 && along < 30) {
    // Starting away from a road (car park, field): not off route yet, just not on it.
    set(banner, h('b', {}, `Head to the road (${C.formatDistance(best)})`), h('span', {}, ` · then ${r.steps[0]?.instruction ?? ''}`));
    return;
  }
  if (best > 80) {
    banner.replaceChildren(h('b', {}, `Off route (${C.formatDistance(best)})`), h('span', {}, ' · tap to recalculate'));
    return;
  }
  const next = r.steps.find((s) => s.along > along + 15) ?? r.steps.at(-1);
  banner.replaceChildren(
    h('b', {}, `${C.formatDistance(Math.max(0, next.along - along))}  ${next.instruction}`),
    h('span', {}, ` · ${C.formatDistance(left)} left`));
}

// ------------------------------------------------------------------ keep the screen on

export async function keepAwake(on) {
  app.settings.keepAwake = on;
  save();
  try {
    if (on && 'wakeLock' in navigator) app.wakeLock = await navigator.wakeLock.request('screen');
    else { await app.wakeLock?.release(); app.wakeLock = null; }
  } catch { if (on) toast('This browser can\'t keep the screen on. Add Field Map to the Home Screen and try again.'); }
}

// ------------------------------------------------------------------ tabs

export function openTab(tab) {
  if (app.panel.isOpen && app.panel.current?.tab === tab && app.panel.stack.length === 1) return app.panel.close();
  const views = { search: V.searchView, nearest: V.nearestView, layers: V.layersView, items: V.itemsView, more: V.moreView };
  app.panel.show(views[tab]());
}

start();
