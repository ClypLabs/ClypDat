const state = {
  media: null,
  library: [],
  selectedClips: new Map(),
  selectedGroups: new Set(),
  renderedClips: 0,
  totalClips: 0,
  libraryLoading: false,
  audioTracks: [],
  exporting: false,
  replay: {
    enabled: false,
    starting: false,
    saving: false,
    hotkey: 'CommandOrControl+Shift+F9',
    recorder: null,
    stream: null,
    segmentTimer: null,
    activeChunks: [],
    chunks: [],
    segmentSeconds: 5,
    durationSeconds: 60
  },
  trimStart: 0,
  trimEnd: 0,
  activeTrimHandle: null,
  activePlayhead: false,
  playheadFrame: null,
  previewAudioElements: [],
  previewGainNodes: [],
  audioContext: null,
  timelineWidth: 0,
  resizeFrame: null
};

const resizeObserver = new ResizeObserver(() => {
  if (!state.media || els.libraryView?.classList.contains('active')) return;
  cancelAnimationFrame(state.resizeFrame);
  state.resizeFrame = requestAnimationFrame(() => {
    const width = Math.round(els.timelineTracks.getBoundingClientRect().width);
    if (Math.abs(width - state.timelineWidth) < 4) return;
    state.timelineWidth = width;
    renderTimeline();
  });
});

const els = {
  openFile: document.querySelector('#openFile'),
  showLibrary: document.querySelector('#showLibrary'),
  closeEditor: document.querySelector('#closeEditor'),
  chooseFolder: document.querySelector('#chooseFolder'),
  replayToggle: document.querySelector('#replayToggle'),
  replaySave: document.querySelector('#replaySave'),
  replayStatus: document.querySelector('#replayStatus'),
  libraryLocationButton: document.querySelector('#libraryLocationButton'),
  refreshLibrary: document.querySelector('#refreshLibrary'),
  deleteSummary: document.querySelector('#deleteSummary'),
  deleteSelected: document.querySelector('#deleteSelected'),
  deleteDialog: document.querySelector('#deleteDialog'),
  deleteDialogPath: document.querySelector('#deleteDialogPath'),
  deleteDialogClose: document.querySelector('#deleteDialogClose'),
  deleteDialogCancel: document.querySelector('#deleteDialogCancel'),
  deleteDialogConfirm: document.querySelector('#deleteDialogConfirm'),
  libraryView: document.querySelector('#libraryView'),
  clipLibrary: document.querySelector('#clipLibrary'),
  libraryFolder: document.querySelector('#libraryFolder'),
  editorFacts: document.querySelectorAll('[data-editor-fact]'),
  preview: document.querySelector('#preview'),
  previewAudio: document.querySelector('#previewAudio'),
  previewStage: document.querySelector('.preview-stage'),
  emptyPreview: document.querySelector('#emptyPreview'),
  createdAt: document.querySelector('#createdAt'),
  videoQuality: document.querySelector('#videoQuality'),
  fileSize: document.querySelector('#fileSize'),
  fileLocation: document.querySelector('#fileLocation'),
  clipTitle: document.querySelector('#clipTitle'),
  trimStart: document.querySelector('#trimStart'),
  trimEnd: document.querySelector('#trimEnd'),
  trimRange: document.querySelector('#trimRange'),
  trimSelection: document.querySelector('#trimSelection'),
  trimStartHandle: document.querySelector('#trimStartHandle'),
  trimEndHandle: document.querySelector('#trimEndHandle'),
  playhead: document.querySelector('#playhead'),
  playheadTime: document.querySelector('#playheadTime'),
  playPause: document.querySelector('#playPause'),
  jumpStart: document.querySelector('#jumpStart'),
  jumpEnd: document.querySelector('#jumpEnd'),
  stepBack: document.querySelector('#stepBack'),
  stepForward: document.querySelector('#stepForward'),
  ruler: document.querySelector('#ruler'),
  timelineTracks: document.querySelector('#timelineTracks'),
  trackHeaders: document.querySelector('#trackHeaders'),
  trimStartLabel: document.querySelector('#trimStartLabel'),
  trimEndLabel: document.querySelector('#trimEndLabel'),
  trimDurationLabel: document.querySelector('#trimDurationLabel'),
  audioCount: document.querySelector('#audioCount'),
  audioTracks: document.querySelector('#audioTracks'),
  exportFile: document.querySelector('#exportFile'),
  cancelExport: document.querySelector('#cancelExport')
};

const libraryPreviewObserver = new IntersectionObserver((entries) => {
  entries.forEach((entry) => {
    const video = entry.target;
    setLibraryPreviewLoaded(video, entry.isIntersecting);
  });
}, {
  root: els.libraryView,
  rootMargin: '400px 0px'
});

function formatTime(seconds) {
  if (!Number.isFinite(seconds)) return '0:00';
  const total = Math.max(0, Math.round(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = total % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
    : `${minutes}:${String(secs).padStart(2, '0')}`;
}

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) return '--';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index += 1;
  }
  return `${value.toFixed(index === 0 ? 0 : 2)} ${units[index]}`;
}

function formatDateGroup(value) {
  const date = new Date(value);
  return date.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' }).toUpperCase();
}

function relativeAge(value) {
  const days = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 86400000));
  if (days === 0) return 'TODAY';
  if (days === 1) return '1 DAY AGO';
  return `${days} DAYS AGO`;
}

function trackLabel(track, fallbackIndex) {
  return ['Game Audio', 'Chat Audio', 'Microphone'][fallbackIndex] || `Audio ${fallbackIndex + 1}`;
}

function trackColorClass(index) {
  return ['track-green', 'track-blue', 'track-yellow'][index] || 'track-green';
}

function clamp(value, min, max) {
  return Math.min(Math.max(value, min), max);
}

function getDuration() {
  return Math.max(0, Number.parseFloat(state.media?.duration) || 0);
}

function getCurrentTime() {
  return clamp(els.preview.currentTime || 0, 0, getDuration());
}

function clipPrefsKey(path) {
  return `eve:clip:${path}`;
}

function loadClipPrefs(path) {
  try {
    return JSON.parse(localStorage.getItem(clipPrefsKey(path)) || '{}');
  } catch {
    return {};
  }
}

