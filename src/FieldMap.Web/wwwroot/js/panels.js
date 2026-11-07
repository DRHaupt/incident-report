// The panel views: search, place details, directions, nearest, layers, event items, more, sign-in.

import { app, save, select, clearSelection, fitTo, showRoute, clearRoute, setTheme, setLayerVisible, setEventVisible,
  addLocalEvent, removeEventLayers, keepAwake, myPositionOrCentre, updateStatus, loadEventList } from './app.js';
import * as C from './coords.js';
import { getJson, postJson, postFile, qs } from './api.js';
import { saveLocalFile, deleteLocalFile } from './store.js';
import { h, icon, row, toggle, segmented, button, note, heading, toast, copy, add, set } from './ui.js';
import { THEMES } from './style.js';

const PROFILES = [
  { id: 'car', label: 'Car' }, { id: 'emergency', label: 'Emergency' }, { id: 'truck', label: 'Truck' }, { id: 'walking', label: 'Walk' }
];

const layerDef = (id) => app.info?.layers.find((l) => l.id === id);
const fromHere = (lat, lon) => {
  if (!app.pos) return '';
  const d = C.distance([app.pos.lon, app.pos.lat], [lon, lat]);
  return `${C.formatDistance(d)} ${C.compass(C.bearing([app.pos.lon, app.pos.lat], [lon, lat]))} of you`;
};
const errorText = (e) => (e?.name === 'AbortError' ? '' : e?.message || 'Something went wrong.');

export function refreshIfShowing(...tabs) {
  const v = app.panel?.current;
  if (v && tabs.includes(v.tab) && app.panel.stack.length === 1) app.panel.refresh();
}

// ------------------------------------------------------------------ search

let lastQuery = '';

export function searchView() {
  let abort, timer;
  return {
    tab: 'search', title: 'Search', full: false,
    dispose: () => { abort?.abort(); clearTimeout(timer); },
    render(body) {
      const input = h('input', {
        type: 'search', class: 'search-input', placeholder: 'Address, place, MGRS, “hydrant”…', value: lastQuery,
        enterkeyhint: 'search', autocomplete: 'off', autocorrect: 'off', autocapitalize: 'off', spellcheck: 'false', 'aria-label': 'Search'
      });
      const results = h('div', { class: 'list' });
      const run = async () => {
        const q = input.value.trim();
        lastQuery = q;
        abort?.abort();
        if (!q) { set(results, hints()); return; }
        abort = new AbortController();
        const near = myPositionOrCentre();
        const events = searchEvents(q, near);
        let server = [];
        try {
          server = await getJson(`/api/search?${qs({ q, lat: near.lat.toFixed(5), lon: near.lon.toFixed(5) })}`, { signal: abort.signal }) ?? [];
        } catch (e) {
          if (e.name === 'AbortError') return;
          set(results, ...events.map(eventRow), note(errorText(e), 'warn'));
          return;
        }
        const rows = [...events.map(eventRow), ...server.map(resultRow)];
        set(results, ...(rows.length ? rows : [note(`Nothing found for “${q}”.`)]));
      };
      input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(run, 250); });
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { clearTimeout(timer); input.blur(); run(); } });
      add(body, h('div', { class: 'search-box' }, icon('search'), input), results);
      run();
      setTimeout(() => input.focus(), 60);
    }
  };
}

function hints() {
  return h('div', { class: 'hints' },
    note('Try a street address, an intersection (“Main & 5th”), a place, a business, a highway km (“Hwy 5 km 120”), coordinates or MGRS, or a type of place such as “hospital” or “hydrant”.'),
    app.events.size ? note('Names in your event files are searched too.') : null);
}

const KIND_BADGE = { place: '◎', street: '═', address: '#', intersection: '+', kmmarker: 'km', exit: 'Ex', coordinate: '⌖', business: '$', category: '≡' };

