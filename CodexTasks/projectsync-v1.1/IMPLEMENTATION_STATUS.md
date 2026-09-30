# ProjectSync v1.1 Implementation Status

## Status

`IMPLEMENTED — VERIFICATION INCOMPLETE`

The first approved local behavioral slice is implemented. Production GitHub, Git LFS, VRChat build/upload, and deployment paths remain disabled and unexecuted.

## Workspace/runtime recheck

- Unity: 2022.3.22f1, open during the change
- VRChat Worlds SDK: 3.10.5
- Render pipeline: Built-in RP
- Repository: not initialized as Git; no remote identity exists
- Available .NET SDK: 8.0.204; .NET 10 was not installed automatically
- Production deployment: not requested and not executed

## Implemented evidence

- Static: Core, Infrastructure, specification tests, and WPF desktop compile with zero warnings/errors.
- Domain: exact Session fencing, recovery state, Candidate invalidation, fixed BuildTargetSHA.
- Persistence: operation checkpoint survives disposing and reopening the file journal.
- Failure/Recovery: Issue-success/lock-failure, push failure/retry, unlock failure/retry.
- Concurrency: simultaneous same-account starts permit exactly one active Session.
- Security: forbidden destructive/force Git commands are rejected before process start; client contains no GitHub App key.
- Deployment: not executed.

## Remaining external verification

- Real GitHub Contents CAS, Issue, Check Run, Candidate, PR, Ruleset, and Squash Merge
- Real Git LFS lock behavior for the selected `.unity` path
- Two physical Windows devices using the same GitHub account
- Unity bridge request/response through the active Editor after script import
- Real build clone and BuildTargetSHA evidence
- Real VRChat Upload result and Deployment-record recovery

These require the unresolved production identities and policies listed in `DESIGN_REVIEW_JA.md`.