function saveClipPrefs() {
  if (!state.media?.path) return;
  const prefs = {
    trimStart: state.trimStart,
    trimEnd: state.trimEnd,
    audioTracks: state.audioTracks.map((track) => ({
      index: track.index,
      enabled: track.enabled,
      volume: track.volume
    }))
  };
  localStorage.setItem(clipPrefsKey(state.media.path), JSON.stringify(prefs));
}

function applyClipPrefs(media) {
  const prefs = loadClipPrefs(media.path);
  state.audioTracks = media.audioTracks.map((track) => {
    const saved = prefs.audioTracks?.find((item) => item.index === track.index);
    return {
      ...track,
      enabled: saved?.enabled ?? true,
      volume: Number.isFinite(saved?.volume) ? saved.volume : 1
    };
  });

  const duration = Number(media.duration) || 0;
  const trimStart = Number.isFinite(prefs.trimStart) ? prefs.trimStart : 0;
  const trimEnd = Number.isFinite(prefs.trimEnd) ? prefs.trimEnd : duration;
  return {
    trimStart: clamp(trimStart, 0, duration),
    trimEnd: clamp(trimEnd, trimStart, duration)
  };
}

function syncPreviewAudio(force = false) {
  state.previewAudioElements.forEach((audio) => {
    try {
      const drift = Math.abs(audio.currentTime - els.preview.currentTime);
      if (force || drift > 0.08) {
        audio.currentTime = els.preview.currentTime;
      }
    } catch {
      // Media may not be seekable until metadata loads.
    }
  });
}

function pausePreviewAudio() {
  state.previewAudioElements.forEach((audio) => audio.pause());
}

function stopPlayback() {
  els.preview.pause();
  pausePreviewAudio();
}

function playPreviewAudio() {
  syncPreviewAudio(true);
  if (state.audioContext?.state === 'suspended') state.audioContext.resume();
  state.previewAudioElements.forEach((audio) => {
    audio.play().catch(() => {});
  });
}

function playAllPreview() {
  if (!state.media) return;
  syncPreviewAudio(true);
  playPreviewAudio();
  els.preview.play().catch(() => {});
}

function pauseAllPreview() {
  els.preview.pause();
  pausePreviewAudio();
}

function clearPreviewAudio() {
  pausePreviewAudio();
  state.previewAudioElements.forEach((audio) => {
    audio.removeAttribute('src');
    audio.load();
    if (audio !== els.previewAudio) audio.remove();
  });
  state.previewAudioElements = [];
  state.previewGainNodes = [];
}

function releaseLoadedMedia() {
  stopPlayback();
  stopPlayheadLoop();
  clearPreviewAudio();
  els.preview.removeAttribute('src');
  els.preview.load();
  els.previewStage.classList.remove('has-media');
  state.media = null;
  state.audioTracks = [];
}

function releaseLibraryPreviewVideos(paths) {
  const selectedPaths = new Set(paths);
  els.clipLibrary.querySelectorAll('video').forEach((video) => {
    if (!selectedPaths.has(video.dataset.path)) return;
    setLibraryPreviewLoaded(video, false);
  });
}

function setLibraryPreviewLoaded(video, loaded) {
  if (loaded && !video.src) {
    video.src = video.dataset.src;
    video.load();
    return;
  }

  if (!loaded && video.src) {
    video.pause();
    video.removeAttribute('src');
    video.load();
  }
}

function setupPreviewAudio(media) {
  clearPreviewAudio();
  state.audioContext ||= new AudioContext();

  const previewTracks = media.previewAudioTracks || [];
  if (previewTracks.length > 0) {
    state.previewAudioElements = previewTracks.map((trackInfo, index) => {
      const audio = new Audio(trackInfo.previewUrl);
      audio.preload = 'auto';
      audio.volume = 1;
      const source = state.audioContext.createMediaElementSource(audio);
      const gain = state.audioContext.createGain();
      source.connect(gain).connect(state.audioContext.destination);
      state.previewGainNodes[index] = gain;
      document.body.append(audio);
      return audio;
    });
    applyPreviewVolumes();
    els.preview.muted = true;
    return;
  }

  if (media.previewAudioUrl) {
    els.previewAudio.src = media.previewAudioUrl;
    state.previewAudioElements = [els.previewAudio];
    els.preview.muted = true;
  } else {
    els.previewAudio.removeAttribute('src');
    els.previewAudio.load();
    els.preview.muted = false;
  }
}

function applyPreviewVolumes() {
  state.audioTracks.forEach((track, index) => {
    const audio = state.previewAudioElements[index];
    if (!audio) return;
    const gain = state.previewGainNodes[index];
    if (gain) {
      const value = track.enabled ? clamp(track.volume, 0, 1.5) : 0;
      gain.gain.setValueAtTime(value, state.audioContext.currentTime);
      audio.volume = 1;
    } else {
      audio.volume = track.enabled ? clamp(track.volume, 0, 1) : 0;
    }
  });
}

function setVolumeFill(input, target = input) {
  const value = Number.parseFloat(input.value) || 0;
  target.style.setProperty('--track-fill', `${clamp(value / 1.5, 0, 1) * 100}%`);
}

function setPlayhead(time) {
  const duration = getDuration();
  const next = clamp(time, 0, duration);
  if (duration > 0) els.playhead.style.left = `${(next / duration) * 100}%`;
  else els.playhead.style.left = '0%';
  els.playheadTime.textContent = `${formatTime(next)} / ${formatTime(duration)}`;
  const clock = els.trackHeaders.querySelector('.timeline-clock');
  if (clock) clock.textContent = `${formatTime(next)} / ${formatTime(duration)}`;
}

function stopPlayheadLoop() {
  if (state.playheadFrame) {
    cancelAnimationFrame(state.playheadFrame);
    state.playheadFrame = null;
  }
}

function startPlayheadLoop() {
  stopPlayheadLoop();

  const tick = () => {
    setPlayhead(getCurrentTime());
    if (!els.preview.paused && !els.preview.ended) {
      state.playheadFrame = requestAnimationFrame(tick);
    }
  };

  state.playheadFrame = requestAnimationFrame(tick);
}

