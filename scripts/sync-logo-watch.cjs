// Refresh the optional local watch from the same masters used by the app.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const app = path.resolve(__dirname, '..');
const watch = path.resolve(app, '../../ClypDat-logo-thickness');

async function syncLogoWatch() {
  if (!fs.existsSync(path.join(watch, 'index.html'))) return null;
  const next = path.dirname(require.resolve('next/package.json', { paths: [path.resolve(app, '../clypdat-webapp')] }));
  const sharp = require(require.resolve('sharp', { paths: [next] }));
  const master = fs.readFileSync(path.join(app, 'assets/branding/clypdat-mark.png'));
  const { data, info } = await sharp(master).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  let left = info.width, top = info.height, right = -1, bottom = -1;
  for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++) {
    if (data[(y * info.width + x) * 4 + 3] < 128) continue;
    left = Math.min(left, x); right = Math.max(right, x);
    top = Math.min(top, y); bottom = Math.max(bottom, y);
  }
  assert.ok(right >= left && bottom >= top, 'Master must contain visible artwork');
  const output = path.join(watch, 'current/previews');
  for (const folder of [output, path.join(output, 'svg'), path.join(output, 'sizes'), path.join(watch, 'exports')]) {
    fs.mkdirSync(folder, { recursive: true });
  }
  // Reuse the existing vector border treatments, preserving the study itself.
  const frames = path.join(watch, 'experiments/gap-and-neutral/svg');
  const files = fs.readdirSync(frames).filter(name => /^[a-z-]+\.svg$/.test(name));
  for (const file of files) {
    const template = fs.readFileSync(path.join(frames, file), 'utf8');
    const tag = template.match(/<image\b[^>]*>/)?.[0];
    assert.ok(tag, `Missing embedded mark in ${file}`);
    const width = Number(tag.match(/\bwidth="([^"]+)"/)[1]);
    const height = Number(tag.match(/\bheight="([^"]+)"/)[1]);
    const x = (256 - (left + right + 1) / 2 * width / info.width).toFixed(6);
    const y = (256 - (top + bottom + 1) / 2 * height / info.height).toFixed(6);
    const replacement = `<image x="${x}" y="${y}" width="${width}" height="${height}" xlink:href="data:image/png;base64,${master.toString('base64')}"/>`;
    const svg = template.replace(tag, replacement);
    fs.writeFileSync(path.join(output, 'svg', file), svg);
    const name = path.basename(file, '.svg');
    await sharp(Buffer.from(svg)).png().toFile(path.join(output, `${name}.png`));
    for (const size of [16, 24, 32, 48, 64, 128, 256]) {
      await sharp(Buffer.from(svg)).resize(size, size).png().toFile(path.join(output, 'sizes', `${name}-${size}.png`));
    }
  }
  for (const file of ['clypdat-mark.png', 'clypdat-avatar.svg', 'clypdat-avatar.png']) {
    fs.copyFileSync(path.join(app, 'assets/branding', file), path.join(watch, 'current', file));
  }
  let githubExport;
  for (const size of [768, 640, 512]) {
    const png = await sharp(path.join(app, 'assets/branding/clypdat-avatar.svg')).resize(size, size).png({ compressionLevel: 9, adaptiveFiltering: true }).toBuffer();
    if (png.length < 1000000) { githubExport = png; break; }
  }
  assert.ok(githubExport, 'GitHub avatar must fit below 1 MB');
  fs.writeFileSync(path.join(watch, 'exports/clypdat-avatar-github.png'), githubExport);
  // Keep the served page and its checks versioned with the exporter.
  for (const file of ['index.html', 'README.md', 'test-workbench-preview.cjs']) {
    const target = file.endsWith('.cjs') ? path.join(watch, 'scripts', file) : path.join(watch, file);
    fs.copyFileSync(path.join(__dirname, 'logo-watch', file), target);
  }
  return { watch, treatments: files.length };
}

module.exports = { syncLogoWatch };
if (require.main === module) syncLogoWatch().then(result => console.log(result ?? 'Local icon watch not present; skipped.')).catch(error => { console.error(error); process.exitCode = 1; });
