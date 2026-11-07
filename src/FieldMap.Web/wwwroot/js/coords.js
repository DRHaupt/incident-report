// Coordinate formats used on the radio and by dispatch. A port of Shared.Core.Geo (CoordinateFormat, Utm, Mgrs) so the
// phone shows exactly what the desktop app shows, without asking the server every time the position changes.

const A = 6378137.0;
const F = 1 / 298.257223563;
const K0 = 0.9996;
const E0 = 500000;
const BANDS = 'CDEFGHJKLMNPQRSTUVWX';
const N = F / (2 - F);
const BIG_A = A / (1 + N) * (1 + N * N / 4 + N ** 4 / 64);
const ALPHA = [N / 2 - 2 * N * N / 3 + 5 * N ** 3 / 16, 13 * N * N / 48 - 3 * N ** 3 / 5, 61 * N ** 3 / 240];
const COLUMN_SETS = ['ABCDEFGH', 'JKLMNPQR', 'STUVWXYZ'];
const ROW_LETTERS = 'ABCDEFGHJKLMNPQRSTUV';

const normLon = (lon) => ((lon + 180) % 360 + 360) % 360 - 180;
export const utmInRange = (lat) => lat >= -80 && lat < 84;

function zoneFor(lat, lon) {
  lon = normLon(lon);
  let zone = Math.floor((lon + 180) / 6) + 1;
  if (zone > 60) zone = 60;
  if (lat >= 56 && lat < 64 && lon >= 3 && lon < 12) return 32;
  if (lat >= 72 && lat < 84) {
    if (lon >= 0 && lon < 9) return 31;
    if (lon >= 9 && lon < 21) return 33;
    if (lon >= 21 && lon < 33) return 35;
    if (lon >= 33 && lon < 42) return 37;
  }
  return zone;
}

const bandFor = (lat) => (lat >= 72 ? 'X' : BANDS[Math.floor((lat + 80) / 8)]);

export function toUtm(lat, lon) {
  const zone = zoneFor(lat, lon);
  const phi = lat * Math.PI / 180;
  const dLambda = normLon(lon - ((zone - 1) * 6 - 180 + 3)) * Math.PI / 180;
  const c = 2 * Math.sqrt(N) / (1 + N);
  const t = Math.sinh(Math.atanh(Math.sin(phi)) - c * Math.atanh(c * Math.sin(phi)));
  const xiP = Math.atan2(t, Math.cos(dLambda));
  const etaP = Math.atanh(Math.sin(dLambda) / Math.sqrt(1 + t * t));
  let xi = xiP, eta = etaP;
  for (let j = 1; j <= 3; j++) {
    xi += ALPHA[j - 1] * Math.sin(2 * j * xiP) * Math.cosh(2 * j * etaP);
    eta += ALPHA[j - 1] * Math.cos(2 * j * xiP) * Math.sinh(2 * j * etaP);
  }
  let northing = K0 * BIG_A * xi;
  if (lat < 0) northing += 10000000;
  return { zone, band: bandFor(lat), easting: E0 + K0 * BIG_A * eta, northing };
}

const pad = (n, w) => String(n).padStart(w, '0');

export function utm(lat, lon) {
  if (!utmInRange(lat)) return 'n/a (polar)';
  const u = toUtm(lat, lon);
  return `${u.zone}${u.band} ${Math.floor(u.easting)} ${Math.floor(u.northing)}`;
}

export function mgrs(lat, lon, digits = 5) {
  if (!utmInRange(lat)) return 'n/a (polar)';
  const u = toUtm(lat, lon);
  const e100k = Math.floor(u.easting / 100000);
  const n100k = Math.floor(u.northing / 100000) % 20;
  const column = COLUMN_SETS[(u.zone - 1) % 3][e100k - 1];
  const row = ROW_LETTERS[(n100k + (u.zone % 2 === 0 ? 5 : 0)) % 20];
  const div = 10 ** (5 - digits);
  const e = Math.floor((u.easting % 100000) / div);
  const n = Math.floor((u.northing % 100000) / div);
  return `${u.zone}${u.band} ${column}${row} ${pad(e, digits)} ${pad(n, digits)}`;
}

