export interface FileItem {
  id: string;
  name: string;
  extension: string; // e.g. ".ts", ".tar.gz", ".exe", or "" for folders
  path: string;
  parentPath: string;
  isDirectory: boolean;
  isSymbolicLink: boolean;
  size: number; // in raw bytes
  formattedSize: string; // e.g. "1.45 MB" or "<DIR>"
  mtime: number;
  ctime: number;
  atime: number;
  formattedMtime: string;
  formattedCtime: string;
  formattedAtime: string;
  isHidden: boolean;
  isSystem: boolean;
  isReadOnly: boolean;
  isArchive: boolean;
  attributesString: string;
}

export interface DriveInfo {
  letter: string;
  name: string;
  root: string;
  type: 'fixed' | 'removable' | 'network' | 'cdrom' | 'ram' | 'unknown';
  totalSpace: number;
  freeSpace: number;
  usedSpace: number;
  percentUsed: number;
  formattedTotal: string;
  formattedFree: string;
  formattedUsed: string;
}

export interface QuickAccessItem {
  id: string;
  name: string;
  path: string;
  icon: 'desktop' | 'downloads' | 'documents' | 'pictures' | 'music' | 'videos' | 'home' | 'folder' | 'drive' | 'code' | 'trash';
  isCustom?: boolean;
}

export type ViewMode = 'details' | 'compact' | 'grid' | 'tree';
export type SortField = 'name' | 'extension' | 'size' | 'mtime' | 'ctime' | 'type' | 'attributes';
export type SortOrder = 'asc' | 'desc';

export interface ExplorerTab {
  id: string;
  title: string;
  currentPath: string;
  history: string[];
  historyIndex: number;
  isPinned?: boolean;
  isLocked?: boolean;
  viewMode: ViewMode;
  sortField: SortField;
  sortOrder: SortOrder;
  filterText: string;
  filterRegex: boolean;
  filterWildcard: boolean;
  selectedPaths: string[];
  lastFocusedPath?: string;
  scrollPosition?: number;
}

export interface PaneState {
  id: string;
  tabs: ExplorerTab[];
  activeTabId: string;
}

export interface BatchRenameRule {
  mode: 'replace' | 'prefix_suffix' | 'numbering' | 'change_case';
  findText?: string;
  replaceText?: string;
  isRegex?: boolean;
  caseSensitive?: boolean;
  prefix?: string;
  suffix?: string;
  startNumber?: number;
  padding?: number;
  caseMode?: 'lower' | 'upper' | 'title' | 'camel';
}

export interface BatchRenamePreviewItem {
  originalPath: string;
  originalName: string;
  newName: string;
  newPath: string;
  willChange: boolean;
  hasConflict: boolean;
  error?: string;
}

export interface FilePreviewData {
  path: string;
  name: string;
  extension: string;
  size: number;
  formattedSize: string;
  mtime: string;
  type: 'text' | 'image' | 'audio' | 'video' | 'binary' | 'directory' | 'error';
  textContent?: string;
  lineCount?: number;
  encoding?: string;
  mediaUrl?: string;
  hexData?: {
    offset: string;
    hex: string;
    ascii: string;
  }[];
  imageDimensions?: { width: number; height: number };
  error?: string;
}

export interface HashResult {
  md5: string;
  sha256: string;
}

export interface ClipboardState {
  operation: 'copy' | 'cut' | null;
  paths: string[];
}

declare global {
  interface Window {
    clankerApi?: {
      minimize: () => Promise<void>;
      maximize: () => Promise<void>;
      close: () => Promise<void>;
      isMaximized: () => Promise<boolean>;
      getDrives: () => Promise<DriveInfo[]>;
      getQuickAccess: () => Promise<QuickAccessItem[]>;
      readDir: (dirPath: string) => Promise<{ items: FileItem[]; error?: string; currentPath: string }>;
      getPreviewData: (filePath: string) => Promise<FilePreviewData>;
      calculateHash: (filePath: string) => Promise<HashResult>;
      createFolder: (parentDir: string, folderName: string) => Promise<string>;
      createFile: (parentDir: string, fileName: string) => Promise<string>;
      rename: (oldPath: string, newName: string) => Promise<string>;
      delete: (targetPaths: string[], permanent?: boolean) => Promise<boolean>;
      copy: (sourcePaths: string[], targetDir: string) => Promise<boolean>;
      move: (sourcePaths: string[], targetDir: string) => Promise<boolean>;
      previewBatchRename: (targetPaths: string[], rule: BatchRenameRule) => Promise<BatchRenamePreviewItem[]>;
      executeBatchRename: (items: { originalPath: string; newPath: string }[]) => Promise<{ successCount: number; errors: string[] }>;
      openItem: (itemPath: string) => Promise<string>;
      showInFolder: (itemPath: string) => Promise<boolean>;
      openTerminal: (dirPath: string, terminalType?: string) => Promise<boolean>;
      openEditor: (dirOrFilePath: string) => Promise<boolean>;
      writeClipboardText: (text: string) => Promise<boolean>;
    };
  }
}
