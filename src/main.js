const { app, BrowserWindow, desktopCapturer, dialog, globalShortcut, ipcMain, session, shell } = require('electron');
const { spawn } = require('child_process');
const path = require('path');
const { pathToFileURL } = require('url');
const fsSync = require('fs');
const fs = require('fs/promises');

let mainWindow;
let currentExport = null;
const replayState = {
  process: null,
  starting: false,
  stopping: false,
  saving: false,
  segmentDir: '',
  settings: {
    enabled: false,
    durationSeconds: 60,
    segmentSeconds: 5,
    framerate: 60,
    hotkey: 'CommandOrControl+Shift+F9'
  }
};
let libraryIndexCache = {
  folder: '',
  files: [],
  promise: null
};
const libraryClipCache = new Map();

const cacheRoot = app.isPackaged
  ? path.join(app.getPath('appData'), 'EVE')
  : path.join(__dirname, '..', '.cache');
const windowIconPath = path.join(__dirname, '..', 'assets', 'eve-icon.png');
const appDataPath = path.join(cacheRoot, 'user-data');
const sessionDataPath = path.join(cacheRoot, 'session-data');
const diskCachePath = path.join(cacheRoot, 'disk-cache');
const settingsPath = path.join(cacheRoot, 'settings.json');
const videoExtensions = new Set(['.mkv', '.mp4', '.mov', '.webm']);

for (const dir of [cacheRoot, appDataPath, sessionDataPath, diskCachePath]) {
  fsSync.mkdirSync(dir, { recursive: true });
}

app.setPath('userData', appDataPath);
app.setPath('sessionData', sessionDataPath);
app.setAppUserModelId('EVE');
app.commandLine.appendSwitch('no-sandbox');
app.commandLine.appendSwitch('disk-cache-dir', diskCachePath);
app.commandLine.appendSwitch('disable-gpu-shader-disk-cache');

function readSettingsSync() {
  try {
    return JSON.parse(fsSync.readFileSync(settingsPath, 'utf8'));
  } catch {
    return { libraryFolder: '' };
  }
}

function getSavedWindowBounds() {
  const bounds = readSettingsSync().windowBounds;
  if (!bounds) return {};
  const width = Number(bounds.width);
  const height = Number(bounds.height);
  const x = Number(bounds.x);
  const y = Number(bounds.y);

  if (!Number.isFinite(width) || !Number.isFinite(height)) return {};
  return {
    width: Math.max(920, Math.round(width)),
    height: Math.max(620, Math.round(height)),
    ...(Number.isFinite(x) && Number.isFinite(y) ? { x: Math.round(x), y: Math.round(y) } : {})
  };
}

function saveWindowBoundsSync() {
  if (!mainWindow || mainWindow.isDestroyed() || mainWindow.isMinimized()) return;
  const settings = readSettingsSync();
  fsSync.mkdirSync(cacheRoot, { recursive: true });
  fsSync.writeFileSync(settingsPath, JSON.stringify({
    ...settings,
    windowBounds: mainWindow.getBounds(),
    windowMaximized: mainWindow.isMaximized()
  }, null, 2));
}

function createWindow() {
  session.defaultSession.setDisplayMediaRequestHandler((_request, callback) => {
    desktopCapturer.getSources({ types: ['screen'] })
      .then((sources) => callback({ video: sources[0] }))
      .catch(() => callback({}));
  }, { useSystemPicker: false });

  const savedBounds = getSavedWindowBounds();
  mainWindow = new BrowserWindow({
    width: savedBounds.width || 1180,
    height: savedBounds.height || 780,
    ...(Number.isFinite(savedBounds.x) && Number.isFinite(savedBounds.y) ? { x: savedBounds.x, y: savedBounds.y } : {}),
    minWidth: 920,
    minHeight: 620,
    title: 'EVE - Easy Video Editor',
    icon: windowIconPath,
    backgroundColor: '#101418',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  });

  mainWindow.setMenuBarVisibility(false);
  mainWindow.setAutoHideMenuBar(true);
  if (readSettingsSync().windowMaximized) mainWindow.maximize();
  mainWindow.on('close', saveWindowBoundsSync);
  mainWindow.loadFile(path.join(__dirname, 'renderer.html'));
}

app.whenReady().then(() => {
  createWindow();
  registerReplayHotkey();
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});

app.on('will-quit', () => {
  globalShortcut.unregisterAll();
  if (replayState.process && !replayState.process.killed) replayState.process.kill('SIGTERM');
});

