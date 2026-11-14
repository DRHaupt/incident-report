// Keeps Field Map usable with a weak or no signal: the app itself and every map tile seen (or saved with
// "Save this area") are kept on the phone. Search, nearest and directions still need the server.

const APP = 'fieldmap-app-v1';
const TILES = 'fieldmap-tiles-v1';
const MAX_TILES = 12000;

const SHELL = [
  './', 'index.html', 'app.css', 'manifest.webmanifest',
  'js/app.js', 'js/panels.js', 'js/team.js', 'js/aprs.js', 'js/live.js', 'js/hazmat.js', 'js/alerts.js', 'js/firstaid.js',
  'js/ui.js', 'js/api.js', 'js/native.js', 'js/home.js', 'js/reports.js', 'js/store.js', 'js/style.js', 'js/coords.js',
  'vendor/maplibre/maplibre-gl.js', 'vendor/maplibre/maplibre-gl.css',
  'icons/icon-192.png', 'icons/apple-touch-icon.png'
];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(APP).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil((async () => {
    for (const key of await caches.keys())
      if (key !== APP && key !== TILES) await caches.delete(key);
    await self.clients.claim();
  })());
});

let trimming = false;
async function trimTiles() {
  if (trimming) return;
  trimming = true;
  try {
    const cache = await caches.open(TILES);
    const keys = await cache.keys();
    // Oldest entries come first; drop a chunk once over the limit.
    if (keys.length > MAX_TILES)
      for (const k of keys.slice(0, keys.length - MAX_TILES + 1000)) await cache.delete(k);
  } finally {
    trimming = false;
  }
}

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== location.origin) return;

  // Map tiles and label fonts never change under the same URL: cache first.
  if (url.pathname.startsWith('/tiles/') || url.pathname.startsWith('/fonts/')) {
    e.respondWith((async () => {
      const cache = await caches.open(TILES);
      const hit = await cache.match(req);
      if (hit) return hit;
      const res = await fetch(req);
      if (res.status === 200) {
        cache.put(req, res.clone());
        trimTiles();
      }
      return res;
    })());
    return;
  }

  // Map info and the event files: fresh from the server when possible, the last copy when offline.
  if (url.pathname === '/api/info' || url.pathname === '/api/erg' || url.pathname === '/api/firstaid' || url.pathname === '/api/forms' || url.pathname.startsWith('/api/events')) {
    e.respondWith((async () => {
      try {
        const res = await fetch(req);
        if (res.ok) (await caches.open(APP)).put(req, res.clone());
        return res;
      } catch {
        return (await caches.match(req)) ?? Response.json({ error: 'offline' }, { status: 503 });
      }
    })());
    return;
  }
  // Online imagery is only viewed, never kept by the app (most imagery terms don't allow storing it).
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/imagery/')) return;

  // The app: network first so updates arrive straight away, cached copy when offline.
  e.respondWith((async () => {
    try {
      const res = await fetch(req);
      if (res.ok) (await caches.open(APP)).put(req, res.clone());
      return res;
    } catch {
      return (await caches.match(req, { ignoreSearch: true })) ?? (await caches.match('index.html'));
    }
  })());
});
