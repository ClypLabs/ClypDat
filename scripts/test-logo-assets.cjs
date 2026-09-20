const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const app = path.resolve(__dirname, '..');
const web = path.resolve(app, '../clypdat-webapp');
const nextPackage = path.dirname(require.resolve('next/package.json', { paths: [web] }));
const sharp = require(require.resolve('sharp', { paths: [nextPackage] }));
const asset = name => fs.readFileSync(path.join(app, 'assets', name));

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
