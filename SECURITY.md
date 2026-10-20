# Security policy

Venquery runs on people's own PCs and can write scripts that repair Windows, so security reports are taken
seriously and are always welcome.

## Supported versions

Security fixes go into the latest release of both builds:

| Build | Supported |
|---|---|
| Venquery (latest release) | Yes |
| Venquery Legacy (latest release) | Yes |
| Older releases | No, please update first |

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Instead, use GitHub's private reporting:
go to the repository's **Security** tab and choose **Report a vulnerability**.

It helps a lot if you include:

- what you found and why it matters (for example "a file in the Downloads folder can make Venquery run X"),
- the steps to reproduce it, and the Venquery version and Windows version you used,
- a proof of concept if you have one.

You can expect a first reply within a week. Once a fix is ready it ships in a release with credit to you in the
changelog (unless you'd rather stay anonymous).

## What's in scope

Things we especially want to hear about:

- anything that makes Venquery run, install or delete something without the user's confirmation,
- a way to get a generated script, the Repair Kit's elevated read, or a startup switch to do more than it says,
- path tricks that make a backup, restore or organize run write outside the folder it should,
- a restore script that can be fooled into running on a different machine,
- any network request that sends personal or hardware data, or that isn't HTTPS.

Out of scope: problems that need an attacker who already has administrator rights on the PC, and issues in
Windows' own tools (please report those to Microsoft).

## How Venquery protects itself

A quick summary of the safeguards already in place (details in [docs/how-it-works.md](docs/how-it-works.md)):

- Venquery never runs elevated itself; each admin step is a separate, user-approved UAC prompt.
- Windows tools are launched by absolute path, never by bare name.
- Generated scripts escape every value they embed, and user-typed values (like a drive letter) are validated.
- Downloads are HTTPS-only and signature-checked; the app never runs what it downloads.
- Imports and copies stay inside their target folder and never overwrite.
- Unit tests act as tripwires for new network egress and bare-name process launches.