export const decimal = (lat, lon) => `${lat.toFixed(5)}, ${lon.toFixed(5)}`;

function dmsPart(v, pos, neg) {
  const a = Math.abs(v);
  let d = Math.trunc(a);
  const mFull = (a - d) * 60;
  let m = Math.trunc(mFull);
  let s = Math.round((mFull - m) * 60 * 10) / 10;
  if (s >= 60) { s = 0; m++; }
  if (m >= 60) { m = 0; d++; }
  return `${d}°${pad(m, 2)}′${s.toFixed(1).padStart(4, '0')}″${v >= 0 ? pos : neg}`;
}

function ddmPart(v, pos, neg) {
  const a = Math.abs(v);
  let d = Math.trunc(a);
  let m = Math.round((a - d) * 60 * 1000) / 1000;
  if (m >= 60) { d++; m = 0; }
  return `${d}°${m.toFixed(3).padStart(6, '0')}′${v >= 0 ? pos : neg}`;
}

export const dms = (lat, lon) => `${dmsPart(lat, 'N', 'S')} ${dmsPart(lon, 'E', 'W')}`;
export const ddm = (lat, lon) => `${ddmPart(lat, 'N', 'S')} ${ddmPart(lon, 'E', 'W')}`;

export const FORMATS = [
  { id: 'dec', label: 'Lat/Lon', fn: decimal },
  { id: 'dms', label: 'DMS', fn: dms },
  { id: 'ddm', label: 'Deg-min', fn: ddm },
  { id: 'mgrs', label: 'MGRS', fn: (a, b) => mgrs(a, b) },
  { id: 'utm', label: 'UTM', fn: utm }
];

export const all = (lat, lon) => Object.fromEntries(FORMATS.map((f) => [f.id, f.fn(lat, lon)]));

/** Text for pasting into a CAD note or a message: ASCII-safe primes, decimal and MGRS. */
export function dispatchText(label, lat, lon, extra = []) {
  const lines = [];
  if (label) lines.push(label);
  lines.push(`${decimal(lat, lon)} / MGRS ${mgrs(lat, lon)}`);
  lines.push(dms(lat, lon).replace(/′/g, "'").replace(/″/g, '"'));
  return [...lines, ...extra].join('\n');
}

const R = 6371008.8;
export function distance(a, b) {
  const toR = Math.PI / 180;
  const dLat = (b[1] - a[1]) * toR, dLon = (b[0] - a[0]) * toR;
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(a[1] * toR) * Math.cos(b[1] * toR) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.min(1, Math.sqrt(h)));
}

export function bearing(a, b) {
  const toR = Math.PI / 180;
  const y = Math.sin((b[0] - a[0]) * toR) * Math.cos(b[1] * toR);
  const x = Math.cos(a[1] * toR) * Math.sin(b[1] * toR) - Math.sin(a[1] * toR) * Math.cos(b[1] * toR) * Math.cos((b[0] - a[0]) * toR);
  return (Math.atan2(y, x) / toR + 360) % 360;
}

const COMPASS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];
export const compass = (deg) => COMPASS[Math.round(deg / 22.5) % 16];

export function formatDistance(m) {
  if (m == null || !isFinite(m)) return '';
  if (m < 950) return `${Math.round(m / 10) * 10 || Math.round(m)} m`;
  if (m < 9950) return `${(m / 1000).toFixed(1)} km`;
  return `${Math.round(m / 1000)} km`;
}

export function formatDuration(s) {
  if (s == null) return '';
  const min = Math.round(s / 60);
  if (min < 60) return `${Math.max(1, min)} min`;
  return `${Math.floor(min / 60)} h ${pad(min % 60, 2)} min`;
}
