// Emergency alerts and sending your location.
// - Banners (with vibration and a short tone) for team SOS alerts and APRS stations reporting an emergency.
// - SOS: alerts every team phone with this phone's position, kept up to date until cancelled.
// - Send my location: the phone's share sheet with coordinates, MGRS, nearest address and map links.

import { app, focus } from './app.js';
import { placeView } from './panels.js';
import * as C from './coords.js';
import { getJson, postJson, qs } from './api.js';
import { h, button, toast, copy, set } from './ui.js';

const dismissed = new Set(read());
function read() { try { return JSON.parse(sessionStorage.getItem('fieldmap.alerts.dismissed') ?? '[]'); } catch { return []; } }
function remember() { try { sessionStorage.setItem('fieldmap.alerts.dismissed', JSON.stringify([...dismissed])); } catch { /* private mode */ } }

let teamAlerts = [], aprsAlerts = [];
const announced = new Set();

const myDevice = () => app.settings.teamDevice;
const fromHere = (lat, lon) => (app.pos ? `${C.formatDistance(C.distance([app.pos.lon, app.pos.lat], [lon, lat]))} ${C.compass(C.bearing([app.pos.lon, app.pos.lat], [lon, lat]))}` : '');
const ago = (s) => (s < 60 ? 'just now' : s < 3600 ? `${Math.round(s / 60)} min ago` : `${Math.round(s / 3600)} h ago`);

/** Called by team.js with the alerts from each poll. */
export function updateTeamAlerts(list) {
  teamAlerts = (list ?? []).filter((a) => a.device !== myDevice());
  // After a reload the server still knows about this phone's SOS; right after cancelling, ignore the stale copy.
  if ((list ?? []).some((a) => a.device === myDevice()) && Date.now() - cancelledAt > 15000) sosActive = true;
  render();
}

/** Called by aprs.js with the stations from each poll. */
export function updateAprsAlerts(stations) {
  aprsAlerts = (stations ?? []).filter((s) => s.emergency);
  render();
}

function render() {
  const el = document.getElementById('alerts');
  if (!el) return;
  const items = [];
  if (sosActive)
    items.push(h('div', { class: 'alert own' },
      h('div', { class: 'alert-text' }, h('b', {}, 'Your SOS is active'), h('span', {}, 'Every team phone sees your position.')),
      button('Cancel SOS', () => cancelSos(), 'small')));
  for (const a of teamAlerts) {
    const key = `team:${a.device}:${a.started}`;
    if (dismissed.has(key)) continue;
    items.push(banner(key, `SOS – ${a.name}`, [a.message, fromHere(a.lat, a.lon), ago(a.ageSeconds)].filter(Boolean).join(' · '), () => openTeamAlert(a)));
  }
  for (const s of aprsAlerts) {
    const key = `aprs:${s.key}:${s.heard?.slice(0, 13)}`;
    if (dismissed.has(key)) continue;
    items.push(banner(key, `APRS EMERGENCY – ${s.key}`, [s.micE, fromHere(s.lat, s.lon)].filter(Boolean).join(' · '), () => import('./aprs.js').then((m) => m.openAprsStation(s.key))));
  }
  set(el, ...items.slice(0, 3));
  el.hidden = items.length === 0;
  // New alerts buzz and beep once.
  const fresh = [...teamAlerts.map((a) => `team:${a.device}:${a.started}`), ...aprsAlerts.map((s) => `aprs:${s.key}:${s.heard?.slice(0, 13)}`)]
    .filter((k) => !dismissed.has(k) && !announced.has(k));
  if (fresh.length) {
    fresh.forEach((k) => announced.add(k));
    navigator.vibrate?.([400, 150, 400, 150, 800]);
    tone();
  }
}

function banner(key, title, detail, onView) {
  return h('div', { class: 'alert', role: 'alert' },
    h('div', { class: 'alert-text' }, h('b', {}, title), detail ? h('span', {}, detail) : null),
    button('View', onView, 'small'),
    button('Dismiss', () => { dismissed.add(key); remember(); render(); }, 'small'));
}

let audio;
function tone() {
  try {
    audio ??= new (window.AudioContext || window.webkitAudioContext)();
    const o = audio.createOscillator(), g = audio.createGain();
    o.type = 'square'; o.frequency.value = 880;
    g.gain.setValueAtTime(0.0001, audio.currentTime);
    g.gain.exponentialRampToValueAtTime(0.25, audio.currentTime + 0.02);
    g.gain.exponentialRampToValueAtTime(0.0001, audio.currentTime + 0.9);
    o.connect(g).connect(audio.destination);
    o.start(); o.stop(audio.currentTime + 0.9);
  } catch { /* no sound until the user has touched the page; the vibration and banner still work */ }
}

/** Opens a team SOS by its device (tapped on the map). */
export function openTeamAlertById(device) { const a = teamAlerts.find((x) => x.device === device); if (a) openTeamAlert(a); }