function setTrim(start, end, seekTo) {
  const duration = getDuration();
  state.trimStart = clamp(start, 0, duration);
  state.trimEnd = clamp(end, state.trimStart, duration);

  els.trimStart.value = state.trimStart.toFixed(3);
  els.trimEnd.value = state.trimEnd.toFixed(3);

  const startPercent = duration ? (state.trimStart / duration) * 100 : 0;
  const endPercent = duration ? (state.trimEnd / duration) * 100 : 0;
  els.trimStartHandle.style.left = `${startPercent}%`;
  els.trimEndHandle.style.left = `${endPercent}%`;
  els.trimSelection.style.left = `${startPercent}%`;
  els.trimSelection.style.width = `${Math.max(0, endPercent - startPercent)}%`;
  document.documentElement.style.setProperty('--trim-start', `${startPercent}%`);
  document.documentElement.style.setProperty('--trim-end', `${endPercent}%`);

  els.trimStartLabel.textContent = formatTime(state.trimStart);
  els.trimEndLabel.textContent = formatTime(state.trimEnd);
  els.trimDurationLabel.textContent = formatTime(state.trimEnd - state.trimStart);

  if (Number.isFinite(seekTo) && els.preview.src) {
    els.preview.currentTime = clamp(seekTo, 0, duration);
    syncPreviewAudio(true);
    setPlayhead(els.preview.currentTime);
  }
}

function timeFromPointer(event) {
  const duration = getDuration();
  const rect = els.trimRange.getBoundingClientRect();
  const x = clamp(event.clientX - rect.left, 0, rect.width);
  return rect.width > 0 ? (x / rect.width) * duration : 0;
}

function moveTrimHandle(event) {
  if (!state.activeTrimHandle || !state.media) return;

  const time = timeFromPointer(event);
  if (state.activeTrimHandle === 'start') {
    const start = Math.min(time, state.trimEnd);
    setTrim(start, state.trimEnd, start);
  } else {
    const end = Math.max(time, state.trimStart);
    setTrim(state.trimStart, end, end);
  }
}

function movePlayhead(event) {
  if (!state.activePlayhead || !state.media) return;
  const time = timeFromPointer(event);
  els.preview.currentTime = time;
  syncPreviewAudio(true);
  setPlayhead(time);
}

function pickClosestTrimHandle(event) {
  const time = timeFromPointer(event);
  return Math.abs(time - state.trimStart) <= Math.abs(time - state.trimEnd) ? 'start' : 'end';
}

function startTrimDrag(handle, event) {
  if (!state.media) return;

  state.activeTrimHandle = handle;
  event.currentTarget.setPointerCapture(event.pointerId);
  event.currentTarget.classList.add('dragging');
  moveTrimHandle(event);
}

function endTrimDrag(event) {
  state.activeTrimHandle = null;
  event.currentTarget.classList.remove('dragging');
  saveClipPrefs();
}

function startPlayheadDrag(event) {
  if (!state.media || event.target.classList.contains('trim-handle')) return;
  state.activePlayhead = true;
  event.currentTarget.setPointerCapture(event.pointerId);
  movePlayhead(event);
}

function endPlayheadDrag() {
  state.activePlayhead = false;
}

function resamplePeaks(peaks, count) {
  if (!peaks?.length || count <= 0) return [];
  const output = [];
  const scale = peaks.length / count;

  for (let index = 0; index < count; index += 1) {
    const start = Math.floor(index * scale);
    const end = Math.max(start + 1, Math.ceil((index + 1) * scale));
    output.push(Math.max(...peaks.slice(start, end)));
  }

  return output;
}

function makeSpeakerIcon(withWaves = false) {
  const icon = document.createElement('span');
  icon.className = withWaves ? 'speaker speaker-loud' : 'speaker speaker-soft';
  icon.innerHTML = withWaves
    ? '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 9v6h4l5 4V5L8 9H4z"/><path d="M16 8.5a5 5 0 0 1 0 7"/><path d="M18.5 6a8.5 8.5 0 0 1 0 12"/></svg>'
    : '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 9v6h4l5 4V5L8 9H4z"/></svg>';
  return icon;
}

function makeWaveform(seed, peaks = null, width = 1200) {
  const wrapper = document.createElement('div');
  wrapper.className = 'waveform';
  const barCount = Math.max(260, Math.floor(width / 2));
  const values = peaks?.length ? resamplePeaks(peaks, barCount) : null;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', `0 0 ${barCount} 100`);
  svg.setAttribute('preserveAspectRatio', 'none');
  const top = [];
  const bottom = [];

  for (let i = 0; i < barCount; i += 1) {
    let peak;
    let height;
    let silent;

    if (values) {
      peak = clamp(values[i], 0, 1);
      silent = peak < 0.025;
    } else {
      const wave = Math.sin((i + seed) * 0.31) + Math.sin((i + seed) * 0.083);
      const noise = Math.abs(Math.sin((i + 3) * (seed + 2) * 0.017));
      silent = Math.sin((i + seed) * 0.12) > 0.76 || Math.sin((i + seed) * 0.047) < -0.88;
      peak = silent ? 0.02 : clamp(((Math.abs(wave) * 9 + noise * 20) % 30) / 30, 0, 1);
    }

    height = silent ? 0.7 : Math.max(0.7, peak * 34);
    top.push(`${i},${50 - height}`);
    bottom.unshift(`${i},${50 + height}`);
  }

  const area = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
  area.setAttribute('points', `${top.join(' ')} ${bottom.join(' ')}`);
  svg.append(area);
  wrapper.append(svg);

  return wrapper;
}

function renderRuler() {
  const duration = getDuration();
  els.ruler.innerHTML = '';
  if (duration <= 0) return;

  const targetTicks = 6;
  const step = Math.max(1, Math.ceil(duration / targetTicks / 5) * 5);
  const times = [];
  for (let time = 0; time <= duration + 0.001; time += step) {
    times.push(time);
  }
  if (Math.abs(times[times.length - 1] - duration) > 0.5) times.push(duration);

  times.forEach((time, index) => {
    const tick = document.createElement('div');
    tick.className = 'ruler-tick';
    if (index === 0) tick.classList.add('edge-start');
    if (Math.abs(time - duration) < 0.5) tick.classList.add('edge-end');
    tick.style.left = `${(time / duration) * 100}%`;
    const label = document.createElement('span');
    label.textContent = formatTime(time);
    tick.append(label);
    els.ruler.append(tick);
  });
}

