// What the phone keeps for itself: settings (localStorage) and event files imported from the phone (IndexedDB).
// Both can be unavailable (private browsing, storage cleared), so every call is guarded and falls back to defaults.

const KEY = 'fieldmap.settings.v1';

export const DEFAULTS = {
  theme: 'standard',
  coordFormat: 'dec',
  layers: null,            // null = the server's default-on layers
  coverage: false,
  repeaters: true,
  hiddenEvents: [],
  profile: 'car',
  keepAwake: false,
  headingUp: false,
  baseMode: 'map',         // 'map' | 'imagery' | 'hybrid'
  imageryId: null,
  acceptedImagery: [],     // imagery sources whose terms this phone's user accepted
  view: null,
  teamName: '',            // name or callsign shown to other units
  teamDevice: null,        // random id for this phone, made on first use
  shareTeam: false,        // send this phone's position to the team
  showTeam: true           // show units and shared pins on the map
};

export function loadSettings() {
  try {
    const raw = localStorage.getItem(KEY);
    return { ...DEFAULTS, ...(raw ? JSON.parse(raw) : {}) };
  } catch {
    return { ...DEFAULTS };
  }
}

export function saveSettings(s) {
  try { localStorage.setItem(KEY, JSON.stringify(s)); } catch { /* storage full or blocked */ }
}

// ------------------------------------------------------------------ phone event files

const DB = 'fieldmap';
const STORE = 'eventFiles';

function open() {
  return new Promise((resolve, reject) => {
    if (!('indexedDB' in window)) return reject(new Error('no IndexedDB'));
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE, { keyPath: 'id' });
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function tx(mode, fn) {
  const db = await open();
  return new Promise((resolve, reject) => {
    const t = db.transaction(STORE, mode);
    const result = fn(t.objectStore(STORE));
    t.oncomplete = () => resolve(result?.result ?? result);
    t.onerror = () => reject(t.error);
  });
}

export async function listLocalFiles() {
  try { return (await tx('readonly', (s) => s.getAll())) ?? []; } catch { return []; }
}

export async function saveLocalFile(file) {
  await tx('readwrite', (s) => s.put(file));
}

export async function deleteLocalFile(id) {
  try { await tx('readwrite', (s) => s.delete(id)); } catch { /* already gone */ }
}
