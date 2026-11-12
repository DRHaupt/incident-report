// First aid guide and CPR helper, from the Field Toolkit's content (loaded with its own loader on the server, numbers
// filled in). Downloaded once and kept for offline use. Search is the toolkit's symptom search, ported.

import { app } from './app.js';
import { getJson } from './api.js';
import { h, row, button, note, heading, segmented, toast, add, set, icon } from './ui.js';

let data = null, loading = null;

export async function loadFirstAid() {
  if (data) return data;
  loading ??= getJson('/api/firstaid').then((d) => {
    data = d;
    data.byId = new Map(d.topics.map((t) => [t.id, t]));
    data.index = new Map(d.topics.map((t) => [t.id, buildIndex(t)]));
    data.synonyms = d.guide.synonyms.flatMap((g) => g.phrases.map((p) => ({ phrase: normalize(p), topics: g.topics }))).filter((s) => s.phrase);
    return data;
  }).finally(() => { loading = null; });
  return loading;
}

// ------------------------------------------------------------------ search (FieldToolkit.Core FirstAidSearch, ported)

const STOP = new Set('a an the is are was were be been my his her their they he she it someone somebody person patient casualty has have had of to in on at and or with very really lot lots bit just got get getting keeps keep what do does i me we you after from for by them him this that there some'.split(' '));

