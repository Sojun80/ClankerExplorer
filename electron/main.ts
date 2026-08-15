import { app, BrowserWindow, ipcMain, shell, clipboard, dialog } from 'electron';
import path from 'path';
import fs from 'fs';
import { promises as fsp } from 'fs';
import os from 'os';
import crypto from 'crypto';
import { exec, spawn } from 'child_process';
import { promisify } from 'util';
import type {
  FileItem,
  DriveInfo,
  QuickAccessItem,
  FilePreviewData,
  HashResult,
  BatchRenameRule,
  BatchRenamePreviewItem,
} from './types.js';

const execAsync = promisify(exec);

let mainWindow: BrowserWindow | null = null;

const isDev = process.env.NODE_ENV === 'development' || !app.isPackaged;

function formatBytes(bytes: number, decimals: number = 2): string {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const dm = decimals < 0 ? 0 : decimals;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  const idx = Math.min(i, sizes.length - 1);
  return `${parseFloat((bytes / Math.pow(k, idx)).toFixed(dm))} ${sizes[idx]}`;
}

function formatDate(date: Date): string {
  try {
    const yyyy = date.getFullYear();
    const mm = String(date.getMonth() + 1).padStart(2, '0');
    const dd = String(date.getDate()).padStart(2, '0');
    const hh = String(date.getHours()).padStart(2, '0');
    const min = String(date.getMinutes()).padStart(2, '0');
    const ss = String(date.getSeconds()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd} ${hh}:${min}:${ss}`;
  } catch {
    return 'Unknown';
  }
}

function getFileExtension(filename: string, isDirectory: boolean): string {
  if (isDirectory) return '';
  const lower = filename.toLowerCase();
  if (lower.endsWith('.tar.gz')) return '.tar.gz';
  if (lower.endsWith('.tar.bz2')) return '.tar.bz2';
  if (lower.endsWith('.tar.xz')) return '.tar.xz';
  const ext = path.extname(filename);
  return ext || '';
}

async function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 800,
    minHeight: 550,
    frame: false, // Frameless for custom power-user dark titlebar
    titleBarStyle: 'hidden',
    backgroundColor: '#0b0f17',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
      webSecurity: false, // Enable local media/image previews
    },
    icon: path.join(__dirname, '../public/icon.png'),
  });

  if (isDev) {
    // Development mode
    await mainWindow.loadURL('http://localhost:5173');
    // mainWindow.webContents.openDevTools({ mode: 'detach' });
  } else {
    // Production mode
    await mainWindow.loadFile(path.join(__dirname, '../dist/index.html'));
  }

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

// Ensure single instance lock
const gotTheLock = app.requestSingleInstanceLock();
if (!gotTheLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.whenReady().then(createWindow);

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') {
      app.quit();
    }
  });

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
}

// -------------------------------------------------------------
// IPC Handlers
// -------------------------------------------------------------

// Window controls
ipcMain.handle('window:minimize', () => {
  mainWindow?.minimize();
});

ipcMain.handle('window:maximize', () => {
  if (mainWindow?.isMaximized()) {
    mainWindow.unmaximize();
  } else {
    mainWindow?.maximize();
  }
});

ipcMain.handle('window:close', () => {
  mainWindow?.close();
});

ipcMain.handle('window:isMaximized', () => {
  return mainWindow?.isMaximized() ?? false;
});

// System Drives Enumeration
ipcMain.handle('fs:getDrives', async (): Promise<DriveInfo[]> => {
  const drives: DriveInfo[] = [];

  if (process.platform === 'win32') {
    try {
      // Use PowerShell Get-CimInstance to get accurate volume names and free space
      const psScript = `Get-CimInstance Win32_LogicalDisk | Select-Object DeviceID, VolumeName, DriveType, Size, FreeSpace | ConvertTo-Json -Compress`;
      const { stdout } = await execAsync(`powershell -NoProfile -Command "${psScript}"`);

      if (stdout.trim()) {
        let parsed = JSON.parse(stdout.trim());
        if (!Array.isArray(parsed)) {
          parsed = [parsed];
        }

        for (const disk of parsed) {
          if (!disk.DeviceID) continue;
          const letter = disk.DeviceID; // e.g. "C:"
          const root = `${letter}\\`;
          const total = Number(disk.Size) || 0;
          const free = Number(disk.FreeSpace) || 0;
          const used = Math.max(0, total - free);
          const percentUsed = total > 0 ? Math.round((used / total) * 100) : 0;

          let type: DriveInfo['type'] = 'fixed';
          switch (disk.DriveType) {
            case 2:
              type = 'removable';
              break;
            case 3:
              type = 'fixed';
              break;
            case 4:
              type = 'network';
              break;
            case 5:
              type = 'cdrom';
              break;
            case 6:
              type = 'ram';
              break;
            default:
              type = 'unknown';
          }

          drives.push({
            letter,
            name: disk.VolumeName || (type === 'fixed' ? 'Local Disk' : 'Drive'),
            root,
            type,
            totalSpace: total,
            freeSpace: free,
            usedSpace: used,
            percentUsed,
            formattedTotal: formatBytes(total),
            formattedFree: formatBytes(free),
            formattedUsed: formatBytes(used),
          });
        }
      }
    } catch (err) {
      console.error('Error fetching drives via PowerShell:', err);
    }
  }

  // Fallback if no drives detected
  if (drives.length === 0) {
    if (process.platform === 'win32') {
      const letters = ['C', 'D', 'E', 'F', 'G', 'Z'];
      for (const l of letters) {
        const root = `${l}:\\`;
        try {
          if (fs.existsSync(root)) {
            drives.push({
              letter: `${l}:`,
              name: `Disk (${l}:)`,
              root,
              type: 'fixed',
              totalSpace: 1000000000000,
              freeSpace: 500000000000,
              usedSpace: 500000000000,
              percentUsed: 50,
              formattedTotal: '1 TB',
              formattedFree: '500 GB',
              formattedUsed: '500 GB',
            });
          }
        } catch {
          // Ignore
        }
      }
    } else {
      drives.push({
        letter: '/',
        name: 'Root',
        root: '/',
        type: 'fixed',
        totalSpace: 1000000000000,
        freeSpace: 500000000000,
        usedSpace: 500000000000,
        percentUsed: 50,
        formattedTotal: '1 TB',
        formattedFree: '500 GB',
        formattedUsed: '500 GB',
      });
    }
  }

  return drives;
});

// Quick Access Shortcuts
ipcMain.handle('fs:getQuickAccess', async (): Promise<QuickAccessItem[]> => {
  const homeDir = os.homedir();
  const items: QuickAccessItem[] = [
    {
      id: 'home',
      name: 'User Home',
      path: homeDir,
      icon: 'home',
    },
    {
      id: 'desktop',
      name: 'Desktop',
      path: path.join(homeDir, 'Desktop'),
      icon: 'desktop',
    },
    {
      id: 'downloads',
      name: 'Downloads',
      path: path.join(homeDir, 'Downloads'),
      icon: 'downloads',
    },
    {
      id: 'documents',
      name: 'Documents',
      path: path.join(homeDir, 'documents'),
      icon: 'documents',
    },
    {
      id: 'pictures',
      name: 'Pictures',
      path: path.join(homeDir, 'Pictures'),
      icon: 'pictures',
    },
    {
      id: 'videos',
      name: 'Videos',
      path: path.join(homeDir, 'Videos'),
      icon: 'videos',
    },
  ];

  // Filter only paths that actually exist
  return items.filter((item) => fs.existsSync(item.path));
});

// Read Directory Content - NEVER HIDDEN
ipcMain.handle(
  'fs:readDir',
  async (
    _event,
    dirPath: string
  ): Promise<{ items: FileItem[]; error?: string; currentPath: string }> => {
    try {
      const normalizedPath = path.resolve(dirPath);

      // Verify directory exists
      if (!fs.existsSync(normalizedPath)) {
        return { items: [], currentPath: normalizedPath, error: `Path does not exist: ${normalizedPath}` };
      }

      const entries = await fsp.readdir(normalizedPath, { withFileTypes: true });

      const fileItems: FileItem[] = [];

      for (const entry of entries) {
        const fullPath = path.join(normalizedPath, entry.name);
        try {
          // Use lstat so symbolic links are properly detected
          const stats = await fsp.lstat(fullPath);
          const isDir = entry.isDirectory();
          const isSymlink = entry.isSymbolicLink();
          const ext = getFileExtension(entry.name, isDir);

          // Attribute checks:
          // In Windows, files starting with '.' are considered dotfiles (hidden in unix/dev tools)
          // Also check typical Windows system file names or system directories
          const isDotFile = entry.name.startsWith('.');
          const isWindowsSysName = [
            '$recycle.bin',
            'system volume information',
            'pagefile.sys',
            'swapfile.sys',
            'hiberfil.sys',
            'dumpstack.log.tmp',
            'bootmgr',
            'bootsect.bak',
            'thumbs.db',
            'desktop.ini',
          ].includes(entry.name.toLowerCase());

          // Hidden flag: either dotfile or windows hidden
          const isHidden = isDotFile || isWindowsSysName;
          const isSystem = isWindowsSysName;
          const isReadOnly = (stats.mode & 0o200) === 0;
          const isArchive = !isDir;

          const attrList: string[] = [];
          if (isDir) attrList.push('D');
          if (isReadOnly) attrList.push('R');
          if (isHidden) attrList.push('H');
          if (isSystem) attrList.push('S');
          if (isArchive) attrList.push('A');
          if (isSymlink) attrList.push('L');

          fileItems.push({
            id: fullPath,
            name: entry.name,
            extension: ext,
            path: fullPath,
            parentPath: normalizedPath,
            isDirectory: isDir,
            isSymbolicLink: isSymlink,
            size: isDir ? 0 : stats.size,
            formattedSize: isDir ? '<DIR>' : formatBytes(stats.size),
            mtime: stats.mtimeMs,
            ctime: stats.birthtimeMs || stats.ctimeMs,
            atime: stats.atimeMs,
            formattedMtime: formatDate(stats.mtime),
            formattedCtime: formatDate(stats.birthtime || stats.ctime),
            formattedAtime: formatDate(stats.atime),
            isHidden,
            isSystem,
            isReadOnly,
            isArchive,
            attributesString: attrList.join(' '),
          });
        } catch (itemErr: any) {
          // If a specific file has permission lock, still include it with placeholder data so power users see it!
          fileItems.push({
            id: fullPath,
            name: entry.name,
            extension: getFileExtension(entry.name, entry.isDirectory()),
            path: fullPath,
            parentPath: normalizedPath,
            isDirectory: entry.isDirectory(),
            isSymbolicLink: false,
            size: 0,
            formattedSize: '<LOCKED>',
            mtime: Date.now(),
            ctime: Date.now(),
            atime: Date.now(),
            formattedMtime: 'Access Denied',
            formattedCtime: 'Access Denied',
            formattedAtime: 'Access Denied',
            isHidden: entry.name.startsWith('.'),
            isSystem: true,
            isReadOnly: true,
            isArchive: false,
            attributesString: 'S LOCKED',
          });
        }
      }

      return { items: fileItems, currentPath: normalizedPath };
    } catch (err: any) {
      return {
        items: [],
        currentPath: dirPath,
        error: err.message || 'Failed to read directory',
      };
    }
  }
);

// File Preview Provider (Text, Hex, Images, Media, Dimensions)
ipcMain.handle('fs:getPreviewData', async (_event, filePath: string): Promise<FilePreviewData> => {
  try {
    const stats = await fsp.stat(filePath);
    const ext = path.extname(filePath).toLowerCase();
    const name = path.basename(filePath);

    if (stats.isDirectory()) {
      return {
        path: filePath,
        name,
        extension: '',
        size: 0,
        formattedSize: '<DIR>',
        mtime: formatDate(stats.mtime),
        type: 'directory',
      };
    }

    const imageExts = ['.png', '.jpg', '.jpeg', '.gif', '.svg', '.webp', '.bmp', '.ico', '.tiff'];
    const audioExts = ['.mp3', '.wav', '.ogg', '.flac', '.m4a', '.aac'];
    const videoExts = ['.mp4', '.webm', '.mkv', '.mov', '.avi'];
    const textExts = [
      '.txt', '.md', '.markdown', '.js', '.jsx', '.ts', '.tsx', '.json', '.html', '.css',
      '.scss', '.less', '.xml', '.yaml', '.yml', '.py', '.rs', '.go', '.c', '.cpp', '.h',
      '.hpp', '.cs', '.java', '.kt', '.php', '.rb', '.sh', '.bash', '.zsh', '.ps1', '.bat',
      '.cmd', '.ini', '.conf', '.env', '.gitignore', '.gitattributes', '.toml', '.sql',
      '.log', '.diff', '.patch', '.svelte', '.vue', '.graphql', '.proto', '.lock'
    ];

    const isImage = imageExts.includes(ext);
    const isAudio = audioExts.includes(ext);
    const isVideo = videoExts.includes(ext);
    const isText = textExts.includes(ext) || name.startsWith('.');

    if (isImage) {
      const fileUrl = `file:///${filePath.replace(/\\/g, '/')}`;
      return {
        path: filePath,
        name,
        extension: ext,
        size: stats.size,
        formattedSize: formatBytes(stats.size),
        mtime: formatDate(stats.mtime),
        type: 'image',
        mediaUrl: fileUrl,
      };
    }

    if (isAudio || isVideo) {
      const fileUrl = `file:///${filePath.replace(/\\/g, '/')}`;
      return {
        path: filePath,
        name,
        extension: ext,
        size: stats.size,
        formattedSize: formatBytes(stats.size),
        mtime: formatDate(stats.mtime),
        type: isAudio ? 'audio' : 'video',
        mediaUrl: fileUrl,
      };
    }

    if (isText || stats.size < 1024 * 1024 * 2) {
      // Try to read as UTF-8 text (up to 512KB for preview)
      const maxRead = Math.min(stats.size, 512 * 1024);
      const fd = await fsp.open(filePath, 'r');
      const buffer = Buffer.alloc(maxRead);
      await fd.read(buffer, 0, maxRead, 0);
      await fd.close();

      // Check if buffer contains null bytes (indicator of binary)
      const isBinary = buffer.includes(0);

      if (!isBinary) {
        const textContent = buffer.toString('utf-8');
        const lines = textContent.split('\n');
        return {
          path: filePath,
          name,
          extension: ext,
          size: stats.size,
          formattedSize: formatBytes(stats.size),
          mtime: formatDate(stats.mtime),
          type: 'text',
          textContent,
          lineCount: lines.length,
          encoding: 'UTF-8',
        };
      }
    }

    // Otherwise render Binary Hex View
    const hexBytesToRead = Math.min(stats.size, 4096);
    const fd = await fsp.open(filePath, 'r');
    const hexBuffer = Buffer.alloc(hexBytesToRead);
    await fd.read(hexBuffer, 0, hexBytesToRead, 0);
    await fd.close();

    const hexRows: { offset: string; hex: string; ascii: string }[] = [];
    for (let i = 0; i < hexBytesToRead; i += 16) {
      const chunk = hexBuffer.subarray(i, Math.min(i + 16, hexBytesToRead));
      const offset = i.toString(16).padStart(8, '0').toUpperCase();
      const hexParts: string[] = [];
      let ascii = '';

      for (let j = 0; j < 16; j++) {
        if (j < chunk.length) {
          const byte = chunk[j];
          hexParts.push(byte.toString(16).padStart(2, '0').toUpperCase());
          // Printable ASCII (32 to 126)
          ascii += byte >= 32 && byte <= 126 ? String.fromCharCode(byte) : '.';
        } else {
          hexParts.push('  ');
        }
      }

      // Group 8 bytes with extra space
      const hex = `${hexParts.slice(0, 8).join(' ')}  ${hexParts.slice(8).join(' ')}`;
      hexRows.push({ offset, hex, ascii });
    }

    return {
      path: filePath,
      name,
      extension: ext,
      size: stats.size,
      formattedSize: formatBytes(stats.size),
      mtime: formatDate(stats.mtime),
      type: 'binary',
      hexData: hexRows,
    };
  } catch (err: any) {
    return {
      path: filePath,
      name: path.basename(filePath),
      extension: path.extname(filePath),
      size: 0,
      formattedSize: '0 B',
      mtime: '',
      type: 'error',
      error: err.message || 'Failed to preview file',
    };
  }
});

