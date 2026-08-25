# ClypDat - Security Policy

## Reporting a Vulnerability

If you believe you have found a security vulnerability in ClypDat, please report it
responsibly using **one of the methods below**.

### Preferred: GitHub Private Vulnerability Reporting

Use GitHub's [Private Vulnerability Reporting](https://github.com/ClypLabs/ClypDat/security/advisories/new)
on this repository. It lets you report confidentially, without public disclosure, and
keeps the discussion attached to the code.

This is the recommended method.

### Alternative: Email

If Private Vulnerability Reporting is unavailable or unsuitable, contact us by email.

**Email:** hi@clypdat.xyz

Please include:

- A clear description of the issue
- Steps to reproduce (if applicable)
- Potential impact
- Any relevant logs, screenshots, or proof-of-concept details

If the issue involves a crash or capture failure, the diagnostic bundle from
**Settings → About → Diagnostics → Export Bundle** is useful. It scrubs your user profile path,
library folders, machine name and UNC share names, but please read it before sending -
it also contains game titles and window names.

**Do not** open public GitHub issues or pull requests to report security vulnerabilities.

---

## Project Scope

ClypDat is a Windows desktop application that records gameplay, plus the infrastructure
that ships and updates it:

- The desktop application (open source, this repository)
- The auto-update path: release metadata, the signed release manifest, and the installer
- The download mirror (`clypdat.xyz` and its object storage) and the marketing site
- The Avalonia fork used for the UI, where the issue affects ClypDat

Security reports affecting **any of these** are in scope, including components that are
hosted rather than distributed.

Because of what ClypDat does, these areas are of particular interest:

- **The updater.** Anything that could cause a client to install a payload we did not
  publish - forged or replayed release metadata, a bypass of the signed-manifest check,
  or a way to reach an untrusted download URL.
- **The local listeners.** ClypDat binds loopback HTTP ports to receive Game State
  Integration events from supported games. Anything that lets an unauthorised local
  process, or a web page in the user's browser, drive recording or reach app state.
- **Media handling.** ClypDat parses video the user did not create - imported clips and
  files from other capture tools - through bundled FFmpeg and its own parsing code.
- **File handling.** Anything that causes ClypDat to read, move, or delete a file outside
  its library, including via junctions, symlinks, or crafted importer databases.
- **The capture pipeline.** Memory-safety issues in the native capture and encode path.

### Out of scope

- Vulnerabilities in third-party software we bundle but do not maintain (FFmpeg, LibVLC,
  .NET, Avalonia upstream) - report those to their maintainers. If a specific bundled
  version leaves ClypDat users exposed, we do want to know, so tell us and we will ship
  an updated build.
- Attacks that require an attacker who already has administrator rights, or the ability
  to run arbitrary code as the user. ClypDat installs per-user into a writable directory;
  someone at that privilege level can replace the executable outright, so we do not treat
  that as a boundary.
- Reports produced solely by an automated scanner, with no demonstrated impact.

---

## Release Integrity

Releases carry a signed manifest (`ClypDat-Release.manifest.json` and its detached
`.sig`) listing each asset's SHA-256. The updater verifies that signature against a
public key compiled into the application before it will trust any digest or install
anything, and refuses updates that are unsigned or that fail verification.

If you believe a release signing key has been compromised, or that a client accepted an
update it should have rejected, treat it as a **critical** report and say so in the
subject line.

---

## Supported Versions

Security fixes are shipped in the current release. There are no long-term support
branches; users on older versions should update via the in-app updater or by installing
the latest release.

---

## CVE Policy

CVE identifiers may be requested for vulnerabilities affecting publicly distributed
components - the desktop application and its installers.

Issues limited to hosted infrastructure (the mirror, the website, the release pipeline)
may be fixed and documented without a CVE identifier, since there is no version for users
to upgrade.

---

## Disclosure

We review reports and decide remediation and disclosure case by case. We will confirm
receipt, tell you whether we consider the report in scope, and let you know when a fix
ships.

We ask that you do not publicly disclose an issue before we have had a reasonable
opportunity to review and address it. We will not take legal action against researchers
who report in good faith, act within the scope above, and avoid privacy violations,
service disruption, or data destruction while investigating.

Credit is given to reporters in the release notes unless you ask us not to.
