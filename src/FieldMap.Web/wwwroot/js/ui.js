// Small DOM helpers, the slide-up panel, toasts and icons.

export function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props ?? {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

/** append / replaceChildren that skip null, undefined and false (the DOM methods would print them as text). */
const clean = (kids) => kids.flat(Infinity).filter((k) => k !== null && k !== undefined && k !== false);
export const add = (el, ...kids) => el.append(...clean(kids));
export const set = (el, ...kids) => el.replaceChildren(...clean(kids));

const P = (d) => `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${d}</svg>`;
export const ICONS = {
  search: P('<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>'),
  nearest: P('<path d="M12 21s-7-6.2-7-11.5A7 7 0 0 1 19 9.5C19 14.8 12 21 12 21z"/><circle cx="12" cy="9.5" r="2.5"/>'),
  layers: P('<path d="m12 3 9 5-9 5-9-5 9-5z"/><path d="m3 13 9 5 9-5"/>'),
  items: P('<path d="M8 6h13M8 12h13M8 18h13"/><circle cx="3.5" cy="6" r="1"/><circle cx="3.5" cy="12" r="1"/><circle cx="3.5" cy="18" r="1"/>'),
  more: P('<circle cx="5" cy="12" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="19" cy="12" r="1.6"/>'),
  me: P('<circle cx="12" cy="12" r="3.5"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3"/><circle cx="12" cy="12" r="7.5"/>'),
  close: P('<path d="M6 6l12 12M18 6 6 18"/>'),
  back: P('<path d="m15 18-6-6 6-6"/>'),
  route: P('<circle cx="6" cy="19" r="2.5"/><circle cx="18" cy="5" r="2.5"/><path d="M8.5 19H15a3.5 3.5 0 0 0 0-7H9a3.5 3.5 0 0 1 0-7h6.5"/>'),
  copy: P('<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/>'),
  share: P('<path d="M12 3v12M7 8l5-5 5 5"/><path d="M5 13v6a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-6"/>'),
  pin: P('<path d="M12 21s-7-6.2-7-11.5A7 7 0 0 1 19 9.5C19 14.8 12 21 12 21z"/>'),
  file: P('<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>'),
  trash: P('<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>'),
  upload: P('<path d="M12 16V4M7 9l5-5 5 5"/><path d="M4 16v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3"/>'),
  info: P('<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.5"/>'),
  download: P('<path d="M12 4v12M7 11l5 5 5-5"/><path d="M4 20h16"/>'),
  phone: P('<path d="M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2"/>'),
  car: P('<path d="M5 17h14v-4l-2-5H7l-2 5v4z"/><circle cx="7.5" cy="17.5" r="1.5"/><circle cx="16.5" cy="17.5" r="1.5"/>'),
  warn: P('<path d="M12 3 2 20h20L12 3z"/><path d="M12 10v4M12 17v.5"/>')
};

export const icon = (name, cls = 'ico') => h('span', { class: cls, html: ICONS[name] ?? '' });

// ------------------------------------------------------------------ toast

let toastTimer;
export function toast(text, ms = 2600) {
  const el = document.getElementById('toast');
  el.textContent = text;
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), ms);
}

export async function copy(text, what = 'Copied') {
  try {
    await navigator.clipboard.writeText(text);
    toast(what);
  } catch {
    // Older iOS without clipboard permission: fall back to a hidden text area.
    const ta = h('textarea', { style: { position: 'fixed', opacity: '0' } }, text);
    document.body.append(ta);
    ta.select();
    try { document.execCommand('copy'); toast(what); } catch { toast('Copy isn\'t available here'); }
    ta.remove();
  }
}

// ------------------------------------------------------------------ the panel (bottom sheet on phones, side panel when wide)

export class Panel {
  constructor(root, { onClose, onResize } = {}) {
    this.root = root;
    this.title = root.querySelector('.panel-title');
    this.body = root.querySelector('.panel-body');
    this.backBtn = root.querySelector('.panel-back');
    this.stack = [];
    this.onClose = onClose;
    this.onResize = onResize;
    root.querySelector('.panel-close').addEventListener('click', () => this.close());
    this.backBtn.addEventListener('click', () => this.back());
    this.#dragToResize(root.querySelector('.panel-grab'));
  }

  get isOpen() { return this.root.classList.contains('open'); }
  get current() { return this.stack.at(-1); }

