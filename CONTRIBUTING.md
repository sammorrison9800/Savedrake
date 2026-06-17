# Contributing to Savedrake

Thanks for your interest. Savedrake is a Windows save manager for Dragon's Dogma 2, written in C#
on .NET Framework 4.8 (WinForms).

## Build and test

You need Windows with Visual Studio 2022 (Community is fine) or the matching MSBuild, plus
`nuget.exe`. The project uses `packages.config`, so restore with NuGet, not `dotnet restore`.

```
nuget restore Savedrake.sln
msbuild Savedrake.sln -t:Build -p:Configuration=Release
```

The solution has three projects:

- the app (`Savedrake v1.2.3`),
- the updater (`updater`),
- a reflection-based restore test harness (`RestoreHarness`).

The harness loads the built app and exercises the restore, backup-naming, and interval-parsing
helpers. Run it after building:

```
RestoreHarness\bin\RestoreHarness.exe
```

It must report all checks passing (a non-zero exit fails CI). CI runs the build in Debug and
Release and runs the harness on every push and pull request, plus a weekly CodeQL scan.

## Making changes

- Branch from `master`, one change per branch.
- Open a pull request against `master`. Keep the build green and the harness passing.
- For changes to backup or restore, add or update a harness check where it makes sense.
- Keep commits focused, and say what changed and why.

## Reporting issues

Use the issue templates for bugs and feature requests. For security issues, see
[SECURITY.md](SECURITY.md). Please do not report vulnerabilities in public issues.
