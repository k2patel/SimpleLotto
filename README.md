# SimpleLotto

Simple WinUI 3 lottery counter app based on the `../windowsPOS` project shape.

See [docs/product-instructions.md](docs/product-instructions.md) for the high-level product direction before implementation work.

## Current Scope

- Add local lottery ticket sales
- Track shift ticket count, revenue, average ticket value, and game summary
- Void a selected sale
- Close the current shift

## Build

On Windows with the WinUI prerequisites installed:

```powershell
cd SimpleLotto.App
..\BuildAndRun.ps1 -SkipRun
```

To run after a successful build:

```powershell
cd SimpleLotto.App
..\BuildAndRun.ps1
```

## Windows Installer

Development may happen on macOS, but Windows builds are produced by GitHub Actions. Run the **Build Windows Installer** workflow manually, or push to `main`, to produce and upload:

```text
SimpleLotto-0.0.1-<short-sha>.exe
```

The workflow builds a self-contained WinUI app, packages it with Inno Setup, opens the local Rdisplay/API firewall rule in the installer, signs the installer when code-signing secrets are configured, and uploads it to `files.k2patel.in`.