function runProcess(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { windowsHide: true });
    let stdout = '';
    let stderr = '';

    child.stdout.on('data', (data) => {
      stdout += data.toString();
    });

    child.stderr.on('data', (data) => {
      stderr += data.toString();
    });

    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) resolve({ stdout, stderr });
      else reject(new Error(stderr || `${command} exited with code ${code}`));
    });
  });
}

function runBufferProcess(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { windowsHide: true });
    const stdout = [];
    let stderr = '';

    child.stdout.on('data', (data) => {
      stdout.push(data);
    });

    child.stderr.on('data', (data) => {
      stderr += data.toString();
    });

    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) resolve(Buffer.concat(stdout));
      else reject(new Error(stderr || `${command} exited with code ${code}`));
    });
  });
}

function secondsFromDuration(duration) {
  const parsed = Number.parseFloat(duration);
  return Number.isFinite(parsed) ? parsed : 0;
}

async function readSettings() {
  try {
    return JSON.parse(await fs.readFile(settingsPath, 'utf8'));
  } catch {
    return { libraryFolder: '' };
  }
}

async function writeSettings(settings) {
  await fs.mkdir(cacheRoot, { recursive: true });
  await fs.writeFile(settingsPath, JSON.stringify(settings, null, 2));
  return settings;
}

function replaySettingsFrom(settings = {}) {
  return {
    ...replayState.settings,
    ...(settings.replay || {})
  };
}

function sendReplayStatus(extra = {}) {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.webContents.send('replay:status', getReplayStatus(extra));
}

function getReplayStatus(extra = {}) {
  return {
    enabled: Boolean(replayState.process),
    starting: replayState.starting,
    saving: replayState.saving,
    hotkey: replayState.settings.hotkey,
    durationSeconds: replayState.settings.durationSeconds,
    ...extra
  };
}

function getReplayOutputFolder(settings = readSettingsSync()) {
  return settings.libraryFolder || path.join(app.getPath('videos'), 'EVE Clips');
}

async function clearReplaySegments() {
  if (!replayState.segmentDir) return;
  await fs.rm(replayState.segmentDir, { recursive: true, force: true });
  await fs.mkdir(replayState.segmentDir, { recursive: true });
}

function replaySegmentCount(settings) {
  return Math.max(3, Math.ceil(settings.durationSeconds / settings.segmentSeconds) + 2);
}

async function startReplayBuffer() {
  if (replayState.process || replayState.starting) return getReplayStatus();

  replayState.starting = true;
  const settingsFile = await readSettings();
  replayState.settings = replaySettingsFrom(settingsFile);
  replayState.segmentDir = path.join(cacheRoot, 'replay-buffer');
  await clearReplaySegments();

  const segmentPattern = path.join(replayState.segmentDir, 'replay-%03d.mp4');
  const args = [
    '-hide_banner',
    '-loglevel',
    'warning',
    '-f',
    'gdigrab',
    '-framerate',
    String(replayState.settings.framerate),
    '-i',
    'desktop',
    '-an',
    '-c:v',
    'libx264',
    '-preset',
    'veryfast',
    '-tune',
    'zerolatency',
    '-pix_fmt',
    'yuv420p',
    '-force_key_frames',
    `expr:gte(t,n_forced*${replayState.settings.segmentSeconds})`,
    '-f',
    'segment',
    '-segment_time',
    String(replayState.settings.segmentSeconds),
    '-segment_wrap',
    String(replaySegmentCount(replayState.settings)),
    '-reset_timestamps',
    '1',
    segmentPattern
  ];

  return new Promise((resolve, reject) => {
    const child = spawn('ffmpeg', args, { windowsHide: true });
    let stderr = '';
    let settled = false;
    replayState.process = child;

    const settle = (error) => {
      if (settled) return;
      settled = true;
      replayState.starting = false;
      sendReplayStatus(error ? { error: error.message } : {});
      if (error) reject(error);
      else resolve(getReplayStatus());
    };

    child.stderr.on('data', (data) => {
      stderr += data.toString();
    });

    child.on('error', (error) => {
      replayState.process = null;
      settle(error);
    });

    child.on('close', (code) => {
      replayState.process = null;
      replayState.starting = false;
      const wasStopping = replayState.stopping;
      replayState.stopping = false;
      sendReplayStatus(code === 0 || wasStopping ? {} : { error: stderr || `Replay recorder exited with code ${code}` });
    });

    setTimeout(() => settle(), 1200);
  });
}

