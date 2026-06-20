# Security Policy

## Supported versions

Savedrake is a small Windows utility distributed through GitHub Releases. Only the latest release
gets fixes. Please update to the
[latest release](https://github.com/sammorrison9800/Savedrake/releases/latest) before reporting.

| Version        | Supported |
|----------------|-----------|
| Latest release | Yes       |
| Older releases | No        |

## Reporting a vulnerability

Please report security issues privately. Do not open a public issue for a vulnerability.

- Preferred: use GitHub private vulnerability reporting on this repository (the "Report a
  vulnerability" button on the Security tab).
- Or email sammorrison9800@gmail.com with "Savedrake security" in the subject.

Include what you found, how to reproduce it, and the version you tested. This is a hobby project,
so responses are best effort. Please give the maintainer a chance to release a fix before
disclosing the issue publicly.

## Scope

Savedrake reads and writes save files, and it can download and install updates from GitHub
Releases over HTTPS when you choose to update. Reports about the backup and restore flow, or about
the update flow, are especially welcome.

Known limitation: the updater verifies that a downloaded update is a valid package containing the
application, but it does not yet verify a cryptographic signature of the package. Signed updates
are planned. See [ROADMAP.md](ROADMAP.md).