// Checksum Calculator
ipcMain.handle(
  'fs:calculateHash',
  async (_event, filePath: string): Promise<HashResult> => {
    return new Promise((resolve, reject) => {
      try {
        const md5Hash = crypto.createHash('md5');
        const sha256Hash = crypto.createHash('sha256');

        const stream = fs.createReadStream(filePath);
        stream.on('data', (chunk) => {
          md5Hash.update(chunk);
          sha256Hash.update(chunk);
        });

        stream.on('end', () => {
          resolve({
            md5: md5Hash.digest('hex'),
            sha256: sha256Hash.digest('hex'),
          });
        });

        stream.on('error', (err) => {
          reject(err);
        });
      } catch (err) {
        reject(err);
      }
    });
  }
);

// File Operations: Create, Rename, Delete, Copy, Move
ipcMain.handle('fs:createFolder', async (_event, parentDir: string, folderName: string) => {
  const targetPath = path.join(parentDir, folderName);
  await fsp.mkdir(targetPath, { recursive: false });
  return targetPath;
});

ipcMain.handle('fs:createFile', async (_event, parentDir: string, fileName: string) => {
  const targetPath = path.join(parentDir, fileName);
  await fsp.writeFile(targetPath, '', { flag: 'wx' }); // wx fails if file exists
  return targetPath;
});

