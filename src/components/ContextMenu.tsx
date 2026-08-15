import React, { useEffect, useRef } from 'react';
import {
  FolderOpen,
  Terminal,
  Cpu,
  Copy,
  Scissors,
  Clipboard,
  Trash2,
  FileEdit,
  Info,
  Hash,
  ExternalLink,
  FolderPlus,
  FilePlus,
  RefreshCw,
} from 'lucide-react';
import type { FileItem } from '../types/explorer';

interface ContextMenuProps {
  x: number;
  y: number;
  targetItem?: FileItem;
  currentPath: string;
  selectedPaths: string[];
  clipboardCount: number;
  onClose: () => void;
  onOpen: () => void;
  onCopyPath: (type?: 'raw' | 'quoted' | 'posix') => void;
  onCopy: () => void;
  onCut: () => void;
  onPaste: () => void;
  onRename: () => void;
  onBatchRename: () => void;
  onDelete: (permanent?: boolean) => void;
  onOpenTerminal: () => void;
  onOpenVSCode: () => void;
  onNewFolder: () => void;
  onNewFile: () => void;
  onProperties: () => void;
}

export const ContextMenu: React.FC<ContextMenuProps> = ({
  x,
  y,
  targetItem,
  currentPath,
  selectedPaths,
  clipboardCount,
  onClose,
  onOpen,
  onCopyPath,
  onCopy,
  onCut,
  onPaste,
  onRename,
  onBatchRename,
  onDelete,
  onOpenTerminal,
  onOpenVSCode,
  onNewFolder,
  onNewFile,
  onProperties,
}) => {
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClickOutside = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        onClose();
      }
    };
    window.addEventListener('mousedown', handleClickOutside);
    return () => window.removeEventListener('mousedown', handleClickOutside);
  }, [onClose]);

  // Ensure menu doesn't overflow viewport
  const adjustedX = Math.min(x, window.innerWidth - 220);
  const adjustedY = Math.min(y, window.innerHeight - 340);

  return (
    <div
      ref={menuRef}
      style={{ left: `${adjustedX}px`, top: `${adjustedY}px` }}
      className="glass-dropdown fixed z-50 w-56 rounded-lg py-1 shadow-2xl text-xs font-sans text-[#f8fafc] border border-[#334155] select-none"
    >
      {targetItem ? (
        <>
          {/* Open Action */}
          <button
            onClick={() => {
              onOpen();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left text-[#38bdf8] font-semibold"
          >
            <FolderOpen size={14} />
            <span>Open {targetItem.isDirectory ? 'Folder' : 'File'}</span>
          </button>

          <div className="h-[1px] bg-[#1e293b] my-1" />

          {/* Copy Paths */}
          <button
            onClick={() => {
              onCopyPath('raw');
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center justify-between text-left"
          >
            <div className="flex items-center gap-2.5">
              <Copy size={13} className="text-[#38bdf8]" />
              <span>Copy Full Path</span>
            </div>
            <span className="text-[10px] font-mono-code text-[#64748b]">Ctrl+Shift+C</span>
          </button>

          <button
            onClick={() => {
              onCopyPath('quoted');
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left text-[11.5px]"
          >
            <span className="w-3 text-center text-[#38bdf8] font-bold">""</span>
            <span>Copy as "Quoted Path"</span>
          </button>

          <button
            onClick={() => {
              onCopyPath('posix');
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left text-[11.5px]"
          >
            <span className="w-3 text-center text-[#38bdf8] font-bold">/</span>
            <span>Copy as POSIX Path</span>
          </button>

          <div className="h-[1px] bg-[#1e293b] my-1" />

          {/* Standard File Ops */}
          <button
            onClick={() => {
              onCut();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center justify-between text-left"
          >
            <div className="flex items-center gap-2.5">
              <Scissors size={13} className="text-[#94a3b8]" />
              <span>Cut</span>
            </div>
            <span className="text-[10px] font-mono-code text-[#64748b]">Ctrl+X</span>
          </button>

          <button
            onClick={() => {
              onCopy();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center justify-between text-left"
          >
            <div className="flex items-center gap-2.5">
              <Copy size={13} className="text-[#94a3b8]" />
              <span>Copy</span>
            </div>
            <span className="text-[10px] font-mono-code text-[#64748b]">Ctrl+C</span>
          </button>

          {/* Rename / Batch Rename */}
          {selectedPaths.length > 1 ? (
            <button
              onClick={() => {
                onBatchRename();
                onClose();
              }}
              className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left text-[#38bdf8]"
            >
              <FileEdit size={13} />
              <span>Power Batch Rename...</span>
            </button>
          ) : (
            <button
              onClick={() => {
                onRename();
                onClose();
              }}
              className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center justify-between text-left"
            >
              <div className="flex items-center gap-2.5">
                <FileEdit size={13} className="text-[#94a3b8]" />
                <span>Rename</span>
              </div>
              <span className="text-[10px] font-mono-code text-[#64748b]">F2</span>
            </button>
          )}

          {/* Delete */}
          <button
            onClick={() => {
              onDelete(false);
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#ef4444]/20 text-[#ef4444] flex items-center justify-between text-left"
          >
            <div className="flex items-center gap-2.5">
              <Trash2 size={13} />
              <span>Delete to Recycle Bin</span>
            </div>
            <span className="text-[10px] font-mono-code text-[#ef4444]/80">Del</span>
          </button>

          <div className="h-[1px] bg-[#1e293b] my-1" />

          {/* Power Shell & VS Code */}
          <button
            onClick={() => {
              onOpenTerminal();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left"
          >
            <Terminal size={13} className="text-[#38bdf8]" />
            <span>Open PowerShell Here</span>
          </button>

          <button
            onClick={() => {
              onOpenVSCode();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left"
          >
            <Cpu size={13} className="text-[#38bdf8]" />
            <span>Open in VS Code</span>
          </button>

          <div className="h-[1px] bg-[#1e293b] my-1" />

          {/* Properties */}
          <button
            onClick={() => {
              onProperties();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left text-[#94a3b8]"
          >
            <Info size={13} />
            <span>Properties & Checksums</span>
          </button>
        </>
      ) : (
        /* Context menu on empty background */
        <>
          <button
            onClick={() => {
              onNewFolder();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left"
          >
            <FolderPlus size={13} className="text-[#38bdf8]" />
            <span>New Folder</span>
          </button>

          <button
            onClick={() => {
              onNewFile();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left"
          >
            <FilePlus size={13} className="text-[#38bdf8]" />
            <span>New File</span>
          </button>

          {clipboardCount > 0 && (
            <button
              onClick={() => {
                onPaste();
                onClose();
              }}
              className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center justify-between text-left"
            >
              <div className="flex items-center gap-2.5">
                <Clipboard size={13} className="text-[#10b981]" />
                <span>Paste ({clipboardCount} items)</span>
              </div>
              <span className="text-[10px] font-mono-code text-[#64748b]">Ctrl+V</span>
            </button>
          )}

          <div className="h-[1px] bg-[#1e293b] my-1" />

          <button
            onClick={() => {
              onCopyPath('raw');
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left"
          >
            <Copy size={13} className="text-[#38bdf8]" />
            <span>Copy Current Directory Path</span>
          </button>

          <button
            onClick={() => {
              onOpenTerminal();
              onClose();
            }}
            className="w-full px-3 py-1.5 hover:bg-[#1c2b45] flex items-center gap-2.5 text-left"
          >
            <Terminal size={13} className="text-[#38bdf8]" />
            <span>Open PowerShell Here</span>
          </button>
        </>
      )}
    </div>
  );
};
