import React, { useState, useEffect, useRef } from 'react';
import {
  ArrowLeft,
  ArrowRight,
  ArrowUp,
  RotateCw,
  Copy,
  Check,
  ChevronRight,
  Folder,
  FolderPlus,
  FilePlus,
  Terminal,
  Code2,
  ChevronDown,
  Edit2,
  HardDrive,
} from 'lucide-react';
import { fileService } from '../services/fileService';

interface AddressBarProps {
  currentPath: string;
  canGoBack: boolean;
  canGoForward: boolean;
  onNavigate: (path: string) => void;
  onGoBack: () => void;
  onGoForward: () => void;
  onGoUp: () => void;
  onRefresh: () => void;
  onNewFolder: () => void;
  onNewFile: () => void;
}

export const AddressBar: React.FC<AddressBarProps> = ({
  currentPath,
  canGoBack,
  canGoForward,
  onNavigate,
  onGoBack,
  onGoForward,
  onGoUp,
  onRefresh,
  onNewFolder,
  onNewFile,
}) => {
  const [isEditing, setIsEditing] = useState(false);
  const [inputValue, setInputValue] = useState(currentPath);
  const [copiedType, setCopiedType] = useState<string | null>(null);
  const [showCopyMenu, setShowCopyMenu] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    setInputValue(currentPath);
  }, [currentPath]);

  useEffect(() => {
    if (isEditing && inputRef.current) {
      inputRef.current.focus();
      inputRef.current.select();
    }
  }, [isEditing]);

  // Break current path into breadcrumbs
  const getBreadcrumbs = () => {
    const isWindows = currentPath.includes('\\') || currentPath.match(/^[A-Z]:/i);
    const separator = isWindows ? '\\' : '/';
    const parts = currentPath.split(/[\\/]/).filter(Boolean);

    const crumbs: { name: string; fullPath: string; isDrive: boolean }[] = [];

    if (isWindows && currentPath.match(/^[A-Z]:/i)) {
      const driveLetter = currentPath.substring(0, 2);
      crumbs.push({
        name: driveLetter,
        fullPath: `${driveLetter}\\`,
        isDrive: true,
      });

      let accumulated = `${driveLetter}\\`;
      for (let i = 1; i < parts.length; i++) {
        accumulated = `${accumulated}${parts[i]}\\`;
        crumbs.push({
          name: parts[i],
          fullPath: accumulated.endsWith('\\') ? accumulated.slice(0, -1) : accumulated,
          isDrive: false,
        });
      }
    } else {
      let accumulated = '';
      for (const part of parts) {
        accumulated = `${accumulated}/${part}`;
        crumbs.push({
          name: part,
          fullPath: accumulated,
          isDrive: false,
        });
      }
    }

    return crumbs;
  };

  const handleInputSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (inputValue.trim()) {
      onNavigate(inputValue.trim());
      setIsEditing(false);
    }
  };

  const handleInputKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      setInputValue(currentPath);
      setIsEditing(false);
    }
  };

  const copyToClipboard = async (text: string, label: string) => {
    await fileService.writeClipboardText(text);
    setCopiedType(label);
    setShowCopyMenu(false);
    setTimeout(() => {
      setCopiedType(null);
    }, 2000);
  };

  const breadcrumbs = getBreadcrumbs();

  return (
    <div className="flex items-center gap-1.5 bg-[#0f1626] border-b border-[#1e293b] px-2 py-1.5 text-xs select-none">
      {/* Navigation History & Up Buttons */}
      <div className="flex items-center gap-0.5">
        <button
          onClick={onGoBack}
          disabled={!canGoBack}
          title="Back (Alt+Left)"
          className={`p-1.5 rounded transition-colors ${
            canGoBack
              ? 'hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#f8fafc]'
              : 'text-[#475569] cursor-not-allowed'
          }`}
        >
          <ArrowLeft size={14} />
        </button>

        <button
          onClick={onGoForward}
          disabled={!canGoForward}
          title="Forward (Alt+Right)"
          className={`p-1.5 rounded transition-colors ${
            canGoForward
              ? 'hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#f8fafc]'
              : 'text-[#475569] cursor-not-allowed'
          }`}
        >
          <ArrowRight size={14} />
        </button>

        <button
          onClick={onGoUp}
          title="Up one level (Alt+Up)"
          className="p-1.5 rounded hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#f8fafc] transition-colors"
        >
          <ArrowUp size={14} />
        </button>

        <button
          onClick={onRefresh}
          title="Refresh (F5)"
          className="p-1.5 rounded hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#f8fafc] transition-colors"
        >
          <RotateCw size={13} />
        </button>
      </div>

      {/* Interactive & Always-Visible Path Bar */}
      <div className="relative flex-1 flex items-center bg-[#090d16] border border-[#1e293b] hover:border-[#334155] focus-within:border-[#0284c7] rounded px-2 h-7.5 transition-colors group">
        {isEditing ? (
          <form onSubmit={handleInputSubmit} className="w-full flex items-center">
            <input
              ref={inputRef}
              type="text"
              value={inputValue}
              onChange={(e) => setInputValue(e.target.value)}
              onKeyDown={handleInputKeyDown}
              onBlur={() => setIsEditing(false)}
              className="w-full bg-transparent text-[#f8fafc] font-mono-code text-[12px] outline-none"
              placeholder="Enter directory path..."
              spellCheck={false}
            />
          </form>
        ) : (
          <div
            onClick={() => setIsEditing(true)}
            className="flex items-center gap-1 w-full overflow-x-auto no-scrollbar cursor-text h-full"
            title="Click to edit raw path (Ctrl+L)"
          >
            {breadcrumbs.map((crumb, idx) => (
              <React.Fragment key={crumb.fullPath + idx}>
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    onNavigate(crumb.fullPath);
                  }}
                  className="flex items-center gap-1 px-1 py-0.5 rounded hover:bg-[#1c2b45] text-[#cbd5e1] hover:text-[#38bdf8] font-mono-code text-[11.5px] transition-colors whitespace-nowrap"
                >
                  {crumb.isDrive ? (
                    <HardDrive size={12} className="text-[#38bdf8]" />
                  ) : (
                    <Folder size={12} className="text-[#64748b]" />
                  )}
                  <span>{crumb.name}</span>
                </button>
                {idx < breadcrumbs.length - 1 && (
                  <ChevronRight size={11} className="text-[#475569] shrink-0" />
                )}
              </React.Fragment>
            ))}

            {/* Click to edit spacer */}
            <div className="flex-1 min-w-[30px] h-full" />
          </div>
        )}

        {/* Edit Raw Path Icon */}
        {!isEditing && (
          <button
            onClick={() => setIsEditing(true)}
            title="Edit raw path (Ctrl+L)"
            className="opacity-0 group-hover:opacity-100 p-1 hover:bg-[#1c2b45] text-[#64748b] hover:text-[#94a3b8] rounded transition-all"
          >
            <Edit2 size={11} />
          </button>
        )}
      </div>

      {/* 1-Click Copy Path Button & Options Dropdown */}
      <div className="relative flex items-center">
        <button
          onClick={() => copyToClipboard(currentPath, 'Path')}
          title="Copy Directory Path to Clipboard (Ctrl+Shift+C)"
          className={`flex items-center gap-1.5 px-2.5 h-7.5 rounded text-[11.5px] font-mono-code font-medium transition-all ${
            copiedType
              ? 'bg-[#10b981] text-black font-semibold'
              : 'bg-[#152035] hover:bg-[#0284c7] text-[#38bdf8] hover:text-white border border-[#1e293b]'
          }`}
        >
          {copiedType ? <Check size={13} /> : <Copy size={13} />}
          <span>{copiedType ? `${copiedType} Copied!` : 'Copy Path'}</span>
        </button>

        {/* Dropdown toggle for copy variants */}
        <button
          onClick={() => setShowCopyMenu(!showCopyMenu)}
          title="More copy path options"
          className="h-7.5 px-1 bg-[#152035] hover:bg-[#1c2b45] border-y border-r border-[#1e293b] rounded-r text-[#94a3b8] hover:text-white transition-colors"
        >
          <ChevronDown size={12} />
        </button>

        {showCopyMenu && (
          <div className="glass-dropdown absolute right-0 top-full mt-1 w-48 rounded shadow-xl py-1 z-50 text-[11.5px] font-mono-code">
            <button
              onClick={() => copyToClipboard(currentPath, 'Raw Path')}
              className="w-full text-left px-3 py-1.5 hover:bg-[#1c2b45] text-[#f8fafc] flex items-center gap-2"
            >
              <Copy size={12} className="text-[#38bdf8]" />
              <span>Copy Raw Path</span>
            </button>
            <button
              onClick={() => copyToClipboard(`"${currentPath}"`, 'Quoted Path')}
              className="w-full text-left px-3 py-1.5 hover:bg-[#1c2b45] text-[#f8fafc] flex items-center gap-2"
            >
              <span className="text-[#38bdf8] font-bold">""</span>
              <span>Copy Quoted Path</span>
            </button>
            <button
              onClick={() => copyToClipboard(currentPath.replace(/\\/g, '/'), 'POSIX Path')}
              className="w-full text-left px-3 py-1.5 hover:bg-[#1c2b45] text-[#f8fafc] flex items-center gap-2"
            >
              <span className="text-[#38bdf8] font-bold">/</span>
              <span>Copy POSIX Path</span>
            </button>
            <button
              onClick={() => {
                const folderName = currentPath.split(/[\\/]/).filter(Boolean).pop() || '';
                copyToClipboard(folderName, 'Folder Name');
              }}
              className="w-full text-left px-3 py-1.5 hover:bg-[#1c2b45] text-[#f8fafc] flex items-center gap-2"
            >
              <Folder size={12} className="text-[#38bdf8]" />
              <span>Copy Folder Name</span>
            </button>
          </div>
        )}
      </div>

      <div className="w-[1px] h-4 bg-[#1e293b] mx-0.5" />

      {/* Fast Creation Quick Buttons */}
      <div className="flex items-center gap-1">
        <button
          onClick={onNewFolder}
          title="New Folder (Ctrl+Shift+N)"
          className="flex items-center gap-1 px-2 h-7.5 rounded bg-[#152035] hover:bg-[#1c2b45] border border-[#1e293b] text-[#94a3b8] hover:text-[#38bdf8] transition-colors text-[11px]"
        >
          <FolderPlus size={13} className="text-[#38bdf8]" />
          <span className="hidden md:inline">New Folder</span>
        </button>

        <button
          onClick={onNewFile}
          title="New File (Ctrl+N)"
          className="flex items-center gap-1 px-2 h-7.5 rounded bg-[#152035] hover:bg-[#1c2b45] border border-[#1e293b] text-[#94a3b8] hover:text-[#38bdf8] transition-colors text-[11px]"
        >
          <FilePlus size={13} className="text-[#38bdf8]" />
          <span className="hidden md:inline">New File</span>
        </button>
      </div>
    </div>
  );
};