ipcMain.handle('fs:rename', async (_event, oldPath: string, newName: string) => {
  const dir = path.dirname(oldPath);
  const newPath = path.join(dir, newName);
  await fsp.rename(oldPath, newPath);
  return newPath;
});

ipcMain.handle('fs:delete', async (_event, targetPaths: string[], permanent: boolean = false) => {
  for (const itemPath of targetPaths) {
    if (permanent) {
      await fsp.rm(itemPath, { recursive: true, force: true });
    } else {
      await shell.trashItem(itemPath);
    }
  }
  return true;
});

ipcMain.handle('fs:copy', async (_event, sourcePaths: string[], targetDir: string) => {
  for (const src of sourcePaths) {
    const fileName = path.basename(src);
    let dest = path.join(targetDir, fileName);

    // If file already exists in same folder, create a copy name
    if (src === dest || fs.existsSync(dest)) {
      const ext = path.extname(fileName);
      const base = path.basename(fileName, ext);
      dest = path.join(targetDir, `${base} - Copy${ext}`);
    }

    const stat = await fsp.stat(src);
    if (stat.isDirectory()) {
      await fsp.cp(src, dest, { recursive: true });
    } else {
      await fsp.copyFile(src, dest);
    }
  }
  return true;
});

ipcMain.handle('fs:move', async (_event, sourcePaths: string[], targetDir: string) => {
  for (const src of sourcePaths) {
    const fileName = path.basename(src);
    const dest = path.join(targetDir, fileName);
    await fsp.rename(src, dest);
  }
  return true;
});

