import React, { useState, useEffect, useCallback } from 'react';
import { TitleBar } from './components/TitleBar';
import { Sidebar } from './components/Sidebar';
import { ExplorerPane } from './components/ExplorerPane';
import { PreviewPanel } from './components/PreviewPanel';
import { StatusBar } from './components/StatusBar';
import { BatchRenameModal } from './components/BatchRenameModal';
import { PropertiesModal } from './components/PropertiesModal';
import { NewItemModal } from './components/NewItemModal';
import type {
  ExplorerTab,
  FileItem,
  DriveInfo,
  ClipboardState,
} from './types/explorer';
import { fileService } from './services/fileService';

export const App: React.FC = () => {
  // Dual Pane Mode: false = single pane, true = side-by-side dual pane
  const [isDualPane, setIsDualPane] = useState(false);
  const [activePaneId, setActivePaneId] = useState<'left' | 'right'>('left');

  // Inspector Preview Panel
  const [showPreview, setShowPreview] = useState(true);

  // Left Pane Tabs
  const [leftTabs, setLeftTabs] = useState<ExplorerTab[]>([
    {
      id: 'left-tab-1',
      title: 'ClankerExplorer',
      currentPath: 'C:\\ClankerExplorer',
      history: ['C:\\', 'C:\\ClankerExplorer'],
      historyIndex: 1,
      viewMode: 'details',
      sortField: 'name',
      sortOrder: 'asc',
      filterText: '',
      filterRegex: false,
      filterWildcard: false,
      selectedPaths: [],
    },
  ]);
  const [leftActiveTabId, setLeftActiveTabId] = useState<string>('left-tab-1');

  // Right Pane Tabs (for dual pane)
  const [rightTabs, setRightTabs] = useState<ExplorerTab[]>([
    {
      id: 'right-tab-1',
      title: 'Local Disk (C:)',
      currentPath: 'C:\\',
      history: ['C:\\'],
      historyIndex: 0,
      viewMode: 'details',
      sortField: 'name',
      sortOrder: 'asc',
      filterText: '',
      filterRegex: false,
      filterWildcard: false,
      selectedPaths: [],
    },
  ]);
  const [rightActiveTabId, setRightActiveTabId] = useState<string>('right-tab-1');

  // Global Clipboard state
  const [clipboard, setClipboard] = useState<ClipboardState>({
    operation: null,
    paths: [],
  });

  // Selected items & telemetry
  const [selectedItems, setSelectedItems] = useState<FileItem[]>([]);
  const [drives, setDrives] = useState<DriveInfo[]>([]);

  // Modals state
  const [batchRenamePaths, setBatchRenamePaths] = useState<string[] | null>(null);
  const [propertiesItem, setPropertiesItem] = useState<FileItem | null>(null);
  const [newModalState, setNewModalState] = useState<{
    isOpen: boolean;
    type: 'folder' | 'file';
    parentPath: string;
  }>({
    isOpen: false,
    type: 'folder',
    parentPath: 'C:\\ClankerExplorer',
  });

  // Active tab helper
  const getActiveTab = useCallback(() => {
    if (activePaneId === 'left') {
      return leftTabs.find((t) => t.id === leftActiveTabId) || leftTabs[0];
    } else {
      return rightTabs.find((t) => t.id === rightActiveTabId) || rightTabs[0];
    }
  }, [activePaneId, leftTabs, leftActiveTabId, rightTabs, rightActiveTabId]);

  const activeTab = getActiveTab();

  // Load drives for status telemetry
  useEffect(() => {
    fileService.getDrives().then(setDrives);
  }, []);

  const currentDrive = drives.find((d) =>
    activeTab?.currentPath?.toLowerCase().startsWith(d.root.toLowerCase())
  );

  // Global Hotkey handlers
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      // Ctrl+Shift+C: Copy active directory or selected item path
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'C' || e.key === 'c')) {
        e.preventDefault();
        const tab = getActiveTab();
        if (tab) {
          const path = tab.selectedPaths.length > 0 ? tab.selectedPaths[0] : tab.currentPath;
          fileService.writeClipboardText(path);
        }
      }

      // F3: Toggle Inspector Preview
      if (e.key === 'F3') {
        e.preventDefault();
        setShowPreview((prev) => !prev);
      }

      // Ctrl+Shift+D: Toggle Dual Pane
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'D' || e.key === 'd')) {
        e.preventDefault();
        setIsDualPane((prev) => !prev);
      }

      // Ctrl+Shift+T: Open PowerShell in current path
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'T' || e.key === 't')) {
        e.preventDefault();
        const tab = getActiveTab();
        if (tab) {
          fileService.openTerminal(tab.currentPath, 'powershell');
        }
      }

      // Ctrl+T: New Tab in active pane
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && (e.key === 't' || e.key === 'T')) {
        e.preventDefault();
        const tab = getActiveTab();
        const path = tab?.currentPath || 'C:\\';
        const newTab: ExplorerTab = {
          id: `tab-${Date.now()}`,
          title: path.split(/[\\/]/).filter(Boolean).pop() || path,
          currentPath: path,
          history: [path],
          historyIndex: 0,
          viewMode: tab?.viewMode || 'details',
          sortField: 'name',
          sortOrder: 'asc',
          filterText: '',
          filterRegex: false,
          filterWildcard: false,
          selectedPaths: [],
        };
        if (activePaneId === 'left') {
          setLeftTabs((prev) => [...prev, newTab]);
          setLeftActiveTabId(newTab.id);
        } else {
          setRightTabs((prev) => [...prev, newTab]);
          setRightActiveTabId(newTab.id);
        }
      }

      // Ctrl+W: Close active tab
      if ((e.ctrlKey || e.metaKey) && (e.key === 'w' || e.key === 'W')) {
        e.preventDefault();
        if (activePaneId === 'left' && leftTabs.length > 1) {
          const idx = leftTabs.findIndex((t) => t.id === leftActiveTabId);
          const newTabs = leftTabs.filter((t) => t.id !== leftActiveTabId);
          setLeftTabs(newTabs);
          setLeftActiveTabId(newTabs[Math.max(0, idx - 1)].id);
        } else if (activePaneId === 'right' && rightTabs.length > 1) {
          const idx = rightTabs.findIndex((t) => t.id === rightActiveTabId);
          const newTabs = rightTabs.filter((t) => t.id !== rightActiveTabId);
          setRightTabs(newTabs);
          setRightActiveTabId(newTabs[Math.max(0, idx - 1)].id);
        }
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [getActiveTab, activePaneId, leftTabs, leftActiveTabId, rightTabs, rightActiveTabId]);

  // Sidebar navigation handler
  const handleSidebarNavigate = (path: string) => {
    if (activePaneId === 'left') {
      const tab = leftTabs.find((t) => t.id === leftActiveTabId) || leftTabs[0];
      const history = tab.history.slice(0, tab.historyIndex + 1);
      history.push(path);
      setLeftTabs((prev) =>
        prev.map((t) =>
          t.id === leftActiveTabId
            ? { ...t, currentPath: path, history, historyIndex: history.length - 1, selectedPaths: [] }
            : t
        )
      );
    } else {
      const tab = rightTabs.find((t) => t.id === rightActiveTabId) || rightTabs[0];
      const history = tab.history.slice(0, tab.historyIndex + 1);
      history.push(path);
      setRightTabs((prev) =>
        prev.map((t) =>
          t.id === rightActiveTabId
            ? { ...t, currentPath: path, history, historyIndex: history.length - 1, selectedPaths: [] }
            : t
        )
      );
    }
  };

  const handleOpenItemGlobal = async (item: FileItem) => {
    await fileService.openItem(item.path);
  };

  const handleCreateNewItem = async (name: string) => {
    if (newModalState.type === 'folder') {
      await fileService.createFolder(newModalState.parentPath, name);
    } else {
      await fileService.createFile(newModalState.parentPath, name);
    }
    // Trigger refresh on active tab by resetting path
    const tab = getActiveTab();
    if (tab) {
      if (activePaneId === 'left') {
        setLeftTabs((prev) => [...prev]);
      } else {
        setRightTabs((prev) => [...prev]);
      }
    }
  };

  const focusedSelectedPath =
    activeTab?.selectedPaths.length && activeTab.selectedPaths.length > 0
      ? activeTab.selectedPaths[activeTab.selectedPaths.length - 1]
      : null;

  return (
    <div className="flex flex-col h-screen w-screen bg-[#090d16] text-[#f8fafc] overflow-hidden select-none font-sans">
      {/* Title Bar & Power Tools */}
      <TitleBar
        currentPath={activeTab?.currentPath || 'C:\\'}
        isDualPane={isDualPane}
        onToggleDualPane={() => setIsDualPane(!isDualPane)}
        showPreview={showPreview}
        onTogglePreview={() => setShowPreview(!showPreview)}
        onOpenTerminal={() => fileService.openTerminal(activeTab?.currentPath || 'C:\\', 'powershell')}
        onOpenVSCode={() => fileService.openEditor(activeTab?.currentPath || 'C:\\')}
        onRefresh={() => {
          setLeftTabs((prev) => [...prev]);
          setRightTabs((prev) => [...prev]);
        }}
      />

      {/* Main Center Area: Sidebar + Panes + Inspector */}
      <div className="flex flex-1 overflow-hidden">
        {/* Left Sidebar: Drives & Bookmarks */}
        <Sidebar
          currentPath={activeTab?.currentPath || 'C:\\'}
          onNavigate={handleSidebarNavigate}
          onAddBookmark={(path) => alert(`Bookmarked: ${path}`)}
        />

        {/* Center Explorer Panes (Single or Dual Side-by-Side) */}
        <main className="flex-1 flex overflow-hidden bg-[#090d16]">
          {/* Left / Primary Pane */}
          <div className={`${isDualPane ? 'w-1/2' : 'w-full'} h-full flex flex-col`}>
            <ExplorerPane
              paneId="left"
              paneLabel={isDualPane ? 'PANE 1' : undefined}
              isActivePane={activePaneId === 'left'}
              onFocusPane={() => setActivePaneId('left')}
              tabs={leftTabs}
              activeTabId={leftActiveTabId}
              onUpdateTabs={(tabs, newActiveId) => {
                setLeftTabs(tabs);
                if (newActiveId) setLeftActiveTabId(newActiveId);
              }}
              onOpenItemGlobal={handleOpenItemGlobal}
              onOpenBatchRename={(paths) => setBatchRenamePaths(paths)}
              onOpenProperties={(item) => setPropertiesItem(item)}
              onOpenNewModal={(type, parentPath) =>
                setNewModalState({ isOpen: true, type, parentPath })
              }
              clipboard={clipboard}
              setClipboard={setClipboard}
              onSelectionChange={(selected) => {
                if (activePaneId === 'left') setSelectedItems(selected);
              }}
            />
          </div>

          {/* Right / Secondary Pane (Dual Split) */}
          {isDualPane && (
            <div className="w-1/2 h-full flex flex-col border-l border-[#1e293b]">
              <ExplorerPane
                paneId="right"
                paneLabel="PANE 2"
                isActivePane={activePaneId === 'right'}
                onFocusPane={() => setActivePaneId('right')}
                tabs={rightTabs}
                activeTabId={rightActiveTabId}
                onUpdateTabs={(tabs, newActiveId) => {
                  setRightTabs(tabs);
                  if (newActiveId) setRightActiveTabId(newActiveId);
                }}
                onOpenItemGlobal={handleOpenItemGlobal}
                onOpenBatchRename={(paths) => setBatchRenamePaths(paths)}
                onOpenProperties={(item) => setPropertiesItem(item)}
                onOpenNewModal={(type, parentPath) =>
                  setNewModalState({ isOpen: true, type, parentPath })
                }
                clipboard={clipboard}
                setClipboard={setClipboard}
                onSelectionChange={(selected) => {
                  if (activePaneId === 'right') setSelectedItems(selected);
                }}
              />
            </div>
          )}
        </main>

        {/* Right Inspector Preview Panel */}
        {showPreview && (
          <PreviewPanel
            selectedPath={focusedSelectedPath}
            onClose={() => setShowPreview(false)}
          />
        )}
      </div>

      {/* Bottom Status Bar */}
      <StatusBar
        totalItems={0} // Dynamically populated via file rows
        hiddenItemsCount={selectedItems.filter((i) => i.isHidden).length}
        selectedItems={selectedItems}
        currentDrive={currentDrive}
        isFilterActive={!!activeTab?.filterText}
      />

      {/* Power Batch Rename Modal */}
      {batchRenamePaths && (
        <BatchRenameModal
          selectedPaths={batchRenamePaths}
          isOpen={true}
          onClose={() => setBatchRenamePaths(null)}
          onSuccess={() => {
            setLeftTabs((prev) => [...prev]);
            setRightTabs((prev) => [...prev]);
          }}
        />
      )}

      {/* Properties & Checksums Modal */}
      {propertiesItem && (
        <PropertiesModal
          item={propertiesItem}
          isOpen={true}
          onClose={() => setPropertiesItem(null)}
        />
      )}

      {/* Create New Item Modal */}
      <NewItemModal
        isOpen={newModalState.isOpen}
        type={newModalState.type}
        parentPath={newModalState.parentPath}
        onClose={() => setNewModalState((prev) => ({ ...prev, isOpen: false }))}
        onSubmit={handleCreateNewItem}
      />
    </div>
  );
};

export default App;