export function normalize(text) {
  if (!text) return '';
  return text.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/['’]/g, '').replace(/[^\p{L}\p{N}]+/gu, ' ').trim().replace(/\s+/g, ' ');
}
const unDouble = (w) => (w.length > 2 && w.at(-1) === w.at(-2) && !'aeiouls'.includes(w.at(-1)) ? w.slice(0, -1) : w);
export function stem(w) {
  if (w.length > 5 && w.endsWith('ing')) return unDouble(w.slice(0, -3));
  if (w.length > 4 && w.endsWith('ied')) return w.slice(0, -3) + 'y';
  if (w.length > 4 && w.endsWith('ed')) return unDouble(w.slice(0, -2));
  if (w.length > 4 && w.endsWith('ies')) return w.slice(0, -3) + 'y';
  if (w.length > 3 && w.endsWith('s') && !w.endsWith('ss') && !w.endsWith('us') && !w.endsWith('is')) return w.slice(0, -1);
  return w;
}
export const terms = (text) => [...new Set(normalize(text).split(' ').filter((w) => w && !STOP.has(w)).map(stem).filter((w) => w.length > 1))];

function buildIndex(t) {
  const body = new Set([...terms(t.summary), ...t.warningSigns.flatMap(terms), ...t.callEmergency.when.flatMap(terms), ...t.steps.flatMap((s) => terms(s.title))]);
  return { title: new Set(terms(t.title)), keywords: new Set(t.keywords.flatMap(terms)), keywordPhrases: t.keywords.map(normalize), body };
}

export function searchTopics(query, max = 50) {
  const q = normalize(query);
  if (!q) return data.topics;
  const padded = ` ${q} `, ts = terms(query), scores = new Map();
  const addScore = (id, s) => scores.set(id, (scores.get(id) ?? 0) + s);
  for (const { phrase, topics } of data.synonyms)
    if (padded.includes(` ${phrase} `) || (q.length >= 4 && phrase.startsWith(q))) topics.forEach((id, i) => addScore(id, 100 + phrase.length - 10 * i));
  for (const t of data.topics) {
    const idx = data.index.get(t.id);
    for (const kp of idx.keywordPhrases) {
      if (!kp) continue;
      if (padded.includes(` ${kp} `)) addScore(t.id, 60 + kp.length);
      else if (q.length >= 3 && kp.startsWith(q)) addScore(t.id, 40);
    }
    let matched = 0;
    for (const term of ts) {
      let hit = false;
      if (idx.title.has(term)) { addScore(t.id, 30); hit = true; }
      if (idx.keywords.has(term)) { addScore(t.id, 15); hit = true; }
      if (idx.body.has(term)) { addScore(t.id, 4); hit = true; }
      if (!hit && term.length >= 3 && [...idx.title].some((w) => w.startsWith(term))) { addScore(t.id, 12); hit = true; }
      if (hit) matched++;
    }
    if (ts.length > 1 && matched === ts.length) addScore(t.id, 20);
  }
  return [...scores].map(([id, s]) => ({ t: data.byId.get(id), s })).filter((x) => x.t && x.s > 0)
    .sort((a, b) => b.s - a.s || (b.t.pinnedFirst - a.t.pinnedFirst) || a.t.order - b.t.order).slice(0, max).map((x) => x.t);
}

// ------------------------------------------------------------------ timers (e.g. tourniquet applied) – kept across reloads

const TKEY = 'fieldmap.firstaid.timers';
let timers = readTimers();
function readTimers() { try { return JSON.parse(localStorage.getItem(TKEY) ?? '[]'); } catch { return []; } }
function saveTimers() { try { localStorage.setItem(TKEY, JSON.stringify(timers)); } catch { /* storage blocked */ } }
const clock = (ms) => { const s = Math.max(0, Math.floor(ms / 1000)); const hh = Math.floor(s / 3600), mm = Math.floor(s / 60) % 60, ss = s % 60; return `${hh ? hh + ':' : ''}${String(mm).padStart(hh ? 2 : 1, '0')}:${String(ss).padStart(2, '0')}`; };

function startTimer(a) {
  let start = Date.now();
  if (a.askStartTime) {
    const ago = prompt(`${a.timerLabel}: how many minutes ago did it start? (0 = now)`, '0');
    if (ago === null) return;
    start -= Math.max(0, parseFloat(ago) || 0) * 60000;
  }
  timers.push({ id: Date.now(), label: a.timerLabel || a.label, mode: a.mode || 'stopwatch', minutes: a.minutes || 0, start });
  saveTimers();
  toast(`${a.timerLabel || a.label} – timer started at ${new Date(start).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`);
  app.panel.refresh();
}

function timersBlock() {
  if (!timers.length) return null;
  const list = h('div', { class: 'fa-timers' });
  const draw = () => set(list, ...timers.map((t) => {
    const ms = Date.now() - t.start;
    const left = t.mode === 'countdown' ? t.minutes * 60000 - ms : null;
    return h('div', { class: `fa-timer${left !== null && left <= 0 ? ' done' : ''}` },
      h('span', { class: 'fa-timer-label' }, t.label, h('small', {}, ` since ${new Date(t.start).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`)),
      h('b', {}, left !== null ? (left > 0 ? clock(left) : 'Time!') : clock(ms)),
      button('Stop', () => { if (confirm(`Stop the “${t.label}” timer?`)) { timers = timers.filter((x) => x.id !== t.id); saveTimers(); app.panel.refresh(); } }, 'small'));
  }));
  draw();
  const tick = setInterval(() => { if (!list.isConnected) clearInterval(tick); else draw(); }, 1000);
  return h('div', {}, heading('Timers'), list);
}

// ------------------------------------------------------------------ views

const callBox = () => h('a', { class: 'fa-call', href: `tel:${(data.guide.emergencyNumber.match(/\d+/) ?? ['911'])[0]}` }, icon('phone'), `Call ${data.guide.emergencyNumber}`);
const draftNote = () => (data.guide.review?.reviewedBy ? null : note(`${data.guide.review?.status || 'Not clinically reviewed'}. Use your training and the dispatcher’s instructions.`, 'warn'));

export function firstAidView() {
  let timer;
  return {
    tab: null, title: 'First aid', dispose: () => clearTimeout(timer),
    render(body) {
      const input = h('input', { type: 'search', class: 'search-input', placeholder: 'What’s happening? e.g. “not breathing”, “bleeding”', autocomplete: 'off', autocorrect: 'off', 'aria-label': 'Search first aid' });
      const results = h('div', { class: 'list' });
      const run = () => {
        if (!data) return;
        const q = input.value.trim();
        const list = searchTopics(q);
        if (q) return set(results, ...(list.length ? list.map(topicRow) : [note(`Nothing matches “${q}”. Try other words, or call ${data.guide.emergencyNumber}.`)]));
        // Empty box: by category, scene safety first.
        const groups = new Map();
        for (const t of data.topics) groups.set(t.category, [...(groups.get(t.category) ?? []), t]);
        set(results, ...[...groups].flatMap(([cat, ts]) => [heading(cat), ...ts.map(topicRow)]));
      };
      input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(run, 120); });
      const top = h('div', {});
      add(body, top, h('div', { class: 'search-box' }, icon('search'), input), results);
      const ready = () => {
        set(top, callBox(), draftNote(), h('div', { class: 'actions' }, button('CPR helper', () => app.panel.show(cprView('adult'), { push: true }), 'primary')), timersBlock());
        run();
      };
      if (data) ready();
      else { set(results, note('Loading the first aid guide…', 'muted')); loadFirstAid().then(ready).catch((e) => set(results, note(e.message, 'warn'))); }
    }
  };
}

const topicRow = (t) => row({ title: t.title, detail: t.summary, onClick: () => app.panel.show(topicView(t.id), { push: true }) });

