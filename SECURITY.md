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

Savedrake reads and writes save files. It checks GitHub Releases over HTTPS to see whether a newer
version exists and, if so, points you to the Nexus Mods downloads page; it does not download or run
updates itself. Reports about the backup and restore flow are especially welcome.

Known limitation: Savedrake is not yet code-signed, so Windows SmartScreen may warn on first run and
you cannot yet verify the publisher of a download cryptographically. Signed builds are planned. See
[ROADMAP.md](ROADMAP.md).
