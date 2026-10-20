// Renders every artboard of the Claude Design canvas to docs/design/reference/<Board>.png (CSS px, scale 1,
// full page height at the canvas width, animations off, reduced motion).
//
// The .dc.html artboards need the canvas runtime to render: put it next to them as support.js (it is the canvas's
// `artifact-type/dc-runtime.js`, not stored in this repo) together with the Inter / Geist Mono font files and a
// fonts.css that the artboards' Google Fonts <link> is rewritten to. See docs/design/README.md.
//
// usage: NODE_PATH=<global node_modules with playwright> node scripts/design/render-mockups.mjs <renderDir> <outDir>
//        <renderDir> holds the *.dc.html files, canvas.json, support.js and fonts/fonts.css
// env:   CHROMIUM=<path to chromium> (default /opt/pw-browsers/chromium)
import { createRequire } from 'module';
import fs from 'fs';
import path from 'path';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const [dir, outDir] = process.argv.slice(2);
if (!dir || !outDir) {
  console.error('usage: node render-mockups.mjs <renderDir> <outDir>');
  process.exit(2);
}

const canvas = JSON.parse(fs.readFileSync(path.join(dir, 'canvas.json'), 'utf8'));
fs.mkdirSync(outDir, { recursive: true });
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM ?? '/opt/pw-browsers/chromium', args: ['--no-sandbox'] });
let failed = 0;
for (const file of canvas.order) {
  const board = canvas.boards[file];
  const page = await browser.newPage({ viewport: { width: board.w, height: board.h }, deviceScaleFactor: 1, reducedMotion: 'reduce' });
  const errors = [];
  page.on('pageerror', e => errors.push(String(e)));
  await page.goto('file://' + path.resolve(dir, file));
  await page.waitForFunction(() => document.querySelector('#dc-root')?.children.length > 0, null, { timeout: 15000 })
    .catch(() => errors.push('the artboard did not render (#dc-root is empty)'));
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(300);
  const height = await page.evaluate(() => Math.ceil(document.documentElement.scrollHeight));
  const name = file.replace('.dc.html', '');
  await page.screenshot({ path: path.join(outDir, name + '.png'), fullPage: true, animations: 'disabled' });
  console.log(`${name}\t${board.w}x${height}${errors.length ? '\tERRORS: ' + errors.join(' | ') : ''}`);
  failed += errors.length > 0 ? 1 : 0;
  await page.close();
}

await browser.close();
process.exit(failed > 0 ? 1 : 0);
