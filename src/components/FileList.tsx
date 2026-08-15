import React, { useState, useEffect, useRef } from 'react';
import {
  Folder,
  File,
  FileCode,
  FileText,
  FileArchive,
  Image,
  Music,
  Video,
  Terminal,
  ShieldAlert,
  ArrowUpDown,
  ArrowUp,
  ArrowDown,
  Lock,
  ExternalLink,
  ChevronRight,
  Database,
  Cpu,
} from 'lucide-react';
import type { FileItem, SortField, SortOrder, ViewMode } from '../types/explorer';

interface FileListProps {
  items: FileItem[];
  currentPath: string;
  selectedPaths: string[];
  viewMode: ViewMode;
  sortField: SortField;
  sortOrder: SortOrder;
  filterText: string;
  isFilterRegex: boolean;
  onSelect: (paths: string[], lastFocusedPath?: string) => void;
  onOpenItem: (item: FileItem) => void;
  onContextMenu: (e: React.MouseEvent, item?: FileItem) => void;
  onSortChange: (field: SortField) => void;
  onInlineRename: (oldPath: string, newName: string) => Promise<void>;
  isLoading?: boolean;
}

export const FileList: React.FC<FileListProps> = ({
  items,
  currentPath,
  selectedPaths,
  viewMode,
  sortField,
  sortOrder,
  filterText,
  isFilterRegex,
  onSelect,
  onOpenItem,
  onContextMenu,
  onSortChange,
  onInlineRename,
  isLoading,
}) => {
  const [renamingPath, setRenamingPath] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const renameInputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (renamingPath && renameInputRef.current) {
      renameInputRef.current.focus();
      // Select filename without extension
      const lastDot = renameValue.lastIndexOf('.');
      if (lastDot > 0) {
        renameInputRef.current.setSelectionRange(0, lastDot);
      } else {
        renameInputRef.current.select();
      }
    }
  }, [renamingPath]);

  // Filter items based on filterText
  const filteredItems = items.filter((item) => {
    if (!filterText) return true;

    if (isFilterRegex) {
      try {
        const regex = new RegExp(filterText, 'i');
        return regex.test(item.name) || regex.test(item.extension);
      } catch {
        return item.name.toLowerCase().includes(filterText.toLowerCase());
      }
    }

    // Wildcard support e.g. *.ts or test*
    if (filterText.includes('*') || filterText.includes('?')) {
      const globPattern = filterText
        .replace(/[.+^${}()|[\]\\]/g, '\\$&')
        .replace(/\*/g, '.*')
        .replace(/\?/g, '.');
      try {
        const regex = new RegExp(`^${globPattern}$`, 'i');
        return regex.test(item.name);
      } catch {
        return item.name.toLowerCase().includes(filterText.toLowerCase());
      }
    }

    return (
      item.name.toLowerCase().includes(filterText.toLowerCase()) ||
      item.extension.toLowerCase().includes(filterText.toLowerCase())
    );
  });

  // Sort items (folders always on top unless sorting by size/ext strictly)
  const sortedItems = [...filteredItems].sort((a, b) => {
    // Folders first
    if (a.isDirectory && !b.isDirectory) return -1;
    if (!a.isDirectory && b.isDirectory) return 1;

    let comp = 0;
    switch (sortField) {
      case 'name':
        comp = a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
        break;
      case 'extension':
        comp = a.extension.localeCompare(b.extension);
        break;
      case 'size':
        comp = a.size - b.size;
        break;
      case 'mtime':
        comp = a.mtime - b.mtime;
        break;
      case 'ctime':
        comp = a.ctime - b.ctime;
        break;
      case 'attributes':
        comp = a.attributesString.localeCompare(b.attributesString);
        break;
      default:
        comp = a.name.localeCompare(b.name);
    }

    return sortOrder === 'asc' ? comp : -comp;
  });

  const getFileIcon = (item: FileItem) => {
    if (item.isDirectory) {
      return (
        <Folder
          size={15}
          className={item.isHidden ? 'text-[#eab308]/70' : 'text-[#38bdf8]'}
          fill={item.isHidden ? 'rgba(234,179,8,0.2)' : 'rgba(56,189,248,0.15)'}
        />
      );
    }

    const ext = item.extension.toLowerCase();
    if (['.ts', '.tsx', '.js', '.jsx', '.json', '.html', '.css', '.py', '.rs', '.cs', '.c', '.cpp', '.sh', '.ps1'].includes(ext)) {
      return <FileCode size={15} className="text-[#38bdf8]" />;
    }
    if (['.png', '.jpg', '.jpeg', '.gif', '.svg', '.webp', '.ico', '.bmp'].includes(ext)) {
      return <Image size={15} className="text-[#ec4899]" />;
    }
    if (['.mp3', '.wav', '.ogg', '.flac', '.m4a'].includes(ext)) {
      return <Music size={15} className="text-[#a855f7]" />;
    }
    if (['.mp4', '.mkv', '.webm', '.mov', '.avi'].includes(ext)) {
      return <Video size={15} className="text-[#ef4444]" />;
    }
    if (['.zip', '.rar', '.7z', '.tar', '.gz', '.tar.gz', '.bz2'].includes(ext)) {
      return <FileArchive size={15} className="text-[#f59e0b]" />;
    }
    if (['.exe', '.dll', '.bat', '.cmd', '.msi', '.ps1'].includes(ext)) {
      return <Cpu size={15} className="text-[#10b981]" />;
    }
    if (['.sql', '.db', '.sqlite', '.dat'].includes(ext)) {
      return <Database size={15} className="text-[#8b5cf6]" />;
    }
    return <FileText size={15} className="text-[#94a3b8]" />;
  };

  const handleRowClick = (e: React.MouseEvent, item: FileItem, index: number) => {
    e.stopPropagation();

    if (e.ctrlKey || e.metaKey) {
      // Toggle selection
      if (selectedPaths.includes(item.path)) {
        onSelect(selectedPaths.filter((p) => p !== item.path), item.path);
      } else {
        onSelect([...selectedPaths, item.path], item.path);
      }
    } else if (e.shiftKey && selectedPaths.length > 0) {
      // Range selection
      const lastIndex = sortedItems.findIndex((i) => i.path === selectedPaths[selectedPaths.length - 1]);
      const start = Math.min(lastIndex, index);
      const end = Math.max(lastIndex, index);
      const range = sortedItems.slice(start, end + 1).map((i) => i.path);
      onSelect(Array.from(new Set([...selectedPaths, ...range])), item.path);
    } else {
      // Single selection
      onSelect([item.path], item.path);
    }
  };

  const handleStartRename = (item: FileItem) => {
    setRenamingPath(item.path);
    setRenameValue(item.name);
  };

  const handleFinishRename = async () => {
    if (renamingPath && renameValue.trim()) {
      await onInlineRename(renamingPath, renameValue.trim());
    }
    setRenamingPath(null);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (renamingPath) return;

    if (e.key === 'F2' && selectedPaths.length === 1) {
      e.preventDefault();
      const item = sortedItems.find((i) => i.path === selectedPaths[0]);
      if (item) handleStartRename(item);
    } else if (e.key === 'Enter' && selectedPaths.length === 1) {
      e.preventDefault();
      const item = sortedItems.find((i) => i.path === selectedPaths[0]);
      if (item) onOpenItem(item);
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const currentIndex = sortedItems.findIndex((i) => i.path === selectedPaths[0]);
      const nextIndex =
        e.key === 'ArrowDown'
          ? Math.min(sortedItems.length - 1, currentIndex + 1)
          : Math.max(0, currentIndex - 1);

      if (sortedItems[nextIndex]) {
        onSelect([sortedItems[nextIndex].path], sortedItems[nextIndex].path);
      }
    }
  };

  const renderSortIndicator = (field: SortField) => {
    if (sortField !== field) {
      return <ArrowUpDown size={11} className="opacity-0 group-hover:opacity-40 ml-1 inline" />;
    }
    return sortOrder === 'asc' ? (
      <ArrowUp size={12} className="text-[#38bdf8] ml-1 inline" />
    ) : (
      <ArrowDown size={12} className="text-[#38bdf8] ml-1 inline" />
    );
  };

  return (
    <div
      ref={containerRef}
      tabIndex={0}
      onKeyDown={handleKeyDown}
      onContextMenu={(e) => onContextMenu(e)}
      onClick={() => onSelect([])}
      className="flex-1 bg-[#090d16] overflow-y-auto overflow-x-hidden select-none outline-none relative"
    >
      {/* Details View (Default Power-User High Density) */}
      {viewMode === 'details' && (
        <table className="file-table font-sans">
          <thead>
            <tr>
              <th
                onClick={(e) => {
                  e.stopPropagation();
                  onSortChange('name');
                }}
                className="text-left w-[40%] cursor-pointer group hover:text-[#f8fafc]"
              >
                <span>Name</span>
                {renderSortIndicator('name')}
              </th>
              <th
                onClick={(e) => {
                  e.stopPropagation();
                  onSortChange('extension');
                }}
                className="text-left w-[10%] cursor-pointer group hover:text-[#f8fafc]"
              >
                <span>Ext</span>
                {renderSortIndicator('extension')}
              </th>
              <th
                onClick={(e) => {
                  e.stopPropagation();
                  onSortChange('size');
                }}
                className="text-right w-[14%] cursor-pointer group hover:text-[#f8fafc]"
              >
                <span>Size</span>
                {renderSortIndicator('size')}
              </th>
              <th
                onClick={(e) => {
                  e.stopPropagation();
                  onSortChange('mtime');
                }}
                className="text-left w-[18%] cursor-pointer group hover:text-[#f8fafc]"
              >
                <span>Date Modified</span>
                {renderSortIndicator('mtime')}
              </th>
              <th
                onClick={(e) => {
                  e.stopPropagation();
                  onSortChange('attributes');
                }}
                className="text-center w-[18%] cursor-pointer group hover:text-[#f8fafc]"
              >
                <span>Attributes</span>
                {renderSortIndicator('attributes')}
              </th>
            </tr>
          </thead>
          <tbody>
            {sortedItems.map((item, index) => {
              const isSelected = selectedPaths.includes(item.path);
              const isRenaming = renamingPath === item.path;

              return (
                <tr
                  key={item.path}
                  onClick={(e) => handleRowClick(e, item, index)}
                  onDoubleClick={(e) => {
                    e.stopPropagation();
                    onOpenItem(item);
                  }}
                  onContextMenu={(e) => {
                    e.stopPropagation();
                    if (!isSelected) onSelect([item.path], item.path);
                    onContextMenu(e, item);
                  }}
                  className={`file-row ${isSelected ? 'selected' : ''} ${
                    item.isHidden ? 'is-hidden' : ''
                  }`}
                >
                  {/* Name Column */}
                  <td className="flex items-center gap-2">
                    <div className="shrink-0">{getFileIcon(item)}</div>

                    {isRenaming ? (
                      <form
                        onSubmit={(e) => {
                          e.preventDefault();
                          handleFinishRename();
                        }}
                        className="flex-1"
                        onClick={(e) => e.stopPropagation()}
                      >
                        <input
                          ref={renameInputRef}
                          type="text"
                          value={renameValue}
                          onChange={(e) => setRenameValue(e.target.value)}
                          onBlur={handleFinishRename}
                          onKeyDown={(e) => {
                            if (e.key === 'Escape') setRenamingPath(null);
                          }}
                          className="w-full bg-[#0f1626] text-[#f8fafc] px-1.5 py-0.5 rounded border border-[#0284c7] outline-none text-xs font-mono-code"
                        />
                      </form>
                    ) : (
                      <div className="flex items-center gap-1.5 overflow-hidden">
                        <span
                          className={`truncate text-[12.5px] ${
                            isSelected
                              ? 'text-white font-medium'
                              : item.isHidden
                              ? 'text-[#94a3b8]'
                              : 'text-[#f1f5f9]'
                          }`}
                        >
                          {item.name}
                        </span>

                        {/* Hidden Pill - always visible, but flags the file */}
                        {item.isHidden && (
                          <span className="badge-hidden shrink-0" title="Hidden file (Visible in Clanker)">
                            HIDDEN
                          </span>
                        )}

                        {/* System Pill */}
                        {item.isSystem && (
                          <span className="badge-sys shrink-0" title="Windows System File">
                            SYS
                          </span>
                        )}
                      </div>
                    )}
                  </td>

                  {/* Extension Column */}
                  <td>
                    {item.extension ? (
                      <span className="badge-ext">{item.extension}</span>
                    ) : item.isDirectory ? (
                      <span className="text-[#64748b] text-[11px]">&lt;DIR&gt;</span>
                    ) : (
                      <span className="text-[#64748b] text-[11px]">—</span>
                    )}
                  </td>

                  {/* Size Column */}
                  <td className="text-right font-mono-code text-[12px] text-[#cbd5e1]">
                    <span title={item.isDirectory ? 'Directory' : `${item.size.toLocaleString()} bytes`}>
                      {item.formattedSize}
                    </span>
                  </td>

                  {/* Date Modified */}
                  <td className="font-mono-code text-[11.5px] text-[#94a3b8]">
                    {item.formattedMtime}
                  </td>

                  {/* Attributes Column */}
                  <td className="text-center font-mono-code text-[11px]">
                    <span className="badge-attr tracking-widest">{item.attributesString || '—'}</span>
                  </td>
                </tr>
              );
            })}

            {sortedItems.length === 0 && !isLoading && (
              <tr>
                <td colSpan={5} className="text-center py-12 text-[#64748b]">
                  {filterText ? `No items matching filter "${filterText}"` : 'This directory is empty'}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      )}

      {/* Compact View Mode */}
      {viewMode === 'compact' && (
        <div className="p-3 grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-1.5">
          {sortedItems.map((item, index) => {
            const isSelected = selectedPaths.includes(item.path);
            return (
              <div
                key={item.path}
                onClick={(e) => handleRowClick(e, item, index)}
                onDoubleClick={(e) => {
                  e.stopPropagation();
                  onOpenItem(item);
                }}
                onContextMenu={(e) => {
                  e.stopPropagation();
                  if (!isSelected) onSelect([item.path], item.path);
                  onContextMenu(e, item);
                }}
                className={`flex items-center gap-2 p-1.5 rounded cursor-pointer transition-colors border ${
                  isSelected
                    ? 'bg-[#0284c7]/20 border-[#38bdf8] text-white font-medium'
                    : 'bg-[#0f1626]/40 hover:bg-[#152035] border-[#1e293b]/60 text-[#cbd5e1]'
                } ${item.isHidden ? 'opacity-75' : ''}`}
              >
                <div className="shrink-0">{getFileIcon(item)}</div>
                <span className="truncate text-[12px] font-sans flex-1">{item.name}</span>
                {item.extension && <span className="badge-ext text-[9.5px]">{item.extension}</span>}
              </div>
            );
          })}
        </div>
      )}

      {/* Grid View Mode */}
      {viewMode === 'grid' && (
        <div className="p-4 grid grid-cols-3 sm:grid-cols-4 md:grid-cols-6 lg:grid-cols-8 gap-3">
          {sortedItems.map((item, index) => {
            const isSelected = selectedPaths.includes(item.path);
            return (
              <div
                key={item.path}
                onClick={(e) => handleRowClick(e, item, index)}
                onDoubleClick={(e) => {
                  e.stopPropagation();
                  onOpenItem(item);
                }}
                onContextMenu={(e) => {
                  e.stopPropagation();
                  if (!isSelected) onSelect([item.path], item.path);
                  onContextMenu(e, item);
                }}
                className={`flex flex-col items-center justify-center p-3 rounded-lg cursor-pointer transition-all border text-center group ${
                  isSelected
                    ? 'bg-[#0284c7]/20 border-[#38bdf8] shadow-lg'
                    : 'bg-[#0f1626]/60 hover:bg-[#152035] border-[#1e293b]'
                }`}
              >
                <div className="p-2 rounded-md bg-[#090d16] border border-[#1e293b] group-hover:border-[#38bdf8]/50 mb-2">
                  {getFileIcon(item)}
                </div>
                <span className="text-[11.5px] text-[#f1f5f9] truncate w-full px-1 font-sans">
                  {item.name}
                </span>
                <span className="text-[10px] font-mono-code text-[#64748b] mt-0.5">
                  {item.formattedSize}
                </span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
};
