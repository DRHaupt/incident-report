// The installed iOS/Android app (the same screens packaged with Capacitor, see mobile/). There the app's files are
// inside the app and the Field Map server is somewhere else, so this module does what the browser does for the web
// version: remembers which server to use, sends the sign-in with every request (a cookie can't cross to another
// site) and keeps map tiles and reference data on the phone (there is no service worker inside an iOS app).
// In a browser `native` is false and none of this runs.

const cap = window.Capacitor;
export const native = !!cap?.isNativePlatform?.();
export const platform = native ? cap.getPlatform() : 'web';

const SERVER = 'fieldmap.server';
const TOKEN = 'fieldmap.token';
/** Map tiles, label fonts and imagery go through this scheme so MapLibre's requests get the sign-in and the cache. */
export const MAP_ORIGIN = 'fieldmap://server';

const read = (k) => { try { return localStorage.getItem(k) ?? ''; } catch { return ''; } };
const write = (k, v) => { try { v ? localStorage.setItem(k, v) : localStorage.removeItem(k); } catch { /* storage blocked */ } };

export const serverBase = () => (native ? read(SERVER) : '');
export const setToken = (t) => write(TOKEN, t);
export function setServer(url) {
  if (url !== read(SERVER)) write(TOKEN, '');   // a sign-in belongs to one server
  write(SERVER, url);
}

/**
 * Tidies what someone typed as the server address: "maps.example.org" becomes "https://maps.example.org".
 * Returns null when it isn't an address. Only HTTPS works: the app itself is a secure page, and phones refuse
 * plain http from one (the exception is a server on this same device, for testing).
 */
export function normalizeServer(text) {
  let t = (text ?? '').trim();
  if (!t) return null;
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(t)) t = 'https://' + t;
  let u;
  try { u = new URL(t); } catch { return null; }
  const local = u.hostname === 'localhost' || u.hostname === '127.0.0.1';
  if (u.protocol !== 'https:' && !(u.protocol === 'http:' && local)) return null;
  if (!u.hostname || u.username || u.password) return null;
  return u.origin + u.pathname.replace(/\/+$/, '');
}

/** Asks a server who it is. Returns its status, or throws with a message for the set-up screen. */
export async function probeServer(base) {
  let res;
  try {
    res = await fetch(base + '/api/status', { credentials: 'omit', signal: AbortSignal.timeout?.(15000) });
  } catch {
    throw new Error('Can’t reach that address. Check the spelling and the phone’s connection.');
  }
  let body = null;
  try { body = await res.json(); } catch { /* not a Field Map server */ }
  if (!res.ok || body?.ok !== true || typeof body.codeRequired !== 'boolean')
    throw new Error('That address answered, but it isn’t a Field Map server.');
  return body;
}

// ------------------------------------------------------------------ what the phone keeps (IndexedDB)

const DB = 'fieldmap-cache';
const TILES = 'tiles';   // map tiles and fonts: { url, body, type, time }
const DATA = 'data';     // last good copy of map info, hazmat, first aid and event files
const MAX_TILES = 12000;

