const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const app = path.resolve(__dirname, '..');
const web = path.resolve(app, '../clypdat-webapp');
const nextPackage = path.dirname(require.resolve('next/package.json', { paths: [web] }));
const sharp = require(require.resolve('sharp', { paths: [nextPackage] }));
const asset = name => fs.readFileSync(path.join(app, 'assets', name));

test('master combines heavy white bands, visible tip gaps and connected black joins', async () => {
  const { data, info } = await sharp(asset('branding/clypdat-mark.png')).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  assert.deepEqual([info.width, info.height], [1254, 1254]);
  const whiteRuns = y => {
    const runs = [];
    let start = -1;
    for (let x = 0; x <= info.width; x++) {
      const i = (y * info.width + x) * 4;
      const white = x < info.width && data[i + 3] > 127 && Math.min(data[i], data[i + 1], data[i + 2]) > 220;
      if (white && start < 0) start = x;
      if (!white && start >= 0) { runs.push([start, x]); start = -1; }
    }
    return runs;
  };
  // Original master measured 104 / 96 / 101px at this midline. The rejected
  // narrow version measured 99 / 92 / 97px. Keep original weight within 3px.
  const bands = whiteRuns(600);
  assert.equal(bands.length, 3);
  bands.forEach(([start, end], i) => assert.ok(Math.abs(end - start - [104, 96, 101][i]) <= 3, 'White bands must retain their original weight'));
  for (const y of [380, 820]) {
    const runs = whiteRuns(y);
    assert.equal(runs.length, 2);
    const gap = runs[1][0] - runs[0][1];
    assert.ok(gap >= 45 && gap <= 70, 'Both white tips need a visible, modest gap');
  }
  // Flood the transparent exterior. Neither inner hole may be reachable;
  // otherwise a black bridge was cut while widening the white-to-white gap.
  const visited = new Uint8Array(info.width * info.height);
  const queue = new Int32Array(visited.length);
  let head = 0, tail = 1;
  visited[0] = 1;
  while (head < tail) {
    const p = queue[head++], x = p % info.width, y = Math.floor(p / info.width);
    for (const q of [x > 0 ? p - 1 : -1, x < info.width - 1 ? p + 1 : -1, y > 0 ? p - info.width : -1, y < info.height - 1 ? p + info.width : -1]) {
      if (q < 0 || visited[q] || data[q * 4 + 3] >= 128) continue;
      visited[q] = 1;
      queue[tail++] = q;
    }
  }
  for (const x of [350, 900]) {
    const p = 600 * info.width + x;
    assert.equal(data[p * 4 + 3], 0, 'Inner holes must remain transparent');
    assert.equal(visited[p], 0, 'Black joins must enclose both inner holes');
  }
});

// Two different jobs, two different treatments. The desktop icon is drawn on
// the user's taskbar and tray, where a transparent symbol reads as a floating
// shape, so it carries the Silver Edge tile. In-app marks sit on ClypDat's own
// surfaces, which already supply a background, so they stay unframed.
test('desktop icon carries the Silver Edge tile at every frame size', async () => {
  const ico = asset('clypdat-icon.ico');
  const sizes = [16, 24, 32, 48, 64, 128, 256];
  assert.equal(ico.readUInt16LE(4), sizes.length);
  const svg = asset('clypdat-logo.svg');
  for (let index = 0; index < sizes.length; index++) {
    const size = sizes[index];
    const offset = ico.readUInt32LE(6 + index * 16 + 12);
    const length = ico.readUInt32LE(6 + index * 16 + 8);
    const frame = ico.subarray(offset, offset + length);
    // Byte-identical to the framed render the web favicon is built from, so
    // the two icons cannot drift apart as the mark changes.
    const framed = await sharp(svg).resize(size, size).png({ compressionLevel: 9, adaptiveFiltering: true }).toBuffer();
    assert.ok(framed.equals(frame), `${size}px desktop icon must be the framed Silver Edge render`);
    const { data, info } = await sharp(frame).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    const centre = (Math.floor(info.height / 2) * info.width + Math.floor(info.width / 2)) * 4;
    assert.equal(data[centre + 3], 255, `${size}px desktop icon must be opaque where the tile sits`);
  }
  assert.ok(asset('clypdat-icon.ico').equals(fs.readFileSync(path.join(web, 'app/favicon.ico'))));
});

