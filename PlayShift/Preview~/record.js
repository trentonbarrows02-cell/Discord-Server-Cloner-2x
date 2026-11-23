// Renders the scripted preview frame by frame (deterministic, independent of render speed) and encodes an MP4.
// Usage: node record.js [out.mp4] [--width 1280 --height 720 --frames N --query "cells=200&bpc=6"]
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const out = path.resolve(args[0] && !args[0].startsWith('--') ? args[0] : 'out/preview.mp4');
const width = +opt('width', 1280), height = +opt('height', 720);
const maxFrames = +opt('frames', 100000);
const query = opt('query', '');
const stills = opt('stills', ''); // comma-separated frame numbers to also save as PNG

const root = __dirname;
const files = {
  '/index.html': ['index.html', 'text/html'],
  '/preview.js': ['preview.js', 'text/javascript'],
  '/grass-core.js': ['grass-core.js', 'text/javascript'],
  '/three.module.js': ['node_modules/three/build/three.module.js', 'text/javascript'],
};

(async () => {
  const framesDir = path.join(path.dirname(out), path.basename(out, '.mp4') + '_frames');
  fs.mkdirSync(framesDir, { recursive: true });
  for (const f of fs.readdirSync(framesDir)) if (f.endsWith('.jpg')) fs.unlinkSync(path.join(framesDir, f));

  const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
  const page = await browser.newPage({ viewport: { width, height } });
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') console.log('[page]', m.text()); });
  page.on('pageerror', (e) => console.log('[pageerror]', e.message));
  await page.route('http://preview.local/**', (route) => {
    const p = new URL(route.request().url()).pathname;
    const f = files[p];
    if (!f) return route.fulfill({ status: 404, body: 'not found' });
    route.fulfill({ status: 200, contentType: f[1], body: fs.readFileSync(path.join(root, f[0])) });
  });
  await page.goto(`http://preview.local/index.html?record=1&${query}`);
  await page.waitForFunction(() => window.previewReady === true, null, { timeout: 120000 });

  const stillSet = new Set(stills.split(',').filter(Boolean).map(Number));
  const t0 = Date.now();
  let n = 0, evalMs = 0;
  for (; n < maxFrames; n++) {
    const te = Date.now(); const done = await page.evaluate(() => window.frame()); evalMs += Date.now() - te;
    const file = path.join(framesDir, String(n).padStart(5, '0') + '.jpg');
    await page.screenshot({ path: file, type: 'jpeg', quality: 90 });
    if (stillSet.has(n)) await page.screenshot({ path: path.join(path.dirname(out), `still_${n}.png`) });
    if (n % 60 === 0) console.log(`frame ${n}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
    if (done) break;
  }
  await browser.close();
  console.log(`rendered ${n + 1} frames in ${((Date.now() - t0) / 1000).toFixed(0)}s (frame() ${(evalMs / 1000).toFixed(0)}s)`);

  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-framerate', '30', '-i', path.join(framesDir, '%05d.jpg'),
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '24', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', out]);
  console.log('wrote', out, (fs.statSync(out).size / 1e6).toFixed(1) + ' MB');
})().catch((e) => { console.error(e); process.exit(1); });
