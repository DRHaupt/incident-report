// Copies the phone web app (src/FieldMap.Web/wwwroot) into mobile/www for Capacitor to package.
// Left out: the service worker and web manifest (the installed app keeps its own offline copies, see js/native.js)
// and the label fonts (about 5 MB that the app fetches from the server and keeps, like map tiles).
import { cpSync, rmSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const from = join(here, '..', '..', 'src', 'FieldMap.Web', 'wwwroot');
const to = join(here, '..', 'www');
const skip = new Set(['sw.js', 'manifest.webmanifest', 'fonts']);

if (!existsSync(join(from, 'index.html'))) throw new Error(`Web app not found at ${from}`);
rmSync(to, { recursive: true, force: true });
cpSync(from, to, { recursive: true, filter: (src) => !skip.has(relative(from, src).split(sep)[0]) });

// The page no longer has a web manifest to point at.
const index = join(to, 'index.html');
const html = readFileSync(index, 'utf8');
const out = html.replace(/^\s*<link rel="manifest"[^>]*>\r?\n/m, '');
if (out === html) throw new Error('index.html: the manifest link was not found (has the page changed?)');
writeFileSync(index, out);
console.log(`Copied the web app to ${relative(process.cwd(), to) || '.'}`);