function resultRow(r) {
  const l = r.layer ? layerDef(r.layer) : null;
  const badge = r.kind === 'poi' && l ? l.glyph : KIND_BADGE[r.kind] ?? '•';
  const color = r.kind === 'poi' && l ? l.color : r.kind === 'category' ? 'var(--amber)' : 'var(--chip)';
  return row({
    title: r.name, detail: r.detail + (r.approximate ? ' (approximate)' : ''), badge, color,
    right: r.distance != null ? C.formatDistance(r.distance) : null,
    onClick: () => {
      if (r.kind === 'category' && r.layer) return app.panel.show(nearestListView(r.layer), { push: true });
      if (r.kind === 'poi' && r.poiId) return openPoi(r.poiId, { push: true });
      if (r.kind === 'business' && r.businessId) return openBusiness(r.businessId, { push: true });
      openPoint(r.lat, r.lon, r.name, r.detail, { push: true });
    }
  });
}

// Event files are searched on the phone: names, folders, descriptions and attribute values.
function searchEvents(q, near) {
  const words = q.toLowerCase().split(/\s+/).filter(Boolean);
  const out = [];
  for (const ev of app.events.values()) {
    for (const f of ev.data?.features ?? []) {
      const p = f.properties;
      const name = (p.name || '').toLowerCase();
      const hay = `${name} ${(p.folder || '').toLowerCase()} ${(p.description || '').toLowerCase()} ${Object.values(p.attrs ?? {}).join(' ').toLowerCase()}`;
      if (!words.every((w) => hay.includes(w))) continue;
      const score = (name === q.toLowerCase() ? 3 : 0) + (name.startsWith(words[0]) ? 2 : 0) + (words.every((w) => name.includes(w)) ? 1 : 0);
      const at = anchor(f);
      out.push({ ev, f, score, d: C.distance([near.lon, near.lat], at) });
    }
  }
  return out.sort((a, b) => b.score - a.score || a.d - b.d).slice(0, 8);
}

function eventRow({ ev, f, d }) {
  const p = f.properties;
  return row({
    title: p.name || `(${p.kind} without a name)`, detail: `${ev.name}${p.folder ? ' › ' + p.folder : ''}`, badge: '◆', color: p.stroke || '#ff9f1c',
    right: C.formatDistance(d), onClick: () => openEventFeature(ev, f, null, { push: true })
  });
}

function anchor(f) {
  const g = f.geometry;
  if (g.type === 'Point') return g.coordinates;
  const pts = g.type === 'LineString' ? g.coordinates : g.type === 'MultiLineString' ? g.coordinates.flat() : g.coordinates[0];
  if (g.type === 'Polygon') {
    const n = pts.length;
    return [pts.reduce((s, p) => s + p[0], 0) / n, pts.reduce((s, p) => s + p[1], 0) / n];
  }
  return pts[Math.floor(pts.length / 2)];
}

function bboxOf(f) {
  const g = f.geometry;
  const pts = g.type === 'Point' ? [g.coordinates] : g.type === 'LineString' ? g.coordinates : g.type === 'MultiLineString' ? g.coordinates.flat() : g.coordinates.flat();
  const xs = pts.map((p) => p[0]), ys = pts.map((p) => p[1]);
  return [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)];
}

// ------------------------------------------------------------------ place details

