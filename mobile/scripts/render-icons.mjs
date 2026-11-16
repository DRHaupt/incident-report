// Renders the app icon and splash sources in mobile/assets from assets/fieldmap/fieldmap.svg (needs Playwright's
// Chromium). After changing the logo:  node scripts/render-icons.mjs && npx @capacitor/assets generate --ios --android
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = dirname(fileURLToPath(import.meta.url));
const svg = readFileSync(join(here, '..', '..', 'assets', 'fieldmap', 'fieldmap.svg'), 'utf8');
const plate = /<rect x="8" y="8" width="240" height="240" rx="44" fill="(#[0-9A-Fa-f]{6})"\/>/;
const colour = svg.match(plate)?.[1];
if (!colour) throw new Error('fieldmap.svg: the background plate was not found');
const glyph = svg.replace(plate, '');
const inner = glyph.replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '');
const wrap = (body, bg) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">${bg ? `<rect width="256" height="256" fill="${bg}"/>` : ''}${body}</svg>`;
const scaled = (k) => `<g transform="translate(128 128) scale(${k}) translate(-128 -128)">${inner}</g>`;

const images = {
  'icon-only.png': [1024, wrap(scaled(0.9), colour)],              // iOS: full square, no transparency
  'icon-foreground.png': [1024, wrap(scaled(0.68), null)],           // Android adaptive icon: inside the safe circle
  'icon-background.png': [1024, wrap('', colour)],
  'splash.png': [2732, wrap(`<g transform="translate(128 128) scale(0.17) translate(-128 -128)">${svg.replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '')}</g>`, '#12161c')],
};
images['splash-dark.png'] = images['splash.png'];

const browser = await chromium.launch();
for (const [name, [size, markup]] of Object.entries(images)) {
  const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
  await page.setContent(`<style>html,body{margin:0;background:transparent}svg{display:block;width:${size}px;height:${size}px}</style>${markup}`);
  await page.screenshot({ path: join(here, '..', 'assets', name), omitBackground: true });
  await page.close();
  console.log(name);
}
await browser.close();
