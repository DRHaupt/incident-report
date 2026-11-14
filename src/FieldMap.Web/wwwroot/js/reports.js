// Incident reports on the phone: the same form templates as the Windows Incident Report Manager (downloaded once
// from the server and kept for offline use), filled in here. Reports are kept on this phone only (IndexedDB) and
// leave it when the person shares one; nothing is sent to the server.

import { app, myPositionOrCentre } from './app.js';
import { getJson, qs } from './api.js';
import * as C from './coords.js';
import { h, row, button, note, heading, toast, copy, add, set, icon } from './ui.js';

// ------------------------------------------------------------------ the forms

let catalog = null, loading = null;

export function loadForms() {
  if (catalog) return Promise.resolve(catalog);
  loading ??= getJson('/api/forms').then((d) => {
    if (!d?.forms?.length) throw new Error('This server has no report forms. Update the Field Map server.');
    catalog = { forms: d.forms, byCode: new Map(d.forms.map((f) => [f.code, f])) };
    return catalog;
  }).finally(() => { loading = null; });
  return loading;
}

const fieldsOf = (form) => form.sections.flatMap((s) => s.fields.map((f) => ({ section: s.title, field: f })));

// ------------------------------------------------------------------ what the phone keeps

const DB = 'fieldsuite-reports';
const STORE = 'reports';

function open() {
  return new Promise((resolve, reject) => {
    if (!('indexedDB' in window)) return reject(new Error('This browser can’t keep reports (no storage).'));
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
    const req = fn(t.objectStore(STORE));
    t.oncomplete = () => resolve(req?.result);
    t.onerror = t.onabort = () => reject(t.error);
  });
}

export const listReports = async () => ((await tx('readonly', (s) => s.getAll())) ?? []).sort((a, b) => b.modified - a.modified);
const saveReport = (r) => tx('readwrite', (s) => s.put(r));
const deleteReport = (id) => tx('readwrite', (s) => s.delete(id));

// ------------------------------------------------------------------ values

const pad = (n) => String(n).padStart(2, '0');
const isoDate = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;   // the Windows app's formats
const isoTime = (d) => `${pad(d.getHours())}:${pad(d.getMinutes())}`;
const LIST = '; ';

function newReport(form) {
  const now = new Date();
  const values = { incident_date: isoDate(now), incident_time: isoTime(now), date_reported: isoDate(now) };
  const name = (app.settings.teamName ?? '').trim();
  if (name) values.reporter_name = name;
  const keys = new Set(fieldsOf(form).map((x) => x.field.key));
  for (const k of Object.keys(values)) if (!keys.has(k)) delete values[k];
  const id = crypto.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  return {
    id, formCode: form.code, status: 'Draft', created: now.getTime(), modified: now.getTime(), values,
    number: `${form.code}-${isoDate(now).replaceAll('-', '').slice(2)}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`
  };
}

/** Labels of the required fields that are still empty. */
export const missingRequired = (form, report) =>
  fieldsOf(form).filter((x) => x.field.required && !(report.values[x.field.key] ?? '').trim()).map((x) => `${x.section}: ${x.field.label}`);

function display(field, raw) {
  if (!raw) return '';
  if (field.type === 'CheckBox') return raw === 'Yes' ? 'Yes' : '';
  return raw;
}

/** The report as plain text, for the share sheet, a text message or an email. */
export function reportText(form, report) {
  const lines = [form.name.toUpperCase(), `Report ${report.number} · ${report.status}`, ''];
  for (const s of form.sections) {
    const filled = s.fields.map((f) => [f, display(f, report.values[f.key] ?? '')]).filter(([, v]) => v);
    if (!filled.length) continue;
    lines.push(s.title.toUpperCase());
    for (const [f, v] of filled) lines.push(f.type === 'MultiLine' ? `${f.label}:\n${v}` : `${f.label}: ${v}`);
    lines.push('');
  }
  return lines.join('\n').trim();
}

// ------------------------------------------------------------------ the list

const when = (ms) => new Date(ms).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });

