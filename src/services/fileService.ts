import type {
  FileItem,
  DriveInfo,
  QuickAccessItem,
  FilePreviewData,
  HashResult,
  BatchRenameRule,
  BatchRenamePreviewItem,
} from '../types/explorer';

// Check if running in Electron native environment
export const isElectron = typeof window !== 'undefined' && !!window.clankerApi;

// Fallback mock data for web browser testing
const mockDrives: DriveInfo[] = [
  {
    letter: 'C:',
    name: 'Windows System',
    root: 'C:\\',
    type: 'fixed',
    totalSpace: 2000000000000,
    freeSpace: 89490000000,
    usedSpace: 1910510000000,
    percentUsed: 95,
    formattedTotal: '1.82 TB',
    formattedFree: '89.5 GB',
    formattedUsed: '1.73 TB',
  },
  {
    letter: 'E:',
    name: 'Fast NVMe Storage',
    root: 'E:\\',
    type: 'fixed',
    totalSpace: 8000000000000,
    freeSpace: 5734600000000,
    usedSpace: 2265400000000,
    percentUsed: 28,
    formattedTotal: '7.28 TB',
    formattedFree: '5.22 TB',
    formattedUsed: '2.06 TB',
  },
  {
    letter: 'G:',
    name: 'Games & Assets',
    root: 'G:\\',
    type: 'fixed',
    totalSpace: 1000000000000,
    freeSpace: 65910000000,
    usedSpace: 934090000000,
    percentUsed: 93,
    formattedTotal: '931.5 GB',
    formattedFree: '65.9 GB',
    formattedUsed: '865.6 GB',
  },
  {
    letter: 'Z:',
    name: 'Network Share',
    root: 'Z:\\',
    type: 'network',
    totalSpace: 14000000000000,
    freeSpace: 7059620000000,
    usedSpace: 6940380000000,
    percentUsed: 50,
    formattedTotal: '12.73 TB',
    formattedFree: '6.42 TB',
    formattedUsed: '6.31 TB',
  },
];