export function topicView(id) {
  return {
    tab: null, title: 'First aid',
    render(body) {
      const t = data.byId.get(id);
      if (!t) return add(body, note('That topic isn’t in this guide.', 'warn'));
      add(body,
        h('div', { class: 'place-head' }, h('h2', {}, t.title), h('p', { class: 'kind' }, t.category)),
        draftNote(),
        t.callEmergency.headline || t.callEmergency.when.length ? h('div', { class: 'fa-emergency' },
          h('b', {}, t.callEmergency.headline || `Call ${data.guide.emergencyNumber} if:`),
          t.callEmergency.when.length ? h('ul', {}, t.callEmergency.when.map((w) => h('li', {}, w))) : null,
          callBox()) : null,
        t.summary ? h('p', { class: 'line' }, t.summary) : null,
        h('ol', { class: 'fa-steps' }, t.steps.map((s) => h('li', {},
          h('b', {}, s.title),
          s.text ? h('p', {}, s.text) : null,
          s.points.length ? h('ul', {}, s.points.map((p) => h('li', {}, p))) : null,
          s.caution ? note(s.caution, 'warn') : null,
          s.action ? actionButton(s.action) : null))),
        t.warningSigns.length ? h('div', {}, heading('Warning signs'), h('ul', { class: 'erg-items' }, t.warningSigns.map((w) => h('li', {}, w)))) : null,
        t.doNot.length ? h('div', {}, heading('Do not'), h('ul', { class: 'erg-items fa-donot' }, t.doNot.map((w) => h('li', {}, w)))) : null,
        timersBlock(),
        t.related.length ? h('div', {}, heading('Related'), t.related.map((r) => data.byId.get(r)).filter(Boolean).map(topicRow)) : null,
        t.sources.length ? note('Sources: ' + t.sources.map((s) => [s.body, s.title, s.year].filter(Boolean).join(', ')).join(' · '), 'muted') : null,
        note(data.guide.disclaimer, 'muted'));
    }
  };
}

function actionButton(a) {
  if (a.type === 'timer') return h('div', { class: 'actions small' }, button(a.label, () => startTimer(a), 'primary'));
  if (a.type === 'cpr') return h('div', { class: 'actions small' }, button(a.label, () => app.panel.show(cprView(a.target), { push: true }), 'primary'));
  if (a.type === 'topic') return h('div', { class: 'actions small' }, button(a.label, () => app.panel.show(topicView(a.target), { push: true })));
  return null;
}

// ------------------------------------------------------------------ CPR helper: metronome and compression counter

let cpr = null; // running state survives switching views

function ratioOf(r) { const m = /^(\d+):(\d+)$/.exec(r); return m ? { c: +m[1], b: +m[2] } : { c: 0, b: 0 }; }