function renderTimeline() {
  els.timelineTracks.innerHTML = '';
  els.trackHeaders.innerHTML = '';
  state.timelineWidth = Math.round(els.timelineTracks.getBoundingClientRect().width);

  if (!state.media) {
    els.trackHeaders.innerHTML = '<div class="timeline-clock">0:00 / 0:00</div><div class="track-header muted">No clip</div>';
    return;
  }

  const clock = document.createElement('div');
  clock.className = 'timeline-clock';
  clock.textContent = `${formatTime(getCurrentTime())} / ${formatTime(getDuration())}`;
  els.trackHeaders.append(clock);

  const videoHeader = document.createElement('div');
  videoHeader.className = 'track-header video-header';
  videoHeader.textContent = 'Video';
  els.trackHeaders.append(videoHeader);

  const videoLane = document.createElement('div');
  videoLane.className = 'timeline-lane video';
  const block = document.createElement('div');
  block.className = 'clip-block';
  block.textContent = '';
  videoLane.append(block);
  els.timelineTracks.append(videoLane);

  state.audioTracks.forEach((track, index) => {
    const colorClass = trackColorClass(index);
    const header = document.createElement('div');
    header.className = `track-header audio-header ${colorClass}`;

    const label = document.createElement('label');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = track.enabled;
    checkbox.addEventListener('change', () => {
      track.enabled = checkbox.checked;
      applyPreviewVolumes();
      saveClipPrefs();
      renderAudioTracks();
    });

    const name = document.createElement('span');
    name.className = 'track-name';
    name.textContent = trackLabel(track, index);
    const speakerLeft = document.createElement('span');
    speakerLeft.className = 'speaker';
    speakerLeft.textContent = '▸';
    label.append(checkbox, name, speakerLeft);

    const volume = document.createElement('input');
    volume.type = 'range';
    volume.min = '0';
    volume.max = '1.5';
    volume.step = '0.01';
    volume.value = String(track.volume);
    setVolumeFill(volume);

    const volumeWrap = document.createElement('div');
    volumeWrap.className = 'volume-wrap';
    volumeWrap.dataset.value = `${Math.round(track.volume * 100)}%`;
    setVolumeFill(volume, volumeWrap);

    volume.addEventListener('input', () => {
      track.volume = Number.parseFloat(volume.value);
      setVolumeFill(volume);
      setVolumeFill(volume, volumeWrap);
      volumeWrap.dataset.value = `${Math.round(track.volume * 100)}%`;
      applyPreviewVolumes();
      saveClipPrefs();
      renderAudioTracks();
    });
    volume.addEventListener('pointerdown', () => volumeWrap.classList.add('show-value'));
    volume.addEventListener('pointerup', () => volumeWrap.classList.remove('show-value'));
    volume.addEventListener('pointercancel', () => volumeWrap.classList.remove('show-value'));
    volume.addEventListener('blur', () => volumeWrap.classList.remove('show-value'));
    volumeWrap.append(volume);

    const speakerRight = document.createElement('span');
    speakerRight.className = 'speaker';
    speakerRight.textContent = '◖';

    header.append(label, makeSpeakerIcon(false), volumeWrap, makeSpeakerIcon(true), speakerRight);
    els.trackHeaders.append(header);

    const lane = document.createElement('div');
    lane.className = `timeline-lane ${colorClass}`;
    const waveform = state.media.waveforms?.find((item) => item.index === track.index);
    const laneWidth = Math.max(800, els.timelineTracks.getBoundingClientRect().width);
    lane.append(makeWaveform(track.index + index * 7, waveform?.peaks, laneWidth));
    els.timelineTracks.append(lane);
  });
}

resizeObserver.observe(els.timelineTracks);

function renderAudioTracks() {
  els.audioCount.textContent = String(state.audioTracks.length);
  els.audioTracks.innerHTML = '';

  state.audioTracks.forEach((track, index) => {
    const row = document.createElement('article');
    row.textContent = `${track.enabled ? 'on' : 'off'} ${trackLabel(track, index)} ${Math.round(track.volume * 100)}%`;
    els.audioTracks.append(row);
  });
}

function setExporting(isExporting) {
  state.exporting = isExporting;
  els.openFile.disabled = isExporting;
  els.exportFile.disabled = isExporting || !state.media;
  els.cancelExport.disabled = !isExporting;
}

function updateMediaChrome(media) {
  const video = media.videoTracks[0] || {};
  const height = Number(video.height) || 0;
  const resolution = height >= 2160
    ? '4K'
    : height >= 1440
      ? '1440p'
      : height >= 1080
        ? '1080p'
        : height >= 720
          ? '720p'
          : height > 0
            ? `${height}p`
            : 'Video';
  const fps = video.avg_frame_rate && video.avg_frame_rate !== '0/0'
    ? Math.round(video.avg_frame_rate.split('/').reduce((a, b) => Number(a) / Number(b)))
    : null;

  const createdDate = media.createdAt ? new Date(media.createdAt) : new Date();
  els.createdAt.textContent = createdDate.toLocaleString([], {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit'
  });
  els.videoQuality.textContent = fps ? `${resolution}, ${fps} FPS` : resolution;
  els.fileSize.textContent = formatBytes(media.size);
  els.fileLocation.textContent = media.path;
  els.clipTitle.value = media.name.replace(/\.[^.]+$/, '');
}

function showEditor() {
  els.libraryView.classList.remove('active');
  els.closeEditor.classList.add('active');
  els.showLibrary.hidden = true;
  els.chooseFolder.hidden = true;
  els.openFile.hidden = true;
  els.refreshLibrary.hidden = true;
  els.deleteSummary.hidden = true;
  els.deleteSelected.hidden = true;
  els.libraryLocationButton.hidden = true;
  els.editorFacts.forEach((fact) => { fact.hidden = false; });
}

function showLibrary() {
  stopPlayback();
  els.libraryView.classList.add('active');
  els.libraryView.scrollTop = 0;
  els.libraryView.scrollLeft = 0;
  els.closeEditor.classList.remove('active');
  els.showLibrary.hidden = true;
  updateLibrarySelectionChrome();
  els.editorFacts.forEach((fact) => { fact.hidden = true; });
  requestAnimationFrame(() => {
    els.libraryView.classList.add('force-repaint');
    void els.libraryView.offsetHeight;
    requestAnimationFrame(() => els.libraryView.classList.remove('force-repaint'));
  });
}

