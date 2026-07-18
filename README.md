# SimpleLotto

Simple WinUI 3 lottery counter app based on the `../windowsPOS` project shape.

See [docs/product-instructions.md](docs/product-instructions.md) for the high-level product direction before implementation work.

## Current Scope

- Add local lottery ticket sales
- Track shift ticket count, revenue, average ticket value, and game summary
- Void a selected sale
- Close the current shift

## Build

Development does not require a local Windows machine. Build and package validation runs through `.github/workflows/build-windows.yml` on GitHub Actions.

## Windows Installer

Run the **Build Windows Installer** workflow manually, open a pull request, or push to `main` to build the Windows package. Installer upload occurs only for allowed non-fork workflow runs.

```text
SimpleLotto-0.0.1-<short-sha>.exe
```

The workflow builds a self-contained WinUI app, packages it with Inno Setup, opens the local Rdisplay/API firewall rule in the installer, signs the installer when code-signing secrets are configured, and uploads it to `files.k2patel.in`.
