// Package Silver Edge for web branding and the desktop icon (taskbar, tray,
// installer); every in-app mark stays unframed.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const app = path.resolve(__dirname, '..');
const web = path.resolve(app, '../clypdat-webapp');
const classicLoaderCommit = '7bdfbac8';
const nextPackage = path.dirname(require.resolve('next/package.json', { paths: [web] }));
const sharp = require(require.resolve('sharp', { paths: [nextPackage] }));
const markSource = path.join(app, 'assets/branding/clypdat-mark.png');
const sizes = [16, 24, 32, 48, 64, 128, 256];
const pngOptions = { compressionLevel: 9, adaptiveFiltering: true };

function ico(frames) {
  const header = Buffer.alloc(6 + frames.length * 16);
  header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  for (let i = 0; i < frames.length; i++) {
    const entry = 6 + i * 16;
    header[entry] = sizes[i] === 256 ? 0 : sizes[i]; header[entry + 1] = header[entry];
    header.writeUInt16LE(1, entry + 4); header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(frames[i].length, entry + 8); header.writeUInt32LE(offset, entry + 12);
    offset += frames[i].length;
  }
  return Buffer.concat([header, ...frames]);
}

// BIMI must remain path-only. Trace the approved white silhouette for this
// small email asset, with subpixel polygon simplification tolerance.
function traceWhite(data, width, height, whiteOnly = true) {
  const mask = new Uint8Array(width * height);
  for (let i = 0; i < mask.length; i++) mask[i] = data[i * 4 + 3] > 127 && (!whiteOnly || Math.min(data[i * 4], data[i * 4 + 1], data[i * 4 + 2]) > 127) ? 1 : 0;
  const edges = new Map();
  const stride = width + 1;
  const add = (x1, y1, x2, y2) => { const key = y1 * stride + x1; const list = edges.get(key) || []; list.push(y2 * stride + x2); edges.set(key, list); };
  const on = (x, y) => x >= 0 && x < width && y >= 0 && y < height && mask[y * width + x];
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) if (on(x, y)) {
    if (!on(x, y - 1)) add(x, y, x + 1, y);
    if (!on(x + 1, y)) add(x + 1, y, x + 1, y + 1);
    if (!on(x, y + 1)) add(x + 1, y + 1, x, y + 1);
    if (!on(x - 1, y)) add(x, y + 1, x, y);
  }
  const loops = [];
  while (edges.size) {
    const first = edges.keys().next().value; let current = first; const points = [];
    do {
      points.push([current % stride, Math.floor(current / stride)]);
      const next = edges.get(current); assert.ok(next?.length);
      const target = next.pop(); if (!next.length) edges.delete(current); current = target;
    } while (current !== first);
    if (points.length > 100) loops.push(points);
  }
  const distance = (p, a, b) => {
    const dx = b[0] - a[0], dy = b[1] - a[1];
    const t = Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / (dx * dx + dy * dy || 1)));
    return Math.hypot(p[0] - a[0] - t * dx, p[1] - a[1] - t * dy);
  };
  const simplify = points => {
    if (points.length < 3) return points;
    let worst = 0, index = 0;
    for (let i = 1; i < points.length - 1; i++) { const d = distance(points[i], points[0], points.at(-1)); if (d > worst) { worst = d; index = i; } }
    if (worst <= 1.25) return [points[0], points.at(-1)];
    return [...simplify(points.slice(0, index + 1)).slice(0, -1), ...simplify(points.slice(index))];
  };
  return loops.map(loop => { const halfway = Math.floor(loop.length / 2); return [...simplify(loop.slice(0, halfway + 1)).slice(0, -1), ...simplify([...loop.slice(halfway), loop[0]]).slice(0, -1)]; });
}

