// The phone app's coordinate formats must match the desktop app's (Shared.Core.Geo). Run: node --test tests/web/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as C from '../../src/FieldMap.Web/wwwroot/js/coords.js';

test('MGRS and UTM match the C# implementation', () => {
  // Expected values from Shared.Core.Geo.CoordinateFormat (the server's /api/whatshere returns the same).
  assert.equal(C.mgrs(57.405, -125.9), '10V CJ 25780 66185');
  assert.equal(C.utm(57.405, -125.9), '10V 325780 6366185');
  assert.equal(C.mgrs(57.40935, -125.90534), '10V CJ 25480 66683');
  assert.equal(C.mgrs(51.066133, -114.087712).slice(0, 8), '11U QS 0');
  assert.equal(C.mgrs(-33.8568, 151.2153).split(' ')[0], '56H');
  assert.equal(C.utm(85, 0), 'n/a (polar)');
});

test('degrees formats round like the desktop', () => {
  assert.equal(C.dms(57.405, -125.9), '57°24′18.0″N 125°54′00.0″W');
  assert.equal(C.ddm(57.405, -125.9), '57°24.300′N 125°54.000′W');
  assert.equal(C.decimal(57.405, -125.9), '57.40500, -125.90000');
  assert.equal(C.dms(49.99999999, -120), '50°00′00.0″N 120°00′00.0″W');
});

test('dispatch text is ASCII-safe for CAD notes', () => {
  const t = C.dispatchText('Gate A', 57.405, -125.9);
  assert.match(t, /^Gate A\n57\.40500, -125\.90000 \/ MGRS 10V CJ 25780 66185\n57°24'18\.0"N/);
});

test('distances, bearings and labels', () => {
  assert.ok(Math.abs(C.distance([-125.9, 57.4], [-125.9, 57.5]) - 11120) < 30);
  assert.equal(C.compass(C.bearing([-125.9, 57.4], [-125.8, 57.4])), 'E');
  assert.equal(C.formatDistance(430), '430 m');
  assert.equal(C.formatDistance(4336), '4.3 km');
  assert.equal(C.formatDuration(214), '4 min');
  assert.equal(C.formatDuration(4000), '1 h 07 min');
});