function selectedClipSize() {
  let total = 0;
  state.selectedClips.forEach((clip) => {
    total += Number(clip.size) || 0;
  });
  return total;
}

function updateLibrarySelectionChrome() {
  const hasSelection = state.selectedClips.size > 0;
  els.libraryLocationButton.hidden = hasSelection;
  els.chooseFolder.hidden = hasSelection;
  els.openFile.hidden = hasSelection;
  els.refreshLibrary.hidden = hasSelection;
  els.deleteSummary.hidden = !hasSelection;
  els.deleteSelected.hidden = !hasSelection;

  if (!hasSelection) return;
  const count = state.selectedClips.size;
  els.deleteSummary.textContent = `${count} selected - ${formatBytes(selectedClipSize())}`;
}

function formatHotkey(value) {
  return String(value || 'Ctrl+Shift+F9')
    .replace('CommandOrControl', 'Ctrl')
    .replace(/\+/g, '+');
}

function updateReplayChrome(status = {}) {
  state.replay = {
    ...state.replay,
    ...status
  };

  const enabled = Boolean(state.replay.enabled);
  const busy = Boolean(state.replay.starting || state.replay.saving);
  els.replayToggle.textContent = state.replay.starting
    ? 'Starting...'
    : enabled
      ? 'Replay On'
      : 'Replay Off';
  els.replayToggle.classList.toggle('active', enabled);
  els.replayToggle.disabled = busy;
  els.replaySave.disabled = !enabled || busy;
  els.replayStatus.textContent = state.replay.saving
    ? 'Saving clip...'
    : state.replay.savedPath
      ? 'Clip saved'
      : state.replay.error
        ? state.replay.error
        : formatHotkey(state.replay.hotkey);
}

function replayMimeType() {
  const candidates = [
    'video/webm;codecs=vp9',
    'video/webm;codecs=vp8',
    'video/webm'
  ];
  return candidates.find((type) => MediaRecorder.isTypeSupported(type)) || '';
}

function trimReplayChunks() {
  const cutoff = Date.now() - (state.replay.durationSeconds * 1000);
  state.replay.chunks = state.replay.chunks.filter((chunk) => chunk.createdAt >= cutoff);
}

function startReplaySegment() {
  if (!state.replay.stream) return;
  const mimeType = replayMimeType();
  const recorder = new MediaRecorder(state.replay.stream, mimeType ? { mimeType } : undefined);
  state.replay.recorder = recorder;
  state.replay.activeChunks = [];

  recorder.addEventListener('dataavailable', (event) => {
    if (event.data && event.data.size > 0) state.replay.activeChunks.push(event.data);
  });

  recorder.addEventListener('stop', () => {
    if (state.replay.segmentTimer) clearTimeout(state.replay.segmentTimer);
    state.replay.segmentTimer = null;

    if (!state.replay.stream) {
      state.replay.activeChunks = [];
      return;
    }

    if (state.replay.activeChunks.length > 0) {
      state.replay.chunks.push({
        blob: new Blob(state.replay.activeChunks, { type: 'video/webm' }),
        createdAt: Date.now()
      });
      trimReplayChunks();
    }

    state.replay.activeChunks = [];
    state.replay.recorder = null;
    if (state.replay.stream) startReplaySegment();
  });

  recorder.start();
  state.replay.segmentTimer = setTimeout(() => {
    if (state.replay.recorder?.state === 'recording') state.replay.recorder.stop();
  }, state.replay.segmentSeconds * 1000);
}

async function startLocalReplay() {
  if (state.replay.recorder) return;
  updateReplayChrome({ starting: true, error: '', savedPath: '' });

  const stream = await navigator.mediaDevices.getDisplayMedia({
    audio: false,
    video: {
      frameRate: 60
    }
  });

  state.replay.stream = stream;
  state.replay.chunks = [];
  startReplaySegment();
  updateReplayChrome({ enabled: true, starting: false, error: '', savedPath: '' });
}

async function stopLocalReplay() {
  if (state.replay.segmentTimer) clearTimeout(state.replay.segmentTimer);
  state.replay.segmentTimer = null;
  const stream = state.replay.stream;
  state.replay.stream = null;
  if (state.replay.recorder?.state === 'recording') state.replay.recorder.stop();
  stream?.getTracks().forEach((track) => track.stop());
  state.replay.recorder = null;
  state.replay.activeChunks = [];
  state.replay.chunks = [];
  updateReplayChrome({ enabled: false, starting: false, saving: false });
}

async function saveLocalReplay() {
  if (!state.replay.recorder) throw new Error('Replay buffer is not recording.');
  trimReplayChunks();
  if (state.replay.chunks.length === 0) throw new Error('Replay buffer is still warming up.');

  updateReplayChrome({ saving: true, error: '', savedPath: '' });
  const blob = new Blob(state.replay.chunks.map((chunk) => chunk.blob), { type: 'video/webm' });
  const data = new Uint8Array(await blob.arrayBuffer());
  const result = await window.eve.writeReplayClip(data);
  updateReplayChrome({ saving: false, savedPath: result.path });
  await refreshLibrary();
}

function getClipGroupCards(group) {
  return [...els.clipLibrary.querySelectorAll('.clip-card')]
    .filter((card) => card.dataset.group === group);
}

function updateClipGroupSelection(group) {
  const checkbox = [...els.clipLibrary.querySelectorAll('.clip-group-select')]
    .find((item) => item.dataset.group === group);
  if (!checkbox) return;

  const cards = getClipGroupCards(group);
  const selectedCount = cards.filter((card) => state.selectedClips.has(card.dataset.path)).length;
  checkbox.checked = cards.length > 0 && selectedCount === cards.length;
  checkbox.indeterminate = selectedCount > 0 && selectedCount < cards.length;
}

function updateAllClipGroupSelections() {
  els.clipLibrary.querySelectorAll('.clip-group-select').forEach((checkbox) => {
    updateClipGroupSelection(checkbox.dataset.group);
  });
}