async function stopReplayBuffer() {
  const child = replayState.process;
  replayState.process = null;
  replayState.starting = false;
  replayState.stopping = Boolean(child);
  if (child && !child.killed) child.kill('SIGTERM');
  sendReplayStatus();
  return getReplayStatus();
}

async function listReplaySegments() {
  if (!replayState.segmentDir) return [];
  const entries = await fs.readdir(replayState.segmentDir);
  const segments = await Promise.all(
    entries
      .filter((entry) => entry.toLowerCase().endsWith('.mp4'))
      .map(async (entry) => {
        const fullPath = path.join(replayState.segmentDir, entry);
        const stat = await fs.stat(fullPath);
        return { path: fullPath, mtimeMs: stat.mtimeMs, size: stat.size };
      })
  );

  return segments
    .filter((segment) => segment.size > 0)
    .sort((a, b) => a.mtimeMs - b.mtimeMs);
}

async function saveReplayClip() {
  if (!replayState.process) throw new Error('Replay buffer is not recording.');
  if (replayState.saving) throw new Error('A replay is already being saved.');

  replayState.saving = true;
  sendReplayStatus();

  try {
    const settings = await readSettings();
    const outputFolder = getReplayOutputFolder(settings);
    await fs.mkdir(outputFolder, { recursive: true });

    const segments = await listReplaySegments();
    const count = Math.max(1, Math.ceil(replayState.settings.durationSeconds / replayState.settings.segmentSeconds));
    const stableSegments = segments.length > 1 ? segments.slice(0, -1) : segments;
    const selected = stableSegments.slice(-count);
    if (selected.length === 0) throw new Error('Replay buffer is still warming up.');

    const listPath = path.join(replayState.segmentDir, 'concat.txt');
    const listContent = selected
      .map((segment) => `file '${segment.path.replace(/'/g, "'\\''")}'`)
      .join('\n');
    await fs.writeFile(listPath, listContent);

    const stamp = new Date().toISOString().replace(/[:T]/g, '-').replace(/\..+$/, '');
    const outputPath = path.join(outputFolder, `EVE Replay ${stamp}.mp4`);
    await runProcess('ffmpeg', [
      '-y',
      '-hide_banner',
      '-loglevel',
      'error',
      '-f',
      'concat',
      '-safe',
      '0',
      '-i',
      listPath,
      '-c',
      'copy',
      outputPath
    ]);

    invalidateLibraryCache(outputFolder);
    sendReplayStatus({ savedPath: outputPath });
    return { path: outputPath };
  } finally {
    replayState.saving = false;
    sendReplayStatus();
  }
}

function registerReplayHotkey() {
  globalShortcut.unregisterAll();
  replayState.settings = replaySettingsFrom(readSettingsSync());
  globalShortcut.register(replayState.settings.hotkey, () => {
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send('replay:save-request');
    }
  });
}

async function getVideoDuration(filePath) {
  try {
    const probe = await runProcess('ffprobe', [
      '-v',
      'quiet',
      '-print_format',
      'json',
      '-show_format',
      filePath
    ]);
    return secondsFromDuration(JSON.parse(probe.stdout).format?.duration);
  } catch {
    return 0;
  }
}

async function walkVideos(folderPath) {
  const entries = await fs.readdir(folderPath, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const fullPath = path.join(folderPath, entry.name);
    if (entry.isDirectory()) {
      files.push(...await walkVideos(fullPath));
    } else if (entry.isFile() && videoExtensions.has(path.extname(entry.name).toLowerCase())) {
      files.push(fullPath);
    }
  }

  return files;
}

async function scanClip(filePath) {
  const stat = await fs.stat(filePath);
  const cacheKey = `${filePath}:${stat.size}:${stat.mtimeMs}`;
  const cached = libraryClipCache.get(cacheKey);
  if (cached) return cached;

  for (const key of libraryClipCache.keys()) {
    if (key.startsWith(`${filePath}:`)) libraryClipCache.delete(key);
  }

  const clip = {
    path: filePath,
    previewUrl: pathToFileURL(filePath).toString(),
    name: path.basename(filePath),
    size: stat.size,
    createdAt: stat.birthtime?.toISOString?.() || stat.ctime?.toISOString?.(),
    duration: 0
  };
  libraryClipCache.set(cacheKey, clip);
  return clip;
}

