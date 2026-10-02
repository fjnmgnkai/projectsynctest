# ProjectSync

ProjectSync is a Windows front end for the Git, Git LFS, GitHub, and Unity workflow defined by `VRChat_ProjectSync_全体運用仕様書_v1.1.docx`.

## Implemented slice

- CAS-based coordination aggregate with Device ID / Session ID / generation fencing
- Concurrent Scene Task start claim so only one same-account device can become active
- Cross-system partial-failure transition to `RecoveryRequired`
- Durable operation checkpoint contract and atomic JSON journal
- Four-stage save workflow: Unity save, local snapshot, push, remote reachability
- Formal abort workflow: preserve, revoke, unlock, verify, complete
- Submitted/Base/Candidate/conflict/generation validation identity
- Fixed `BuildTargetSHA`, distinct Upload outcome, and `auto_merge=false` deployment contract
- Git process policy rejecting reset, stash, clean, force push, and normal-path remote deletion
- Task-only Git CLI adapter with full-SHA, non-force push and remote reachability verification
- Start/save branch guards that reject `main` and mismatched Task branches before external effects
- Unity 2022.3 Editor bridge for main-thread Scene and Asset save
- WPF shell that remains safely disabled until trusted coordination and production gateways are connected
- Self-contained failure/concurrency specification tests without external packages

## Safety boundary

The repository is connected to the private `fjnmgnkai/projectsynctest` GitHub repository, and the Main Scene LFS lock conflict was proven in an isolated PoC. The new Git adapter is exercised only against temporary local repositories; it is not wired to the desktop actions. Production Task start/save/submit, merge, build, upload, publish, and Deployment remain disabled pending trusted coordination, Unity and GitHub integration, and recovery verification.

GitHub Free cannot enforce Branch Protection or Rulesets on this private repository. The owner accepted a limited, trust-based pilot in which they manage merges; ProjectSync must never present that as satisfying the canonical GitHub-side Quality Gate. See `CodexTasks/projectsync-v1.1/PRIVATE_FREE_PILOT_EXCEPTION.md`. The repository will remain private.

The desktop shell intentionally reports a safe-stop state while the trusted service and verification paths are absent. GitHub App private keys must never be placed in this project or distributed to clients.

## Build and test

```powershell
dotnet build .\src\ProjectSync.Desktop\ProjectSync.Desktop.csproj
dotnet run --project .\tests\ProjectSync.SpecTests\ProjectSync.SpecTests.csproj
```

The current machine has .NET 8 SDK. The production framework target must be reviewed before release because the design candidate was .NET 10 LTS; no SDK was installed automatically.

## Portable Windows preview

After committing source changes, run `packaging/New-PortablePackage.ps1` from PowerShell to produce a self-contained Windows x64 ZIP and SHA-256 file in `dist/`. The archive contains the desktop executable and Japanese usage/safety notes, not the Unity project or credentials. On another PC, extract it, launch the EXE, and select a Unity project folder. Task actions remain disabled until the trusted production gateways are completed.