/** A details view for any point. `lines` are extra rows of text; the address is looked up from the server. */
function placeView({ title, kind, lat, lon, lines = [], warning, source, extraButtons = [], lookup = true, tab }) {
  return {
    tab, title: title.length > 28 ? 'Details' : title,
    render(body) {
      const addr = h('p', { class: 'addr' }, lookup ? '…' : '');
      add(body, 
        h('div', { class: 'place-head' }, h('h2', {}, title), kind ? h('p', { class: 'kind' }, kind) : null, addr,
          app.pos ? h('p', { class: 'from-me' }, fromHere(lat, lon)) : null),
        warning ? note(warning, 'warn') : null,
        ...lines.filter(Boolean).map((l) => (typeof l === 'string' ? h('p', { class: 'line' }, l) : l)),
        h('div', { class: 'actions' },
          button('Directions', () => openDirections({ lat, lon }, title, { push: true }), 'primary', 'route'),
          button('Copy for dispatch', () => copy(C.dispatchText(title, lat, lon, kind ? [kind] : []), 'Copied for dispatch'), '', 'copy'),
          navigator.share ? button('Share', () => navigator.share({ title, text: C.dispatchText(title, lat, lon), url: `https://maps.apple.com/?ll=${lat},${lon}&q=${encodeURIComponent(title)}` }).catch(() => {}), '', 'share') : null,
          ...extraButtons),
        coordsBlock(lat, lon),
        h('div', { class: 'actions small' },
          h('a', { class: 'btn', href: `https://maps.apple.com/?daddr=${lat},${lon}&dirflg=d`, target: '_blank', rel: 'noopener' }, 'Apple Maps'),
          h('a', { class: 'btn', href: `https://www.google.com/maps/dir/?api=1&destination=${lat},${lon}`, target: '_blank', rel: 'noopener' }, 'Google Maps')),
        source ? note(source, 'muted') : null);
      if (lookup)
        getJson(`/api/whatshere?${qs({ lat, lon })}`).then((w) => { addr.textContent = w?.summary ?? ''; }).catch(() => { addr.textContent = ''; });
    }
  };
}

function coordsBlock(lat, lon) {
  const c = C.all(lat, lon);
  return h('div', { class: 'coords' }, C.FORMATS.map((f) =>
    h('button', { type: 'button', class: 'coord', onclick: () => copy(c[f.id], `Copied ${f.label}`) },
      h('span', { class: 'coord-label' }, f.label), h('span', { class: 'coord-value' }, c[f.id]), icon('copy', 'ico tiny'))));
}

export function openPoint(lat, lon, title, kind, { push = false } = {}) {
  select(lat, lon);
  app.panel.show(placeView({ title, kind, lat, lon }), { push });
}

export function openWhatsHere(lat, lon) {
  select(lat, lon, { fly: false });
  app.panel.show(placeView({ title: 'Dropped pin', kind: 'Touch and hold anywhere on the map to drop a pin', lat, lon }));
}

export async function openPoi(id, { push = false } = {}) {
  let p;
  try { p = await getJson(`/api/poi/${id}`); } catch (e) { return toast(errorText(e)); }
  if (!p) return toast('That place is no longer in the map data.');
  select(p.lat, p.lon, { fly: push });
  const props = p.props ?? {};
  const lines = [];
  const phone = props.phone || props['contact:phone'];
  if (props.address || props['addr:full']) lines.push(props.address || props['addr:full']);
  if (props.opening_hours) lines.push('Hours: ' + props.opening_hours);
  if (props.operator) lines.push('Operator: ' + props.operator);
  if (props.access) lines.push('Access: ' + props.access);
  if (props.description) lines.push(props.description);
  if (props.location || props.level) lines.push(['Location: ' + (props.location || ''), props.level ? `level ${props.level}` : ''].filter(Boolean).join(', '));
  if (phone) lines.push(h('a', { class: 'btn', href: `tel:${phone.replace(/[^\d+]/g, '')}` }, icon('phone'), phone));
  app.panel.show(placeView({
    title: p.name, kind: p.kindLabel, lat: p.lat, lon: p.lon, lines, warning: p.warning,
    source: `Source: ${p.source}${p.lastVerified ? ', checked ' + p.lastVerified : ''}`
  }), { push });
}

export async function openBusiness(id, { push = false } = {}) {
  let b;
  try { b = await getJson(`/api/business/${id}`); } catch (e) { return toast(errorText(e)); }
  if (!b) return;
  select(b.lat, b.lon);
  app.panel.show(placeView({
    title: b.name, kind: b.kind, lat: b.lat, lon: b.lon,
    lines: [b.address, b.hours ? 'Hours: ' + b.hours : null, b.phone ? h('a', { class: 'btn', href: `tel:${b.phone.replace(/[^\d+]/g, '')}` }, icon('phone'), b.phone) : null, b.website],
    source: 'From OpenStreetMap: names, hours and phone numbers may be out of date.'
  }), { push });
}

