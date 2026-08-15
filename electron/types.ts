export interface FileItem {
  id: string;
  name: string;
  extension: string; // e.g. ".txt", ".tar.gz", ".exe", or "" for folders/no ext
  path: string;
  parentPath: string;
  isDirectory: boolean;
  isSymbolicLink: boolean;
  size: number; // raw bytes
  formattedSize: string; // e.g. "1.45 MB"
  mtime: number; // timestamp
  ctime: number;
  atime: number;
  formattedMtime: string;
  formattedCtime: string;
  formattedAtime: string;
  // Attributes
  isHidden: boolean;
  isSystem: boolean;
  isReadOnly: boolean;
  isArchive: boolean;
  attributesString: string; // e.g. "H S R A"
}

export interface DriveInfo {
  letter: string; // e.g. "C:"
  name: string; // e.g. "Local Disk"
  root: string; // e.g. "C:\\"
  type: 'fixed' | 'removable' | 'network' | 'cdrom' | 'ram' | 'unknown';
  totalSpace: number; // bytes
  freeSpace: number; // bytes
  usedSpace: number; // bytes
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