async function main() {
  // Export the checked-in master: heavy white bands with separated round tips.
  // Restoring a historical master here would silently erase the corrected gaps.
  const mark = fs.readFileSync(markSource);
  const { data, info } = await sharp(markSource).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  let left = info.width, top = info.height, right = 0, bottom = 0;
  for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++) if (data[(y * info.width + x) * 4 + 3] > 16) {
    left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y);
  }
  const crop = { left: Math.max(0, left - 2), top: Math.max(0, top - 2), width: Math.min(info.width, right + 3) - Math.max(0, left - 2), height: Math.min(info.height, bottom + 3) - Math.max(0, top - 2) };
  const cropped = await sharp(markSource).extract(crop).png(pngOptions).toBuffer();
  const framedSize = 488;
  const framedX = (256 - (left + right + 1) / 2 * framedSize / info.width).toFixed(6);
  const framedY = (256 - (top + bottom + 1) / 2 * framedSize / info.height).toFixed(6);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="768" height="768" viewBox="0 0 512 512"><title>ClypDat — Silver Edge</title><defs><linearGradient id="silver-edge" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#eef2f6"/><stop offset="1" stop-color="#7f8b97"/></linearGradient></defs><rect x="9" y="9" width="494" height="494" rx="83" fill="#17191c" stroke="url(#silver-edge)" stroke-width="18"/><image x="${framedX}" y="${framedY}" width="${framedSize}" height="${framedSize}" xlink:href="data:image/png;base64,${mark.toString('base64')}"/></svg>`;
  const avatar = await sharp(Buffer.from(svg)).png(pngOptions).toBuffer();
  fs.writeFileSync(path.join(app, 'assets/branding/clypdat-avatar.svg'), svg);
  fs.writeFileSync(path.join(app, 'assets/branding/clypdat-avatar.png'), avatar);
  assert.match(svg, /<linearGradient id="silver-edge"/);
  assert.match(svg, /stroke="url\(#silver-edge\)" stroke-width="18"/);
  assert.match(svg, /fill="#17191c"/);
  fs.writeFileSync(path.join(web, 'public/logo.png'), avatar);
  // Header uses a tight transparent crop. The square source master has generous
  // alpha padding intended for icon canvases, which reads as a tile at 18px.
  await sharp(cropped).resize({ width: 768 }).png(pngOptions).toFile(path.join(web, 'public/logo-mark.png'));
  fs.writeFileSync(path.join(app, 'assets/clypdat-logo.svg'), svg);
  fs.writeFileSync(path.join(web, 'public/logo.svg'), svg);

  const transparentFrames = [];
  const frames = [];
  for (const size of sizes) {
    const width = Math.round(size * 0.94);
    const resized = await sharp(cropped).resize({ width }).png().toBuffer();
    const meta = await sharp(resized).metadata();
    const icon = await sharp({ create: { width: size, height: size, channels: 4, background: '#00000000' } }).composite([{ input: resized, left: Math.floor((size - width) / 2), top: Math.floor((size - meta.height) / 2) }]).png(pngOptions).toBuffer();
    transparentFrames.push(icon);
    const themed = await sharp(Buffer.from(svg)).resize(size, size).png(pngOptions).toBuffer();
    frames.push(themed);
    fs.writeFileSync(path.join(app, `assets/clypdat-icon-${size}.png`), icon);
    if (size === 256) fs.writeFileSync(path.join(app, 'assets/clypdat-icon.png'), icon);
    if ([24, 32, 256].includes(size)) await sharp(icon).negate({ alpha: false }).png(pngOptions).toFile(path.join(app, `assets/clypdat-icon-${size}-light.png`));
  }
  // The desktop icon is framed: taskbar and tray render it against whatever
  // the user's taskbar colour is, where a transparent mark reads as a floating
  // shape rather than an application. In-app marks stay unframed - they sit on
  // ClypDat's own surfaces, which already supply the tile.
  const windowsIcon = ico(frames);
  fs.writeFileSync(path.join(app, 'assets/clypdat-icon.ico'), windowsIcon);
  fs.writeFileSync(path.join(web, 'app/favicon.ico'), windowsIcon);
  await sharp(Buffer.from(svg)).resize(512, 512).png(pngOptions).toFile(path.join(web, 'public/icon.png'));

  const contours = traceWhite(data, info.width, info.height);
  const markTag = svg.match(/<image\b[^>]*>/)[0];
  const attribute = name => Number(markTag.match(new RegExp(`\\b${name}="([^"]+)"`))[1]);
  const scale = attribute('width') / info.width / 2;
  const dx = attribute('x') / 2;
  const dy = attribute('y') / 2;
  const bimiPath = loops => loops.map(points => points.map(([x,y], i) => `${i ? 'L' : 'M'}${(x * scale + dx).toFixed(2)} ${(y * scale + dy).toFixed(2)}`).join('') + 'Z').join('');
  const outline = traceWhite(data, info.width, info.height, false);
  // Tiny PS permits no gradient URL references; retain a solid steel fallback.
  const bimi = `<svg xmlns="http://www.w3.org/2000/svg" version="1.2" baseProfile="tiny-ps" width="256" height="256" viewBox="0 0 256 256"><title>ClypDat</title><desc>Silver Edge logo</desc><rect width="256" height="256" fill="#17191c"/><rect x="2.25" y="2.25" width="251.5" height="251.5" rx="43.75" fill="#17191c" stroke="#9da7b1" stroke-width="9"/><path fill="#000000" fill-rule="evenodd" d="${bimiPath(outline)}"/><path fill="#FCFCFC" fill-rule="evenodd" d="${bimiPath(contours)}"/></svg>\n`;
  fs.writeFileSync(path.join(web, 'public/bimi/clypdat.svg'), bimi);
  assert.ok(Buffer.byteLength(bimi) < 32768);

  const loader = execFileSync('git', ['show', `${classicLoaderCommit}:assets/clypdat-loader.svg`], { cwd: app });
  assert.match(loader.toString(), /id="mark-outer"/);
  assert.match(loader.toString(), /id="mark-inner"/);
  fs.writeFileSync(path.join(app, 'assets/clypdat-loader.svg'), loader);
  // SVG renderers premultiply alpha; allow its one-level rounding only.
  for (const background of ['#000000', '#ffffff']) {
    const svgPixels = await sharp(Buffer.from(svg)).flatten({ background }).raw().toBuffer();
    const pngPixels = await sharp(avatar).flatten({ background }).raw().toBuffer();
    assert.equal(svgPixels.length, pngPixels.length);
    for (let i = 0; i < svgPixels.length; i++) assert.ok(Math.abs(svgPixels[i] - pngPixels[i]) <= 1, 'SVG wrapper must preserve the selected mark');
  }
  for (let i = 0; i < sizes.length; i++) {
    const start = windowsIcon.readUInt32LE(6 + i * 16 + 12);
    const length = windowsIcon.readUInt32LE(6 + i * 16 + 8);
    const meta = await sharp(windowsIcon.subarray(start, start + length)).metadata();
    assert.equal(meta.width, sizes[i]); assert.equal(meta.height, sizes[i]);
  }
  for (const size of [24, 32, 256]) {
    const dark = await sharp(path.join(app, `assets/clypdat-icon-${size}.png`)).ensureAlpha().raw().toBuffer();
    const light = await sharp(path.join(app, `assets/clypdat-icon-${size}-light.png`)).ensureAlpha().raw().toBuffer();
    for (let i = 0; i < dark.length; i++) assert.equal(light[i], i % 4 === 3 ? dark[i] : 255 - dark[i], 'Light-theme assets preserve alpha and invert RGB');
  }
  for (const frame of transparentFrames) {
    const { data, info } = await sharp(frame).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    assert.equal(data[Math.floor(info.width / 2) * 4 + 3], 0, 'Desktop icon must not have a tile background');
  }
  const watch = await require('./sync-logo-watch.cjs').syncLogoWatch();
  console.log(JSON.stringify({ style: 'Silver Edge', desktop: 'transparent', crop, icoSizes: sizes, bimiBytes: Buffer.byteLength(bimi), watch }, null, 2));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