export function openRepeater(r) {
  select(r.lat, r.lon, { fly: false });
  app.panel.show(placeView({
    title: r.label, kind: `Repeater · ${r.frequency}`, lat: r.lat, lon: r.lon, lookup: false,
    lines: [r.name && r.name !== r.label ? r.name : null, r.mode ? 'Mode: ' + r.mode : null, r.owner ? 'Owner: ' + r.owner : null, r.notes],
    source: r.source
  }));
}

export function openEventFeature(ev, f, tapped, { push = false } = {}) {
  const p = f.properties;
  const [lon, lat] = f.geometry.type === 'Point' ? f.geometry.coordinates : tapped ?? anchor(f);
  if (f.geometry.type === 'Point') select(lat, lon, { fly: !tapped });
  else { clearSelection(); if (!tapped) fitTo(bboxOf(f)); }
  const attrs = Object.entries(p.attrs ?? {}).filter(([, v]) => v).slice(0, 25);
  app.panel.show(placeView({
    title: p.name || `(${p.kind} without a name)`, kind: `${ev.name}${p.folder ? ' › ' + p.folder : ''}`, lat, lon,
    lines: [
      p.description ? h('p', { class: 'line desc' }, p.description) : null,
      attrs.length ? h('dl', { class: 'attrs' }, attrs.flatMap(([k, v]) => [h('dt', {}, k), h('dd', {}, v)])) : null,
      f.geometry.type !== 'Point' ? h('p', { class: 'line muted' }, `${p.kind === 'line' ? 'Line' : 'Area'} – the position below is where you tapped.`) : null
    ],
    extraButtons: f.geometry.type !== 'Point' ? [button('Zoom to', () => fitTo(bboxOf(f)))] : []
  }), { push });
}

// ------------------------------------------------------------------ directions

export function openDirections(to, toLabel, { push = false } = {}) {
  app.panel.show(directionsView(to, toLabel), { push });
}

function directionsView(to, toLabel) {
  let abort;
  return {
    tab: null, title: 'Directions',
    dispose: () => abort?.abort(),
    render(body) {
      const out = h('div', {});
      const run = async () => {
        abort?.abort();
        abort = new AbortController();
        const from = myPositionOrCentre();
        set(out, note('Working out the route…'));
        try {
          const r = await getJson(`/api/route?${qs({ from: `${from.lat.toFixed(6)},${from.lon.toFixed(6)}`, to: `${to.lat.toFixed(6)},${to.lon.toFixed(6)}`, profile: app.settings.profile })}`, { signal: abort.signal });
          showRoute(r, to, toLabel);
          set(out, 
            h('div', { class: 'route-sum' }, h('b', {}, r.durationText), ` · ${r.distanceText} · ${r.profile}`),
            !from.mine ? note('No GPS fix, so this starts from the middle of the map.', 'warn') : null,
            ...r.warnings.map((w) => note(w, 'warn')),
            h('ol', { class: 'steps' }, r.steps.map((s) => h('li', {},
              h('button', { type: 'button', onclick: () => app.map.flyTo({ center: [s.lon, s.lat], zoom: 16 }) },
                h('span', { class: 'step-text' }, s.instruction), s.distance > 0 ? h('span', { class: 'step-dist' }, s.distanceText) : null)))),
            h('div', { class: 'actions' },
              button('Clear route', () => { clearRoute(); app.panel.back(); }),
              button('Copy directions', () => copy(`Directions to ${toLabel} (${r.durationText}, ${r.distanceText})\n` + r.steps.map((s, i) => `${i + 1}. ${s.instruction}${s.distance > 0 ? ' – ' + s.distanceText : ''}`).join('\n'), 'Copied directions'), '', 'copy')),
            note('Drive times are estimates from the map data. Check road conditions and closures with dispatch.', 'muted'));
        } catch (e) {
          if (e.name !== 'AbortError') set(out, note(errorText(e), 'warn'));
        }
      };
      add(body, h('p', { class: 'to' }, 'To ', h('b', {}, toLabel)),
        segmented(PROFILES, app.settings.profile, (id) => { app.settings.profile = id; save(); app.panel.refresh(); }), out);
      run();
    }
  };
}

