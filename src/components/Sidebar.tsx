import React, { useState, useEffect } from 'react';
import {
  HardDrive,
  Home,
  Monitor,
  Download,
  FileText,
  Image,
  Music,
  Video,
  Bookmark,
  Plus,
  Trash2,
  FolderTree,
  ChevronDown,
  ChevronRight,
  Server,
  Disc,
  Folder,
  Code,
} from 'lucide-react';
import type { DriveInfo, QuickAccessItem } from '../types/explorer';
import { fileService } from '../services/fileService';

interface SidebarProps {
  currentPath: string;
  onNavigate: (path: string) => void;
  onAddBookmark: (path: string) => void;
}

export const Sidebar: React.FC<SidebarProps> = ({
  currentPath,
  onNavigate,
  onAddBookmark,
}) => {
  const [drives, setDrives] = useState<DriveInfo[]>([]);
  const [quickAccess, setQuickAccess] = useState<QuickAccessItem[]>([]);
  const [expandedSections, setExpandedSections] = useState({
    drives: true,
    quickAccess: true,
    bookmarks: true,
  });

  const loadData = async () => {
    try {
      const [drivesList, qaList] = await Promise.all([
        fileService.getDrives(),
        fileService.getQuickAccess(),
      ]);
      setDrives(drivesList);
      setQuickAccess(qaList);
    } catch (err) {
      console.error('Error loading sidebar data:', err);
    }
  };

  useEffect(() => {
    loadData();
    // Refresh drives periodically (every 15s)
    const interval = setInterval(loadData, 15000);
    return () => clearInterval(interval);
  }, []);

  const toggleSection = (section: keyof typeof expandedSections) => {
    setExpandedSections((prev) => ({ ...prev, [section]: !prev[section] }));
  };

  const getQuickIcon = (iconName: string) => {
    switch (iconName) {
      case 'desktop':
        return <Monitor size={14} className="text-[#38bdf8]" />;
      case 'downloads':
        return <Download size={14} className="text-[#10b981]" />;
      case 'documents':
        return <FileText size={14} className="text-[#f59e0b]" />;
      case 'pictures':
        return <Image size={14} className="text-[#ec4899]" />;
      case 'music':
        return <Music size={14} className="text-[#a855f7]" />;
      case 'videos':
        return <Video size={14} className="text-[#ef4444]" />;
      case 'home':
        return <Home size={14} className="text-[#06b6d4]" />;
      case 'code':
        return <Code size={14} className="text-[#6366f1]" />;
      default:
        return <Folder size={14} className="text-[#64748b]" />;
    }
  };

  const getDriveIcon = (type: DriveInfo['type']) => {
    switch (type) {
      case 'network':
        return <Server size={14} className="text-[#a855f7]" />;
      case 'cdrom':
        return <Disc size={14} className="text-[#94a3b8]" />;
      default:
        return <HardDrive size={14} className="text-[#38bdf8]" />;
    }
  };

  return (
    <aside className="w-56 bg-[#080c14] border-r border-[#1e293b] flex flex-col h-full select-none overflow-y-auto no-scrollbar text-xs">
      {/* Drives Section */}
      <div className="p-2 border-b border-[#1e293b]/60">
        <div
          onClick={() => toggleSection('drives')}
          className="flex items-center justify-between text-[#94a3b8] hover:text-[#f8fafc] px-2 py-1 cursor-pointer font-bold text-[11px] uppercase tracking-wider"
        >
          <div className="flex items-center gap-1.5">
            {expandedSections.drives ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
            <span>Drives & Volumes</span>
          </div>
          <span className="font-mono-code text-[10px] text-[#64748b] bg-[#0f1626] px-1 rounded">
            {drives.length}
          </span>
        </div>

        {expandedSections.drives && (
          <div className="mt-1 space-y-1">
            {drives.map((drive) => {
              const isCurrent = currentPath.toLowerCase().startsWith(drive.root.toLowerCase());
              return (
                <div
                  key={drive.letter}
                  onClick={() => onNavigate(drive.root)}
                  className={`px-2 py-1.5 rounded cursor-pointer transition-colors group ${
                    isCurrent
                      ? 'bg-[#152035] text-[#f8fafc] border-l-2 border-[#38bdf8]'
                      : 'text-[#94a3b8] hover:bg-[#0f1626] hover:text-[#e2e8f0]'
                  }`}
                >
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      {getDriveIcon(drive.type)}
                      <span className="font-semibold font-mono-code text-[11.5px]">
                        {drive.letter}
                      </span>
                      <span className="text-[11px] text-[#64748b] truncate max-w-[80px]">
                        {drive.name}
                      </span>
                    </div>
                    <span className="text-[10px] font-mono-code text-[#94a3b8]">
                      {drive.formattedFree} free
                    </span>
                  </div>

                  {/* Storage Bar */}
                  <div className="w-full bg-[#1e293b] h-1.5 rounded-full mt-1.5 overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all ${
                        drive.percentUsed > 90
                          ? 'bg-[#ef4444]'
                          : drive.percentUsed > 75
                          ? 'bg-[#f59e0b]'
                          : 'bg-[#0284c7]'
                      }`}
                      style={{ width: `${Math.min(100, Math.max(2, drive.percentUsed))}%` }}
                    />
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* Quick Access Section */}
      <div className="p-2 border-b border-[#1e293b]/60">
        <div
          onClick={() => toggleSection('quickAccess')}
          className="flex items-center justify-between text-[#94a3b8] hover:text-[#f8fafc] px-2 py-1 cursor-pointer font-bold text-[11px] uppercase tracking-wider"
        >
          <div className="flex items-center gap-1.5">
            {expandedSections.quickAccess ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
            <span>Quick Access</span>
          </div>
        </div>

        {expandedSections.quickAccess && (
          <div className="mt-1 space-y-0.5">
            {quickAccess.map((qa) => {
              const isCurrent = currentPath.toLowerCase() === qa.path.toLowerCase();
              return (
                <div
                  key={qa.id}
                  onClick={() => onNavigate(qa.path)}
                  className={`flex items-center gap-2.5 px-2 py-1.5 rounded cursor-pointer transition-colors ${
                    isCurrent
                      ? 'bg-[#152035] text-[#f8fafc] font-medium border-l-2 border-[#38bdf8]'
                      : 'text-[#94a3b8] hover:bg-[#0f1626] hover:text-[#e2e8f0]'
                  }`}
                >
                  {getQuickIcon(qa.icon)}
                  <span className="truncate text-[12px]">{qa.name}</span>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* Bookmarks & Power Pins */}
      <div className="p-2 flex-1">
        <div className="flex items-center justify-between text-[#94a3b8] px-2 py-1 font-bold text-[11px] uppercase tracking-wider">
          <div
            onClick={() => toggleSection('bookmarks')}
            className="flex items-center gap-1.5 cursor-pointer hover:text-[#f8fafc]"
          >
            {expandedSections.bookmarks ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
            <span>Pinned Folders</span>
          </div>
          <button
            onClick={() => onAddBookmark(currentPath)}
            title="Pin current directory"
            className="p-0.5 hover:bg-[#1c2b45] text-[#64748b] hover:text-[#38bdf8] rounded"
          >
            <Plus size={13} />
          </button>
        </div>

        {expandedSections.bookmarks && (
          <div className="mt-1 space-y-0.5">
            <div
              onClick={() => onNavigate('C:\\ClankerExplorer')}
              className="flex items-center justify-between px-2 py-1.5 rounded hover:bg-[#0f1626] text-[#94a3b8] hover:text-[#f8fafc] cursor-pointer group"
            >
              <div className="flex items-center gap-2">
                <Bookmark size={13} className="text-[#38bdf8]" />
                <span className="text-[12px] truncate">ClankerExplorer</span>
              </div>
            </div>
          </div>
        )}
      </div>
    </aside>
  );
};
