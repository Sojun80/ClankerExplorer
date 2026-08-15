import React, { useEffect, useState } from 'react';
import {
  Terminal,
  Columns,
  Square,
  Minus,
  Maximize2,
  Minimize2,
  X,
  Eye,
  Settings,
  FolderSync,
  Cpu,
  Layers,
} from 'lucide-react';
import { fileService, isElectron } from '../services/fileService';

interface TitleBarProps {
  currentPath: string;
  isDualPane: boolean;
  onToggleDualPane: () => void;
  showPreview: boolean;
  onTogglePreview: () => void;
  onOpenTerminal: () => void;
  onOpenVSCode: () => void;
  onRefresh: () => void;
}

export const TitleBar: React.FC<TitleBarProps> = ({
  currentPath,
  isDualPane,
  onToggleDualPane,
  showPreview,
  onTogglePreview,
  onOpenTerminal,
  onOpenVSCode,
  onRefresh,
}) => {
  const [isMaximized, setIsMaximized] = useState(false);

  useEffect(() => {
    if (isElectron) {
      fileService.isMaximized().then(setIsMaximized);
    }
  }, []);

  const handleMinimize = async () => {
    await fileService.minimize();
  };

  const handleMaximize = async () => {
    await fileService.maximize();
    const max = await fileService.isMaximized();
    setIsMaximized(max);
  };

  const handleClose = async () => {
    await fileService.close();
  };

  return (
    <header
      className="draggable-titlebar flex items-center justify-between h-9 bg-[#080c14] border-b border-[#1e293b] px-3 select-none text-xs text-[#94a3b8]"
      style={{ WebkitAppRegion: 'drag' } as any}
    >
      {/* Brand & Power User Logo */}
      <div className="flex items-center gap-2.5 no-drag" style={{ WebkitAppRegion: 'no-drag' } as any}>
        <div className="flex items-center justify-center w-5 h-5 rounded bg-gradient-to-tr from-[#0284c7] via-[#06b6d4] to-[#38bdf8] text-black font-extrabold text-[11px] shadow-sm">
          ⚡
        </div>
        <div className="flex items-center gap-1.5">
          <span className="font-bold text-[#f8fafc] tracking-wide text-xs">ClankerExplorer</span>
          <span className="bg-[#0f172a] border border-[#0284c7]/40 text-[#38bdf8] font-mono-code text-[9.5px] px-1.5 py-0.5 rounded font-semibold">
            POWER USER
          </span>
        </div>
      </div>

      {/* Global Quick Actions Bar */}
      <div
        className="flex items-center gap-1.5 no-drag bg-[#0f1626]/80 px-2 py-0.5 rounded-md border border-[#1e293b]"
        style={{ WebkitAppRegion: 'no-drag' } as any}
      >
        {/* Dual Pane Toggle */}
        <button
          onClick={onToggleDualPane}
          title={isDualPane ? 'Switch to Single Pane' : 'Switch to Dual Pane (Side-by-Side) [Ctrl+Shift+D]'}
          className={`flex items-center gap-1 px-2 py-1 rounded transition-colors text-[11px] font-medium ${
            isDualPane
              ? 'bg-[#0284c7] text-white shadow-sm'
              : 'hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#f8fafc]'
          }`}
        >
          <Columns size={13} />
          <span>{isDualPane ? 'Dual Pane' : 'Single Pane'}</span>
        </button>

        {/* Inspector / Preview Toggle */}
        <button
          onClick={onTogglePreview}
          title="Toggle Quick Inspector Panel (F3 / Space)"
          className={`flex items-center gap-1 px-2 py-1 rounded transition-colors text-[11px] font-medium ${
            showPreview
              ? 'bg-[#0369a1] text-white'
              : 'hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#f8fafc]'
          }`}
        >
          <Eye size={13} />
          <span>Inspector</span>
          <span className="text-[9px] bg-black/30 px-1 rounded font-mono-code">F3</span>
        </button>

        <div className="w-[1px] h-3.5 bg-[#1e293b] mx-0.5" />

        {/* Open in PowerShell */}
        <button
          onClick={onOpenTerminal}
          title="Open PowerShell in current folder (Ctrl+Shift+T)"
          className="flex items-center gap-1 px-1.5 py-1 rounded hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#38bdf8] transition-colors text-[11px]"
        >
          <Terminal size={13} />
          <span className="hidden sm:inline font-mono-code text-[10.5px]">PowerShell</span>
        </button>

        {/* Open in VS Code */}
        <button
          onClick={onOpenVSCode}
          title="Open in VS Code"
          className="flex items-center gap-1 px-1.5 py-1 rounded hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#38bdf8] transition-colors text-[11px]"
        >
          <Cpu size={13} />
          <span className="hidden sm:inline font-mono-code text-[10.5px]">VS Code</span>
        </button>

        {/* Refresh */}
        <button
          onClick={onRefresh}
          title="Refresh All (F5)"
          className="p-1 rounded hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#f8fafc] transition-colors"
        >
          <FolderSync size={13} />
        </button>
      </div>

      {/* Window Controls (Native Electron) */}
      <div className="flex items-center gap-1 no-drag" style={{ WebkitAppRegion: 'no-drag' } as any}>
        <button
          onClick={handleMinimize}
          title="Minimize"
          className="w-7 h-6 flex items-center justify-center rounded hover:bg-[#1e293b] text-[#94a3b8] hover:text-[#f8fafc] transition-colors"
        >
          <Minus size={13} />
        </button>
        <button
          onClick={handleMaximize}
          title={isMaximized ? 'Restore' : 'Maximize'}
          className="w-7 h-6 flex items-center justify-center rounded hover:bg-[#1e293b] text-[#94a3b8] hover:text-[#f8fafc] transition-colors"
        >
          {isMaximized ? <Minimize2 size={12} /> : <Maximize2 size={12} />}
        </button>
        <button
          onClick={handleClose}
          title="Close"
          className="w-7 h-6 flex items-center justify-center rounded hover:bg-[#e11d48] text-[#94a3b8] hover:text-white transition-colors"
        >
          <X size={13} />
        </button>
      </div>
    </header>
  );
};