// ------------------------------------------------------------------ nearest

export function nearestView() {
  return {
    tab: 'nearest', title: 'Nearest',
    render(body) {
      const from = myPositionOrCentre();
      add(body, note(from.mine ? 'From your position, by drive time.' : 'No GPS fix yet: from the middle of the map.', from.mine ? 'muted' : 'warn'));
      const grid = h('div', { class: 'grid' });
      for (const l of app.info.layers.filter((x) => x.count > 0 && x.id !== 'markers'))
        grid.append(h('button', { type: 'button', class: 'tile', onclick: () => app.panel.show(nearestListView(l.id), { push: true }) },
          h('span', { class: 'badge big', style: { background: l.color } }, l.glyph), h('span', {}, l.title)));
      add(body, grid);
    }
  };
}

function nearestListView(layerId) {
  const l = layerDef(layerId);
  let abort;
  return {
    tab: 'nearest', title: l?.title ?? 'Nearest',
    dispose: () => abort?.abort(),
    render(body) {
      const list = h('div', { class: 'list' }, note('Finding the nearest…'));
      add(body, list);
      abort = new AbortController();
      const from = myPositionOrCentre();
      getJson(`/api/nearest?${qs({ layer: layerId, lat: from.lat.toFixed(6), lon: from.lon.toFixed(6), count: 10, profile: app.settings.profile })}`, { signal: abort.signal })
        .then((res) => {
          if (!res) return set(list, note('Not available.'));
          set(list, 
            res.warning ? note(res.warning, 'warn') : null,
            !res.byDriveTime ? note('Straight-line distance (no road route from here).', 'muted') : null,
            ...res.items.map((p) => row({
              title: p.name, badge: l?.glyph, color: l?.color,
              detail: [p.kindLabel, p.driveSeconds != null ? C.formatDuration(p.driveSeconds) + ' drive' : null].filter(Boolean).join(' · '),
              right: `${C.formatDistance(p.driveMeters ?? p.distance)}`,
              onClick: () => openPoi(p.id, { push: true })
            })),
            res.items.length ? null : note('None found nearby.'));
        })
        .catch((e) => { if (e.name !== 'AbortError') set(list, note(errorText(e), 'warn')); });
    }
  };
}

// ------------------------------------------------------------------ layers