async function buildLibraryIndex(folderPath) {
  const files = await walkVideos(folderPath);
  return (await Promise.all(
    files.map(async (filePath) => {
      try {
        const stat = await fs.stat(filePath);
        return {
          path: filePath,
          createdAt: stat.birthtime?.toISOString?.() || stat.ctime?.toISOString?.()
        };
      } catch {
        return null;
      }
    })
  )).filter(Boolean).sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
}

async function getLibraryIndex(folderPath, force = false) {
  if (!force && libraryIndexCache.folder === folderPath && libraryIndexCache.files.length > 0) {
    return libraryIndexCache.files;
  }

  if (!force && libraryIndexCache.folder === folderPath && libraryIndexCache.promise) {
    return libraryIndexCache.promise;
  }

  libraryIndexCache.folder = folderPath;
  libraryIndexCache.promise = buildLibraryIndex(folderPath);
  try {
    libraryIndexCache.files = await libraryIndexCache.promise;
    return libraryIndexCache.files;
  } finally {
    libraryIndexCache.promise = null;
  }
}

function invalidateLibraryCache(folderPath = '') {
  libraryIndexCache = {
    folder: folderPath,
    files: [],
    promise: null
  };
}

async function createPreviewAudio(inputPath, audioTracks) {
  if (audioTracks.length === 0) return null;

  await fs.mkdir(appDataPath, { recursive: true });
  const parsed = path.parse(inputPath);
  const outputPath = path.join(
    appDataPath,
    `${parsed.name.replace(/[^a-z0-9_-]/gi, '_')}-${Date.now()}-preview.m4a`
  );
  const inputs = audioTracks.map((track) => `[0:${track.index}]`).join('');

  await runProcess('ffmpeg', [
    '-y',
    '-i',
    inputPath,
    '-filter_complex',
    `${inputs}amix=inputs=${audioTracks.length}:duration=longest:normalize=0[a]`,
    '-map',
    '[a]',
    '-vn',
    '-c:a',
    'aac',
    '-b:a',
    '192k',
    outputPath
  ]);

  return pathToFileURL(outputPath).toString();
}

async function createPreviewAudioTracks(inputPath, audioTracks) {
  if (audioTracks.length === 0) return [];

  await fs.mkdir(appDataPath, { recursive: true });
  const parsed = path.parse(inputPath);
  const safeName = parsed.name.replace(/[^a-z0-9_-]/gi, '_');

  return Promise.all(
    audioTracks.map(async (track, index) => {
      const outputPath = path.join(appDataPath, `${safeName}-${Date.now()}-track-${index + 1}.m4a`);
      await runProcess('ffmpeg', [
        '-y',
        '-i',
        inputPath,
        '-map',
        `0:${track.index}`,
        '-vn',
        '-c:a',
        'aac',
        '-b:a',
        '192k',
        outputPath
      ]);

      return {
        index: track.index,
        previewUrl: pathToFileURL(outputPath).toString()
      };
    })
  );
}

async function getAudioWaveform(inputPath, track, samples = 420) {
  const pcm = await runBufferProcess('ffmpeg', [
    '-hide_banner',
    '-v',
    'error',
    '-i',
    inputPath,
    '-map',
    `0:${track.index}`,
    '-ac',
    '1',
    '-ar',
    '8000',
    '-f',
    'f32le',
    'pipe:1'
  ]);

  const sampleCount = Math.floor(pcm.length / 4);
  if (sampleCount === 0) return [];

  const bucketSize = Math.max(1, Math.ceil(sampleCount / samples));
  const buckets = [];

  for (let index = 0; index < sampleCount; index += bucketSize) {
    const end = Math.min(sampleCount, index + bucketSize);
    let peak = 0;
    let sum = 0;

    for (let sampleIndex = index; sampleIndex < end; sampleIndex += 1) {
      const value = Math.abs(pcm.readFloatLE(sampleIndex * 4));
      if (value > peak) peak = value;
      sum += value;
    }

    const average = sum / (end - index);
    const shaped = Math.max(peak * 0.8, average * 2.8);
    buckets.push(shaped < 0.003 ? 0 : Math.min(1, shaped));
  }

  return buckets;
}

async function getAudioWaveforms(inputPath, audioTracks) {
  return Promise.all(
    audioTracks.map(async (track) => ({
      index: track.index,
      peaks: await getAudioWaveform(inputPath, track)
    }))
  );
}