const mockFiles: Record<string, FileItem[]> = {
  'C:\\': [
    {
      id: 'C:\\ClankerExplorer',
      name: 'ClankerExplorer',
      extension: '',
      path: 'C:\\ClankerExplorer',
      parentPath: 'C:\\',
      isDirectory: true,
      isSymbolicLink: false,
      size: 0,
      formattedSize: '<DIR>',
      mtime: Date.now() - 3600000,
      ctime: Date.now() - 86400000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 10:04:39',
      formattedCtime: '2026-08-14 09:12:00',
      formattedAtime: '2026-08-15 10:04:39',
      isHidden: false,
      isSystem: false,
      isReadOnly: false,
      isArchive: false,
      attributesString: 'D',
    },
    {
      id: 'C:\\Program Files',
      name: 'Program Files',
      extension: '',
      path: 'C:\\Program Files',
      parentPath: 'C:\\',
      isDirectory: true,
      isSymbolicLink: false,
      size: 0,
      formattedSize: '<DIR>',
      mtime: Date.now() - 100000000,
      ctime: Date.now() - 500000000,
      atime: Date.now(),
      formattedMtime: '2026-07-20 14:15:22',
      formattedCtime: '2025-11-10 11:00:00',
      formattedAtime: '2026-08-15 08:30:00',
      isHidden: false,
      isSystem: false,
      isReadOnly: false,
      isArchive: false,
      attributesString: 'D',
    },
    {
      id: 'C:\\Windows',
      name: 'Windows',
      extension: '',
      path: 'C:\\Windows',
      parentPath: 'C:\\',
      isDirectory: true,
      isSymbolicLink: false,
      size: 0,
      formattedSize: '<DIR>',
      mtime: Date.now() - 200000000,
      ctime: Date.now() - 600000000,
      atime: Date.now(),
      formattedMtime: '2026-06-15 18:22:11',
      formattedCtime: '2025-08-01 00:00:00',
      formattedAtime: '2026-08-15 10:00:00',
      isHidden: false,
      isSystem: true,
      isReadOnly: true,
      isArchive: false,
      attributesString: 'D S R',
    },
    {
      id: 'C:\\$Recycle.Bin',
      name: '$Recycle.Bin',
      extension: '',
      path: 'C:\\$Recycle.Bin',
      parentPath: 'C:\\',
      isDirectory: true,
      isSymbolicLink: false,
      size: 0,
      formattedSize: '<DIR>',
      mtime: Date.now() - 500000,
      ctime: Date.now() - 600000000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 09:55:00',
      formattedCtime: '2025-08-01 00:00:00',
      formattedAtime: '2026-08-15 09:55:00',
      isHidden: true,
      isSystem: true,
      isReadOnly: false,
      isArchive: false,
      attributesString: 'D H S',
    },
    {
      id: 'C:\\pagefile.sys',
      name: 'pagefile.sys',
      extension: '.sys',
      path: 'C:\\pagefile.sys',
      parentPath: 'C:\\',
      isDirectory: false,
      isSymbolicLink: false,
      size: 17179869184, // 16 GB
      formattedSize: '16.00 GB',
      mtime: Date.now(),
      ctime: Date.now() - 600000000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 10:04:39',
      formattedCtime: '2025-08-01 00:00:00',
      formattedAtime: '2026-08-15 10:04:39',
      isHidden: true,
      isSystem: true,
      isReadOnly: false,
      isArchive: true,
      attributesString: 'H S A',
    },
    {
      id: 'C:\\dumpstack.log.tmp',
      name: 'dumpstack.log.tmp',
      extension: '.tmp',
      path: 'C:\\dumpstack.log.tmp',
      parentPath: 'C:\\',
      isDirectory: false,
      isSymbolicLink: false,
      size: 12288,
      formattedSize: '12.00 KB',
      mtime: Date.now() - 86400000,
      ctime: Date.now() - 86400000,
      atime: Date.now(),
      formattedMtime: '2026-08-14 12:00:00',
      formattedCtime: '2026-08-14 12:00:00',
      formattedAtime: '2026-08-15 10:00:00',
      isHidden: true,
      isSystem: true,
      isReadOnly: false,
      isArchive: true,
      attributesString: 'H S A',
    },
  ],
  'C:\\ClankerExplorer': [
    {
      id: 'C:\\ClankerExplorer\\.git',
      name: '.git',
      extension: '',
      path: 'C:\\ClankerExplorer\\.git',
      parentPath: 'C:\\ClankerExplorer',
      isDirectory: true,
      isSymbolicLink: false,
      size: 0,
      formattedSize: '<DIR>',
      mtime: Date.now() - 3600000,
      ctime: Date.now() - 3600000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 09:04:00',
      formattedCtime: '2026-08-15 09:04:00',
      formattedAtime: '2026-08-15 10:00:00',
      isHidden: true,
      isSystem: false,
      isReadOnly: false,
      isArchive: false,
      attributesString: 'D H',
    },
    {
      id: 'C:\\ClankerExplorer\\.env',
      name: '.env',
      extension: '',
      path: 'C:\\ClankerExplorer\\.env',
      parentPath: 'C:\\ClankerExplorer',
      isDirectory: false,
      isSymbolicLink: false,
      size: 248,
      formattedSize: '248 B',
      mtime: Date.now() - 1800000,
      ctime: Date.now() - 1800000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 09:34:00',
      formattedCtime: '2026-08-15 09:34:00',
      formattedAtime: '2026-08-15 10:00:00',
      isHidden: true,
      isSystem: false,
      isReadOnly: false,
      isArchive: true,
      attributesString: 'H A',
    },
    {
      id: 'C:\\ClankerExplorer\\package.json',
      name: 'package.json',
      extension: '.json',
      path: 'C:\\ClankerExplorer\\package.json',
      parentPath: 'C:\\ClankerExplorer',
      isDirectory: false,
      isSymbolicLink: false,
      size: 1420,
      formattedSize: '1.39 KB',
      mtime: Date.now() - 300000,
      ctime: Date.now() - 3600000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 10:00:00',
      formattedCtime: '2026-08-15 09:04:00',
      formattedAtime: '2026-08-15 10:04:39',
      isHidden: false,
      isSystem: false,
      isReadOnly: false,
      isArchive: true,
      attributesString: 'A',
    },
    {
      id: 'C:\\ClankerExplorer\\src',
      name: 'src',
      extension: '',
      path: 'C:\\ClankerExplorer\\src',
      parentPath: 'C:\\ClankerExplorer',
      isDirectory: true,
      isSymbolicLink: false,
      size: 0,
      formattedSize: '<DIR>',
      mtime: Date.now() - 100000,
      ctime: Date.now() - 3600000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 10:03:00',
      formattedCtime: '2026-08-15 09:04:00',
      formattedAtime: '2026-08-15 10:04:39',
      isHidden: false,
      isSystem: false,
      isReadOnly: false,
      isArchive: false,
      attributesString: 'D',
    },
    {
      id: 'C:\\ClankerExplorer\\electron',
      name: 'electron',
      extension: '',
      path: 'C:\\ClankerExplorer\\electron',
      parentPath: 'C:\\ClankerExplorer',
      isDirectory: true,
      isSymbolicLink: false,
      size: 0,
      formattedSize: '<DIR>',
      mtime: Date.now() - 200000,
      ctime: Date.now() - 3600000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 10:02:00',
      formattedCtime: '2026-08-15 09:04:00',
      formattedAtime: '2026-08-15 10:04:39',
      isHidden: false,
      isSystem: false,
      isReadOnly: false,
      isArchive: false,
      attributesString: 'D',
    },
    {
      id: 'C:\\ClankerExplorer\\archive_release_v1.tar.gz',
      name: 'archive_release_v1.tar.gz',
      extension: '.tar.gz',
      path: 'C:\\ClankerExplorer\\archive_release_v1.tar.gz',
      parentPath: 'C:\\ClankerExplorer',
      isDirectory: false,
      isSymbolicLink: false,
      size: 45892300,
      formattedSize: '43.77 MB',
      mtime: Date.now() - 1200000,
      ctime: Date.now() - 1200000,
      atime: Date.now(),
      formattedMtime: '2026-08-15 09:44:00',
      formattedCtime: '2026-08-15 09:44:00',
      formattedAtime: '2026-08-15 10:00:00',
      isHidden: false,
      isSystem: false,
      isReadOnly: false,
      isArchive: true,
      attributesString: 'A',
    },
  ],
};