export function layersView() {
  return {
    tab: 'layers', title: 'Layers',
    render(body) {
      add(body, heading('Map style'),
        segmented(Object.entries(THEMES).map(([id, t]) => ({ id, label: t.name })), app.settings.theme, (id) => { setTheme(id); app.panel.refresh(); }));

      add(body, heading('Event files'));
      const events = [...app.events.values()];
      if (!events.length) add(body, note('No event files yet. Files placed in the server’s events folder show up for everyone; files added here stay on this phone.', 'muted'));
      for (const ev of events) {
        const visible = !app.settings.hiddenEvents.includes(ev.id);
        const t = toggle({
          label: ev.name, detail: `${ev.source === 'phone' ? 'On this phone' : 'Shared'} · ${ev.summary ?? ev.data?.summary ?? ''}`,
          checked: visible, badge: '◆', color: '#ff9f1c', onChange: (on) => setEventVisible(ev.id, on)
        });
        const actions = h('div', { class: 'row-actions' },
          ev.data?.bbox ? button('Show', () => fitTo(ev.data.bbox), 'small') : null,
          ev.source === 'phone' ? button('Remove', async () => {
            if (!confirm(`Remove “${ev.name}” from this phone?`)) return;
            await deleteLocalFile(ev.id);
            removeEventLayers(ev.id);
            app.events.delete(ev.id);
            app.panel.refresh();
          }, 'small danger') : null);
        add(body, h('div', { class: 'event-entry' }, t, actions));
      }
      const picker = h('input', { type: 'file', accept: '.kml,.kmz,.gpx,.geojson,.json,.csv,.txt', hidden: true });
      picker.addEventListener('change', () => picker.files[0] && importFile(picker.files[0]));
      add(body, picker, h('div', { class: 'actions' }, button('Add a file from this phone', () => picker.click(), '', 'upload')));

      add(body, heading('Map symbols'));
      for (const l of app.info.layers.filter((x) => x.count > 0))
        add(body, toggle({
          label: l.title, detail: `${l.count.toLocaleString()} · shown from zoom ${l.minZoom}`, badge: l.glyph, color: l.color,
          checked: app.settings.layers.includes(l.id), onChange: (on) => setLayerVisible(l.id, on)
        }));
      if (app.info.repeaters > 0)
        add(body, toggle({ label: 'Repeaters', detail: `${app.info.repeaters} from the Field Map repeater list`, badge: 'R', color: '#8e44ad', checked: app.settings.repeaters, onChange: (on) => setLayerVisible('repeaters', on) }));
      if (app.info.coverage)
        add(body, heading('Coverage'),
          toggle({ label: 'Cell coverage', detail: app.info.coverage.description || 'Imported coverage areas', checked: app.settings.coverage, onChange: (on) => setLayerVisible('coverage', on) }),
          h('div', { class: 'legend' }, h('span', { class: 'sw', style: { background: '#8e3fd6' } }), '5G ', h('span', { class: 'sw', style: { background: '#1e7ae6' } }), 'LTE ', h('span', { class: 'sw', style: { background: '#00a0b0' } }), '3G'));
    }
  };
}

async function importFile(file) {
  toast(`Reading ${file.name}…`, 10000);
  try {
    const data = await postFile('/api/convert', file);
    if (!data.features.length) return toast(`Nothing usable in ${file.name}.`);
    const entry = { id: data.id, name: data.name, summary: data.summary, data };
    await saveLocalFile(entry).catch(() => toast('Couldn’t save the file on this phone; it will be gone after a reload.'));
    addLocalEvent(entry);
    toast(`Added ${data.name}: ${data.summary}`);
    if (data.bbox) fitTo(data.bbox);
    app.panel.refresh();
  } catch (e) {
    toast(errorText(e), 5000);
  }
}

// ------------------------------------------------------------------ event items

let itemsFilter = '', itemsFile = null, itemsPointsOnly = false;