// Power Tool: Batch Renamer Preview and Execution
ipcMain.handle(
  'fs:previewBatchRename',
  async (
    _event,
    targetPaths: string[],
    rule: BatchRenameRule
  ): Promise<BatchRenamePreviewItem[]> => {
    const results: BatchRenamePreviewItem[] = [];

    let counter = rule.startNumber ?? 1;

    for (const itemPath of targetPaths) {
      const originalName = path.basename(itemPath);
      const ext = path.extname(originalName);
      const nameWithoutExt = path.basename(originalName, ext);
      const dir = path.dirname(itemPath);

      let newBaseName = nameWithoutExt;

      if (rule.mode === 'replace' && rule.findText) {
        if (rule.isRegex) {
          try {
            const regex = new RegExp(rule.findText, rule.caseSensitive ? 'g' : 'gi');
            newBaseName = nameWithoutExt.replace(regex, rule.replaceText || '');
          } catch (e: any) {
            // Invalid regex
          }
        } else {
          if (rule.caseSensitive) {
            newBaseName = nameWithoutExt.split(rule.findText).join(rule.replaceText || '');
          } else {
            const regex = new RegExp(
              rule.findText.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'),
              'gi'
            );
            newBaseName = nameWithoutExt.replace(regex, rule.replaceText || '');
          }
        }
      } else if (rule.mode === 'prefix_suffix') {
        const p = rule.prefix || '';
        const s = rule.suffix || '';
        newBaseName = `${p}${nameWithoutExt}${s}`;
      } else if (rule.mode === 'numbering') {
        const padLen = rule.padding || 3;
        const numStr = String(counter).padStart(padLen, '0');
        const p = rule.prefix || '';
        const s = rule.suffix || '';
        newBaseName = `${p}${numStr}${s}`;
        counter++;
      } else if (rule.mode === 'change_case') {
        if (rule.caseMode === 'lower') {
          newBaseName = nameWithoutExt.toLowerCase();
        } else if (rule.caseMode === 'upper') {
          newBaseName = nameWithoutExt.toUpperCase();
        } else if (rule.caseMode === 'title') {
          newBaseName = nameWithoutExt.replace(/\w\S*/g, (w) =>
            w.charAt(0).toUpperCase() + w.substr(1).toLowerCase()
          );
        }
      }

      const newFullName = `${newBaseName}${ext}`;
      const newFullPath = path.join(dir, newFullName);
      const willChange = originalName !== newFullName;
      const hasConflict = willChange && fs.existsSync(newFullPath);

      results.push({
        originalPath: itemPath,
        originalName,
        newName: newFullName,
        newPath: newFullPath,
        willChange,
        hasConflict,
      });
    }

    return results;
  }
);

