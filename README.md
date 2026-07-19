# SimpleLotto

Simple WinUI 3 lottery counter app based on the `../windowsPOS` project shape.

See [docs/product-instructions.md](docs/product-instructions.md) for the product direction, workflow rules, and first-deliverable architecture before implementation work.

## Current Scope

- Record local lottery ticket sales and shift totals
- Receive unopened inventory and activate bundles into bins
- Reconcile and close the current shift
- Pair a HID barcode scanner for global background capture, with a focused fallback for an unpaired scanner

## Architecture

SimpleLotto is a modular WinUI 3 application with local SQLite storage. Scanner capture is an adapter boundary: it identifies the raw input source and complete barcode, centralizes barcode classification, then routes the classified scan to the current product workflow. The HID Raw Input capture model is reused from WindowsPOS; SimpleLotto retains its own sales, inventory, and placement rules. Each game stores its ticket price, bundle price, and first-ticket mode; those values calculate the valid ticket range and prevent a sold-out bundle from advancing past its last ticket. The accounting ledger assigns every sale a persistent sale ID, explicit close-interval ID, and stable actor ID; closing atomically closes that interval and opens the next one, so wall-clock changes cannot alter shift membership. See the [scanner and runtime contract](docs/product-instructions.md#scanner-and-runtime-behavior) and [backend/storage rules](docs/product-instructions.md#backend-and-storage-direction) for the detailed behavior.

## Build

Development does not require a local Windows machine. Build and package validation runs through `.github/workflows/build-windows.yml` on GitHub Actions.

## Windows Installer

Run the **Build Windows Installer** workflow manually, open a pull request, or push to `main` to build the Windows package. Installer upload occurs only for allowed non-fork workflow runs.

```text
SimpleLotto-0.0.1-<short-sha>.exe
```

The workflow builds a self-contained WinUI app, packages it with Inno Setup, opens the local Rdisplay/API firewall rule in the installer, signs the installer when code-signing secrets are configured, and uploads it to `files.k2patel.in`.