export function itemsView() {
  return {
    tab: 'items', title: 'Event items',
    render(body) {
      const events = [...app.events.values()].filter((e) => e.data);
      if (!events.length) {
        add(body, note('No event files loaded. Add one from Layers, or put KML, KMZ, GPX, GeoJSON or CSV files in the server’s events folder.'));
        return;
      }
      if (itemsFile && !app.events.has(itemsFile)) itemsFile = null;
      const input = h('input', { type: 'search', class: 'search-input', placeholder: 'Filter…', value: itemsFilter, autocomplete: 'off', 'aria-label': 'Filter items' });
      const list = h('div', { class: 'list' });
      const chips = h('div', { class: 'chips' },
        h('button', { type: 'button', class: itemsFile ? '' : 'on', onclick: () => { itemsFile = null; app.panel.refresh(); } }, 'All files'),
        events.map((e) => h('button', { type: 'button', class: itemsFile === e.id ? 'on' : '', onclick: () => { itemsFile = e.id; app.panel.refresh(); } }, e.name)),
        h('button', { type: 'button', class: itemsPointsOnly ? 'on' : '', onclick: () => { itemsPointsOnly = !itemsPointsOnly; app.panel.refresh(); } }, 'Points only'));
      const draw = () => {
        itemsFilter = input.value;
        const words = input.value.toLowerCase().split(/\s+/).filter(Boolean);
        const near = myPositionOrCentre();
        const groups = new Map();
        let total = 0;
        for (const ev of events) {
          if (itemsFile && ev.id !== itemsFile) continue;
          for (const f of ev.data.features) {
            const p = f.properties;
            if (itemsPointsOnly && f.geometry.type !== 'Point') continue;
            const hay = `${p.name} ${p.folder} ${p.description} ${Object.values(p.attrs ?? {}).join(' ')}`.toLowerCase();
            if (!words.every((w) => hay.includes(w))) continue;
            total++;
            const key = `${ev.name}${p.folder ? ' › ' + p.folder : ''}`;
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push({ ev, f, d: C.distance([near.lon, near.lat], anchor(f)) });
          }
        }
        const out = [note(`${total.toLocaleString()} item${total === 1 ? '' : 's'}`, 'muted')];
        let shown = 0;
        for (const [title, items] of [...groups].sort((a, b) => a[0].localeCompare(b[0]))) {
          if (shown >= 300) break;
          out.push(h('h4', { class: 'group' }, `${title} (${items.length})`));
          items.sort((a, b) => (words.length ? a.d - b.d : (a.f.properties.name || '~').localeCompare(b.f.properties.name || '~')));
          for (const it of items) {
            if (shown++ >= 300) break;
            const p = it.f.properties;
            out.push(row({
              title: p.name || `(${p.kind} without a name)`, detail: `${p.kind === 'point' ? 'Point' : p.kind === 'line' ? 'Line' : 'Area'} · ${C.formatDistance(it.d)}`,
              badge: '◆', color: p.stroke || p.fill || '#ff9f1c', onClick: () => openEventFeature(it.ev, it.f, null, { push: true })
            }));
          }
        }
        if (total > 300) out.push(note('Showing the first 300. Type a filter or choose a file to narrow the list.', 'muted'));
        set(list, ...out);
      };
      let timer;
      input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(draw, 200); });
      add(body, h('div', { class: 'search-box' }, icon('search'), input), chips, list);
      draw();
    }
  };
}

// ------------------------------------------------------------------ more

export function moreView() {
  return {
    tab: 'more', title: 'More',
    render(body) {
      const standalone = matchMedia('(display-mode: standalone)').matches || navigator.standalone;
      add(body, 
        heading('While driving'),
        toggle({ label: 'Keep the screen on', detail: 'Stops the phone locking while Field Map is open', checked: app.settings.keepAwake, onChange: (on) => keepAwake(on) }),
        heading('Coordinates in the status bar'),
        segmented(C.FORMATS.map((f) => ({ id: f.id, label: f.label })), app.settings.coordFormat, (id) => { app.settings.coordFormat = id; save(); updateStatus(); app.panel.refresh(); }),
        note('Tap the coordinates at the bottom of the map to switch quickly.', 'muted'),
        heading('Directions'),
        segmented(PROFILES, app.settings.profile, (id) => { app.settings.profile = id; save(); app.panel.refresh(); }),
        app.route ? h('div', { class: 'actions' }, button('Clear the route', () => { clearRoute(); app.panel.refresh(); })) : null,
        heading('Offline'),
        offlineSection(),
        !standalone ? h('div', {}, heading('Install'), note('For a full-screen app with its own icon: in Safari tap Share, then “Add to Home Screen”. On Android, open the browser menu and choose “Install app”.')) : null,
        heading('Map data'),
        ...app.info.regions.map((r) => row({ title: r.name, detail: `Data ${r.dataDate ?? 'unknown'} · version ${r.version}${r.isSample ? ' · SAMPLE DATA' : ''}`, onClick: () => fitTo(r.bounds) })),
        ...app.info.problems.map((p) => note(p, 'warn')),
        h('div', { class: 'actions' }, button('Check for new data', async () => { await loadInfoAgain(); })),
        heading('About'),
        note('Field Map for phones. Map data © OpenStreetMap contributors and other open data listed in Field Map’s data sources. Facility locations, hours and phone numbers can be out of date: confirm with dispatch.', 'muted'));
    }
  };
}

async function loadInfoAgain() {
  const before = app.info.version;
  const info = await getJson('/api/info').catch(() => null);
  if (!info) return toast('Can’t reach the server.');
  if (info.version === before) { await loadEventList(); return toast('Already up to date.'); }
  toast('New map data: reloading…');
  setTimeout(() => location.reload(), 600);
}

