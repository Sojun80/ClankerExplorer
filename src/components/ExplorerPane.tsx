import React, { useState, useEffect, useCallback } from 'react';
import { TabBar } from './TabBar';
import { AddressBar } from './AddressBar';
import { FileList } from './FileList';
import { FilterBar } from './FilterBar';
import { ContextMenu } from './ContextMenu';
import type {
  ExplorerTab,
  FileItem,
  SortField,
  SortOrder,
  ViewMode,
  ClipboardState,
} from '../types/explorer';
import { fileService } from '../services/fileService';

interface ExplorerPaneProps {
  paneId: string;
  paneLabel?: string;
  isActivePane: boolean;
  onFocusPane: () => void;
  tabs: ExplorerTab[];
  activeTabId: string;
  onUpdateTabs: (tabs: ExplorerTab[], newActiveId?: string) => void;
  onOpenItemGlobal: (item: FileItem) => void;
  onOpenBatchRename: (paths: string[]) => void;
  onOpenProperties: (item: FileItem) => void;
  onOpenNewModal: (type: 'folder' | 'file', parentPath: string) => void;
  clipboard: ClipboardState;
  setClipboard: (cb: ClipboardState) => void;
  onSelectionChange?: (selectedItems: FileItem[]) => void;
}

export const ExplorerPane: React.FC<ExplorerPaneProps> = ({
  paneId,
  paneLabel,
  isActivePane,
  onFocusPane,
  tabs,
  activeTabId,
  onUpdateTabs,
  onOpenItemGlobal,
  onOpenBatchRename,
  onOpenProperties,
  onOpenNewModal,
  clipboard,
  setClipboard,
  onSelectionChange,
}) => {
  const activeTab = tabs.find((t) => t.id === activeTabId) || tabs[0];
  const [items, setItems] = useState<FileItem[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [isFilterOpen, setIsFilterOpen] = useState(false);
  const [contextMenu, setContextMenu] = useState<{
    x: number;
    y: number;
    targetItem?: FileItem;
  } | null>(null);

  // Load directory items whenever currentPath changes
  const loadDirectory = useCallback(async (path: string) => {
    setIsLoading(true);
    try {
      const res = await fileService.readDir(path);
      setItems(res.items);
    } catch (err) {
      console.error(err);
      setItems([]);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    if (activeTab?.currentPath) {
      loadDirectory(activeTab.currentPath);
    }
  }, [activeTab?.currentPath, loadDirectory]);

  // Notify selection changes
  useEffect(() => {
    if (activeTab) {
      const selected = items.filter((i) => activeTab.selectedPaths.includes(i.path));
      onSelectionChange?.(selected);
    }
  }, [activeTab?.selectedPaths, items, onSelectionChange]);

  const updateActiveTab = (updates: Partial<ExplorerTab>) => {
    const newTabs = tabs.map((t) => (t.id === activeTabId ? { ...t, ...updates } : t));
    onUpdateTabs(newTabs);
  };

  const navigateTo = (newPath: string) => {
    if (!activeTab) return;
    const history = activeTab.history.slice(0, activeTab.historyIndex + 1);
    history.push(newPath);
    updateActiveTab({
      currentPath: newPath,
      history,
      historyIndex: history.length - 1,
      selectedPaths: [],
    });
  };

  const handleGoBack = () => {
    if (activeTab && activeTab.historyIndex > 0) {
      const newIndex = activeTab.historyIndex - 1;
      updateActiveTab({
        currentPath: activeTab.history[newIndex],
        historyIndex: newIndex,
        selectedPaths: [],
      });
    }
  };

  const handleGoForward = () => {
    if (activeTab && activeTab.historyIndex < activeTab.history.length - 1) {
      const newIndex = activeTab.historyIndex + 1;
      updateActiveTab({
        currentPath: activeTab.history[newIndex],
        historyIndex: newIndex,
        selectedPaths: [],
      });
    }
  };

  const handleGoUp = () => {
    if (!activeTab) return;
    const current = activeTab.currentPath;
    const isWindows = current.includes('\\') || current.match(/^[A-Z]:/i);
    const parts = current.split(/[\\/]/).filter(Boolean);

    if (parts.length <= 1) {
      // Already at root or drive root
      return;
    }

    parts.pop();
    const upPath = isWindows
      ? parts.length === 1
        ? `${parts[0]}\\`
        : parts.join('\\')
      : `/${parts.join('/')}`;
    navigateTo(upPath);
  };

  // Tab management
  const handleSelectTab = (tabId: string) => {
    onUpdateTabs(tabs, tabId);
  };

  const handleCloseTab = (tabId: string) => {
    if (tabs.length <= 1) return;
    const idx = tabs.findIndex((t) => t.id === tabId);
    const newTabs = tabs.filter((t) => t.id !== tabId);
    let nextActiveId = activeTabId;
    if (tabId === activeTabId) {
      const nextIdx = Math.max(0, idx - 1);
      nextActiveId = newTabs[nextIdx].id;
    }
    onUpdateTabs(newTabs, nextActiveId);
  };

  const handleNewTab = (newPath?: string) => {
    const path = newPath || activeTab?.currentPath || 'C:\\';
    const folderName = path.split(/[\\/]/).filter(Boolean).pop() || path;
    const newTab: ExplorerTab = {
      id: `tab-${Date.now()}-${Math.random().toString(36).substr(2, 4)}`,
      title: folderName,
      currentPath: path,
      history: [path],
      historyIndex: 0,
      viewMode: activeTab?.viewMode || 'details',
      sortField: activeTab?.sortField || 'name',
      sortOrder: activeTab?.sortOrder || 'asc',
      filterText: '',
      filterRegex: false,
      filterWildcard: false,
      selectedPaths: [],
    };
    onUpdateTabs([...tabs, newTab], newTab.id);
  };

  const handleDuplicateTab = (tabId: string) => {
    const tab = tabs.find((t) => t.id === tabId);
    if (!tab) return;
    handleNewTab(tab.currentPath);
  };

  const handleTogglePinTab = (tabId: string) => {
    const newTabs = tabs.map((t) => (t.id === tabId ? { ...t, isPinned: !t.isPinned } : t));
    onUpdateTabs(newTabs);
  };

  const handleOpenItem = (item: FileItem) => {
    if (item.isDirectory) {
      navigateTo(item.path);
    } else {
      onOpenItemGlobal(item);
    }
  };

  const handleInlineRename = async (oldPath: string, newName: string) => {
    try {
      await fileService.rename(oldPath, newName);
      loadDirectory(activeTab.currentPath);
    } catch (err: any) {
      alert(`Rename failed: ${err.message}`);
    }
  };

  // Clipboard operations
  const handleCopy = () => {
    if (activeTab && activeTab.selectedPaths.length > 0) {
      setClipboard({ operation: 'copy', paths: activeTab.selectedPaths });
    }
  };

  const handleCut = () => {
    if (activeTab && activeTab.selectedPaths.length > 0) {
      setClipboard({ operation: 'cut', paths: activeTab.selectedPaths });
    }
  };

  const handlePaste = async () => {
    if (!clipboard.operation || clipboard.paths.length === 0 || !activeTab) return;
    try {
      if (clipboard.operation === 'copy') {
        await fileService.copy(clipboard.paths, activeTab.currentPath);
      } else if (clipboard.operation === 'cut') {
        await fileService.move(clipboard.paths, activeTab.currentPath);
        setClipboard({ operation: null, paths: [] });
      }
      loadDirectory(activeTab.currentPath);
    } catch (err: any) {
      alert(`Paste failed: ${err.message}`);
    }
  };

  const handleDelete = async (permanent: boolean = false) => {
    if (!activeTab || activeTab.selectedPaths.length === 0) return;
    const count = activeTab.selectedPaths.length;
    const confirmText = permanent
      ? `Are you sure you want to PERMANENTLY delete ${count} item(s)?`
      : `Send ${count} item(s) to Recycle Bin?`;
    if (window.confirm(confirmText)) {
      try {
        await fileService.delete(activeTab.selectedPaths, permanent);
        loadDirectory(activeTab.currentPath);
        updateActiveTab({ selectedPaths: [] });
      } catch (err: any) {
        alert(`Delete failed: ${err.message}`);
      }
    }
  };

  const handleCopyPath = async (type: 'raw' | 'quoted' | 'posix' = 'raw') => {
    if (!activeTab) return;
    const target =
      activeTab.selectedPaths.length > 0
        ? activeTab.selectedPaths[0]
        : activeTab.currentPath;

    let text = target;
    if (type === 'quoted') text = `"${target}"`;
    if (type === 'posix') text = target.replace(/\\/g, '/');

    await fileService.writeClipboardText(text);
  };

  if (!activeTab) return null;

  return (
    <div
      onClick={onFocusPane}
      className={`flex flex-col h-full overflow-hidden transition-all duration-150 border-r border-[#1e293b] last:border-r-0 ${
        isActivePane ? 'ring-1 ring-[#0284c7]/50' : 'opacity-95'
      }`}
    >
      {/* Tab Bar */}
      <TabBar
        tabs={tabs}
        activeTabId={activeTabId}
        onSelectTab={handleSelectTab}
        onCloseTab={handleCloseTab}
        onNewTab={handleNewTab}
        onDuplicateTab={handleDuplicateTab}
        onTogglePinTab={handleTogglePinTab}
        currentViewMode={activeTab.viewMode}
        onChangeViewMode={(mode) => updateActiveTab({ viewMode: mode })}
        paneLabel={paneLabel}
      />

      {/* Address Bar */}
      <AddressBar
        currentPath={activeTab.currentPath}
        canGoBack={activeTab.historyIndex > 0}
        canGoForward={activeTab.historyIndex < activeTab.history.length - 1}
        onNavigate={navigateTo}
        onGoBack={handleGoBack}
        onGoForward={handleGoForward}
        onGoUp={handleGoUp}
        onRefresh={() => loadDirectory(activeTab.currentPath)}
        onNewFolder={() => onOpenNewModal('folder', activeTab.currentPath)}
        onNewFile={() => onOpenNewModal('file', activeTab.currentPath)}
      />

      {/* File List */}
      <FileList
        items={items}
        currentPath={activeTab.currentPath}
        selectedPaths={activeTab.selectedPaths}
        viewMode={activeTab.viewMode}
        sortField={activeTab.sortField}
        sortOrder={activeTab.sortOrder}
        filterText={activeTab.filterText}
        isFilterRegex={activeTab.filterRegex}
        onSelect={(paths, lastFocused) =>
          updateActiveTab({ selectedPaths: paths, lastFocusedPath: lastFocused })
        }
        onOpenItem={handleOpenItem}
        onContextMenu={(e, item) => {
          setContextMenu({
            x: e.clientX,
            y: e.clientY,
            targetItem: item,
          });
        }}
        onSortChange={(field) => {
          if (activeTab.sortField === field) {
            updateActiveTab({ sortOrder: activeTab.sortOrder === 'asc' ? 'desc' : 'asc' });
          } else {
            updateActiveTab({ sortField: field, sortOrder: 'asc' });
          }
        }}
        onInlineRename={handleInlineRename}
        isLoading={isLoading}
      />

      {/* Filter Bar (Ctrl+F) */}
      {(isFilterOpen || activeTab.filterText) && (
        <FilterBar
          filterText={activeTab.filterText}
          isFilterRegex={activeTab.filterRegex}
          isFilterWildcard={activeTab.filterWildcard}
          totalCount={items.length}
          filteredCount={
            items.filter((i) => i.name.toLowerCase().includes(activeTab.filterText.toLowerCase()))
              .length
          }
          onFilterChange={(text) => updateActiveTab({ filterText: text })}
          onToggleRegex={() => updateActiveTab({ filterRegex: !activeTab.filterRegex })}
          onToggleWildcard={() => updateActiveTab({ filterWildcard: !activeTab.filterWildcard })}
          onClose={() => {
            updateActiveTab({ filterText: '' });
            setIsFilterOpen(false);
          }}
        />
      )}

      {/* Context Menu */}
      {contextMenu && (
        <ContextMenu
          x={contextMenu.x}
          y={contextMenu.y}
          targetItem={contextMenu.targetItem}
          currentPath={activeTab.currentPath}
          selectedPaths={activeTab.selectedPaths}
          clipboardCount={clipboard.paths.length}
          onClose={() => setContextMenu(null)}
          onOpen={() => {
            if (contextMenu.targetItem) handleOpenItem(contextMenu.targetItem);
          }}
          onCopyPath={handleCopyPath}
          onCopy={handleCopy}
          onCut={handleCut}
          onPaste={handlePaste}
          onRename={() => {
            // Handled via F2 in FileList
          }}
          onBatchRename={() => onOpenBatchRename(activeTab.selectedPaths)}
          onDelete={handleDelete}
          onOpenTerminal={() => fileService.openTerminal(activeTab.currentPath, 'powershell')}
          onOpenVSCode={() => fileService.openEditor(activeTab.currentPath)}
          onNewFolder={() => onOpenNewModal('folder', activeTab.currentPath)}
          onNewFile={() => onOpenNewModal('file', activeTab.currentPath)}
          onProperties={() => {
            if (contextMenu.targetItem) onOpenProperties(contextMenu.targetItem);
          }}
        />
      )}
    </div>
  );
};
