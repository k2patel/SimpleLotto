# Repository Guidelines

## Project Structure & Module Organization

This repository is in the architecture and early scaffold phase for a WinUI 3 lottery counter app. The root holds `SimpleLotto.sln`, `global.json`, `BuildAndRun.ps1`, and project documentation. Application scaffolding lives in `SimpleLotto.App/`: `App.xaml` and `App.xaml.cs` define startup, `MainWindow.xaml` and `MainWindow.xaml.cs` contain the current prototype surface, `Services/ScannerInputService.cs` owns paired Raw Input capture, and `Styles/Layout.xaml` holds shared XAML styling. Product direction, scanner routing, and architecture rules are documented in `docs/product-instructions.md`; treat that file as the source of truth when shaping architecture or navigation.

## Build, Test, and Development Commands

Windows build and package validation is performed by `.github/workflows/build-windows.yml` on GitHub Actions. Local development does not require a Windows machine or the PowerShell build script. The workflow restores and builds `SimpleLotto.sln`, publishes the self-contained WinUI app, builds the installer, and uploads eligible artifacts.

## Coding Style & Naming Conventions

Use C# 14 with nullable reference types enabled. Follow the existing style: file-scoped namespaces, four-space indentation, `PascalCase` for types and public members, `camelCase` for locals, and `_camelCase` for private fields. Keep XAML names descriptive and control-specific, such as `SalesListView` or `TicketTextBox`. During architecture work, prefer clear domain names and small boundaries over premature abstractions.

## Testing Guidelines

There is no test project yet. For logic-heavy architecture decisions, plan seams that can later be covered in a sibling project such as `SimpleLotto.App.Tests/`. Name future tests after the behavior being verified, for example `AddSale_CalculatesRevenueTotal`. Verify compile/package health through the GitHub Actions Windows workflow and document manual checks for any prototype workflow you touch.

## Commit & Pull Request Guidelines

The current history only contains `first commit`, so no detailed convention is established. Use short imperative commit messages, for example `Define shift closing model`. Pull requests should include a concise summary, architectural rationale, verification steps, linked issue or task when applicable, and screenshots or screen recordings for visible WinUI changes. Note any changes that affect product rules in `docs/product-instructions.md`.

## Agent-Specific Instructions

Before making any changes, create or switch to a dedicated descriptive work branch. Do not implement changes directly on `main`. Branch names must describe the work and must not use a `codex/` prefix unless the user explicitly requests it.

At the start of every session, read `docs/product-instructions.md` before planning or editing. Treat it as the current product source of truth, especially for login, shift closing, inventory, bundle activation, and Rdisplay behavior.

Also read `TODO.md` at the start of every session. It tracks known follow-up work from the latest manual testing, including closing metrics, inventory cards, PDF cleanup, and ticket backfill behavior.

Do not overwrite user work. Check for existing guidance files before adding new ones, keep generated documentation concise, and avoid unrelated refactors. In this phase, do not treat prototype code as final architecture without checking the product instructions.