function offlineSection() {
  const status = note('Saves the map for the area on screen (down to street level) so it still shows with weak or no signal. Search and directions need the server.', 'muted');
  const btn = button('Save this area', async () => {
    btn.disabled = true;
    try { await saveArea((t) => { status.textContent = t; }); } finally { btn.disabled = false; }
  }, '', 'download');
  return h('div', {}, status, h('div', { class: 'actions' }, btn));
}

async function saveArea(report) {
  const m = app.map, b = m.getBounds();
  const urls = [];
  const z0 = Math.max(0, Math.floor(m.getZoom()) - 2);
  const lon2x = (lon, z) => Math.floor(((lon + 180) / 360) * 2 ** z);
  const lat2y = (lat, z) => { const r = lat * Math.PI / 180; return Math.floor(((1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2) * 2 ** z); };
  for (const r of app.info.regions) {
    for (let z = z0; z <= (r.maxZoom ?? 14); z++) {
      const x0 = lon2x(Math.max(b.getWest(), r.bounds[0]), z), x1 = lon2x(Math.min(b.getEast(), r.bounds[2]), z);
      const y0 = lat2y(Math.min(b.getNorth(), r.bounds[3]), z), y1 = lat2y(Math.max(b.getSouth(), r.bounds[1]), z);
      for (let x = x0; x <= x1; x++) for (let y = y0; y <= y1; y++) urls.push(`/tiles/${encodeURIComponent(r.id)}/${z}/${x}/${y}.pbf?v=${app.info.version}`);
    }
  }
  if (urls.length > 4000) return report(`That's ${urls.length.toLocaleString()} map tiles – zoom in a little and try again.`);
  for (const range of ['0-255', '256-511', '512-767', '768-1023', '7680-7935', '8192-8447'])
    urls.push(`/fonts/Noto%20Sans%20Regular/${range}.pbf`);
  let done = 0, failed = 0;
  const queue = [...urls];
  const worker = async () => {
    while (queue.length) {
      const u = queue.shift();
      try { const res = await fetch(u, { credentials: 'same-origin' }); if (!res.ok && res.status !== 204) failed++; } catch { failed++; }
      if (++done % 25 === 0) report(`Saving… ${done.toLocaleString()} of ${urls.length.toLocaleString()}`);
    }
  };
  await Promise.all(Array.from({ length: 6 }, worker));
  report(failed ? `Saved, but ${failed} tiles failed. Try again with a better signal.` : `Saved ${urls.length.toLocaleString()} map tiles for this area.`);
}

// ------------------------------------------------------------------ sign-in and empty states

export function showLogin() {
  document.getElementById('splash').hidden = true;
  app.panel.show({
    tab: null, title: 'Field Map', full: true, locked: true,
    render(body) {
      const input = h('input', { type: 'password', class: 'search-input', placeholder: 'Access code', autocomplete: 'current-password', enterkeyhint: 'go', 'aria-label': 'Access code' });
      const msg = h('p', { class: 'note warn', hidden: true });
      const go = async () => {
        msg.hidden = true;
        try {
          await postJson('/api/login', { code: input.value });
          location.reload();
        } catch (e) {
          msg.textContent = e.message;
          msg.hidden = false;
        }
      };
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') go(); });
      add(body, note('Enter the access code for this Field Map server. You only need to do this once on each phone.'),
        h('div', { class: 'search-box' }, input), msg, h('div', { class: 'actions' }, button('Sign in', go, 'primary')));
      setTimeout(() => input.focus(), 60);
    }
  });
}

export function showNoData() {
  app.panel.show({
    tab: null, title: 'No map data', full: false,
    render(body) {
      add(body, note('The server has no map data installed yet. On the server, run MapBuilder to download and build a region (see docs/FIELDMAP-WEB.md), then tap Check again.'),
        h('div', { class: 'actions' }, button('Check again', () => location.reload(), 'primary')));
    }
  });
}

