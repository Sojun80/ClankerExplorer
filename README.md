# ClankerExplorer (C-Explorer) ⚡

A high-performance, dark-mode, power-user file manager built with **C# (.NET 8)** and **Avalonia UI**, designed for speed, clarity, deep power-user workflow integration, and seamless state continuity.

Compiles directly to a **single standalone Windows executable (`.exe`)** with zero runtime dependencies, and can be built natively on **Linux (X11 / Wayland)**.

---

## 📸 Overview

![ClankerExplorer Overview](docs/screenshots/clanker_overview.png)

---

## 🌟 Key Features

### 🚀 State Continuity & Session Intelligence
- 🪟 **Window Geometry & Multi-Monitor Intelligence**: Remembers window position, restored dimensions, and maximized state across sessions. Automatically validates bounds against connected screen work areas on startup and recenters/clamps safely if a monitor was disconnected. Minimized state is never restored.
- 🧠 **Smooth Folder View Inheritance**: Navigating into unconfigured folders automatically inherits the visual layout (Details/Thumbnails, thumbnail size, sort column, sort order, smart column sizing, column visibility, and column order) from the previous folder, while starting cleanly at the top without cluttering disk persistence.
- 💾 **Per-Folder Saved View Memory**: Custom-configured folders remember their exact view mode, column widths, order, and scroll offsets across application restarts.

### 📑 Navigation & Tabs
- 📑 **Tabs & Dual-Pane Navigation**: Multi-tab browsing with independent address bars and file grids; side-by-side Dual Pane split (`Ctrl+Shift+D`).
- 🖱️ **Hardware Mouse Navigation**: Native hardware thumb buttons (`XButton1` / `XButton2`) for instant Back and Forward history navigation.
- 📋 **Permanent Directory Path**: Always-visible path with a 1-click **"Copy Path"** button (`Ctrl+Shift+C`), plus full path copy on right-click.
- 👁️ **Zero File Hiding**: File extensions (`.exe`, `.tar.gz`, `.cs`, `.json`) are always displayed. Hidden and system files are clearly highlighted.
- 🕒 **Persistent History & Frequent Locations**:
  - Automatically tracks frequent and recent folder visits (`%APPDATA%\C-Explorer\history.json`).
  - Middle-compact paths (e.g. `C:\...\Subfolder`) with visit frequency badges.
  - Row-level `×` reset with instant `↩ Undo` restoration banner.
- 🌐 **Network & WSL Browser**: Collapsible sidebar section for network shares, local subnets, and WSL Linux distributions.
- 📌 **Stay on Top**: 1-click pin to keep ClankerExplorer floating over your active workspace.

### 🔀 Drag & Drop and Selection
- 🎯 **Identity-Preserving Multi-Selection**: Moving or dragging an item from a multi-selected group preserves the entire selection intact. Fluid Range-selection (`Shift+Click`) stays synchronized between Grid and Details views.
- 🔀 **System-Wide Drag & Drop**: Full Windows OLE drag-and-drop — drag files out of ClankerExplorer into Explorer, Desktop, browsers, email clients, chat apps, editors, or external tools. Drop external files into ClankerExplorer with automatic Copy/Move resolution based on drive boundaries and modifier keys (`Ctrl` for copy).
- 🗂️ **Interactive Tab & Quick Access Dragging**: Drag tabs between panes or reorder Quick Access items with smooth drag-ghost badges.

### 🔎 Search & Live Filtering
- 🔍 **Dedicated Search Workspace (`Ctrl+Shift+F`)**: Dedicated recursive multi-threaded search workspace with real-time discovery, wildcard/regex filters, non-blocking background cancellation, and direct jump-to-location.
- ⚡ **Live Quick Filter (`Ctrl+F`)**: Instant in-folder wildcard (`*.cs`, `*test*`) and regular expression file search with 250ms catastrophic backtracking protection.

### 🖼️ Rich Thumbnails & Media Pipeline
- 🖼️ **Viewport-Prioritized Thumbnail Pipeline**: Bounded async thumbnail worker queue with stale-request eviction, scroll backpressure, and LRU memory caching — maintaining smooth 60fps scrolling even in folders with 10,000+ files.
- 🎬 **Video Frame Extraction & Opportunistic Yielding**: Generates actual frame previews for video files (MP4, MKV, AVI, MOV, WMV, etc.) with custom extraction timestamp support and opportunistic file handle yielding so external media players can open files without lock conflicts.
- 🎨 **High-Resolution Jumbo Shell Icons**: True 256×256 Jumbo shell icons via `SHGetImageList(SHIL_JUMBO)` for crisp rendering in Thumbnail view.
- 🧊 **3D STL Model Thumbnails & Viewer**: Interactive 3D preview for Binary & ASCII `.stl` files. High-performance software Z-buffer rasterizer with directional + specular lighting, orbit rotation, pan, zoom, wireframe toggle, and rendered 3D thumbnails in the file grid.

