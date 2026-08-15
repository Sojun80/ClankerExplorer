import React from 'react';
import { HardDrive, CheckCircle2, Shield, Eye } from 'lucide-react';
import type { DriveInfo, FileItem } from '../types/explorer';

interface StatusBarProps {
  totalItems: number;
  hiddenItemsCount: number;
  selectedItems: FileItem[];
  currentDrive?: DriveInfo;
  isFilterActive: boolean;
}

export const StatusBar: React.FC<StatusBarProps> = ({
  totalItems,
  hiddenItemsCount,
  selectedItems,
  currentDrive,
  isFilterActive,
}) => {
  const selectedTotalBytes = selectedItems.reduce((acc, curr) => acc + (curr.isDirectory ? 0 : curr.size), 0);

  const formatBytes = (bytes: number) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${(bytes / Math.pow(k, i)).toFixed(2)} ${sizes[i]}`;
  };

  return (
    <footer className="flex items-center justify-between h-6 bg-[#080c14] border-t border-[#1e293b] px-3 select-none text-[11px] font-mono-code text-[#94a3b8]">
      {/* Left: Item Counters */}
      <div className="flex items-center gap-3">
        <div className="flex items-center gap-1.5">
          <span className="text-[#f8fafc] font-semibold">{totalItems}</span>
          <span>items</span>
        </div>

        {hiddenItemsCount > 0 && (
          <div className="flex items-center gap-1 text-[#f59e0b]">
            <Eye size={11} />
            <span>({hiddenItemsCount} hidden files visible)</span>
          </div>
        )}

        {selectedItems.length > 0 && (
          <div className="flex items-center gap-1.5 text-[#38bdf8] bg-[#0284c7]/15 px-1.5 py-0.2 rounded border border-[#0284c7]/30 font-semibold">
            <CheckCircle2 size={11} />
            <span>
              {selectedItems.length} selected ({formatBytes(selectedTotalBytes)})
            </span>
          </div>
        )}
      </div>

      {/* Middle: Shortcut tips */}
      <div className="hidden lg:flex items-center gap-2.5 text-[#64748b] text-[10px]">
        <span><strong className="text-[#94a3b8]">Ctrl+Shift+C</strong> Copy Path</span>
        <span>•</span>
        <span><strong className="text-[#94a3b8]">F3</strong> Preview</span>
        <span>•</span>
        <span><strong className="text-[#94a3b8]">Ctrl+F</strong> Filter</span>
        <span>•</span>
        <span><strong className="text-[#94a3b8]">Ctrl+T</strong> New Tab</span>
      </div>

      {/* Right: Drive Storage Telemetry */}
      {currentDrive && (
        <div className="flex items-center gap-2">
          <div className="flex items-center gap-1 text-[#cbd5e1]">
            <HardDrive size={11} className="text-[#38bdf8]" />
            <span>{currentDrive.letter}</span>
            <span>{currentDrive.formattedFree} free</span>
            <span className="text-[#64748b]">/ {currentDrive.formattedTotal}</span>
          </div>

          <div className="w-16 bg-[#1e293b] h-1.5 rounded-full overflow-hidden">
            <div
              className={`h-full rounded-full ${
                currentDrive.percentUsed > 90
                  ? 'bg-[#ef4444]'
                  : currentDrive.percentUsed > 75
                  ? 'bg-[#f59e0b]'
                  : 'bg-[#0284c7]'
              }`}
              style={{ width: `${currentDrive.percentUsed}%` }}
            />
          </div>
        </div>
      )}
    </footer>
  );
};
