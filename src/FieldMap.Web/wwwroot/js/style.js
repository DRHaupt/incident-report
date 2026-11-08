// Map styles for Field Map's own vector tiles (layers: water, waterways, landuse, hazards, roads, rail, boundaries,
// places). Colours follow the three desktop themes: Standard (CAD dark), Daylight and Night.

export const THEMES = {
  standard: {
    name: 'Standard', ui: 'dark',
    background: '#12161c', water: '#173452', waterLine: '#2e6a99', park: '#15271b', glacier: '#2b333c',
    residential: '#1b2028', commercial: '#241e29', industrial: '#22201b', wetland: '#16262a', military: '#2c1a1a',
    airport: '#222631', hazard: '#e74c3c', casing: '#000000',
    motorway: '#f2a900', trunk: '#f0b83c', primary: '#e8d08c', secondary: '#d8d8d8', tertiary: '#bcbcbc',
    minor: '#8c929c', service: '#626872', track: '#9a825a', path: '#6a7a6a', ferry: '#4f9de0', seasonal: '#ff7a1a',
    rail: '#9a9a9a', boundary: '#b07cc6', label: '#e6e6e6', halo: '#000000', place: '#ffffff',
    shield: '#f2a900', shieldText: '#000000', gps: '#299bff', route: '#00b4ff', symbol: 1
  },
  daylight: {
    name: 'Daylight', ui: 'light',
    background: '#ffffff', water: '#7fc2f5', waterLine: '#005cc8', park: '#c9ebc2', glacier: '#e6f2fa',
    residential: '#f0f0f0', commercial: '#f5e6f0', industrial: '#eeeade', wetland: '#d6ece6', military: '#f5d5d5',
    airport: '#e4e6ee', hazard: '#c00000', casing: '#000000',
    motorway: '#ffc400', trunk: '#ffd240', primary: '#ffe896', secondary: '#ffffff', tertiary: '#ffffff',
    minor: '#ffffff', service: '#ffffff', track: '#8b5a00', path: '#404040', ferry: '#003cb4', seasonal: '#e05000',
    rail: '#303030', boundary: '#800080', label: '#000000', halo: '#ffffff', place: '#000000',
    shield: '#ffc400', shieldText: '#000000', gps: '#003cb4', route: '#0050e0', symbol: 1
  },
  night: {
    name: 'Night', ui: 'night',
    background: '#050608', water: '#081420', waterLine: '#102a40', park: '#070f09', glacier: '#101316',
    residential: '#0b0c0e', commercial: '#0e0b0e', industrial: '#0d0c0a', wetland: '#081011', military: '#120808',
    airport: '#0c0d11', hazard: '#8a221a', casing: '#000000',
    motorway: '#8a5800', trunk: '#7a5214', primary: '#665034', secondary: '#52483e', tertiary: '#463e36',
    minor: '#38332e', service: '#2a2724', track: '#4a3a22', path: '#2e342e', ferry: '#224466', seasonal: '#8a3c0c',
    rail: '#403a34', boundary: '#4a3252', label: '#9a886a', halo: '#000000', place: '#b09c78',
    shield: '#6a4400', shieldText: '#d0b080', gps: '#3d7cc9', route: '#1e5e8c', symbol: 0.55
  }
};

export const FONT = ['Noto Sans Regular'];

// Road widths in pixels by zoom: [zoom, width] stops per class.
const WIDTH = {
  motorway: [[5, 1], [9, 2.2], [12, 4], [15, 9], [18, 22]],
  trunk: [[5, 0.8], [9, 2], [12, 3.6], [15, 8], [18, 20]],
  primary: [[7, 0.6], [10, 1.6], [12, 3], [15, 7], [18, 18]],
  secondary: [[9, 0.6], [11, 1.4], [13, 2.6], [15, 6], [18, 16]],
  tertiary: [[10, 0.5], [12, 1.2], [14, 3], [16, 6], [18, 14]],
  minor: [[12, 0.4], [14, 1.6], [16, 4.5], [18, 11]],
  service: [[13, 0.3], [15, 1.2], [18, 6]],
  track: [[12, 0.4], [15, 1.2], [18, 3]],
  path: [[14, 0.4], [16, 1], [18, 2.4]],
  ferry: [[8, 0.6], [14, 1.5]]
};

const interp = (stops, mul = 1, add = 0) => ['interpolate', ['exponential', 1.5], ['zoom'], ...stops.flatMap(([z, w]) => [z, w * mul + add])];

// Draw order: minor roads first so main roads sit on top.
const ROAD_ORDER = ['path', 'track', 'service', 'minor', 'tertiary', 'secondary', 'primary', 'trunk', 'motorway'];

