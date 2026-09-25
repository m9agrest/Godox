# Windows distribution

## Two build modes

| Command | .NET | Python | Output |
|---|---|---|---|
| `setup.cmd` | Installed runtime required | Local development venv | `build` |
| `build.bat` | Self-contained, win-x64 | Bundled isolated CPython + dependencies | `dist` + installer |

The release executable uses `PublishSingleFile`, `SelfContained` and
`IncludeNativeLibrariesForSelfExtract`. Python and the worker remain alongside it:
the **complete directory or installer** is the distribution, not just the EXE.
Trimming is disabled for WPF and the HTTP host.

## Build machine requirements

- Windows, .NET 10 SDK, Python 3.12 x64 with pip, Git.
- Inno Setup 6 for the installer (`ISCC.exe` in PATH or the default installation path).
- Network access to NuGet, PyPI, GitHub and python.org.

From the repository root:

```cmd
build.bat
build.bat -SkipInstaller
```

The PowerShell driver uses the existing `test/.venv` if available, otherwise `python`
from PATH. Python 3.12 x64 is checked before building native dependency wheels.
The embedded distribution is fetched from python.org with a pinned SHA-256 checksum;
all Python runtime dependencies are pinned in `packaging/requirements-win-x64.txt`.
The target machine never runs pip and needs no downloads during installation.

Build cache is in `.build-cache` and is ignored by Git. The script rebuilds only
`dist/Godox-win-x64`; it does not touch AppData settings or Bluetooth keys.
The `.NET` version is read from the project SDK/framework resolution; update and
rebuild releases to include runtime security updates. Python's embedded archive
version and checksum are defined in `scripts/build-release.ps1`.

## Layout

```text
Godox.Desktop.exe
backend/worker.py
runtime/python/python.exe
runtime/python/python312.zip
runtime/python/Lib/site-packages/...
licenses/...
LICENSE
THIRD_PARTY_NOTICES.md
build-info.json
```

`BackendBridge` prefers the bundled interpreter, including for profiles migrated
from development versions. Its `_pth` file keeps Python imports inside the package.
Bytecode writing is disabled, so the installed application does not create caches
in its own directory. User data continues to live in `%LOCALAPPDATA%\Godox`.

## Installer

`innoSetup.iss` has a stable, project-specific AppId. It installs per user under
`%LOCALAPPDATA%\Programs\Godox Desktop`, adds a Start menu shortcut and optionally
a desktop shortcut. No administrator privileges are required. Uninstall removes
the installed program, preserving settings and Mesh bindings in the separate data folder.
The installer and ZIP contain no developer settings, tokens or binding files.

Version comes from `<Version>` in `Godox.Desktop.csproj`; `build.bat` passes it to Inno Setup.
The build is unsigned unless you add your own code-signing configuration.

## Icon

`assets/godox.png` is the original official-site icon. `assets/godox.ico` includes
16, 24, 32, 48, 64, 128 and 256 px frames and is used by the WPF windows, executable,
installer and shortcuts. To regenerate after updating the source asset:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-icon.ps1
```

See [assets/NOTICE.md](../assets/NOTICE.md) for source and brand attribution.
