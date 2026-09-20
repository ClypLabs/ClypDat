const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');

// Run either from the versioned template folder or the copied watch script.
const root = fs.existsSync(path.join(__dirname, 'index.html'))
  ? path.resolve(__dirname, '../../../../ClypDat-logo-thickness')
  : path.resolve(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const previewLoop = html.match(/for\(const el of document\.querySelectorAll\('#treatments \.card'\)\)\{[\s\S]*?check\.src=[^;]+;\s*\}/)?.[0];
assert.ok(previewLoop, 'Find the actual preview image-loading loop');
const treatments = JSON.parse(fs.readFileSync(path.join(root, 'experiments/gap-and-neutral/exports.json'), 'utf8'));
const app = path.resolve(root, '../ClypDat/clypdat-app');
const next = path.dirname(require.resolve('next/package.json', { paths: [path.resolve(app, '../clypdat-webapp')] }));
const sharp = require(require.resolve('sharp', { paths: [next] }));

test('all displayed sizes use high-resolution SVG previews, not 1x PNG exports', () => {
  const cards = treatments.map(({ id }) => {
    const images = [16, 24, 32, 48, 64, 184].map(size => ({
      dataset: {},
      parentElement: { style: { getPropertyValue: () => `${size}px` } },
    }));
    return { dataset: { style: id }, images, querySelectorAll: () => images, querySelector: () => true };
  });
  class AssetProbe {
    set src(value) {
      assert.ok(fs.existsSync(path.join(root, value.split('?')[0])), `Preview asset exists: ${value}`);
      this.onload();
    }
  }
  vm.runInNewContext(previewLoop, { stamp: 123, Image: AssetProbe, document: { querySelectorAll: () => cards } });
  for (const card of cards) {
    const expected = `current/previews/svg/${card.dataset.style}.svg?v=123`;
    for (const img of card.images) {
      assert.equal(img.src, expected);
      assert.equal(img.parentElement.className, 'icon bare', 'Do not add another CSS border over the export');
    }
    const svg = fs.readFileSync(path.join(root, expected.split('?')[0]), 'utf8');
    const mark = Buffer.from(svg.match(/xlink:href="data:image\/png;base64,([^"]+)"/)[1], 'base64');
    assert.ok(mark.readUInt32BE(16) >= 184 * 4, 'Embedded mark supports even the large preview at 4x scale');
    assert.ok(mark.equals(fs.readFileSync(path.join(app, 'assets/branding/clypdat-mark.png'))), 'Displayed previews must match the current app master');
  }
});

test('inline workbench JavaScript parses', () => {
  for (const script of html.matchAll(/<script>([\s\S]*?)<\/script>/g)) new vm.Script(script[1]);
});

test('visible logo bounds are centered inside every frame', async () => {
  const master = path.join(root, 'current/clypdat-mark.png');
  const { data, info } = await sharp(master).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  let left = info.width, top = info.height, right = -1, bottom = -1;
  for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++) {
    if (data[(y * info.width + x) * 4 + 3] < 128) continue;
    left = Math.min(left, x); right = Math.max(right, x);
    top = Math.min(top, y); bottom = Math.max(bottom, y);
  }
  assert.ok(right >= left && bottom >= top, 'Master contains visible artwork');
  for (const { id } of treatments) {
    const svg = fs.readFileSync(path.join(root, `current/previews/svg/${id}.svg`), 'utf8');
    const tag = svg.match(/<image\b[^>]*>/)[0];
    const attr = name => Number(tag.match(new RegExp(`\\b${name}="([^"]+)"`))[1]);
    const scaleX = attr('width') / info.width, scaleY = attr('height') / info.height;
    const centerX = attr('x') + (left + right + 1) / 2 * scaleX;
    const centerY = attr('y') + (top + bottom + 1) / 2 * scaleY;
    assert.ok(Math.abs(centerX - 256) < 0.00001, `${id}: horizontal artwork center is ${centerX}, expected 256`);
    assert.ok(Math.abs(centerY - 256) < 0.00001, `${id}: vertical artwork center is ${centerY}, expected 256`);
    assert.ok(Math.abs(scaleX - scaleY) < 0.00001, `${id}: preserve logo proportions`);
    assert.ok(attr('x') + left * scaleX >= 0 && attr('x') + (right + 1) * scaleX <= 512);
    assert.ok(attr('y') + top * scaleY >= 0 && attr('y') + (bottom + 1) * scaleY <= 512);
  }
});

test('comparison sample rows are separated from main icons and stay inside cards', () => {
  const sheet = fs.readFileSync(path.join(root, 'experiments/gap-and-neutral/comparison.svg'), 'utf8');
  const cards = [...sheet.matchAll(/<g data-card="([^"]+)">([\s\S]*?)<\/g>/g)];
  assert.equal(cards.length, treatments.length + 1);
  const attr = (tag, name) => Number(tag.match(new RegExp(`\\b${name}="([^"]+)"`))[1]);
  for (const [, id, body] of cards) {
    const card = body.match(/<rect data-role="card"[^>]*>/)[0];
    const main = body.match(/<use data-role="main"[^>]*>/)[0];
    const samples = [...body.matchAll(/<use data-role="sample"[^>]*>/g)].map(match => match[0]);
    assert.equal(samples.length, 5);
    let previousRight = -Infinity;
    for (const sample of samples) {
      assert.ok(attr(sample, 'y') - (attr(main, 'y') + attr(main, 'height')) >= 24, `${id}: sample overlaps main icon`);
      assert.ok(attr(sample, 'x') >= previousRight + 10, `${id}: sample icons overlap`);
      assert.ok(attr(sample, 'x') >= attr(card, 'x'));
      assert.ok(attr(sample, 'x') + attr(sample, 'width') <= attr(card, 'x') + attr(card, 'width'));
      assert.ok(attr(sample, 'y') + attr(sample, 'height') + 24 <= attr(card, 'y') + attr(card, 'height'));
      assert.ok(sample.includes(`xlink:href="#icon-${id}"`), 'Samples reuse the vector symbol, not tiny PNGs');
      previousRight = attr(sample, 'x') + attr(sample, 'width');
    }
  }
});

test('comparison PNG is rendered at 3x the SVG layout resolution', async () => {
  const folder = path.join(root, 'experiments/gap-and-neutral');
  const sheet = fs.readFileSync(path.join(folder, 'comparison.svg'), 'utf8');
  const [, width, height] = sheet.match(/viewBox="0 0 (\d+) (\d+)"/);
  const png = await sharp(path.join(folder, 'comparison.png')).metadata();
  assert.equal(png.width, Number(width) * 3);
  assert.equal(png.height, Number(height) * 3);
  assert.ok(sheet.includes('Labels denote layout sizes'));
});