/** Style layers for one region's tiles. `src` is the source id; ids are prefixed so several regions can share a map. */
function regionLayers(src, t) {
  const L = [];
  const id = (n) => `${src}:${n}`;
  const landuseColor = ['match', ['get', 'kind'],
    'park', t.park, 'glacier', t.glacier, 'residential', t.residential, 'commercial', t.commercial,
    'industrial', t.industrial, 'wetland', t.wetland, 'military', t.military, 'airport', t.airport, t.residential];
  L.push({ id: id('landuse'), type: 'fill', source: src, 'source-layer': 'landuse', paint: { 'fill-color': landuseColor } });
  L.push({ id: id('water'), type: 'fill', source: src, 'source-layer': 'water', paint: { 'fill-color': t.water } });
  L.push({
    id: id('waterways'), type: 'line', source: src, 'source-layer': 'waterways', minzoom: 8,
    paint: {
      'line-color': t.waterLine,
      // Zoom has to be the outer expression; the river/canal/stream choice goes inside each stop.
      'line-width': ['interpolate', ['linear'], ['zoom'],
        8, ['match', ['get', 'kind'], 'river', 0.8, 0.3],
        12, ['match', ['get', 'kind'], 'river', 2, 'canal', 1.2, 0.7],
        16, ['match', ['get', 'kind'], 'river', 5, 'canal', 3, 1.5]]
    }
  });
  L.push({
    id: id('hazards'), type: 'fill', source: src, 'source-layer': 'hazards', minzoom: 9,
    paint: { 'fill-color': t.hazard, 'fill-opacity': 0.18, 'fill-outline-color': t.hazard }
  });
  L.push({
    id: id('boundaries'), type: 'line', source: src, 'source-layer': 'boundaries',
    paint: { 'line-color': t.boundary, 'line-width': ['interpolate', ['linear'], ['zoom'], 4, 0.6, 12, 1.6], 'line-dasharray': [3, 2] }
  });

  // Casings (dark outline) for the bigger classes, then fills.
  const cls = (c) => ['==', ['get', 'class'], c];
  for (const c of ROAD_ORDER) {
    if (['motorway', 'trunk', 'primary', 'secondary', 'tertiary', 'minor'].includes(c)) {
      L.push({
        id: id(`road-case-${c}`), type: 'line', source: src, 'source-layer': 'roads', filter: ['all', cls(c), ['!=', ['get', 'tunnel'], true]],
        minzoom: c === 'minor' ? 13 : 10,
        layout: { 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': t.casing, 'line-width': interp(WIDTH[c], 1, 2), 'line-opacity': 0.9 }
      });
    }
  }
  for (const c of ROAD_ORDER) {
    const color = t[c] ?? t.path;
    L.push({
      id: id(`road-${c}`), type: 'line', source: src, 'source-layer': 'roads', filter: cls(c),
      layout: { 'line-cap': 'round', 'line-join': 'round' },
      paint: {
        'line-color': ['case', ['any', ['==', ['get', 'seasonal'], true], ['==', ['get', 'seasonal'], 1]], t.seasonal, color],
        'line-width': interp(WIDTH[c]),
        'line-opacity': ['case', ['any', ['==', ['get', 'tunnel'], true], ['==', ['get', 'tunnel'], 1]], 0.45, 1],
        ...(c === 'track' || c === 'path' ? { 'line-dasharray': [2, 1.5] } : {})
      }
    });
  }
  L.push({
    id: id('ferry'), type: 'line', source: src, 'source-layer': 'roads', filter: cls('ferry'),
    paint: { 'line-color': t.ferry, 'line-width': interp(WIDTH.ferry), 'line-dasharray': [3, 2] }
  });
  L.push({
    id: id('rail'), type: 'line', source: src, 'source-layer': 'rail', minzoom: 9,
    paint: { 'line-color': t.rail, 'line-width': ['interpolate', ['linear'], ['zoom'], 9, 0.6, 16, 2], 'line-dasharray': [4, 2] }
  });

  // Labels.
  L.push({
    id: id('waterway-label'), type: 'symbol', source: src, 'source-layer': 'waterways', minzoom: 12,
    layout: { 'symbol-placement': 'line', 'text-field': ['get', 'name'], 'text-font': FONT, 'text-size': 12, 'text-letter-spacing': 0.05 },
    paint: { 'text-color': t.waterLine, 'text-halo-color': t.halo, 'text-halo-width': 1.4 }
  });
  L.push({
    id: id('road-label'), type: 'symbol', source: src, 'source-layer': 'roads', minzoom: 13,
    filter: ['has', 'name'],
    layout: {
      'symbol-placement': 'line', 'text-field': ['get', 'name'], 'text-font': FONT,
      'text-size': ['interpolate', ['linear'], ['zoom'], 13, 11, 17, 15], 'symbol-spacing': 280, 'text-max-angle': 35
    },
    paint: { 'text-color': t.label, 'text-halo-color': t.halo, 'text-halo-width': 1.6 }
  });
  L.push({
    id: id('road-shield'), type: 'symbol', source: src, 'source-layer': 'roads', minzoom: 8,
    filter: ['all', ['has', 'ref'], ['match', ['get', 'class'], ['motorway', 'trunk', 'primary', 'secondary'], true, false]],
    layout: {
      'symbol-placement': 'line', 'symbol-spacing': 420, 'text-field': ['slice', ['get', 'ref'], 0, 8], 'text-font': FONT,
      'text-size': 11, 'text-rotation-alignment': 'viewport', 'text-padding': 6
    },
    paint: { 'text-color': t.shieldText, 'text-halo-color': t.shield, 'text-halo-width': 3 }
  });
  L.push({
    id: id('places'), type: 'symbol', source: src, 'source-layer': 'places',
    layout: {
      'text-field': ['get', 'name'], 'text-font': FONT,
      'text-size': ['interpolate', ['linear'], ['zoom'], 6, ['match', ['get', 'rank'], 1, 15, 2, 13, 11], 14, ['match', ['get', 'rank'], 1, 22, 2, 19, 3, 17, 15]],
      'symbol-sort-key': ['get', 'rank'], 'text-padding': 4
    },
    paint: { 'text-color': t.place, 'text-halo-color': t.halo, 'text-halo-width': 2 }
  });
  return L;
}

/**
 * A complete MapLibre style for the server's regions. `base` chooses the base map: { mode: 'map' | 'imagery' |
 * 'hybrid', source } where source is one of info.imagery. Imagery is drawn on top of the map's own areas, so when a
 * phone loses signal the imagery tiles that don't arrive simply let the map show through.
 */
export function buildStyle(info, theme, origin, base = { mode: 'map' }) {
  const t = THEMES[theme] ?? THEMES.standard;
  const sources = {};
  const regionLayers_ = [];
  for (const r of info.regions) {
    const src = `region-${r.id}`;
    sources[src] = {
      type: 'vector',
      tiles: [`${origin}/tiles/${encodeURIComponent(r.id)}/{z}/{x}/{y}.pbf?v=${info.version}`],
      minzoom: r.minZoom ?? 0, maxzoom: r.maxZoom ?? 14, bounds: r.bounds,
      attribution: '© OpenStreetMap contributors'
    };
    regionLayers_.push(...regionLayers(src, t));
  }
  const fills = regionLayers_.filter((l) => l.type === 'fill');
  const lines = regionLayers_.filter((l) => l.type === 'line');
  const labels = regionLayers_.filter((l) => l.type === 'symbol');
  const layers = [{ id: 'background', type: 'background', paint: { 'background-color': t.background } }];
  const img = base.mode !== 'map' ? base.source : null;
  if (!img) {
    // Labels from every region go on top of every region's roads.
    layers.push(...fills, ...lines, ...labels);
  } else {
    sources.imagery = {
      type: 'raster', tileSize: 256,
      tiles: [`${origin}/imagery/${encodeURIComponent(img.id)}/{z}/{x}/{y}`],
      minzoom: img.minZoom ?? 0, maxzoom: img.maxZoom ?? 18,
      ...(img.bounds?.length === 4 ? { bounds: img.bounds } : {}),
      attribution: img.attribution
    };
    const raster = {
      id: 'imagery', type: 'raster', source: 'imagery',
      paint: theme === 'night'
        ? { 'raster-brightness-max': 0.45, 'raster-saturation': -0.4, 'raster-fade-duration': 150 }
        : { 'raster-fade-duration': 150 }
    };
    if (base.mode === 'imagery') layers.push(...fills, ...lines, raster);
    else layers.push(...fills, raster, ...lines.map((l) => dimForImagery(l)), ...labels);
  }
  return { version: 8, glyphs: `${origin}/fonts/{fontstack}/{range}.pbf`, sources, layers };
}

// Over imagery, minor roads and area outlines would hide what's on the ground: keep main roads, thin the rest.
function dimForImagery(l) {
  if (!l.id.includes(':road-') && !l.id.endsWith(':rail') && !l.id.endsWith(':boundaries')) return l;
  const minor = /:road-(case-)?(minor|service|track|path)$/.test(l.id);
  return { ...l, paint: { ...l.paint, 'line-opacity': minor ? 0.55 : 0.85 } };
}

/** Dims a symbol colour for the Night theme, like the desktop app. */
export function symbolColor(hex, theme) {
  const k = (THEMES[theme] ?? THEMES.standard).symbol;
  if (k >= 1) return hex;
  const n = parseInt(hex.slice(1), 16);
  const c = (s) => Math.round(((n >> s) & 255) * k).toString(16).padStart(2, '0');
  return `#${c(16)}${c(8)}${c(0)}`;
}
