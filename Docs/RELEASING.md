# Releasing AgentUsage for Windows

The default branch publishes Windows releases only. The preserved macOS application lives on the `macos` branch and is outside this pipeline.

## Prepare a release

1. Update `BuildInfo.Version` in `Windows/Models.cs`.
2. Update the assembly versions in `Windows/Program.cs` and the identity version in `Windows/app.manifest`.
3. Add `Docs/releases/vX.Y.Z.md` with the release notes.
4. Build, test, and package from PowerShell:

```powershell
.\Scripts\build-windows.ps1
.\Windows\bin\AgentUsage.Probe.exe --self-test
.\Windows\bin\AgentUsage.Probe.exe
.\Windows\bin\AgentUsage.Probe.exe --check-update
.\Scripts\package-windows.ps1
```

The live probe requires a locally installed, signed-in Codex CLI or Codex Desktop client. The update probe requires access to GitHub.

## Publish

Commit the release changes, create an annotated `vX.Y.Z` tag, and push both the default branch and tag:

```powershell
git push origin master
git push origin vX.Y.Z
```

`.github/workflows/release.yml` validates the tag against `BuildInfo.Version`, builds the app on a Windows runner, creates the ZIP and SHA-256 file, and publishes both files to the GitHub release.

After the workflow succeeds, verify the release page contains:

- `AgentUsage-Windows-vX.Y.Z.zip`
- `AgentUsage-Windows-vX.Y.Z.zip.sha256`

The package is also created locally under `dist/` for testing before publication.