export function reportsView() {
  return {
    tab: null, title: 'Incident reports',
    render(body) {
      const list = h('div', { class: 'list' });
      add(body,
        h('div', { class: 'actions' }, button('New report', () => app.panel.show(pickFormView(), { push: true }), 'primary', 'pin')),
        list,
        note('Reports are kept on this phone only. Share a finished report to send it on; removing the app or clearing its data removes them.', 'muted'));
      set(list, note('Loading…', 'muted'));
      Promise.all([loadForms(), listReports()]).then(([cat, reports]) => {
        if (!reports.length) return set(list, note('No reports on this phone yet.', 'muted'));
        set(list, ...reports.map((r) => {
          const form = cat.byCode.get(r.formCode);
          return row({
            title: (r.values.title ?? '').trim() || form?.name || r.formCode,
            detail: `${form?.name ?? r.formCode} · ${when(r.modified)}`,
            right: r.status, badge: r.status === 'Complete' ? '✓' : '✎', color: r.status === 'Complete' ? '#2b8a3e' : '#5f3dc4',
            onClick: () => (form ? app.panel.show(editorView(form, r), { push: true }) : toast('This form isn’t on the server any more.'))
          });
        }));
      }).catch((e) => set(list, note(e.message, 'warn')));
    }
  };
}

// ------------------------------------------------------------------ choosing a form

function pickFormView() {
  let timer;
  return {
    tab: null, title: 'New report', dispose: () => clearTimeout(timer),
    render(body) {
      const input = h('input', { type: 'search', class: 'search-input', placeholder: 'Find a form, e.g. “medical”, “vehicle”', autocomplete: 'off', autocorrect: 'off', enterkeyhint: 'search', 'aria-label': 'Find a form' });
      const results = h('div', { class: 'list' });
      add(body, h('div', { class: 'search-box' }, icon('search'), input), results);
      const start = async (form) => {
        const report = newReport(form);
        await saveReport(report);
        app.panel.back();                                   // the list, which now shows the new report…
        app.panel.show(editorView(form, report), { push: true });   // …with the form on top of it
      };
      const formRow = (f) => row({ title: f.name, detail: f.description || f.code, right: f.code, onClick: () => start(f).catch((e) => toast(e.message)) });
      const show = (cat) => {
        const q = input.value.trim().toLowerCase();
        if (q) {
          const words = q.split(/\s+/);
          const hits = cat.forms.filter((f) => { const t = `${f.name} ${f.category} ${f.code} ${f.description}`.toLowerCase(); return words.every((w) => t.includes(w)); });
          return set(results, ...(hits.length ? hits.map(formRow) : [note('No form matches that.', 'muted')]));
        }
        const groups = new Map();
        for (const f of cat.forms) (groups.get(f.category) ?? groups.set(f.category, []).get(f.category)).push(f);
        set(results, ...[...groups].flatMap(([name, forms]) => [heading(name), ...forms.map(formRow)]));
      };
      set(results, note('Loading the forms…', 'muted'));
      loadForms().then((cat) => {
        show(cat);
        input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => show(cat), 120); });
      }).catch((e) => set(results, note(e.message, 'warn')));
    }
  };
}

// ------------------------------------------------------------------ filling a form in

const INPUT = { Text: ['text'], Phone: ['tel', 'tel'], Email: ['email', 'email'], Number: ['text', 'decimal'], Signature: ['text'], ReportLink: ['text'], Date: ['date'], Time: ['time'], DateTime: ['datetime-local'] };