let dbPromise = null;
function db() {
  dbPromise ??= new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => {
      req.result.createObjectStore(TILES, { keyPath: 'url' }).createIndex('time', 'time');
      req.result.createObjectStore(DATA, { keyPath: 'url' });
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
  dbPromise.catch(() => { dbPromise = null; });
  return dbPromise;
}

async function idb(store, mode, fn) {
  const d = await db();
  return new Promise((resolve, reject) => {
    const t = d.transaction(store, mode);
    const req = fn(t.objectStore(store));
    t.oncomplete = () => resolve(req?.result);
    t.onerror = t.onabort = () => reject(t.error);
  });
}

const cacheGet = (store, url) => idb(store, 'readonly', (s) => s.get(url)).catch(() => undefined);
const cachePut = (store, entry) => idb(store, 'readwrite', (s) => s.put(entry)).catch(() => {});

let puts = 0, trimming = false;
async function trimTiles() {
  if (trimming || ++puts % 200 !== 0) return;
  trimming = true;
  try {
    const count = await idb(TILES, 'readonly', (s) => s.count());
    if (count <= MAX_TILES) return;
    let extra = count - MAX_TILES + 1000;
    const d = await db();
    await new Promise((resolve) => {
      const t = d.transaction(TILES, 'readwrite');
      // Oldest first.
      t.objectStore(TILES).index('time').openCursor().onsuccess = (e) => {
        const c = e.target.result;
        if (c && extra-- > 0) { c.delete(); c.continue(); }
      };
      t.oncomplete = t.onerror = t.onabort = () => resolve();
    });
  } catch { /* try again later */ } finally { trimming = false; }
}

const fromCache = (hit) => new Response(hit.status === 204 ? null : hit.body, { status: hit.status ?? 200, headers: { 'Content-Type': hit.type || 'application/octet-stream' } });

// ------------------------------------------------------------------ requests to the server

const keptOffline = (path) => {
  const p = path.split('?')[0];
  return p === '/api/info' || p === '/api/erg' || p === '/api/firstaid' || p === '/api/forms' || p === '/api/events' || p.startsWith('/api/events/');
};
const neverChanges = (path) => path.startsWith('/tiles/') || path.startsWith('/fonts/');

/** fetch() for a server path ("/api/…", "/tiles/…") from inside the installed app. */
export async function nativeFetch(path, init = {}) {
  const base = read(SERVER);
  const key = base + path;
  const get = !init.method || init.method === 'GET';
  const headers = new Headers(init.headers ?? {});
  const token = read(TOKEN);
  if (token) headers.set('Authorization', 'Bearer ' + token);
  // In the background Android slows the page's own requests to a crawl after a few minutes; the phone's own HTTP
  // (Capacitor's) keeps working, so position updates and alert checks go through that while the app isn't on screen.
  const send = () => (document.visibilityState === 'hidden' && !neverChanges(path) && !(init.body instanceof FormData)
    ? phoneHttp(key, init, headers)
    : fetch(key, { ...init, headers, credentials: 'omit' }));

  // Tiles and fonts never change under the same address: the phone's copy first.
  if (get && neverChanges(path)) {
    const hit = await cacheGet(TILES, key);
    if (hit) return fromCache(hit);
    const res = await send();
    if (res.status === 200 || res.status === 204) {
      const body = res.status === 200 ? await res.clone().arrayBuffer() : null;
      cachePut(TILES, { url: key, body, status: res.status, type: res.headers.get('Content-Type') ?? '', time: Date.now() }).then(trimTiles);
    }
    return res;
  }

  // Map info, hazmat, first aid and event files: fresh when possible, the last copy with no signal.
  if (get && keptOffline(path)) {
    try {
      const res = await send();
      if (res.ok) {
        const body = await res.clone().arrayBuffer();
        cachePut(DATA, { url: key, body, type: res.headers.get('Content-Type') ?? 'application/json', time: Date.now() });
      }
      return res;
    } catch (e) {
      if (e.name === 'AbortError') throw e;
      const hit = await cacheGet(DATA, key);
      if (hit) return fromCache(hit);
      throw e;
    }
  }
  return send();
}

async function phoneHttp(url, init, headers) {
  const h = Object.fromEntries(headers.entries());
  let data;
  if (typeof init.body === 'string') { try { data = JSON.parse(init.body); } catch { data = init.body; } }
  let r;
  try {
    r = await cap.nativePromise('CapacitorHttp', 'request', { url, method: init.method ?? 'GET', headers: h, data, responseType: 'text', connectTimeout: 20000, readTimeout: 20000 });
  } catch (e) {
    throw new TypeError(e?.message ?? 'Network request failed');
  }
  const body = typeof r.data === 'string' ? r.data : JSON.stringify(r.data ?? null);
  const empty = r.status === 204 || r.status === 304;
  return new Response(empty ? null : body, { status: r.status, headers: { 'Content-Type': 'application/json' } });
}

/** Lets MapLibre load "fieldmap://server/…" addresses through nativeFetch. Call once, before the map is made. */
export function registerMapProtocol(maplibregl) {
  maplibregl.addProtocol('fieldmap', async (params, abort) => {
    const path = params.url.slice(MAP_ORIGIN.length);
    const res = await nativeFetch(path, { signal: abort.signal });
    if (res.status === 204) return { data: new ArrayBuffer(0) };
    if (!res.ok) throw new Error(`${res.status} for ${path}`);
    return { data: await res.arrayBuffer() };
  });
}

// ------------------------------------------------------------------ the phone itself

// Android's in-app browser has no share sheet of its own: give it the phone's.
if (native && !navigator.share) {
  navigator.share = async ({ title, text, url } = {}) => {
    try {
      await cap.nativePromise('Share', 'share', { title, text, url });
    } catch (e) {
      if (/cancel/i.test(e?.message ?? '')) throw new DOMException('Share cancelled', 'AbortError');
      throw e;
    }
  };
}

/** Keeps the screen on (or lets it sleep again). Returns false when the app can't. */
export async function nativeKeepAwake(on) {
  try {
    await cap.nativePromise('KeepAwake', on ? 'keepAwake' : 'allowSleep', {});
    return true;
  } catch { return false; }
}

/** Android's Back button: `handler` returns false when there's nothing left to go back from, and the app closes. */
export function onBackButton(handler) {
  if (!native) return;
  document.addEventListener('backbutton', () => {
    if (handler() === false) cap.nativePromise('App', 'exitApp', {}).catch(() => {});
  });
}

// ------------------------------------------------------------------ position with the app in the background

let watcher = null;

/**
 * Starts the phone's own location service, which keeps running (and keeps this app running) while the app is in
 * the background or the screen is locked. Android shows a permanent notification while it's on. `onFix` gets
 * { lat, lon, acc, heading, speed, time }; `onProblem` gets 'denied' when the person refused location.
 */
export function startBackgroundLocation(onFix, onProblem) {
  if (!native || watcher) return;
  try {
    watcher = cap.nativeCallback('BackgroundGeolocation', 'addWatcher', {
      backgroundTitle: 'Field Suite is sharing your position',
      backgroundMessage: 'Your team can see where you are. Turn off Share my position in the app to stop.',
      requestPermissions: true, stale: false, distanceFilter: 10
    }, (loc, err) => {
      if (err || !loc || typeof loc.latitude !== 'number') {
        if ((err?.code ?? loc?.code) === 'NOT_AUTHORIZED') onProblem?.('denied');
        return;
      }
      onFix({ lat: loc.latitude, lon: loc.longitude, acc: loc.accuracy ?? null, heading: loc.bearing ?? null, speed: loc.speed ?? null, time: loc.time ?? Date.now() });
    });
  } catch { watcher = null; }
}

export function stopBackgroundLocation() {
  if (!watcher) return;
  cap.nativePromise('BackgroundGeolocation', 'removeWatcher', { id: watcher }).catch(() => {});
  watcher = null;
}

export const backgroundLocationOn = () => !!watcher;

// ------------------------------------------------------------------ notifications

const CHANNEL = 'alerts';
let notifyReady = null, nextId = Math.floor(Date.now() / 1000) % 1000000;

/** Asks for permission to show notifications (once) and sets up the alert channel. Resolves true when allowed. */
export function enableNotifications() {
  if (!native) return Promise.resolve(false);
  notifyReady ??= (async () => {
    try {
      const p = await cap.nativePromise('LocalNotifications', 'requestPermissions', {});
      if (p?.display !== 'granted') return false;
      if (platform === 'android')
        await cap.nativePromise('LocalNotifications', 'createChannel', { id: CHANNEL, name: 'Emergency alerts', description: 'Team SOS and APRS emergency beacons', importance: 5, visibility: 1, vibration: true });
      return true;
    } catch { return false; }
  })();
  return notifyReady;
}

/** Shows a notification now. `extra` comes back to onNotificationTap when the person taps it. */
export async function notify(title, body, extra = {}) {
  if (!(await enableNotifications())) return false;
  try {
    await cap.nativePromise('LocalNotifications', 'schedule', { notifications: [{ id: nextId++, title, body, channelId: CHANNEL, extra }] });
    return true;
  } catch { return false; }
}

export function onNotificationTap(handler) {
  if (!native) return;
  try { cap.addListener('LocalNotifications', 'localNotificationActionPerformed', (e) => handler(e?.notification?.extra ?? {})); } catch { /* plugin missing */ }
}
