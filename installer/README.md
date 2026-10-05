# Windows installer

`Dispatch-Setup-<version>.exe` installs the desktop app and the `dispatch` CLI as self-contained builds, so the PC
needs no .NET. It is built with [Inno Setup](https://jrsoftware.org/isinfo.php) 6.3+ or 7.

## Build

```powershell
.\installer\build-installer.ps1 -Version 1.2.0
```

The script publishes both projects for `win-x64` into `installer\out\publish`, then compiles `Dispatch.iss`.
If Inno Setup is missing it is installed once with `winget`; or pass `-Iscc "C:\path\to\ISCC.exe"`.
The installer lands in `installer\out\`.

CI: the **Windows installer** workflow builds it on every pull request that touches `src/` or `installer/`, and on
`v*` tags (the tag gives the version). Download it from the run's artifacts.

## What setup does

* Installs into Program Files (or, if the user picks "only for me", into their profile without administrator rights).
* Start menu shortcuts for the app and a **Dispatch command line** prompt; optional desktop shortcut.
* Optional (on by default): adds the CLI folder (`<install dir>\cli`) to `PATH`, so `dispatch run ...` works in any
  terminal. Uninstalling removes it again.
* Optional: registers Dispatch under "Open with" for `.json` and `.bru` files; opening one shows the import preview.
* Upgrades in place (same `AppId`); collections, history and settings in `%LOCALAPPDATA%\Dispatch` are never touched.

Silent install: `Dispatch-Setup-1.2.0.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS="addtopath"`.