ipcMain.handle(
  'fs:executeBatchRename',
  async (
    _event,
    items: { originalPath: string; newPath: string }[]
  ): Promise<{ successCount: number; errors: string[] }> => {
    let successCount = 0;
    const errors: string[] = [];

    for (const item of items) {
      if (item.originalPath === item.newPath) continue;
      try {
        await fsp.rename(item.originalPath, item.newPath);
        successCount++;
      } catch (err: any) {
        errors.push(`Failed to rename ${item.originalPath}: ${err.message}`);
      }
    }

    return { successCount, errors };
  }
);

// Shell & OS Integrations
ipcMain.handle('shell:openItem', async (_event, itemPath: string) => {
  return await shell.openPath(itemPath);
});

ipcMain.handle('shell:showInFolder', async (_event, itemPath: string) => {
  shell.showItemInFolder(itemPath);
  return true;
});

ipcMain.handle('shell:openTerminal', async (_event, dirPath: string, terminalType: string = 'powershell') => {
  if (process.platform === 'win32') {
    if (terminalType === 'powershell') {
      spawn('powershell.exe', ['-NoExit', '-Command', `Set-Location -LiteralPath '${dirPath}'`], {
        detached: true,
        stdio: 'ignore',
      }).unref();
    } else if (terminalType === 'cmd') {
      spawn('cmd.exe', ['/k', `cd /d "${dirPath}"`], {
        detached: true,
        stdio: 'ignore',
      }).unref();
    } else if (terminalType === 'wt') {
      spawn('wt.exe', ['-d', dirPath], {
        detached: true,
        stdio: 'ignore',
      }).unref();
    }
  } else {
    spawn('x-terminal-emulator', [], { cwd: dirPath, detached: true, stdio: 'ignore' }).unref();
  }
  return true;
});

ipcMain.handle('shell:openEditor', async (_event, dirOrFilePath: string) => {
  if (process.platform === 'win32') {
    spawn('cmd.exe', ['/c', `code "${dirOrFilePath}"`], {
      detached: true,
      stdio: 'ignore',
    }).unref();
  }
  return true;
});

// Clipboard
ipcMain.handle('clipboard:writeText', (_event, text: string) => {
  clipboard.writeText(text);
  return true;
});
