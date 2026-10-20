// Renders every artboard of the Claude Design canvas to docs/design/reference/<Board>.png (CSS px, scale 1,
// full page height at the canvas width, animations off, reduced motion), plus <Board>.notext.png (the same page with
// every glyph transparent: shapes, fills, borders, icons) and <Board>.text.json (each text node's line boxes, first-line
// baseline, block-level ancestor and font; inputs' placeholders with their text start and vertical centre).
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

  // Text geometry: every visible text node with its line boxes and font, for the app-side text comparison.
  const texts = await page.evaluate(() => {
    const out = [];
    const blockIds = new Map();
    const nodes = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) nodes.push(n);
    for (const node of nodes) {
      const text = node.textContent.replace(/\s+/g, ' ').trim();
      const el = node.parentElement;
      if (!text || !el || el.closest('style,script,helmet,title')) continue;
      const cs = getComputedStyle(el);
      if (cs.visibility === 'hidden' || cs.display === 'none' || parseFloat(cs.opacity) === 0) continue;
      const range = document.createRange();
      range.selectNodeContents(node);
      const lines = [...range.getClientRects()].filter(r => r.width > 0 && r.height > 0)
        .map(r => ({ x: +r.x.toFixed(2), y: +(r.y + window.scrollY).toFixed(2), w: +r.width.toFixed(2), h: +r.height.toFixed(2) }));
      if (lines.length === 0) continue;
      // The first line's baseline, read from a zero-size inline-block marker placed before the text (its bottom edge
      // sits on the baseline), so the app side can compare baselines instead of line boxes. The text and the marker are
      // wrapped in one span for the measurement: a text node that is a direct child of a flex or grid container is an
      // anonymous item, and a bare marker next to it would become an item of its own instead of sitting on its line.
      const parent = node.parentNode;
      const wrapper = document.createElement('span');
      const marker = document.createElement('span');
      marker.style.cssText = 'display:inline-block;width:0;height:0;vertical-align:baseline;padding:0;margin:0;border:0';
      parent.insertBefore(wrapper, node);
      wrapper.appendChild(marker);
      wrapper.appendChild(node);
      const baseline = +(marker.getBoundingClientRect().bottom + window.scrollY).toFixed(2);
      parent.insertBefore(node, wrapper);
      wrapper.remove();
      // The nearest block-level ancestor: texts split around inline chips or highlights share it.
      let block = el;
      while (block.parentElement && /^inline/.test(getComputedStyle(block).display)) block = block.parentElement;
      if (!blockIds.has(block)) blockIds.set(block, blockIds.size);
      out.push({ text, lines, baseline, blockId: blockIds.get(block), blockText: block.innerText.replace(/\s+/g, ' ').trim(), fontFamily: cs.fontFamily, fontSize: parseFloat(cs.fontSize), fontWeight: +cs.fontWeight, letterSpacing: cs.letterSpacing, lineHeight: cs.lineHeight, color: cs.color });
    }
    for (const input of document.querySelectorAll('input[placeholder],textarea[placeholder]')) {
      const r = input.getBoundingClientRect();
      const cs = getComputedStyle(input);
      // A placeholder has no text node: its text starts at the content box's left edge and is centred vertically.
      const textX = r.x + parseFloat(cs.borderLeftWidth) + parseFloat(cs.paddingLeft);
      out.push({ text: input.placeholder.replace(/\s+/g, ' ').trim(), placeholder: true, box: { x: r.x, y: r.y + window.scrollY, w: r.width, h: r.height }, textX: +textX.toFixed(2), centerY: +(r.y + window.scrollY + r.height / 2).toFixed(2), lines: [], fontFamily: cs.fontFamily, fontSize: parseFloat(cs.fontSize), fontWeight: +cs.fontWeight, letterSpacing: cs.letterSpacing, lineHeight: cs.lineHeight, color: cs.color });
    }
    return out;
  });
  fs.writeFileSync(path.join(outDir, name + '.text.json'), JSON.stringify({ board: name, width: board.w, height, texts }, null, 1));

  // The same page without text (shapes, fills, borders, icons): icons keep their colour, every glyph turns transparent.
  await page.evaluate(() => {
    for (const svg of document.querySelectorAll('svg')) {
      const color = getComputedStyle(svg).color;
      for (const el of [svg, ...svg.querySelectorAll('*')]) {
        for (const attr of ['fill', 'stroke']) {
          if (el.getAttribute(attr) === 'currentColor') el.setAttribute(attr, color);
        }
      }
      if (!svg.getAttribute('fill') && getComputedStyle(svg).fill === 'currentcolor') svg.setAttribute('fill', color);
    }
    const style = document.createElement('style');
    style.textContent = '*{color:transparent!important;-webkit-text-fill-color:transparent!important;text-shadow:none!important;caret-color:transparent!important}::placeholder{color:transparent!important}';
    document.head.appendChild(style);
  });
  await page.waitForTimeout(100);
  await page.screenshot({ path: path.join(outDir, name + '.notext.png'), fullPage: true, animations: 'disabled' });
  console.log(`${name}\t${board.w}x${height}${errors.length ? '\tERRORS: ' + errors.join(' | ') : ''}`);
  failed += errors.length > 0 ? 1 : 0;
  await page.close();
}

await browser.close();
process.exit(failed > 0 ? 1 : 0);
