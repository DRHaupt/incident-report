// The Field Suite home screen: big tiles for the map and the tools. The installed app starts here; in a browser it
// is one tap away (the home button beside the search box). A tool opened from here fills the screen ("tool mode")
// and closing it comes back here; anything that needs the map (zones, "show on map") drops out of tool mode.

import { app } from './app.js';
import { h, toast } from './ui.js';
import { native, serverBase } from './native.js';
import { firstAidView } from './firstaid.js';
import { hazmatView } from './hazmat.js';
import { reportsView } from './reports.js';
import { teamView } from './team.js';
import { startSos, cancelSos, isSosActive, sendMyLocation } from './alerts.js';

const S = (d) => `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${d}</svg>`;
const ART = {
  map: S('<path d="M3 6.5 9 4l6 2.5L21 4v13.5L15 20l-6-2.5L3 20z"/><path d="M9 4v13.5M15 6.5V20"/>'),
  firstaid: S('<rect x="3" y="3" width="18" height="18" rx="4"/><path d="M12 7.5v9M7.5 12h9"/>'),
  hazmat: S('<path d="M12 2.5 21.5 12 12 21.5 2.5 12z"/><path d="M12 8v5M12 16h.01"/>'),
  reports: S('<path d="M7 3h7l5 5v13H7z"/><path d="M14 3v5h5M10 13h6M10 17h4"/>'),
  team: S('<circle cx="9" cy="8" r="3"/><path d="M3 20c0-3.3 2.7-6 6-6s6 2.7 6 6"/><circle cx="17" cy="9" r="2.3"/><path d="M16 14.2c2.8.2 5 2.6 5 5.8"/>'),
  send: S('<path d="M3 11 21 3l-8 18-2-8z"/>')
};

const el = () => document.getElementById('home');
export const isHome = () => !el().hidden;
const inTool = () => document.body.classList.contains('tool');

function tile(id, title, detail, color, onClick) {
  return h('button', { type: 'button', class: 'home-tile', 'data-tile': id, onclick: onClick },
    h('span', { class: 'home-art', style: { background: color }, html: ART[id] }),
    h('span', { class: 'home-title' }, title),
    h('span', { class: 'home-detail' }, detail));
}

function render() {
  const team = app.team;
  const sharing = !!app.settings.shareTeam;
  const sos = isSosActive();
  const where = native ? serverBase().replace(/^https?:\/\//, '') : location.host;
  el().replaceChildren(
    h('div', { class: 'home-inner' },
      h('header', { class: 'home-head' },
        h('img', { src: 'icons/icon-192.png', alt: '', width: 52, height: 52 }),
        h('div', {}, h('h1', {}, 'Field Suite'), h('p', {}, [where, app.info?.dataDate ? `map data ${app.info.dataDate}` : null].filter(Boolean).join(' · ')))),
      h('div', { class: 'home-grid' },
        tile('map', 'Map', 'Search, nearest, directions, layers', '#1c7ed6', () => hideHome()),
        tile('firstaid', 'First aid & CPR', 'Step-by-step topics, timers, CPR beat', '#2b8a3e', () => openTool(firstAidView())),
        tile('hazmat', 'Hazmat', 'ERG 2024: UN number, guide, zones', '#e67700', () => openTool(hazmatView())),
        tile('reports', 'Incident reports', 'Fill in a report on this phone', '#5f3dc4', () => openTool(reportsView()))),
      h('div', { class: 'home-row' },
        h('button', { type: 'button', class: 'home-chip', 'data-tile': 'team', onclick: () => openTool(teamView()) },
          h('span', { class: 'home-chip-art', html: ART.team }),
          h('span', {}, h('b', {}, 'Team'), h('small', {}, team?.available === false ? 'Off on this server' : sharing ? 'Sharing your position' : 'Not sharing your position'))),
        h('button', { type: 'button', class: 'home-chip', 'data-tile': 'send', onclick: () => sendMyLocation() },
          h('span', { class: 'home-chip-art', html: ART.send }),
          h('span', {}, h('b', {}, 'Send my location'), h('small', {}, 'By text or any app')))),
      team?.available
        ? h('button', { type: 'button', class: `home-sos${sos ? ' active' : ''}`, onclick: async () => { if (isSosActive()) await cancelSos(); else await startSos(); render(); } },
            sos ? 'Cancel my SOS' : 'SOS – alert every team phone')
        : null));
}

export function showHome() {
  if (app.panel.current?.locked) return;     // sign-in or server set-up is on screen
  document.body.classList.remove('tool');
  if (app.panel.isOpen) app.panel.close();
  render();
  el().hidden = false;
  document.body.classList.add('home');
}

export function hideHome() {
  el().hidden = true;
  document.body.classList.remove('home');
  app.map?.resize();
}

function openTool(view) {
  if (!app.info) return toast('Not connected to the server yet.');
  el().hidden = true;
  document.body.classList.remove('home');
  document.body.classList.add('tool');
  app.panel.show(view);
}

/** The panel was closed by the person (X, or Back at its first screen). From a tool, that goes back to the home screen. */
export function onPanelClosedByUser() {
  if (inTool()) showHome();
}

/** Something wants the map to be seen (zones, "show on map", a route): the tool stops covering it. */
export function leaveTool() {
  if (!inTool()) return;
  document.body.classList.remove('tool');
  app.map?.resize();
}

/** Keeps the tiles' status lines current while the home screen is showing. */
export function refreshHome() { if (isHome()) render(); }