function setClipSelected(card, clip, selected) {
  if (selected) {
    state.selectedClips.set(clip.path, clip);
  } else {
    state.selectedClips.delete(clip.path);
    if (card.dataset.group) state.selectedGroups.delete(card.dataset.group);
  }
  card.classList.toggle('selected', selected);
  const checkbox = card.querySelector('.clip-select');
  if (checkbox) checkbox.checked = selected;
  if (card.dataset.group) updateClipGroupSelection(card.dataset.group);
  updateLibrarySelectionChrome();
}

async function setClipGroupSelected(group, selected) {
  if (selected) state.selectedGroups.add(group);
  else state.selectedGroups.delete(group);

  getClipGroupCards(group).forEach((card) => {
    const clip = state.library.find((item) => item.path === card.dataset.path);
    if (clip) setClipSelected(card, clip, selected);
  });

  while (selected && state.renderedClips < state.totalClips) {
    const before = state.renderedClips;
    await loadMoreLibraryClips();
    if (state.renderedClips === before) break;
  }

  updateClipGroupSelection(group);
}

function confirmDeleteClips(clips) {
  return new Promise((resolve) => {
    const firstPath = clips[0]?.path || '';
    els.deleteDialogPath.textContent = clips.length === 1 ? firstPath : `${clips.length} clips selected`;
    els.deleteDialog.hidden = false;

    const cleanup = (confirmed) => {
      els.deleteDialog.hidden = true;
      els.deleteDialogCancel.removeEventListener('click', onCancel);
      els.deleteDialogClose.removeEventListener('click', onCancel);
      els.deleteDialogConfirm.removeEventListener('click', onConfirm);
      els.deleteDialog.removeEventListener('click', onBackdrop);
      document.removeEventListener('keydown', onKeyDown);
      resolve(confirmed);
    };
    const onCancel = () => cleanup(false);
    const onConfirm = () => cleanup(true);
    const onBackdrop = (event) => {
      if (event.target === els.deleteDialog) cleanup(false);
    };
    const onKeyDown = (event) => {
      if (event.key === 'Escape') cleanup(false);
    };

    els.deleteDialogCancel.addEventListener('click', onCancel);
    els.deleteDialogClose.addEventListener('click', onCancel);
    els.deleteDialogConfirm.addEventListener('click', onConfirm);
    els.deleteDialog.addEventListener('click', onBackdrop);
    document.addEventListener('keydown', onKeyDown);
    els.deleteDialogConfirm.focus();
  });
}

async function openMedia(media) {
  state.media = media;
  const clipPrefs = applyClipPrefs(media);

  els.preview.src = media.previewUrl;
  setupPreviewAudio(media);
  els.previewStage.classList.add('has-media');
  updateMediaChrome(media);
  setTrim(clipPrefs.trimStart, clipPrefs.trimEnd, clipPrefs.trimStart);
  setPlayhead(clipPrefs.trimStart);
  renderRuler();
  renderTimeline();
  renderAudioTracks();
  els.exportFile.disabled = false;
  showEditor();
  playAllPreview();
  if (!media.previewAudioTracks?.length && !media.waveforms?.length) loadMediaExtras(media.path);
}

async function loadMediaExtras(filePath) {
  try {
    const extras = await window.eve.loadExtras(filePath);
    if (state.media?.path !== filePath) return;
    const wasPlaying = !els.preview.paused;
    const currentTime = getCurrentTime();
    state.media = {
      ...state.media,
      previewAudioTracks: extras.previewAudioTracks || [],
      waveforms: extras.waveforms || []
    };
    setupPreviewAudio(state.media);
    els.preview.currentTime = currentTime;
    syncPreviewAudio(true);
    renderTimeline();
    if (wasPlaying) playAllPreview();
  } catch (error) {
    console.error('Failed to load clip extras:', error);
  }
}

function appendLibraryClips(clips) {
  let currentGroup = els.clipLibrary.dataset.lastGroup || '';

  clips.forEach((clip) => {
    const group = formatDateGroup(clip.createdAt);
    if (group !== currentGroup) {
      currentGroup = group;
      els.clipLibrary.dataset.lastGroup = group;
      const heading = document.createElement('h3');
      heading.className = 'clip-group';
      const groupSelect = document.createElement('input');
      groupSelect.className = 'clip-group-select';
      groupSelect.type = 'checkbox';
      groupSelect.dataset.group = group;
      groupSelect.setAttribute('aria-label', `Select ${group} clips`);
      groupSelect.addEventListener('click', async (event) => {
        event.stopPropagation();
        groupSelect.disabled = true;
        try {
          await setClipGroupSelected(group, groupSelect.checked);
        } finally {
          groupSelect.disabled = false;
        }
      });
      const groupLabel = document.createElement('span');
      groupLabel.textContent = group;
      heading.append(groupSelect, groupLabel);
      els.clipLibrary.append(heading);
    }

    const card = document.createElement('article');
    card.className = 'clip-card';
    card.dataset.path = clip.path;
    card.dataset.group = group;
    card.tabIndex = 0;
    card.addEventListener('click', async () => {
      try {
        card.classList.add('loading');
        const media = await window.eve.openPath(clip.path);
        await openMedia(media);
      } catch (error) {
        els.clipLibrary.classList.add('empty');
        els.clipLibrary.textContent = `Failed to open clip: ${error.message}`;
      } finally {
        card.classList.remove('loading');
      }
    });

    const select = document.createElement('input');
    select.className = 'clip-select';
    select.type = 'checkbox';
    select.setAttribute('aria-label', `Select ${clip.name}`);
    select.checked = state.selectedClips.has(clip.path) || state.selectedGroups.has(group);
    card.classList.toggle('selected', select.checked);
    if (select.checked) state.selectedClips.set(clip.path, clip);
    select.addEventListener('click', (event) => {
      event.stopPropagation();
      setClipSelected(card, clip, select.checked);
    });

    const video = document.createElement('video');
    video.dataset.src = clip.previewUrl;
    video.dataset.path = clip.path;
    video.muted = true;
    video.preload = 'none';
    libraryPreviewObserver.observe(video);

    const badge = document.createElement('span');
    badge.className = 'clip-duration';
    badge.textContent = formatTime(clip.duration);
    video.addEventListener('loadedmetadata', () => {
      if (Number.isFinite(video.duration)) badge.textContent = formatTime(video.duration);
    });

    const body = document.createElement('div');
    body.className = 'clip-body';
    const title = document.createElement('strong');
    title.textContent = clip.name.replace(/\.[^.]+$/, '');
    const age = document.createElement('span');
    age.className = 'clip-age';
    age.textContent = relativeAge(clip.createdAt);
    body.append(title, age);

    card.append(video, select, badge, body);
    els.clipLibrary.append(card);
    updateClipGroupSelection(group);
  });

  state.renderedClips += clips.length;
  updateLibrarySelectionChrome();
}

