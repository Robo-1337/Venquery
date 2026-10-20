# Contributing to Venquery

Thanks for wanting to help! Bug reports, ideas, new driver sources and pull requests are all welcome.

## The two house rules

Everything in Venquery follows these, so please keep them in mind for any change:

1. **Windows' own tools only.** Features are built on what ships with Windows (WMI, winget, pnputil, DISM,
   SFC, Task Scheduler, Windows Update, the file system) and .NET itself. No third-party utilities, kernel
   drivers or libraries that replace a Windows feature.
2. **Nothing changes without the user's say-so.** Reading is fine. Writing a file the user asked for is fine.
   Anything that changes the PC is shown first, confirmed (default *No*), reversible where Windows allows it,
   and never deletes the user's data. Repairs are scripts the user runs, behind UAC and a typed `Y`.

There's also a small style rule: user-facing text uses commas or hyphens rather than em dashes.

## Reporting a bug

Open an issue with the bug template. The most useful things to include:

- your Venquery version (title bar) and Windows version,
- what you did, what you expected, and what happened,
- the session log: **Log → Save** in the app, or the files in `%LOCALAPPDATA%\Venquery-Community\Logs`.
  Have a quick look first; logs list device names and folder paths you scanned.

## Suggesting a driver source

`Venquery/definitions.json` is a curated list of official driver pages. An entry is only added when:

- the vendor ID is verified, and
- the URL is the manufacturer's own, live support or download page (no mirrors, no aggregators).

Device-class entries (`pnpClass`) go **before** that vendor's general entry, and bump `updated` when you edit.
Please say in the pull request where you checked the vendor ID and URL.

## Making a change

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download) (Windows).
2. Build and run: `dotnet run --project Venquery`.
3. The Legacy build compiles the same source files for .NET Framework 4.8, so check it still builds:
   `dotnet build legacy`.
4. Run the checks before opening a pull request:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\test.ps1
   ```

   That runs the unit tests, builds both packages and then drives both builds through their features with
   throwaway data in `test-output\` (it never installs, elevates or downloads anything).

Some tips:

- **New network request, process launch or file write?** Expect the tripwire tests (`EgressGuardTests`,
  `LaunchGuardTests`) to have opinions. Launch Windows tools with `SystemTools.Exe("tool.exe")`, and keep any
  network access GET-only and HTTPS.
- **New generated script?** Escape embedded values with `PsText.Lit`, and add it to `ScriptSyntaxTests` so it's
  parsed by PowerShell in the test run.
- **New feature?** Add a check for it in `scripts\feature-check.ps1`.
- **UI:** colours come from theme resources (`{DynamicResource ...}`), so every theme keeps working. On an accent
  background use `OnAccent`, not white. Summary tiles only ever show real data; if a value isn't known yet,
  leave the tile out.

## Pull requests

Keep them focused (one fix or feature each), describe what you changed and how you tested it, and include a
screenshot for anything visual. By contributing you agree that your work is released under the
[MIT license](LICENSE).