async function loadMedia(filePath, options = {}) {
  const stat = await fs.stat(filePath);
  const probe = await runProcess('ffprobe', [
    '-v',
    'quiet',
    '-print_format',
    'json',
    '-show_format',
    '-show_streams',
    filePath
  ]);

  const metadata = JSON.parse(probe.stdout);
  const streams = metadata.streams || [];
  const audioTracks = streams.filter((stream) => stream.codec_type === 'audio');
  let previewAudioUrl = null;
  let previewAudioTracks = [];
  let waveforms = [];

  if (!options.fast) {
    try {
      previewAudioTracks = await createPreviewAudioTracks(filePath, audioTracks);
    } catch (error) {
      console.error('Failed to create preview audio:', error);
    }
  }

  try {
    waveforms = await getAudioWaveforms(filePath, audioTracks);
  } catch (error) {
    console.error('Failed to read audio waveforms:', error);
  }

  return {
    path: filePath,
    previewUrl: pathToFileURL(filePath).toString(),
    name: path.basename(filePath),
    size: stat.size,
    createdAt: stat.birthtime?.toISOString?.() || stat.ctime?.toISOString?.(),
    duration: secondsFromDuration(metadata.format?.duration),
    videoTracks: streams.filter((stream) => stream.codec_type === 'video'),
    audioTracks,
    subtitleTracks: streams.filter((stream) => stream.codec_type === 'subtitle'),
    previewAudioUrl,
    previewAudioTracks,
    waveforms
  };
}

async function loadMediaExtras(filePath) {
  const probe = await runProcess('ffprobe', [
    '-v',
    'quiet',
    '-print_format',
    'json',
    '-show_streams',
    filePath
  ]);
  const streams = JSON.parse(probe.stdout).streams || [];
  const audioTracks = streams.filter((stream) => stream.codec_type === 'audio');
  let previewAudioTracks = [];
  let waveforms = [];

  try {
    previewAudioTracks = await createPreviewAudioTracks(filePath, audioTracks);
  } catch (error) {
    console.error('Failed to create preview audio:', error);
  }

  try {
    waveforms = await getAudioWaveforms(filePath, audioTracks);
  } catch (error) {
    console.error('Failed to read audio waveforms:', error);
  }

  return { previewAudioTracks, waveforms };
}

ipcMain.handle('file:open', async () => {
  const result = await dialog.showOpenDialog(mainWindow, {
    title: 'Open MKV file',
    filters: [
      { name: 'Matroska Video', extensions: ['mkv'] },
      { name: 'Video files', extensions: ['mkv', 'mp4', 'mov', 'webm'] }
    ],
    properties: ['openFile']
  });

  if (result.canceled || result.filePaths.length === 0) return null;

  return loadMedia(result.filePaths[0]);
});

ipcMain.handle('file:openPath', async (_, filePath) => loadMedia(filePath, { fast: true }));
ipcMain.handle('file:loadExtras', async (_, filePath) => loadMediaExtras(filePath));

ipcMain.handle('settings:get', readSettings);

ipcMain.handle('settings:chooseLibraryFolder', async () => {
  const result = await dialog.showOpenDialog(mainWindow, {
    title: 'Choose clips folder',
    properties: ['openDirectory']
  });
  if (result.canceled || result.filePaths.length === 0) return readSettings();
  invalidateLibraryCache(result.filePaths[0]);
  return writeSettings({ ...(await readSettings()), libraryFolder: result.filePaths[0] });
});

ipcMain.handle('settings:showLibraryFolder', async () => {
  const settings = await readSettings();
  if (!settings.libraryFolder) return false;
  const error = await shell.openPath(settings.libraryFolder);
  if (error) throw new Error(error);
  return true;
});

ipcMain.handle('library:scan', async (_, page = {}) => {
  const settings = await readSettings();
  if (!settings.libraryFolder) return { clips: [], total: 0 };
  const offset = Math.max(0, Number(page.offset) || 0);
  const limit = Math.min(100, Math.max(1, Number(page.limit) || 45));
  const fileStats = await getLibraryIndex(settings.libraryFolder, offset === 0);
  const slice = fileStats.slice(offset, offset + limit);
  const clips = (await Promise.all(
    slice.map(async (file) => {
      try {
        return await scanClip(file.path);
      } catch (error) {
        console.error('Failed to scan clip:', file.path, error);
        return null;
      }
    })
  )).filter(Boolean);
  return { clips, total: fileStats.length };
});

