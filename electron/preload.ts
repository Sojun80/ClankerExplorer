import { contextBridge, ipcRenderer } from 'electron';
import type {
  FileItem,
  DriveInfo,
  QuickAccessItem,
  FilePreviewData,
  HashResult,
  BatchRenameRule,
  BatchRenamePreviewItem,
} from './types.js';

export const clankerApi = {
  // Window controls
  minimize: () => ipcRenderer.invoke('window:minimize'),
  maximize: () => ipcRenderer.invoke('window:maximize'),
  close: () => ipcRenderer.invoke('window:close'),
  isMaximized: () => ipcRenderer.invoke('window:isMaximized'),

  // Drives & Quick Access
  getDrives: (): Promise<DriveInfo[]> => ipcRenderer.invoke('fs:getDrives'),
  getQuickAccess: (): Promise<QuickAccessItem[]> => ipcRenderer.invoke('fs:getQuickAccess'),

  // Directory & Files
  readDir: (dirPath: string): Promise<{ items: FileItem[]; error?: string; currentPath: string }> =>
    ipcRenderer.invoke('fs:readDir', dirPath),
  getPreviewData: (filePath: string): Promise<FilePreviewData> =>
    ipcRenderer.invoke('fs:getPreviewData', filePath),
  calculateHash: (filePath: string): Promise<HashResult> =>
    ipcRenderer.invoke('fs:calculateHash', filePath),

  // File Operations
  createFolder: (parentDir: string, folderName: string): Promise<string> =>
    ipcRenderer.invoke('fs:createFolder', parentDir, folderName),
  createFile: (parentDir: string, fileName: string): Promise<string> =>
    ipcRenderer.invoke('fs:createFile', parentDir, fileName),
  rename: (oldPath: string, newName: string): Promise<string> =>
    ipcRenderer.invoke('fs:rename', oldPath, newName),
  delete: (targetPaths: string[], permanent?: boolean): Promise<boolean> =>
    ipcRenderer.invoke('fs:delete', targetPaths, permanent),
  copy: (sourcePaths: string[], targetDir: string): Promise<boolean> =>
    ipcRenderer.invoke('fs:copy', sourcePaths, targetDir),
  move: (sourcePaths: string[], targetDir: string): Promise<boolean> =>
    ipcRenderer.invoke('fs:move', sourcePaths, targetDir),

  // Power Tool: Batch Rename
  previewBatchRename: (
    targetPaths: string[],
    rule: BatchRenameRule
  ): Promise<BatchRenamePreviewItem[]> =>
    ipcRenderer.invoke('fs:previewBatchRename', targetPaths, rule),
  executeBatchRename: (
    items: { originalPath: string; newPath: string }[]
  ): Promise<{ successCount: number; errors: string[] }> =>
    ipcRenderer.invoke('fs:executeBatchRename', items),

  // Shell & OS
  openItem: (itemPath: string): Promise<string> => ipcRenderer.invoke('shell:openItem', itemPath),
  showInFolder: (itemPath: string): Promise<boolean> =>
    ipcRenderer.invoke('shell:showInFolder', itemPath),
  openTerminal: (dirPath: string, terminalType?: string): Promise<boolean> =>
    ipcRenderer.invoke('shell:openTerminal', dirPath, terminalType),
  openEditor: (dirOrFilePath: string): Promise<boolean> =>
    ipcRenderer.invoke('shell:openEditor', dirOrFilePath),

  // Clipboard
  writeClipboardText: (text: string): Promise<boolean> =>
    ipcRenderer.invoke('clipboard:writeText', text),
};

// Expose API to renderer
contextBridge.exposeInMainWorld('clankerApi', clankerApi);
