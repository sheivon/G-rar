# G-RAR

Cross-platform, WinRAR-style archive manager. Browse disks and archives in one window, create and extract ZIP / 7z / TAR / RAR, and handle password-protected files through a dedicated dialog.

Built with **.NET 9** and **Avalonia 11** so the same desktop UI runs on Linux, macOS, and Windows.

## Features

- Dual-pane explorer: folder tree on the left, sortable file list on the right
- Open archives in place and navigate folders inside them
- Create archives (ZIP in-process; 7z and RAR via installed `7z` / `rar` tools)
- Extract all or selected entries, with overwrite ask / replace / skip
- Test archives for corruption
- Password dialog when opening or extracting encrypted archives (retries on a wrong password)
- Add, delete, and update entries in existing ZIP / 7z / RAR containers
- Progress window with cancel for long operations
- Address bar, back / forward / up, hidden files, status bar
- Options for compression defaults, extract behaviour, and confirmations
- Settings stored as JSON under the user config folder (`G-RAR/settings.json`)

## Supported formats

| Format | Read / list | Create / update | Notes |
| --- | --- | --- | --- |
| ZIP | Yes | Yes (built-in) | Optional ZipCrypto password |
| 7z | Yes | Yes if `7z` / `7za` / `7zz` is on PATH | |
| RAR | Yes | Yes if `rar` / WinRAR is on PATH | Proprietary engine required to write |
| TAR | Yes | Yes (built-in) | No password |
| TAR.GZ / GZ | Yes | TAR.GZ write | Gzip is inflated, then listed as TAR or a single file |

Readable extensions also include `.bz2` and `.xz` through SharpCompress.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- A desktop session (X11 / Wayland / macOS / Windows)
- Optional: [7-Zip](https://www.7-zip.org/) and/or [WinRAR / rar](https://www.rarlab.com/) for creating `.7z` and `.rar`

## Build and run

From the repository root:

```bash
# Linux
./build-run-linux.sh
./build-run-linux.sh --release

# macOS
./build-run-macos.sh
./build-run-macos.sh --release
```

```powershell
# Windows
.\build-run-windows.ps1
.\build-run-windows.ps1 -Release
```

Or with the SDK directly:

```bash
dotnet run --project src/G-RAR/G-RAR.csproj
```

The scripts restore, build, and launch the Avalonia app. Extra arguments after `--` are passed through to the application.

## Project structure

```
G-RAR.slnx                 Solution
src/G-RAR/                 Desktop application
  Program.cs               Avalonia entry point
  App.axaml(.cs)           Application + Fluent theme
  Views/                   Main window and dialogs (XAML)
  ViewModels/              Commands and dialog state
  Models/                  Archive entries, settings, file types
  Services/                Archive I/O, filesystem, settings, external tools
  Helpers/                 Paths, icons, formatting
  Styles/                  Theme and toolbar icons
tests/                     Test project placeholder
build-run-*.sh / .ps1      Per-OS build and run
push-github.sh             Push this tree to GitHub
```

### How it is layered

- **Views** bind to view-models (`MainWindow`, extract / add / password / progress / options dialogs).
- **MainWindowViewModel** owns navigation (disk vs archive), selection, and every toolbar command.
- **ArchiveService** lists, extracts, tests, creates, and edits archives. ZIP/TAR writing is in-process (`ZipArchiveWriter`); 7z/RAR writing shells out through **ExternalTools**.
- **GZipContainer** inflates `.gz` / `.tgz` so TAR-inside-gzip and single-file gzip both appear as normal listings.

## Passwords

If an archive (or an entry inside it) is encrypted, G-RAR shows **Enter password** before opening or extracting. The password is kept for that archive for the rest of the session. Creating ZIP / 7z / RAR archives can also set a password from the Add dialog.

## License

MIT — see [LICENSE](LICENSE).
