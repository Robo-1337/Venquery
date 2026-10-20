# Changelog

All notable changes to Venquery are listed here. Versions follow [Semantic Versioning](https://semver.org).

## 1.4.2 (first public release)

Venquery and Venquery Legacy are now open source. This release includes everything below.

**Devices & Drivers**
- Hardware scan matched to manufacturers' official driver sources, with legacy-GPU warnings so you don't pick
  a driver your card can't run.
- Signed, hash-checked downloads from the vendor (HTTPS only). Venquery never runs installers itself.
- Optional driver updates from Windows Update, and a system report export (TXT, CSV, JSON).

**Backup & Restore**
- Installed apps backup through winget, with a guarded restore script (same-PC lock, skips installed apps,
  typed `Y`).
- Portable apps and Documents backups from the drives or folders you pick, with checked receipts.
- Restore tab that copies backups back into dated folders, never overwriting.
- "Back up everything..." wizard, including an offline driver backup with a hardware-locked restore script.

**Files**
- Organize with preview, undo (two days by default, your choice), "Keep changes", your own rules, a duplicate
  finder and "move ticked files".
- What's using space, read-only.

**Health & Repair**
- Overview of drive health, free space, memory, battery wear, stability and uptime.
- Repair Kit: an optional elevated read-only deep check, plus repairs written as scripts you run yourself.
- Startup & Tasks on/off switches (the same ones Task Manager uses), and Showcase, a full-screen spec card.

**Everywhere**
- A redesigned look: labelled pages with tabs, summary tiles with real data only, a title bar built into the
  app, six themes, background images and a left-handed layout.
- Command line: `--health`, `--report [--json]`, `--version`, `--help`.
- Venquery Legacy brings the core features to Windows 7 and 8.1.

**Security hardening in this release**
- Downloads and the definitions update are HTTPS-only, and a redirect to plain HTTP is refused.
