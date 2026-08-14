# WinGet packaging

wip ships to WinGet as a **zip installer with a nested portable executable** — the manifest
shape built for exactly this case, an archive containing one binary. `winget install` unpacks
it, registers `wip.exe` under a links directory, and puts that directory on PATH.

`.github/workflows/winget.yml` opens the pull request against
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) automatically when a release
is published. The manifests below are what that produces; they live here so the shape is
reviewable in this repository and can be validated by hand before the first submission.

## One-time setup

1. **Fork microsoft/winget-pkgs** on the account that will own the submissions.
2. **Create a classic personal access token** with the `public_repo` scope, and add it to this
   repository as the `WINGET_TOKEN` secret. `GITHUB_TOKEN` cannot be used — it has no rights
   on another repository — and the workflow skips itself rather than failing every release
   while the secret is missing.
3. **Confirm the package identifier.** `Slidict.Wip` is assumed throughout. The publisher
   half has to correspond to a real publisher name, so check it before the first submission:
   changing an identifier after the fact means a new package, not a rename.

## Validating locally before the first submission

The first submission gets a human review, and a rejected one costs days. Check it locally
first, against a real published release:

```powershell
winget validate --manifest packaging/winget/manifests
winget install --manifest packaging/winget/manifests
```

## Manifest shape

Three files are required. `<version>` and `<sha256>` are filled in per release; everything
else is stable.

```yaml
# Slidict.Wip.installer.yaml
PackageIdentifier: Slidict.Wip
PackageVersion: <version>
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: wip.exe
    PortableCommandAlias: wip
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/slidict/wip/releases/download/v<version>/wip-<version>-win-x64.zip
    InstallerSha256: <sha256>
ManifestType: installer
ManifestVersion: 1.6.0
```

```yaml
# Slidict.Wip.locale.en-US.yaml
PackageIdentifier: Slidict.Wip
PackageVersion: <version>
PackageLocale: en-US
Publisher: slidict
PublisherUrl: https://github.com/slidict
PackageName: wip
PackageUrl: https://github.com/slidict/wip
License: MIT
LicenseUrl: https://github.com/slidict/wip/blob/main/LICENSE
ShortDescription: A developer-friendly CLI wrapper for Microsoft WSLC.
Tags:
  - wsl
  - wslc
  - containers
  - cli
ManifestType: defaultLocale
ManifestVersion: 1.6.0
```

```yaml
# Slidict.Wip.yaml
PackageIdentifier: Slidict.Wip
PackageVersion: <version>
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
```

## Artifact naming is load-bearing

`InstallerUrl` embeds both the tag and the file name, so the naming the release workflow uses
cannot change once the first manifest is accepted:

```
wip-<version>-win-x64.zip
```

It is already shaped to take an `arm64` sibling: adding one means another `Installers:` entry
and another publish job, not a new convention.

## After installing

From PowerShell or cmd, `wip` works directly. **From a WSL2 shell the extension is
required — `wip.exe`** — because bash does not consult PATHEXT. See the
[migration plan](../../docs/csharp-migration-plan.md) §3.5 for the options being weighed
there.
