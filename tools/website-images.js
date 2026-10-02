// Makes website/img from the trailer project's stills: every screen at five widths in AVIF, WebP
// and JPEG (what index.html's <picture> sets name), the icons, and the social card. Run it again
// after `npm run stills` in trailer/ changes a screenshot.
//
//   cd trailer                       (gitignored, has its own .npmrc with os=win32)
//   npm install --no-save sharp
//   node ..\tools\website-images.js
//
// sharp is resolved from the folder you run it in, so nothing is installed at the repo root.
const path = require('path');
const fs = require('fs');
const sharp = require(require.resolve('sharp', { paths: [process.cwd(), __dirname] }));

const ROOT = path.resolve(__dirname, '..') + path.sep;
const SRC = ROOT + 'trailer/out/screens/';
const OUT = ROOT + 'website/img/';
fs.mkdirSync(OUT, { recursive: true });

// site key -> still
const SCREENS = {
  'lp-library': 'library',
  'lp-library-trailer': 'library-trailer',
  'lp-library-grid': 'library-grid',
  'lp-emulators': 'emulators',
  'lp-game-page': 'game-page',
  'lp-game-menu': 'game-menu',
  'lp-achievements': 'achievements',
  'lp-stats': 'stats',
  'lp-quick-menu': 'quick-menu',
  'lp-switch-window': 'switch-window',
  'lp-shortcuts': 'shortcuts',
  'lp-keyboard': 'keyboard',
};
// Keep in step with WIDTHS in website/site.js and the srcsets in website/index.html.
const WIDTHS = [480, 720, 1080, 1440, 1920];

async function screen(key, name) {
  const src = SRC + name + '.png';
  const meta = await sharp(src).metadata();
  if (meta.width !== 2560 || meta.height !== 1440) throw new Error(name + ' is ' + meta.width + 'x' + meta.height + ', expected 2560x1440');
  for (const w of WIDTHS) {
    const r = sharp(src).resize(w, Math.round(w * 9 / 16), { kernel: 'lanczos3' });
    await Promise.all([
      r.clone().avif({ quality: 52, effort: 5, chromaSubsampling: '4:2:0' }).toFile(OUT + key + '-' + w + '.avif'),
      r.clone().webp({ quality: 78, effort: 5 }).toFile(OUT + key + '-' + w + '.webp'),
      r.clone().jpeg({ quality: 80, mozjpeg: true, progressive: true }).toFile(OUT + key + '-' + w + '.jpg'),
    ]);
    process.stdout.write(key + ' ' + w + '\n');
  }
}

async function icons() {
  const icon = ROOT + 'website/design/assets/lp-icon.png';
  fs.copyFileSync(icon, OUT + 'lp-icon.png');
  await sharp(icon).resize(32, 32).png().toFile(OUT + 'icon-32.png');
  await sharp(icon).resize(180, 180).png().toFile(OUT + 'icon-180.png');
  await sharp(icon).resize(96, 96).png().toFile(OUT + 'icon-96.png');
  await sharp(icon).webp({ quality: 90, alphaQuality: 100 }).toFile(OUT + 'lp-icon.webp');
  fs.copyFileSync(ROOT + 'Loungepad/Loungepad.ico', ROOT + 'website/favicon.ico');
  // The social card: the trailer's poster, 1280x720 -> 1200x630 (22px off the top and bottom).
  await sharp(ROOT + 'trailer/out/stills/Poster.png')
    .resize(1200, 675).extract({ left: 0, top: 22, width: 1200, height: 630 })
    .jpeg({ quality: 86, mozjpeg: true, progressive: true }).toFile(OUT + 'og.jpg');
  process.stdout.write('icons + og.jpg\n');
}

(async () => {
  const t0 = Date.now();
  await icons();
  const queue = Object.keys(SCREENS);
  const worker = async () => { while (queue.length) { const k = queue.shift(); await screen(k, SCREENS[k]); } };
  await Promise.all([worker(), worker()]);
  const files = fs.readdirSync(OUT);
  const total = files.reduce((n, f) => n + fs.statSync(OUT + f).size, 0);
  console.log('done:', files.length, 'files,', (total / 1024 / 1024).toFixed(1), 'MB in', ((Date.now() - t0) / 1000).toFixed(0) + 's');
})().catch((e) => { console.error(e); process.exit(1); });
