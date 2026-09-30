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
- Unity 2022.3 Editor bridge for main-thread Scene and Asset save
- WPF shell that remains safely disabled while GitHub/LFS/service configuration is absent
- Self-contained failure/concurrency specification tests without external packages

## Safety boundary

This slice does not initialize Git, create or mutate a GitHub repository, acquire a real LFS lock, merge, build, upload, publish, or create a Deployment. Those paths require the repository owner, Ruleset authority, trusted GitHub App service location, locked Scene paths, required checks, and VRChat upload evidence contract.

The desktop shell intentionally reports a safe-stop state while those values are absent. GitHub App private keys must never be placed in this project or distributed to clients.

## Build and test

```powershell
dotnet build .\src\ProjectSync.Desktop\ProjectSync.Desktop.csproj
dotnet run --project .\tests\ProjectSync.SpecTests\ProjectSync.SpecTests.csproj
```

The current machine has .NET 8 SDK. The production framework target must be reviewed before release because the design candidate was .NET 10 LTS; no SDK was installed automatically.