function editorView(form, report) {
  let timer, dirty = false;
  const save = async () => {
    clearTimeout(timer);
    if (!dirty) return;
    dirty = false;
    report.modified = Date.now();
    try { await saveReport(report); } catch (e) { toast(`Not saved: ${e.message}`); }
  };
  const changed = (key, value) => {
    if (value) report.values[key] = value; else delete report.values[key];
    if (report.status === 'Complete') { report.status = 'Draft'; statusLine.textContent = statusText(); }
    dirty = true;
    clearTimeout(timer);
    timer = setTimeout(save, 400);
  };
  const statusText = () => `${form.code} · ${report.number} · ${report.status}`;
  const statusLine = note(statusText(), 'muted');

  function control(f) {
    const value = report.values[f.key] ?? '';
    if (f.type === 'MultiLine') {
      const t = h('textarea', { class: 'field-input', rows: 3 }, value);
      t.addEventListener('input', () => changed(f.key, t.value.trim()));
      return t;
    }
    if (f.type === 'Choice') {
      const s = h('select', { class: 'field-input' }, h('option', { value: '' }, '–'), f.options.map((o) => h('option', { value: o, selected: o === value }, o)));
      // A value saved by an older version of the form stays selectable.
      if (value && !f.options.includes(value)) s.append(h('option', { value, selected: true }, value));
      s.addEventListener('change', () => changed(f.key, s.value));
      return s;
    }
    if (f.type === 'MultiChoice') {
      const chosen = new Set(value ? value.split(LIST) : []);
      return h('div', { class: 'chips' }, f.options.map((o) => {
        const b = h('button', { type: 'button', class: chosen.has(o) ? 'chip on' : 'chip', 'aria-pressed': String(chosen.has(o)) }, o);
        b.addEventListener('click', () => {
          chosen.has(o) ? chosen.delete(o) : chosen.add(o);
          b.classList.toggle('on', chosen.has(o));
          b.setAttribute('aria-pressed', String(chosen.has(o)));
          changed(f.key, f.options.filter((x) => chosen.has(x)).join(LIST));
        });
        return b;
      }));
    }
    if (f.type === 'YesNo') {
      let current = value;
      const wrap = h('div', { class: 'seg yesno', role: 'radiogroup' });
      const draw = () => set(wrap, ...['Yes', 'No'].map((o) => h('button', {
        type: 'button', role: 'radio', 'aria-checked': String(o === current), class: o === current ? 'on' : '',
        onclick: () => { current = current === o ? '' : o; changed(f.key, current); draw(); }   // tap again to clear
      }, o)));
      draw();
      return wrap;
    }
    if (f.type === 'CheckBox') {
      const box = h('input', { type: 'checkbox', checked: value === 'Yes' });
      box.addEventListener('change', () => changed(f.key, box.checked ? 'Yes' : ''));
      return h('label', { class: 'field-check' }, box, h('span', {}, f.label));
    }
    const [type, mode] = INPUT[f.type] ?? INPUT.Text;
    const input = h('input', { class: 'field-input', type, inputmode: mode, value: f.type === 'DateTime' ? value.replace(' ', 'T') : value, autocomplete: 'off' });
    const read = () => (f.type === 'DateTime' ? input.value.replace('T', ' ') : input.value.trim());
    input.addEventListener('input', () => changed(f.key, read()));
    input.addEventListener('change', () => changed(f.key, read()));
    if (f.key !== 'location') return input;
    // "Location / Address": one tap fills in where the phone is.
    const here = button('Use my position', async () => {
      const p = myPositionOrCentre();
      let text = `${C.all(p.lat, p.lon).dec} (MGRS ${C.all(p.lat, p.lon).mgrs})`;
      try { const w = await getJson(`/api/whatshere?${qs({ lat: p.lat, lon: p.lon })}`); if (w?.summary) text = `${w.summary} – ${text}`; } catch { /* offline: coordinates only */ }
      if (!app.pos) text += ' (map centre, no GPS fix)';
      input.value = text;
      changed(f.key, text);
    }, 'small', 'pin');
    return h('div', {}, input, h('div', { class: 'actions small' }, here));
  }

  const field = (f) => (f.type === 'CheckBox'
    ? h('div', { class: 'field', 'data-key': f.key }, control(f))
    : h('div', { class: 'field', 'data-key': f.key },
        h('label', { class: 'field-label' }, f.label, f.required ? h('span', { class: 'req', title: 'Required' }, ' *') : null),
        control(f), f.hint ? h('p', { class: 'field-hint' }, f.hint) : null));

  return {
    tab: null, title: form.name, dispose: () => { save(); },
    render(body) {
      const problems = h('div', {});
      const complete = async () => {
        await save();
        const missing = missingRequired(form, report);
        if (missing.length) {
          set(problems, note(`Still needed before this report is complete:\n• ${missing.join('\n• ')}`, 'warn pre'));
          problems.scrollIntoView({ block: 'center', behavior: 'smooth' });
          return;
        }
        set(problems);
        report.status = 'Complete';
        dirty = true;
        await save();
        statusLine.textContent = statusText();
        toast('Report marked complete');
      };
      const share = async () => {
        await save();
        const text = reportText(form, report);
        if (navigator.share) {
          try { await navigator.share({ title: `${form.name} ${report.number}`, text }); return; } catch (e) { if (e.name === 'AbortError') return; }
        }
        copy(text, 'Report copied – paste it into an email or message');
      };
      const remove = async () => {
        if (!confirm('Delete this report from this phone? This can’t be undone.')) return;
        clearTimeout(timer);
        dirty = false;
        await deleteReport(report.id);
        toast('Report deleted');
        app.panel.back();
      };
      add(body, statusLine, form.description ? note(form.description, 'muted') : null,
        ...form.sections.flatMap((s) => [heading(s.title), ...s.fields.map(field)]),
        problems,
        h('div', { class: 'actions' }, button('Mark complete', () => complete(), 'primary'), button('Share', () => share(), '', 'share'), button('Delete', () => remove(), 'danger')),
        note('Saved on this phone as you type.', 'muted'));
    }
  };
}