ipcMain.handle('library:deleteClips', async (_, paths = []) => {
  const uniquePaths = [...new Set(paths)].filter(Boolean);
  const deleted = [];
  const failed = [];

  for (const filePath of uniquePaths) {
    try {
      await fs.rm(filePath, { force: true });
      deleted.push(filePath);
    } catch (error) {
      failed.push({ path: filePath, message: error.message });
    }
  }

  if (deleted.length > 0) {
    const deletedPaths = new Set(deleted);
    libraryIndexCache.files = libraryIndexCache.files.filter((file) => !deletedPaths.has(file.path));
    for (const key of libraryClipCache.keys()) {
      if ([...deletedPaths].some((filePath) => key.startsWith(`${filePath}:`))) {
        libraryClipCache.delete(key);
      }
    }
  }

  return { deleted, failed };
});

ipcMain.handle('replay:status', () => getReplayStatus());
ipcMain.handle('replay:start', startReplayBuffer);
ipcMain.handle('replay:stop', stopReplayBuffer);
ipcMain.handle('replay:save', saveReplayClip);
ipcMain.handle('replay:desktopSource', async () => {
  const sources = await desktopCapturer.getSources({
    types: ['screen'],
    thumbnailSize: { width: 1, height: 1 }
  });
  const source = sources[0];
  if (!source) throw new Error('No desktop capture source found.');
  return { id: source.id, name: source.name };
});
ipcMain.handle('replay:writeClip', async (_, data) => {
  const settings = await readSettings();
  const outputFolder = getReplayOutputFolder(settings);
  await fs.mkdir(outputFolder, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:T]/g, '-').replace(/\..+$/, '');
  const outputPath = path.join(outputFolder, `EVE Replay ${stamp}.webm`);
  await fs.writeFile(outputPath, Buffer.from(data));
  invalidateLibraryCache(outputFolder);
  return { path: outputPath };
});

ipcMain.handle('file:saveExport', async (_, defaultName) => {
  const result = await dialog.showSaveDialog(mainWindow, {
    title: 'Export MP4',
    defaultPath: defaultName,
    filters: [{ name: 'MP4 video', extensions: ['mp4'] }]
  });

  if (result.canceled || !result.filePath) return null;
  return result.filePath;
});

function buildExportArgs(job) {
  const args = ['-y'];
  const start = Number.parseFloat(job.trimStart);
  const end = Number.parseFloat(job.trimEnd);

  if (Number.isFinite(start) && start > 0) args.push('-ss', String(start));
  if (Number.isFinite(end) && end > 0) args.push('-to', String(end));

  args.push('-i', job.inputPath, '-map', '0:v:0');

  const selectedAudio = job.audioTracks.filter((track) => track.enabled);
  const filters = selectedAudio
    .map((track, outputIndex) => {
      const volume = Number.parseFloat(track.volume);
      if (!Number.isFinite(volume) || Math.abs(volume - 1) < 0.001) return null;
      return `[0:${track.index}]volume=${volume.toFixed(2)}[a${outputIndex}]`;
    })
    .filter(Boolean);

  if (filters.length > 0) {
    args.push('-filter_complex', filters.join(';'));
  }

  selectedAudio.forEach((track, outputIndex) => {
    const volume = Number.parseFloat(track.volume);
    if (Number.isFinite(volume) && Math.abs(volume - 1) >= 0.001) {
      args.push('-map', `[a${outputIndex}]`);
    } else {
      args.push('-map', `0:${track.index}`);
    }
  });

  args.push('-c:v', job.reencodeVideo ? 'libx264' : 'copy');
  args.push('-c:a', 'aac', '-b:a', '192k', '-movflags', '+faststart', job.outputPath);
  return args;
}

ipcMain.handle('export:start', async (event, job) => {
  if (currentExport) throw new Error('An export is already running.');

  return new Promise((resolve, reject) => {
    const args = buildExportArgs(job);
    const child = spawn('ffmpeg', args, { windowsHide: true });
    currentExport = child;

    child.stderr.on('data', (data) => {
      const text = data.toString();
      event.sender.send('export:log', text);
      const match = text.match(/time=(\d+):(\d+):(\d+\.\d+)/);
      if (match) {
        const seconds =
          Number(match[1]) * 3600 + Number(match[2]) * 60 + Number(match[3]);
        event.sender.send('export:progress', seconds);
      }
    });

    child.on('error', (error) => {
      currentExport = null;
      reject(error);
    });

    child.on('close', (code) => {
      currentExport = null;
      if (code === 0) resolve({ outputPath: job.outputPath });
      else reject(new Error(`ffmpeg exited with code ${code}`));
    });
  });
});

ipcMain.handle('export:cancel', async () => {
  if (!currentExport) return false;
  currentExport.kill('SIGTERM');
  currentExport = null;
  return true;
});
