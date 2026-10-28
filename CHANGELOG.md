# Changelog

All notable changes to Venquery are listed here. Versions follow [Semantic Versioning](https://semver.org).

## 1.5.0

Much better device identification, and a driver database that's checked, dated and guarded by tests.

**Devices & Drivers**
- About 90 more brands recognised (about 140 in all) across graphics, network, storage, audio, input,
  controllers, capture, docks, printers and scanners, plus more PC makers (Minisforum, Beelink, CHUWI,
  Supermicro, Steam Deck, AYANEO). Every entry is dated and checked against the public PCI / USB ID registries.
- Devices that were missed now show up: Intel Bluetooth, audio on the HD Audio and Intel Smart Sound buses, and
  the HDMI / DisplayPort audio of NVIDIA, AMD and Intel graphics.
- Precise matching: exact device, then board/subsystem, then device type, then vendor, whatever the file order.
  Shared vendor IDs only match listed models, and generic chip IDs are never claimed, so a device is never
  shown under the wrong brand.
- Parts whose chip maker publishes no drivers (a laptop's webcam or audio codec) point to your PC maker's
  support page.
- A board partner's own graphics card tool (ASUS, MSI, GIGABYTE, EVGA, Palit, PNY, GALAX, Sapphire) is listed as
  optional software. The driver still comes from NVIDIA or AMD. Not shown on laptops.
- Links fixed: ROCCAT now points to Turtle Beach, new pages for Cooler Master, Thermaltake, Intel graphics,
  MSI Center and Clevo; VMware links point to Broadcom.
- Printers and USB-serial adapters are scanned.

**For contributors**
- New tests guard the database: no conflicting entries, HTTPS only, links on the vendor's own site, nothing
  claiming a generic ID, every entry dated with its evidence.

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