function renderLibrary(clips) {
  els.clipLibrary.querySelectorAll('video').forEach((video) => {
    libraryPreviewObserver.unobserve(video);
    setLibraryPreviewLoaded(video, false);
  });
  els.clipLibrary.innerHTML = '';
  delete els.clipLibrary.dataset.lastGroup;
  state.selectedGroups.clear();
  state.renderedClips = 0;
  els.clipLibrary.classList.toggle('empty', clips.length === 0);

  if (clips.length === 0) {
    els.clipLibrary.textContent = 'No clips found.';
    return;
  }

  appendLibraryClips(clips);
}

function removeDeletedLibraryCards(paths) {
  const deletedPaths = new Set(paths);
  els.clipLibrary.querySelectorAll('.clip-card').forEach((card) => {
    if (!deletedPaths.has(card.dataset.path)) return;
    const video = card.querySelector('video');
    if (video) {
      libraryPreviewObserver.unobserve(video);
      setLibraryPreviewLoaded(video, false);
    }
    card.remove();
  });
  removeEmptyClipGroups();

  state.library = state.library.filter((clip) => !deletedPaths.has(clip.path));
  state.renderedClips = Math.max(0, state.renderedClips - deletedPaths.size);
  state.totalClips = Math.max(0, state.totalClips - deletedPaths.size);
  els.clipLibrary.classList.toggle('empty', state.totalClips === 0);
  if (state.totalClips === 0) els.clipLibrary.textContent = 'No clips found.';
}

function removeEmptyClipGroups() {
  els.clipLibrary.querySelectorAll('.clip-group').forEach((heading) => {
    let node = heading.nextElementSibling;
    let hasCard = false;
    while (node && !node.classList.contains('clip-group')) {
      if (node.classList.contains('clip-card')) {
        hasCard = true;
        break;
      }
      node = node.nextElementSibling;
    }
    if (!hasCard) {
      const checkbox = heading.querySelector('.clip-group-select');
      if (checkbox) state.selectedGroups.delete(checkbox.dataset.group);
      heading.remove();
    }
  });
  updateAllClipGroupSelections();
}

async function loadMoreLibraryClips() {
  if (state.libraryLoading || state.renderedClips >= state.totalClips) return;
  state.libraryLoading = true;
  try {
    const result = await window.eve.scanLibrary({ offset: state.renderedClips, limit: 45 });
    state.totalClips = result.total;
    state.library.push(...result.clips);
    appendLibraryClips(result.clips);
  } finally {
    state.libraryLoading = false;
  }
}

async function refreshLibrary() {
  try {
    state.selectedClips.clear();
    state.selectedGroups.clear();
    updateLibrarySelectionChrome();
    els.libraryView.scrollTop = 0;
    els.libraryView.scrollLeft = 0;
    const settings = await window.eve.getSettings();
    els.libraryFolder.textContent = settings.libraryFolder || 'No folder selected';
    els.libraryFolder.title = settings.libraryFolder ? 'Open folder in Explorer' : '';
    els.clipLibrary.classList.add('empty');
    els.clipLibrary.textContent = settings.libraryFolder ? 'Scanning clips...' : 'Choose a folder to scan clips.';
    const result = await window.eve.scanLibrary({ offset: 0, limit: 45 });
    state.library = result.clips;
    state.totalClips = result.total;
    renderLibrary(result.clips);
  } catch (error) {
    els.clipLibrary.classList.add('empty');
    els.clipLibrary.textContent = `Library scan failed: ${error.message}`;
  }
}

els.openFile.addEventListener('click', async () => {
  const media = await window.eve.openFile();
  if (!media) return;
  await openMedia(media);
});

els.showLibrary.addEventListener('click', async () => {
  showLibrary();
  await refreshLibrary();
});

els.closeEditor.addEventListener('click', showLibrary);

els.chooseFolder.addEventListener('click', async () => {
  const settings = await window.eve.chooseLibraryFolder();
  els.libraryFolder.textContent = settings.libraryFolder || 'No folder selected';
  showLibrary();
  await refreshLibrary();
});

els.libraryFolder.addEventListener('click', async () => {
  if (els.libraryFolder.textContent === 'No folder selected') return;
  await window.eve.showLibraryFolder();
});

els.libraryFolder.addEventListener('keydown', async (event) => {
  if (event.key !== 'Enter' && event.key !== ' ') return;
  event.preventDefault();
  if (els.libraryFolder.textContent === 'No folder selected') return;
  await window.eve.showLibraryFolder();
});

els.refreshLibrary.addEventListener('click', refreshLibrary);

els.replayToggle.addEventListener('click', async () => {
  updateReplayChrome({ starting: true, error: '', savedPath: '' });
  try {
    if (state.replay.enabled) await stopLocalReplay();
    else await startLocalReplay();
  } catch (error) {
    updateReplayChrome({ starting: false, error: error.message });
  }
});

els.replaySave.addEventListener('click', async () => {
  updateReplayChrome({ saving: true, error: '', savedPath: '' });
  try {
    await saveLocalReplay();
  } catch (error) {
    updateReplayChrome({ saving: false, error: error.message });
  }
});

els.deleteSelected.addEventListener('click', async () => {
  if (state.selectedClips.size === 0) return;
  const clips = [...state.selectedClips.values()];
  const ok = await confirmDeleteClips(clips);
  if (!ok) return;

  els.deleteSelected.disabled = true;
  try {
    const paths = clips.map((clip) => clip.path);
    if (clips.some((clip) => clip.path === state.media?.path)) {
      releaseLoadedMedia();
    } else {
      stopPlayback();
    }
    releaseLibraryPreviewVideos(paths);
    const result = await window.eve.deleteClips(paths);
    removeDeletedLibraryCards(result.deleted || []);
    state.selectedClips.clear();
    state.selectedGroups.clear();
    updateAllClipGroupSelections();
    updateLibrarySelectionChrome();
    if (result.failed?.length) {
      const details = result.failed
        .slice(0, 3)
        .map((item) => item.message || item.path)
        .join('\n');
      window.alert(`Deleted ${result.deleted.length}. Failed ${result.failed.length}.${details ? `\n${details}` : ''}`);
    }
  } finally {
    els.deleteSelected.disabled = false;
  }
});

