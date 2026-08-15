import React from 'react';
import { Plus, X, Folder, Lock, Pin, LayoutList, LayoutGrid, Table, Copy } from 'lucide-react';
import type { ExplorerTab, ViewMode } from '../types/explorer';

interface TabBarProps {
  tabs: ExplorerTab[];
  activeTabId: string;
  onSelectTab: (tabId: string) => void;
  onCloseTab: (tabId: string) => void;
  onNewTab: (path?: string) => void;
  onDuplicateTab: (tabId: string) => void;
  onTogglePinTab: (tabId: string) => void;
  currentViewMode: ViewMode;
  onChangeViewMode: (mode: ViewMode) => void;
  paneLabel?: string;
}

export const TabBar: React.FC<TabBarProps> = ({
  tabs,
  activeTabId,
  onSelectTab,
  onCloseTab,
  onNewTab,
  onDuplicateTab,
  onTogglePinTab,
  currentViewMode,
  onChangeViewMode,
  paneLabel,
}) => {
  return (
    <div className="flex items-center justify-between bg-[#0b101c] border-b border-[#1e293b] px-2 h-9 select-none">
      {/* Tab items list */}
      <div className="flex items-center gap-1 overflow-x-auto no-scrollbar max-w-[80%]">
        {paneLabel && (
          <span className="text-[10px] font-mono-code font-bold uppercase tracking-wider text-[#38bdf8] bg-[#0284c7]/20 border border-[#0284c7]/40 px-1.5 py-0.5 rounded mr-1">
            {paneLabel}
          </span>
        )}

        {tabs.map((tab) => {
          const isActive = tab.id === activeTabId;
          const folderName =
            tab.currentPath === '/' || tab.currentPath.match(/^[A-Z]:\\?$/i)
              ? tab.currentPath
              : tab.currentPath.split(/[\\/]/).filter(Boolean).pop() || tab.currentPath;

          return (
            <div
              key={tab.id}
              onClick={() => onSelectTab(tab.id)}
              onAuxClick={(e) => {
                // Middle click to close
                if (e.button === 1 && tabs.length > 1) {
                  onCloseTab(tab.id);
                }
              }}
              title={`${tab.currentPath} (Middle-click to close)`}
              className={`group relative flex items-center gap-2 px-3 py-1 text-xs rounded-t-md transition-all cursor-pointer border-t-2 ${
                isActive
                  ? 'bg-[#0f1626] text-[#f8fafc] border-[#38bdf8] font-medium shadow-sm'
                  : 'text-[#94a3b8] hover:text-[#e2e8f0] hover:bg-[#152035]/60 border-transparent'
              }`}
            >
              {/* Folder Icon */}
              <Folder
                size={13}
                className={isActive ? 'text-[#38bdf8]' : 'text-[#64748b] group-hover:text-[#94a3b8]'}
              />

              {/* Title */}
              <span className="truncate max-w-[130px] font-sans text-[12px]">{folderName}</span>

              {/* Pin indicator */}
              {tab.isPinned && <Pin size={10} className="text-[#f59e0b]" />}

              {/* Close Button */}
              {tabs.length > 1 && (
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    onCloseTab(tab.id);
                  }}
                  title="Close Tab (Ctrl+W)"
                  className="opacity-0 group-hover:opacity-100 hover:bg-[#334155] text-[#94a3b8] hover:text-white rounded p-0.5 transition-all"
                >
                  <X size={12} />
                </button>
              )}
            </div>
          );
        })}

        {/* New Tab Button */}
        <button
          onClick={() => onNewTab()}
          title="New Tab (Ctrl+T)"
          className="flex items-center justify-center w-6 h-6 rounded hover:bg-[#1c2b45] text-[#94a3b8] hover:text-[#38bdf8] transition-colors ml-1"
        >
          <Plus size={14} />
        </button>
      </div>

      {/* Right side View Mode Toggles */}
      <div className="flex items-center gap-1 bg-[#090d16] p-0.5 rounded border border-[#1e293b]">
        <button
          onClick={() => onChangeViewMode('details')}
          title="Power Details View (Full Attributes & Sizes)"
          className={`p-1 rounded transition-colors ${
            currentViewMode === 'details'
              ? 'bg-[#0284c7] text-white'
              : 'text-[#94a3b8] hover:text-[#f8fafc] hover:bg-[#1c2b45]'
          }`}
        >
          <Table size={13} />
        </button>
        <button
          onClick={() => onChangeViewMode('compact')}
          title="Compact List View"
          className={`p-1 rounded transition-colors ${
            currentViewMode === 'compact'
              ? 'bg-[#0284c7] text-white'
              : 'text-[#94a3b8] hover:text-[#f8fafc] hover:bg-[#1c2b45]'
          }`}
        >
          <LayoutList size={13} />
        </button>
        <button
          onClick={() => onChangeViewMode('grid')}
          title="Grid / Large Icons View"
          className={`p-1 rounded transition-colors ${
            currentViewMode === 'grid'
              ? 'bg-[#0284c7] text-white'
              : 'text-[#94a3b8] hover:text-[#f8fafc] hover:bg-[#1c2b45]'
          }`}
        >
          <LayoutGrid size={13} />
        </button>
      </div>
    </div>
  );
};