### 🔍 Multi-Format Quick Inspector (`F3`)
- 🖼️ **Image Viewer**: High-resolution image preview with Fit/1:1 toggle, zoom slider, live dimension badges (`1440 × 900 (1.3 MP)`), and mouse wheel controls.
- 🎬 **Video Playback**: Hardware-accelerated 60fps LibVLC video engine with timeline scrubbing, instant Play/Pause, and persistent audio volume/mute preferences.
- 📄 **PDF Document Viewer**: Multi-page PDF renderer with page navigation (`Next`/`Prev`), zoom controls (`Ctrl+Wheel`), and fit-to-window mode.
- 🗜️ **Archive Inspector**: Instant ZIP, RAR, and compressed archive file tree viewer showing file names, uncompressed sizes, and compression ratios without manual extraction.
- 💻 **Code & Hex Viewer**: Syntax viewer and binary Hex dump (Offset / Hex / ASCII).
- 🔐 **Instant File Checksums**: On-demand SHA-256 and MD5 cryptographic hash calculator integrated directly into the inspector.

### ⚙️ File Operations & Integrations
- ⚡ **Background Operations Engine**: Asynchronous file transfer queue with pause/resume/cancel, progress bars, and transaction-safe collision resolution. Junction and symlink recursion guards prevent circular copy loops.
- 📊 **Configurable Data Columns**:
  - Right-click column header or menu to toggle visible columns on the fly:
  - `Name`, `Ext`, `Size`, `Date Modified`, `Date Created`, `Date Accessed`, `Type`, `Attributes (Windows)`, `Permissions (POSIX rwx)`, and `Owner:Group`.
- ✂️ **Visual Cut Feedback**: Cut items dim to semi-transparent (`45% opacity`) just like Windows Explorer until paste or overwrite.
- 🗑️ **Safe Deletion Modal**: Clean delete confirmation dialog with keyboard shortcuts (`Enter` to delete, `Esc` to cancel).
- 📦 **7-Zip Integration**: Dedicated context menu actions with explicit **7-Zip** branding for extraction (`Extract Here`, `Extract to "<folder>\"`, `Extract To...`) and compression (`Add to "<name>.zip"`, `Add to archive...`).
- 📝 **Editor Integrations**: Auto-detects **Notepad++**, **VS Code**, and default text editors for 1-click editing of code, config, log, and markdown files.

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl + Shift + C` | **Copy Current Directory Path to Clipboard** |
| `Ctrl + T` | **New Tab** |
| `Ctrl + W` | **Close Active Tab** |
| `Ctrl + Shift + D` | **Toggle Dual-Pane Split View** |
| `F3` | **Toggle File Inspector (Code / Hex / Media / Hashes)** |
| `Ctrl + F` | **Focus Quick Filter Bar (Wildcard & Regex)** |
| `Ctrl + Shift + F` | **Open Dedicated Search Workspace** |
| `F2` | **Inline Rename Selected File / Folder** |
| `Enter` | **Open Selected File or Enter Folder** |
| `Backspace` or `Alt + Left` | **Navigate Back in History** |
| `Alt + Right` | **Navigate Forward in History** |
| `Alt + Up` | **Navigate to Parent Folder** |
| `Ctrl + C` / `Ctrl + X` / `Ctrl + V` | **Copy / Cut / Paste Files** |
| `Ctrl + A` | **Select All Items** |
| `Ctrl + Shift + T` | **Open PowerShell in Current Directory** |
| `Ctrl + Shift + N` | **Create New Folder** |
| `Ctrl + N` | **Create New File** |
| `Delete` | **Delete Selected Item (With Confirmation Modal)** |
| `Shift + Delete` | **Permanent Delete Bypass** |
| `F5` | **Refresh Directory & Drives** |
| `Mouse Back / Forward` | **Navigate Directory History** |

---

## 🛠️ Building & Publishing

### Debug Run
```bash
dotnet run
```

### Tests

Run the test suite from the repository root:

```bash
dotnet test
```

The suite uses disposable filesystem and configuration directories. See [tests/README.md](tests/README.md) for its structure and current UI/integration boundaries.

### Large-folder performance probe

Measure enumeration, allocation, and natural-sort cost against an existing directory:

```bash
dotnet run --project tools/ClankerExplorer.PerformanceProbe -- <directory>
```

See [docs/large-folder-performance.md](docs/large-folder-performance.md) for the 1k/10k/50k baseline, thumbnail scheduling/cache architecture, virtualization proof, and remaining limits.

### Build Single-File Windows Executable (`.exe`)
```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
The standalone `.exe` is generated at:
`bin/Release/net8.0/win-x64/publish/ClankerExplorer.exe`

### Build Linux Executable
```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

---

## 📋 Recommended Integrations (Optional)
- **7-Zip** (Installed at standard paths for high-speed archive extraction and compression dialogs)
- **Notepad++** / **VS Code** (Auto-detected for 1-click code, markdown, and config editing)