export const fileService = {
  // Window management
  minimize: async (): Promise<void> => {
    if (isElectron) return window.clankerApi!.minimize();
  },
  maximize: async (): Promise<void> => {
    if (isElectron) return window.clankerApi!.maximize();
  },
  close: async (): Promise<void> => {
    if (isElectron) return window.clankerApi!.close();
  },
  isMaximized: async (): Promise<boolean> => {
    if (isElectron) return window.clankerApi!.isMaximized();
    return false;
  },

  // Drives & Quick Access
  getDrives: async (): Promise<DriveInfo[]> => {
    if (isElectron) {
      try {
        return await window.clankerApi!.getDrives();
      } catch (err) {
        console.error('Error in getDrives IPC:', err);
      }
    }
    return mockDrives;
  },

  getQuickAccess: async (): Promise<QuickAccessItem[]> => {
    if (isElectron) {
      try {
        return await window.clankerApi!.getQuickAccess();
      } catch (err) {
        console.error('Error in getQuickAccess IPC:', err);
      }
    }
    return [
      { id: 'c-drive', name: 'Local Disk (C:)', path: 'C:\\', icon: 'drive' },
      { id: 'clanker', name: 'ClankerExplorer', path: 'C:\\ClankerExplorer', icon: 'code' },
      { id: 'desktop', name: 'Desktop', path: 'C:\\Users\\User\\Desktop', icon: 'desktop' },
      { id: 'downloads', name: 'Downloads', path: 'C:\\Users\\User\\Downloads', icon: 'downloads' },
      { id: 'documents', name: 'Documents', path: 'C:\\Users\\User\\Documents', icon: 'documents' },
      { id: 'e-drive', name: 'Fast NVMe (E:)', path: 'E:\\', icon: 'drive' },
      { id: 'z-share', name: 'Network Share (Z:)', path: 'Z:\\', icon: 'drive' },
    ];
  },

  // Read Directory
  readDir: async (
    dirPath: string
  ): Promise<{ items: FileItem[]; error?: string; currentPath: string }> => {
    if (isElectron) {
      try {
        return await window.clankerApi!.readDir(dirPath);
      } catch (err: any) {
        return { items: [], currentPath: dirPath, error: err.message };
      }
    }

    // Mock fallback
    const norm = dirPath.replace(/\//g, '\\');
    const matchedKey = Object.keys(mockFiles).find(
      (k) => k.toLowerCase() === norm.toLowerCase() || norm.startsWith(k)
    );

    if (matchedKey && mockFiles[matchedKey]) {
      return { items: mockFiles[matchedKey], currentPath: norm };
    }

    return {
      items: mockFiles['C:\\ClankerExplorer'] || [],
      currentPath: norm || 'C:\\ClankerExplorer',
    };
  },

  // File Preview
  getPreviewData: async (filePath: string): Promise<FilePreviewData> => {
    if (isElectron) {
      try {
        return await window.clankerApi!.getPreviewData(filePath);
      } catch (err: any) {
        return {
          path: filePath,
          name: filePath.split('\\').pop() || '',
          extension: '',
          size: 0,
          formattedSize: '0 B',
          mtime: '',
          type: 'error',
          error: err.message,
        };
      }
    }

    // Mock preview data
    return {
      path: filePath,
      name: filePath.split('\\').pop() || '',
      extension: '.json',
      size: 1420,
      formattedSize: '1.39 KB',
      mtime: '2026-08-15 10:00:00',
      type: 'text',
      textContent: `{\n  "name": "clanker-explorer",\n  "version": "1.0.0",\n  "description": "Power-user Windows explorer",\n  "author": "Clanker Team"\n}`,
      lineCount: 6,
      encoding: 'UTF-8',
    };
  },

  // Checksums
  calculateHash: async (filePath: string): Promise<HashResult> => {
    if (isElectron) {
      return await window.clankerApi!.calculateHash(filePath);
    }
    return {
      md5: '7d35b91b5c2bf9de4906f36ebba2ff89',
      sha256: '9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08',
    };
  },

  // File Operations
  createFolder: async (parentDir: string, folderName: string): Promise<string> => {
    if (isElectron) return await window.clankerApi!.createFolder(parentDir, folderName);
    return `${parentDir}\\${folderName}`;
  },

  createFile: async (parentDir: string, fileName: string): Promise<string> => {
    if (isElectron) return await window.clankerApi!.createFile(parentDir, fileName);
    return `${parentDir}\\${fileName}`;
  },

  rename: async (oldPath: string, newName: string): Promise<string> => {
    if (isElectron) return await window.clankerApi!.rename(oldPath, newName);
    return newName;
  },

  delete: async (targetPaths: string[], permanent?: boolean): Promise<boolean> => {
    if (isElectron) return await window.clankerApi!.delete(targetPaths, permanent);
    return true;
  },

  copy: async (sourcePaths: string[], targetDir: string): Promise<boolean> => {
    if (isElectron) return await window.clankerApi!.copy(sourcePaths, targetDir);
    return true;
  },

  move: async (sourcePaths: string[], targetDir: string): Promise<boolean> => {
    if (isElectron) return await window.clankerApi!.move(sourcePaths, targetDir);
    return true;
  },

  // Batch Rename
  previewBatchRename: async (
    targetPaths: string[],
    rule: BatchRenameRule
  ): Promise<BatchRenamePreviewItem[]> => {
    if (isElectron) return await window.clankerApi!.previewBatchRename(targetPaths, rule);
    return targetPaths.map((p) => {
      const name = p.split('\\').pop() || '';
      return {
        originalPath: p,
        originalName: name,
        newName: `renamed_${name}`,
        newPath: p.replace(name, `renamed_${name}`),
        willChange: true,
        hasConflict: false,
      };
    });
  },

  executeBatchRename: async (
    items: { originalPath: string; newPath: string }[]
  ): Promise<{ successCount: number; errors: string[] }> => {
    if (isElectron) return await window.clankerApi!.executeBatchRename(items);
    return { successCount: items.length, errors: [] };
  },

  // Shell & Integrations
  openItem: async (itemPath: string): Promise<string> => {
    if (isElectron) return await window.clankerApi!.openItem(itemPath);
    console.log('Open item:', itemPath);
    return '';
  },

  showInFolder: async (itemPath: string): Promise<boolean> => {
    if (isElectron) return await window.clankerApi!.showInFolder(itemPath);
    return true;
  },

  openTerminal: async (dirPath: string, terminalType?: string): Promise<boolean> => {
    if (isElectron) return await window.clankerApi!.openTerminal(dirPath, terminalType);
    console.log('Open terminal in:', dirPath, terminalType);
    return true;
  },

  openEditor: async (dirOrFilePath: string): Promise<boolean> => {
    if (isElectron) return await window.clankerApi!.openEditor(dirOrFilePath);
    console.log('Open editor:', dirOrFilePath);
    return true;
  },

  // Clipboard
  writeClipboardText: async (text: string): Promise<boolean> => {
    if (isElectron) {
      return await window.clankerApi!.writeClipboardText(text);
    }
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      return false;
    }
  },
};