// Discord rounds Rich Presence art itself, at a radius that differs by
// surface. Silver to the canvas corners means its mask cuts the outer curve
// of the frame rather than slicing through a rounded stroke.
test('Discord assets keep their frame under Discord corner masks', async () => {
  for (const name of ['clypdat-discord.png', 'clypdat-classic-discord.png']) {
    const { data, info } = await sharp(asset(`branding/${name}`)).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    assert.deepEqual([info.width, info.height], [1024, 1024]);
    const pixel = (x, y) => Array.from(data.subarray((y * info.width + x) * 4, (y * info.width + x) * 4 + 4));
    const silver = ([r, g, b, a]) => a === 255 && Math.min(r, g, b) > 100;
    for (const [x, y] of [[0, 0], [1023, 0], [0, 1023], [1023, 1023]]) assert.ok(silver(pixel(x, y)), `${name} corner ${x},${y} must be opaque silver`);
    // A 25% mask meets the diagonal 75px in from each corner; the silver band
    // must still continue past it before the charcoal window starts.
    for (const [x, y] of [[85, 85], [938, 85], [85, 938], [938, 938]]) assert.ok(silver(pixel(x, y)), `${name} diagonal ${x},${y} must stay silver inside a 25% mask`);
    assert.deepEqual(pixel(512, 60), [0x17, 0x19, 0x1c, 255], `${name} charcoal window sits inside the frame`);
  }
});

test('classic Discord asset carries the hexagon mark', async () => {
  const { data, info } = await sharp(asset('branding/clypdat-classic-discord.png')).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  const pixel = (x, y) => Array.from(data.subarray((y * info.width + x) * 4, (y * info.width + x) * 4 + 4));
  // Outer ring top vertex, inner ring top vertex, the open centre, and the
  // halo gap between the two rings.
  const scale = 720 / 246;
  assert.deepEqual(pixel(512, Math.round(512 - 104 * scale)), [0xf5, 0xf8, 0xfc, 255]);
  assert.deepEqual(pixel(512, Math.round(512 - 53 * scale)), [0xf5, 0xf8, 0xfc, 255]);
  assert.deepEqual(pixel(512, 512), [0x17, 0x19, 0x1c, 255]);
  assert.deepEqual(pixel(Math.round(512 - 65.5 * scale), 512), [0x17, 0x19, 0x1c, 255]);
});

test('every in-app dark mark stays an unframed transparent symbol', async () => {
  for (const size of [16, 24, 32, 48, 64, 128, 256]) {
    const icon = asset(`clypdat-icon-${size}.png`);
    const { data, info } = await sharp(icon).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    assert.deepEqual([info.width, info.height], [size, size]);
    // Row 0 is above the mark's bounding box at every size: a tile or frame
    // would make it opaque.
    assert.equal(data[Math.floor(info.width / 2) * 4 + 3], 0, `${size}px in-app logo must not contain a tile or frame`);
  }
  assert.ok(asset('clypdat-icon.png').equals(asset('clypdat-icon-256.png')));
});

test('light-theme marks keep alpha and invert only RGB', async () => {
  for (const size of [24, 32, 256]) {
    const dark = await sharp(asset(`clypdat-icon-${size}.png`)).ensureAlpha().raw().toBuffer();
    const light = await sharp(asset(`clypdat-icon-${size}-light.png`)).ensureAlpha().raw().toBuffer();
    assert.equal(light.length, dark.length);
    for (let index = 0; index < dark.length; index++) {
      assert.equal(light[index], index % 4 === 3 ? dark[index] : 255 - dark[index]);
    }
  }
});

test('loader preserves the animated classic hexagon mark', () => {
  const loader = asset('clypdat-loader.svg').toString();
  assert.match(loader, /id="mark-outer"/);
  assert.match(loader, /id="mark-inner"/);
  assert.match(loader, /from="0 128 128" to="360 128 128"/);
  assert.match(loader, /from="360 128 128" to="0 128 128"/);

  const avaloniaLoader = fs.readFileSync(path.join(app, 'native/src/ClypDat.App/Controls/ClypDatLoader.axaml'), 'utf8');
  const avaloniaCode = fs.readFileSync(path.join(app, 'native/src/ClypDat.App/Controls/ClypDatLoader.axaml.cs'), 'utf8');
  assert.match(avaloniaLoader, /x:Name="MarkOuter"/);
  assert.match(avaloniaLoader, /x:Name="MarkInner"/);
  assert.doesNotMatch(avaloniaLoader, /AppLogoLarge/);
  assert.match(avaloniaCode, /StartRotation\(MarkOuter, TimeSpan\.FromSeconds\(6\), 0, TwoPi\)/);
  assert.match(avaloniaCode, /StartRotation\(MarkInner, TimeSpan\.FromSeconds\(4\.5\), TwoPi, 0\)/);
});
