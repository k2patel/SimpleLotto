# Handoff

## State
I implemented WindowsPOS-compatible license call-home/cache/signature verification and a shell license banner, then fixed CI with commit `105ac43`.
I also added startup-only, non-blocking automatic app upgrade checks on first-week Monday, second-week Tuesday, third-week Wednesday, and fourth-week Thursday; pending local changes document and implement that schedule.

## Next
Verify Windows CI after committing the upgrade-schedule/docs changes.
Implement the final license expiry lifecycle: renew-soon 7 days before `expires_at`, grace from `expires_at` through `expires_at + 7`, then lock.
Extend Rdisplay payload/renderer for transparent expiry/grace banners; current Rdisplay only understands `license_status`.

## Context
Do not add hourly upgrade polling; user explicitly wants scheduled startup-only background checks.
The macOS host cannot execute WinUI `XamlCompiler.exe`, so use GitHub Actions/Windows for full build verification.
