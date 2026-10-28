# How Venquery works

This page is for anyone who wants to know exactly what Venquery does on their PC before they run it. The short
version: it reads, it writes files you asked for, and everything else waits for your confirmation.

## The Safe Mode promise

The title bar always shows a **Safe Mode** pill with a one-line reminder of what the current page can do.
Under the hood, every action falls into one of four buckets:

| Kind of action | Examples | What guards it |
|---|---|---|
| **Read** | hardware scan, health overview, file scans, What's using space | Nothing to guard: it only looks. File scans read names, sizes and dates, never contents. |
| **Write a file** | driver download, backups, reports, generated scripts | Written to the folder you chose (Downloads by default). Existing files are never overwritten; a taken name becomes `name (2)`. |
| **Run a script you approve** | repairs, restore point, app restore, driver restore | Venquery writes a short, readable PowerShell script. You start it, Windows asks for administrator approval (UAC), and the script itself waits for a typed `Y`. |
| **A confirmed, reversible change** | startup and scheduled-task switches, organizing files | Asked first (the default answer is always *No*), done one item at a time, and reversible: switches flip back, organize runs can be undone. Nothing is deleted. |

Venquery itself never runs as administrator. When something genuinely needs admin rights (the Repair Kit's
deep read, a machine-wide startup switch), only that one step is elevated, and only after you approve the UAC
prompt.

## Windows' own tools only

Every feature is built on something that ships with Windows:

| Feature | Built on |
|---|---|
| Hardware and health data | WMI, the Windows event log, the registry, SMART data through Windows' own storage APIs |
| Driver sources | A curated database (`Venquery/definitions.json`) of manufacturers' official support pages and installers |
| Optional driver updates | The Windows Update API (Windows itself does the check) |
| App backup and restore | winget, Microsoft's package manager |
| Driver backup and restore | `pnputil` (export is copy-only) |
| Repairs | DISM, SFC, CHKDSK, `netsh`, `ipconfig`, `gpupdate`, `powercfg`, Disk Cleanup, System Restore |
| Startup switches | The same `StartupApproved` values and Task Scheduler switches Task Manager uses |
| File organizing | Plain file moves, and .NET's SHA-256 for the duplicate finder |

No kernel drivers, no bundled third-party utilities, no background service, nothing left running when you
close the app. Windows tools are always started by their full path (for example
`C:\Windows\System32\pnputil.exe`), so a look-alike file sitting next to Venquery can't be run instead.

## How a device is identified

Windows gives every device hardware IDs (for example `PCI\VEN_10DE&DEV_2684&SUBSYS_88D51043`). Venquery reads
the vendor and device IDs on the PCI, USB, HD Audio and Intel Smart Sound buses, plus the subsystem ID: the
company that built the board around the chip. Then it looks them up in `Venquery/definitions.json`:

- **The most specific entry wins:** an exact device beats a board/subsystem entry, which beats a device-type
  entry, which beats a whole vendor. The order of entries in the file doesn't matter.
- **Shared vendor IDs only match listed models.** Some companies' IDs cover very different products (a phone
  and a portable SSD, or several brands' SSDs on one controller maker's ID), so those entries list exact
  device IDs.
- **Never-claim list.** Generic chip IDs used by countless unrelated products (USB hub and card-reader chips,
  microcontrollers, open-hardware IDs, generic gamepads) are never matched.
- **Your PC maker's parts.** Some chip companies don't publish drivers for the public; the laptop or PC maker
  ships them. Those parts point to your PC maker's support page and are never shown under the chip maker's name.
  On a self-built PC this only applies to parts on the motherboard.
- **Graphics card makers' tools.** When a card's subsystem ID belongs to a board partner, that partner's tuning
  tool is added as optional software. The driver entry (NVIDIA, AMD) doesn't change. Laptops are skipped, since
  their subsystem ID is the laptop maker's.

Every entry records when it was last checked and how: the vendor ID against the public PCI and USB ID
registries, and the page by opening it. Tests (`DefinitionsDatabaseTests`, `DefinitionMatchingTests`) refuse
conflicting entries, non-HTTPS links, links off the vendor's own site, claimed generic IDs and undated entries.

## Network use

Venquery has no telemetry, analytics, crash reporting or update pings. It talks to the internet in exactly
three situations, all of them GET requests:

1. **Opening a driver link or downloading a driver you selected.** The address comes from the curated
   database and points at the manufacturer's own site. Only HTTPS links are allowed (a redirect to plain HTTP
   is refused), and the downloaded file's signature is checked afterwards.
2. **The NVIDIA lookup** for GeForce cards, which asks NVIDIA's public driver service for its current driver
   using a fixed product query. Your actual GPU model and IDs are not sent.
3. **Installing winget**, only if it's missing and only when you run the script Venquery wrote for that. The
   script downloads Microsoft's own App Installer packages from Microsoft's GitHub releases.

Windows Update and winget make their own connections when Venquery asks them to check for drivers or list
apps; that's Windows talking to Microsoft, the same as when you use those tools directly.

A unit test (`EgressGuardTests`) fails the build if anyone adds a way to send data out (an HTTP POST, a socket,
mail, and so on), and another (`LaunchGuardTests`) fails if a Windows tool is ever started by bare name.

## The hardware locks

Restoring apps or drivers onto the wrong machine can cause real trouble, so the restore scripts check where
they're running before they touch anything:

- The **app restore** script only runs on the PC model the backup was made on (unless you allowed cross-PC
  restore when you made the backup), and skips apps that are already installed.
- The **driver restore** script compares make and model, CPU, motherboard and GPU against a fingerprint saved
  with the backup. If anything differs, or can't be read, it refuses. There is no override.

## Where things are stored

| What | Where |
|---|---|
| Settings, session logs, organize undo history | `%LOCALAPPDATA%\Venquery-Community` (Legacy: `%LOCALAPPDATA%\Venquery-Legacy`) |
| Background images you add | a `Themes` folder next to the exe |
| Downloads, backups, scripts, reports | the folder you chose, otherwise Downloads (reports go to a `SystemReports` folder next to the exe when it's writable) |

Undo records for organize runs are kept for the window you pick (two days by default) and are only ever
Venquery's own small JSON files. Your files are never deleted by retention.