export function cprView(presetId) {
  return {
    tab: null, title: 'CPR helper', full: true,
    dispose: () => { /* keep running in the background until stopped */ },
    render(body) {
      const g = data.cpr;
      const preset = g.presets.find((p) => p.id === (cpr?.preset ?? presetId)) ?? g.presets[0];
      const st = cpr ?? { preset: preset.id, ratio: preset.defaultRatio, rate: g.defaultRate };
      const display = h('div', { class: 'cpr-display' });
      const draw = () => {
        if (!cpr?.running) return set(display, h('div', { class: 'cpr-count' }, '–'), h('div', { class: 'cpr-phase' }, 'Press Start, then push on every beat'));
        const now = performance.now();
        const phase = cpr.breathUntil > now ? `Give ${ratioOf(cpr.ratio).b} breaths · ${Math.ceil((cpr.breathUntil - now) / 1000)} s` : 'Push hard, push fast';
        const every = g.switchEveryMinutes * 60000, elapsed = Date.now() - cpr.startedAt;
        const swap = Math.floor(elapsed / every), showSwap = swap >= 1 && elapsed % every < 15000;
        set(display,
          h('div', { class: `cpr-count${cpr.breathUntil > now ? ' breaths' : ''}` }, cpr.breathUntil > now ? 'Breathe' : String(cpr.count)),
          h('div', { class: 'cpr-phase' }, phase),
          h('div', { class: 'cpr-stats' }, `${clock(Date.now() - cpr.startedAt)} · ${cpr.total} compression${cpr.total === 1 ? '' : 's'} · ${cpr.cycles} cycle${cpr.cycles === 1 ? '' : 's'}`),
          showSwap ? h('div', { class: 'cpr-swap' }, `${g.switchEveryMinutes} minutes: swap compressors / follow the AED prompts`) : null);
        if (showSwap && swap > cpr.swapNoticed) { cpr.swapNoticed = swap; navigator.vibrate?.([200, 100, 200, 100, 200]); }
      };
      const startStop = button(cpr?.running ? 'Stop' : 'Start', () => (cpr?.running ? stopCpr() : startCpr(st)), cpr?.running ? 'danger cpr-go' : 'primary cpr-go');
      add(body,
        draftNote(),
        segmented(g.presets.map((p) => ({ id: p.id, label: p.label })), st.preset, (id) => { if (cpr?.running) return toast('Stop first to change the preset.'); const p = g.presets.find((x) => x.id === id); cpr = { preset: id, ratio: p.defaultRatio, rate: st.rate }; app.panel.refresh(); }),
        segmented(preset.ratios.map((r) => ({ id: r, label: r === 'continuous' ? 'Hands-only' : r })), st.ratio, (r) => { if (cpr?.running) return toast('Stop first to change the ratio.'); cpr = { ...st, ratio: r }; app.panel.refresh(); }),
        note(preset.ratioNotes?.[st.ratio] ?? '', 'muted'),
        display,
        h('div', { class: 'actions' }, startStop),
        h('div', { class: 'cpr-rate' }, h('span', {}, 'Rate'),
          ...[g.minRate, g.defaultRate, g.maxRate].map((r) => button(`${r}/min`, () => { if (cpr?.running) return toast('Stop first to change the rate.'); cpr = { ...st, rate: r }; app.panel.refresh(); }, st.rate === r ? 'small on' : 'small'))),
        h('dl', { class: 'attrs' }, h('dt', {}, 'Depth'), h('dd', {}, preset.depth), h('dt', {}, 'Hands'), h('dd', {}, preset.handPosition)),
        preset.topic ? h('div', { class: 'actions small' }, button('Full CPR steps', () => app.panel.show(topicView(preset.topic), { push: true }))) : null,
        note('The beat keeps going if you switch screens; come back here to stop it.', 'muted'));
      draw();
      const tick = setInterval(() => { if (!display.isConnected) clearInterval(tick); else draw(); }, 200);
    }
  };
}

let audio, schedulerId;
function click(time, accent) {
  const o = audio.createOscillator(), gn = audio.createGain();
  o.frequency.value = accent ? 1320 : 990;
  gn.gain.setValueAtTime(0.0001, time);
  gn.gain.exponentialRampToValueAtTime(0.5, time + 0.002);
  gn.gain.exponentialRampToValueAtTime(0.0001, time + 0.06);
  o.connect(gn).connect(audio.destination);
  o.start(time); o.stop(time + 0.07);
}

function startCpr(st) {
  try { audio ??= new (window.AudioContext || window.webkitAudioContext)(); audio.resume(); } catch { toast('This phone can’t play the beat; follow the counter instead.'); }
  const { c, b } = ratioOf(st.ratio);
  const interval = 60 / st.rate;
  cpr = { ...st, running: true, count: 0, total: 0, cycles: 0, breathUntil: 0, startedAt: Date.now(), swapNoticed: 0 };
  let next = audio ? audio.currentTime + 0.1 : 0, beatAt = performance.now() + 100;
  const breathMs = data.cpr.breathPauseSeconds * 1000;
  // A small look-ahead scheduler: beats are placed on the audio clock, so they don't drift with timer jitter.
  schedulerId = setInterval(() => {
    if (!cpr?.running) return;
    const now = performance.now();
    while (beatAt < now + 120) {
      if (cpr.breathUntil > beatAt) { const skip = cpr.breathUntil - beatAt; beatAt += skip; next += skip / 1000; continue; }
      cpr.count++; cpr.total++;
      if (audio) click(next, cpr.count === 1);
      if (c > 0 && cpr.count >= c) { cpr.cycles++; cpr.count = 0; cpr.breathUntil = beatAt + interval * 1000 + breathMs; navigator.vibrate?.(300); }
      beatAt += interval * 1000; next += interval;
    }
  }, 25);
  if ('wakeLock' in navigator && !app.settings.keepAwake) navigator.wakeLock.request('screen').then((l) => { cpr.lock = l; }).catch(() => {});
  app.panel.refresh();
}

function stopCpr() {
  clearInterval(schedulerId);
  const done = cpr;
  cpr = { preset: done.preset, ratio: done.ratio, rate: done.rate };
  done.lock?.release?.();
  toast(`CPR stopped after ${clock(Date.now() - done.startedAt)} – ${done.total} compressions`);
  app.panel.refresh();
}