  /** Shows a view: { id, title, render(body) }. push=true keeps the previous view for the back button. */
  show(view, { push = false, full = false } = {}) {
    if (!push) this.stack.forEach((v) => v.dispose?.());
    if (!push) this.stack = [];
    this.stack.push(view);
    this.#render(full);
  }

  refresh() { if (this.current) this.#render(this.root.classList.contains('full')); }

  back() {
    if (this.stack.length <= 1) return this.close();
    this.stack.pop().dispose?.();
    this.#render(false);
  }

  close() {
    this.stack.forEach((v) => v.dispose?.());
    this.stack = [];
    this.root.classList.remove('open', 'full');
    this.root.setAttribute('aria-hidden', 'true');
    this.onClose?.();
    this.onResize?.();
  }

  setFull(full) {
    this.root.classList.toggle('full', full);
    this.onResize?.();
  }

  #render(full) {
    const v = this.current;
    this.title.textContent = v.title;
    this.backBtn.hidden = this.stack.length <= 1;
    this.root.querySelector('.panel-close').hidden = !!v.locked;
    this.body.replaceChildren();
    this.body.scrollTop = 0;
    v.render(this.body);
    this.root.classList.add('open');
    this.root.classList.toggle('full', full || !!v.full);
    this.root.setAttribute('aria-hidden', 'false');
    document.querySelectorAll('#tabs button').forEach((b) => b.classList.toggle('active', b.dataset.tab === v.tab));
    this.onResize?.();
  }

  // Drag the handle up for full height, down to shrink or close (phones only; the side panel ignores it).
  #dragToResize(grab) {
    let startY = 0, startH = 0, dragging = false;
    const sheetMode = () => getComputedStyle(this.root).getPropertyValue('--sheet').trim() === '1';
    grab.addEventListener('pointerdown', (e) => {
      if (!sheetMode()) return;
      dragging = true; startY = e.clientY; startH = this.root.getBoundingClientRect().height;
      grab.setPointerCapture(e.pointerId);
      this.root.style.transition = 'none';
    });
    grab.addEventListener('pointermove', (e) => {
      if (!dragging) return;
      this.root.style.height = `${Math.max(80, startH - (e.clientY - startY))}px`;
    });
    const end = (e) => {
      if (!dragging) return;
      dragging = false;
      this.root.style.transition = '';
      this.root.style.height = '';
      const dy = e.clientY - startY;
      if (Math.abs(dy) < 6) this.setFull(!this.root.classList.contains('full'));
      else if (dy < -40) this.setFull(true);
      else if (dy > 120 && !this.root.classList.contains('full')) this.close();
      else if (dy > 40) this.setFull(false);
    };
    grab.addEventListener('pointerup', end);
    grab.addEventListener('pointercancel', end);
  }
}

// ------------------------------------------------------------------ common building blocks

export function row({ title, detail, onClick, badge, color, right, cls = '' }) {
  return h('button', { class: `row ${cls}`, type: 'button', onclick: onClick },
    badge !== undefined ? h('span', { class: 'badge', style: { background: color ?? 'var(--accent)' } }, badge) : null,
    h('span', { class: 'row-text' }, h('span', { class: 'row-title' }, title), detail ? h('span', { class: 'row-detail' }, detail) : null),
    right ? h('span', { class: 'row-right' }, right) : null);
}

export function toggle({ label, detail, checked, onChange, badge, color }) {
  const input = h('input', { type: 'checkbox', role: 'switch', checked: !!checked });
  input.addEventListener('change', () => onChange(input.checked));
  return h('label', { class: 'row toggle' },
    badge !== undefined ? h('span', { class: 'badge', style: { background: color ?? 'var(--accent)' } }, badge) : null,
    h('span', { class: 'row-text' }, h('span', { class: 'row-title' }, label), detail ? h('span', { class: 'row-detail' }, detail) : null),
    input, h('span', { class: 'switch', 'aria-hidden': 'true' }));
}

export function segmented(options, value, onChange) {
  return h('div', { class: 'seg', role: 'radiogroup' }, options.map((o) =>
    h('button', { type: 'button', role: 'radio', 'aria-checked': String(o.id === value), class: o.id === value ? 'on' : '', onclick: () => onChange(o.id) }, o.label)));
}

export const button = (label, onClick, cls = '', ico) =>
  h('button', { type: 'button', class: `btn ${cls}`, onclick: onClick }, ico ? icon(ico) : null, label);

export const note = (text, cls = '') => h('p', { class: `note ${cls}` }, text);
export const heading = (text) => h('h3', { class: 'sub' }, text);
