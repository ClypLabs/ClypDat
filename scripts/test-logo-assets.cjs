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

test('every in-app dark mark matches the unframed transparent desktop symbol', async () => {
  const ico = asset('clypdat-icon.ico');
  const sizes = [16, 24, 32, 48, 64, 128, 256];
  assert.equal(ico.readUInt16LE(4), sizes.length);
  for (let index = 0; index < sizes.length; index++) {
    const size = sizes[index];
    const offset = ico.readUInt32LE(6 + index * 16 + 12);
    const length = ico.readUInt32LE(6 + index * 16 + 8);
    const icon = asset(`clypdat-icon-${size}.png`);
    assert.ok(icon.equals(ico.subarray(offset, offset + length)), `${size}px in-app logo must not contain a tile or frame`);
    const { data, info } = await sharp(icon).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
    assert.equal(data[Math.floor(info.width / 2) * 4 + 3], 0);
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
