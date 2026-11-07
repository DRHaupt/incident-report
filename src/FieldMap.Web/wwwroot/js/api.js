// Server calls. A 401 means the server wants the access code; the app shows the sign-in screen.

export class AuthRequired extends Error { constructor(m) { super(m); this.name = 'AuthRequired'; } }
export class ApiError extends Error { constructor(m) { super(m); this.name = 'ApiError'; } }

let onAuth = () => {};
export const setAuthHandler = (fn) => { onAuth = fn; };

async function request(path, init = {}) {
  let res;
  try {
    res = await fetch(path, { credentials: 'same-origin', ...init });
  } catch (e) {
    if (e.name === 'AbortError') throw e;
    throw new ApiError('No connection to the server.');
  }
  if (res.status === 401) {
    onAuth();
    throw new AuthRequired('Access code needed.');
  }
  return res;
}

export async function getJson(path, init) {
  const res = await request(path, init);
  if (res.status === 404) return null;
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) throw new ApiError(body?.error || `The server said ${res.status}.`);
  return body;
}

export async function postJson(path, data) {
  return getJson(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
}

export async function postFile(path, file) {
  const form = new FormData();
  form.append('file', file, file.name);
  return getJson(path, { method: 'POST', body: form });
}

export const qs = (o) => new URLSearchParams(Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '')).toString();
