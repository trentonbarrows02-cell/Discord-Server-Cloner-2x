// node model-shot.js out.png "a.glb,b.glb" [yaw]  — renders glb files side by side (files are served from absolute paths).
const { chromium } = require('playwright'); const fs = require('fs'); const path = require('path');
const [out, list, yaw = '35', elev = '25'] = process.argv.slice(2);
const map = { '/model-view.html': ['model-view.html', 'text/html'], '/three.module.js': ['node_modules/three/build/three.module.js', 'text/javascript'],
  '/GLTFLoader.js': ['node_modules/three/examples/jsm/loaders/GLTFLoader.js', 'text/javascript'],
  '/utils/BufferGeometryUtils.js': ['node_modules/three/examples/jsm/utils/BufferGeometryUtils.js', 'text/javascript'] };
const glbs = list.split(',');
glbs.forEach((g, i) => (map[`/m${i}.glb`] = [g, 'model/gltf-binary']));
(async () => {
  const b = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
  const p = await b.newPage({ viewport: { width: 1280, height: 640 } });
  p.on('console', (m) => m.type() === 'error' && console.log('[page]', m.text()));
  await p.route('http://v.local/**', (route) => { const f = map[new URL(route.request().url()).pathname];
    if (!f) return route.fulfill({ status: 404 }); route.fulfill({ status: 200, contentType: f[1], body: fs.readFileSync(path.isAbsolute(f[0]) ? f[0] : path.join(__dirname, f[0])) }); });
  await p.goto(`http://v.local/model-view.html?yaw=${yaw}&elev=${elev}&files=${glbs.map((_, i) => `m${i}.glb`).join(',')}`);
  await p.waitForFunction(() => window.ready, null, { timeout: 300000 });
  console.log('ready', await p.evaluate(() => window.ready));
  await p.screenshot({ path: out }); await b.close();
})();
