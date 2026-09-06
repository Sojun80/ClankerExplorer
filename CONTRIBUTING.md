# Contributing to ClankerExplorer (C-Explorer)

Thank you for contributing! Please review the following guidelines before submitting pull requests or updating documentation assets.

---

## 📸 Guidelines for Documentation Screenshots & Media Assets

> [!IMPORTANT]
> **Privacy & Anonymization Policy**:
> When capturing or updating screenshots, GIFs, or videos for the GitHub repository:
> 1. **Never use your personal PC paths or real home directories**:
>    - Avoid paths containing personal usernames (e.g., `C:\Users\<username>\...`), private document names, or real credentials.
> 2. **Use Sanitized Demo Environments**:
>    - Create a clean test folder (e.g., `C:\DemoProjects` or a temporary repository sandbox) with sample dummy files (`App.cs`, `index.html`, `config.json`, `archive.zip`).
> 3. **Sanitize Network / Machine Names**:
>    - Ensure private local network hostnames, IP addresses, or internal NAS drives are blurred or configured with generic mock names (e.g., `SAMPLE-NAS`, `UBUNTU-WSL`).
> 4. **Standard Resolution**:
>    - Capture screenshots at clean aspect ratios (1280x720 or 1920x1080) with consistent Fluent Dark styling.

---

## 🛠️ Development & Building

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022, JetBrains Rider, or VS Code with C# DevKit

### Quick Commands
```bash
# Run locally
dotnet run

# Run tests / verify builds
dotnet build -c Release

# Build single-file executable
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Build Output Location
- Debug builds automatically synchronize to:
  ```text
  C:\ClankerExplorer\bin\Debug\net8.0\ClankerExplorer.exe
  ```
- **File Locking Note**: Always ensure `ClankerExplorer.exe` is closed before rebuilding; otherwise, Windows file locks will prevent updating the binaries in `bin\Debug\net8.0\`.

---

## 🔒 Code Review & Security Checklist
- Avoid synchronous disk I/O or blocking operations on the UI thread (`Dispatcher.UIThread`).
- Ensure all process launches use bounded timeouts and cancellation (`RunProcessWithTimeoutAsync`).
- Avoid command-string interpolation in shell/terminal launchers (`ProcessStartInfo.WorkingDirectory` should be set directly).
- Verify cross-platform path compatibility (`FileSystemService.DefaultRootPath`).
