const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('eve', {
  openFile: () => ipcRenderer.invoke('file:open'),
  openPath: (filePath) => ipcRenderer.invoke('file:openPath', filePath),
  loadExtras: (filePath) => ipcRenderer.invoke('file:loadExtras', filePath),
  getSettings: () => ipcRenderer.invoke('settings:get'),
  chooseLibraryFolder: () => ipcRenderer.invoke('settings:chooseLibraryFolder'),
  showLibraryFolder: () => ipcRenderer.invoke('settings:showLibraryFolder'),
  scanLibrary: (page) => ipcRenderer.invoke('library:scan', page),
  deleteClips: (paths) => ipcRenderer.invoke('library:deleteClips', paths),
  getReplayStatus: () => ipcRenderer.invoke('replay:status'),
  startReplay: () => ipcRenderer.invoke('replay:start'),
  stopReplay: () => ipcRenderer.invoke('replay:stop'),
  saveReplay: () => ipcRenderer.invoke('replay:save'),
  getReplayDesktopSource: () => ipcRenderer.invoke('replay:desktopSource'),
  writeReplayClip: (data) => ipcRenderer.invoke('replay:writeClip', data),
  onReplayStatus: (callback) => {
    ipcRenderer.on('replay:status', (_, status) => callback(status));
  },
  onReplaySaveRequest: (callback) => {
    ipcRenderer.on('replay:save-request', () => callback());
  },
  saveExport: (defaultName) => ipcRenderer.invoke('file:saveExport', defaultName),
  startExport: (job) => ipcRenderer.invoke('export:start', job),
  cancelExport: () => ipcRenderer.invoke('export:cancel'),
  onExportLog: (callback) => {
    ipcRenderer.on('export:log', (_, text) => callback(text));
  },
  onExportProgress: (callback) => {
    ipcRenderer.on('export:progress', (_, seconds) => callback(seconds));
  }
});