function openTeamAlert(a) {
  focus(a.lat, a.lon, 16);
  app.panel.show(placeView({
    title: `SOS – ${a.name}`, kind: `Team alert, started ${ago(a.ageSeconds)}`, lat: a.lat, lon: a.lon,
    warning: a.message || 'This unit asked for help.',
    lines: [a.accuracy != null ? `GPS accuracy ±${Math.round(a.accuracy)} m` : null]
  }));
}

// ------------------------------------------------------------------ SOS

let sosActive = false, sosMessage = '', lastSos = 0, cancelledAt = 0;

const sosName = () => (app.settings.teamName ?? '').trim() || `Phone ${(myDevice() ?? '').slice(-4).toUpperCase() || 'unnamed'}`;

/** Starts an SOS after a confirmation. Needs a GPS fix (or uses the map centre, clearly marked). */
export async function startSos() {
  if (!app.team?.available) return toast('SOS needs team features on the server (an access code).');
  const message = prompt('SOS: every team phone will be alerted with your position.\n\nOptional message (e.g. "officer down", "need medical"):', '');
  if (message === null) return; // cancelled
  sosMessage = message.trim();
  sosActive = true;
  await sendSos(true);
  render();
}

async function sendSos(force) {
  if (!sosActive) return;
  const now = Date.now();
  if (!force && now - lastSos < 10000) return;
  lastSos = now;
  const p = app.pos ?? (() => { const c = app.map.getCenter(); return { lat: c.lat, lon: c.lng, acc: null }; })();
  try {
    await postJson('/api/team/sos', {
      device: (await import('./team.js')).deviceId(), name: sosName(), lat: p.lat, lon: p.lon, accuracy: p.acc,
      message: (sosMessage || 'SOS') + (app.pos ? '' : ' (no GPS fix: position is the map centre)')
    });
    if (force) toast('SOS sent to every team phone');
  } catch (e) { toast(`SOS not sent: ${e.message}`); }
}

/** Called with every GPS fix: keeps an active SOS's position up to date. */
export function alertsOnPosition() { if (sosActive) sendSos(false); }

export async function cancelSos() {
  if (!confirm('Cancel your SOS? Team phones will stop showing it.')) return;
  sosActive = false;
  cancelledAt = Date.now();
  try { await fetch(`/api/team/sos/${encodeURIComponent(myDevice())}`, { method: 'DELETE', credentials: 'same-origin' }); } catch { /* retried by the next poll's state */ }
  toast('SOS cancelled');
  render();
}

export const isSosActive = () => sosActive;

// ------------------------------------------------------------------ send my location

async function locationText() {
  const p = app.pos;
  const who = (app.settings.teamName ?? '').trim();
  let address = '';
  try { address = (await getJson(`/api/whatshere?${qs({ lat: p.lat, lon: p.lon })}`))?.summary ?? ''; } catch { /* offline */ }
  const c = C.all(p.lat, p.lon);
  return [
    `${who ? who + ' – ' : ''}my location${p.acc ? ` (±${Math.round(p.acc)} m)` : ''} at ${new Date(p.time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`,
    c.dec, `MGRS ${c.mgrs}`, address ? `Near ${address}` : null,
    `https://maps.apple.com/?ll=${p.lat.toFixed(6)},${p.lon.toFixed(6)}&q=${encodeURIComponent(who || 'My location')}`,
    `https://www.google.com/maps?q=${p.lat.toFixed(6)},${p.lon.toFixed(6)}`
  ].filter(Boolean).join('\n');
}

/** Opens the share sheet (or copies, where there is none) with this phone's position. */
export async function sendMyLocation() {
  if (!app.pos) return toast('No GPS fix yet. Wait for the green GPS badge and try again.');
  if (Date.now() - app.pos.time > 60000 && !confirm('Your GPS position is more than a minute old. Send it anyway?')) return;
  const text = await locationText();
  if (navigator.share) {
    try { await navigator.share({ title: 'My location', text }); return; } catch (e) { if (e.name === 'AbortError') return; }
  }
  copy(text, 'Location copied – paste it into a text or message');
}

/** "My location": tap the GPS badge. Details of this phone's position with Send and SOS. */
export function openMyLocation() {
  if (!app.pos) return toast('No GPS fix yet.');
  const p = app.pos;
  focus(p.lat, p.lon, 16);
  app.panel.show(placeView({
    title: 'My location', kind: `GPS ±${Math.round(p.acc)} m · ${ago((Date.now() - p.time) / 1000)}`, lat: p.lat, lon: p.lon,
    extraButtons: [
      button('Send my location', () => sendMyLocation(), 'primary', 'share'),
      app.team?.available ? (sosActive ? button('Cancel SOS', () => cancelSos(), 'danger') : button('SOS', () => startSos(), 'danger sos')) : null
    ]
  }));
}