els.libraryView.addEventListener('scroll', () => {
  const remaining = els.libraryView.scrollHeight - els.libraryView.scrollTop - els.libraryView.clientHeight;
  if (remaining < 600) loadMoreLibraryClips();
});

els.trimStartHandle.addEventListener('pointerdown', (event) => {
  event.stopPropagation();
  startTrimDrag('start', event);
});

els.trimEndHandle.addEventListener('pointerdown', (event) => {
  event.stopPropagation();
  startTrimDrag('end', event);
});

els.trimRange.addEventListener('pointerdown', (event) => {
  if (event.shiftKey) startTrimDrag(pickClosestTrimHandle(event), event);
  else startPlayheadDrag(event);
});

els.ruler.addEventListener('pointerdown', startPlayheadDrag);
els.timelineTracks.addEventListener('pointerdown', startPlayheadDrag);

els.trimStartHandle.addEventListener('pointermove', moveTrimHandle);
els.trimEndHandle.addEventListener('pointermove', moveTrimHandle);
els.trimRange.addEventListener('pointermove', (event) => {
  moveTrimHandle(event);
  movePlayhead(event);
});
els.ruler.addEventListener('pointermove', movePlayhead);
els.timelineTracks.addEventListener('pointermove', movePlayhead);
els.trimStartHandle.addEventListener('pointerup', endTrimDrag);
els.trimEndHandle.addEventListener('pointerup', endTrimDrag);
els.trimRange.addEventListener('pointerup', endPlayheadDrag);
els.trimRange.addEventListener('pointercancel', endPlayheadDrag);
els.ruler.addEventListener('pointerup', endPlayheadDrag);
els.ruler.addEventListener('pointercancel', endPlayheadDrag);
els.timelineTracks.addEventListener('pointerup', endPlayheadDrag);
els.timelineTracks.addEventListener('pointercancel', endPlayheadDrag);
els.trimStartHandle.addEventListener('pointercancel', endTrimDrag);
els.trimEndHandle.addEventListener('pointercancel', endTrimDrag);

els.playPause.addEventListener('click', () => {
  if (!state.media) return;
  if (els.preview.paused) {
    playAllPreview();
  } else {
    pauseAllPreview();
  }
});

els.jumpStart.addEventListener('click', () => {
  els.preview.currentTime = state.trimStart;
  syncPreviewAudio(true);
  setPlayhead(state.trimStart);
});

els.jumpEnd.addEventListener('click', () => {
  els.preview.currentTime = state.trimEnd;
  syncPreviewAudio(true);
  setPlayhead(state.trimEnd);
});

els.stepBack.addEventListener('click', () => {
  els.preview.currentTime = clamp(getCurrentTime() - 1, 0, getDuration());
  syncPreviewAudio(true);
  setPlayhead(els.preview.currentTime);
});

els.stepForward.addEventListener('click', () => {
  els.preview.currentTime = clamp(getCurrentTime() + 1, 0, getDuration());
  syncPreviewAudio(true);
  setPlayhead(els.preview.currentTime);
});

els.preview.addEventListener('play', () => {
  els.playPause.classList.add('is-playing');
  startPlayheadLoop();
});

els.preview.addEventListener('pause', () => {
  els.playPause.classList.remove('is-playing');
  stopPlayheadLoop();
  setPlayhead(getCurrentTime());
});

els.preview.addEventListener('timeupdate', () => {
  if (getCurrentTime() > state.trimEnd && state.trimEnd > state.trimStart) {
    els.preview.pause();
    els.preview.currentTime = state.trimEnd;
  }
  setPlayhead(getCurrentTime());
});

els.preview.addEventListener('play', () => {
  syncPreviewAudio(true);
  playPreviewAudio();
});

els.preview.addEventListener('pause', () => {
  pausePreviewAudio();
});

els.preview.addEventListener('seeked', () => {
  syncPreviewAudio(true);
});

els.preview.addEventListener('timeupdate', () => {
  syncPreviewAudio();
});

document.addEventListener('keydown', (event) => {
  const tag = event.target?.tagName;
  const isTyping = tag === 'INPUT' || tag === 'TEXTAREA' || event.target?.isContentEditable;
  if (event.code !== 'Space' || isTyping || !state.media) return;

  event.preventDefault();
  if (els.preview.paused) {
    playAllPreview();
  } else {
    pauseAllPreview();
  }
});

els.exportFile.addEventListener('click', async () => {
  if (!state.media) return;

  const defaultName = state.media.name.replace(/\.[^.]+$/, '.mp4');
  const outputPath = await window.eve.saveExport(defaultName);
  if (!outputPath) return;

  setExporting(true);

  const trimStart = Number.parseFloat(els.trimStart.value) || 0;
  const trimEnd = Number.parseFloat(els.trimEnd.value) || state.media.duration;

  try {
    await window.eve.startExport({
      inputPath: state.media.path,
      outputPath,
      trimStart,
      trimEnd,
      reencodeVideo: true,
      audioTracks: state.audioTracks
    });
  } catch (error) {
    window.alert(`Export failed: ${error.message}`);
  } finally {
    setExporting(false);
  }
});

els.cancelExport.addEventListener('click', async () => {
  await window.eve.cancelExport();
  setExporting(false);
});

showLibrary();
refreshLibrary();
window.eve.getReplayStatus().then((status) => {
  updateReplayChrome({
    hotkey: status.hotkey,
    durationSeconds: status.durationSeconds
  });
}).catch(() => {});
window.eve.onReplayStatus((status) => {
  updateReplayChrome(status);
  if (status.savedPath) refreshLibrary();
});
window.eve.onReplaySaveRequest(() => {
  saveLocalReplay().catch((error) => {
    updateReplayChrome({ saving: false, error: error.message });
  });
});
