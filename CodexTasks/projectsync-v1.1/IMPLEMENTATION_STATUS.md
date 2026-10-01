# ProjectSync v1.1 Implementation Status

## Status

`IMPLEMENTED — VERIFICATION INCOMPLETE`

The first behavioral slice and a local Git Task-save adapter are implemented. Repository bootstrap and an isolated Git LFS lock/unlock PoC were executed; production Task workflows, merge, VRChat build/upload, and deployment remain disabled. The owner approved the limited Private + Free pilot exception recorded in `PRIVATE_FREE_PILOT_EXCEPTION.md`.

## Workspace/runtime recheck

- Unity: 2022.3.22f1; Editor not running at the latest process check. No Scene or Asset files were changed in this slice.
- VRChat Worlds SDK: 3.10.5
- Render pipeline: Built-in RP
- Git repository: `fjnmgnkai/projectsynctest`, private, default branch `main`
- Latest fetched main before this slice: `a7fa87a978ffc24016c46b703a2ea120a979e22d`
- Development branch: `task/projectsync-main-guard`
- Available .NET SDK: 8.0.204; .NET 10 was not installed automatically
- Production deployment: not requested and not executed

## Current change safety contract

| Field | Current slice |
|---|---|
| Gate / mode / risk | `PROCEED` / `FEATURE` / `RISK-HIGH` (Git mutation and remote refs) |
| Behavioral slice | Given a valid active Task branch, a save snapshot may commit and push only that Task ref; a request for `main` or a different Task ref is rejected before Unity or Git mutation. |
| Production path | Desktop actions remain disabled. The new `GitCliTaskGateway` is not yet connected to a production workflow. |
| Authority | Coordination state owns the Task branch identity; Git owns commit/ref facts; the operation journal binds retries to one branch. |
| Trust boundary | The Git process runner is internal to Infrastructure; only typed Task operations are exposed. No GitHub App credential enters the client. |
| Side effects / retry | `git add`, commit, and non-force Task push only. A commit trailer identifies a lost-response snapshot retry; remote reachability is read back after push. |
| Concurrency | The adapter requires a per-repository process lock before production wiring; concurrent external Git processes remain outside this slice. |
| Deployment | No production remote, Unity build, VRChat upload, or deployment operation was executed in this slice. |
| Evidence | Core/domain tests, real temporary local Git remote, remote-main SHA comparison, non-fast-forward rejection, .NET build. GitHub/Unity GUI end-to-end remains pending. |

## Implemented evidence

- Static: Core, Infrastructure, specification tests, and WPF desktop compile with zero warnings/errors.
- Domain: exact Session fencing, recovery state, Candidate invalidation, fixed BuildTargetSHA.
- Persistence: operation checkpoint survives disposing and reopening the file journal.
- Failure/Recovery: Issue-success/lock-failure, push failure/retry, unlock failure/retry.
- Concurrency: simultaneous same-account starts permit exactly one active Session.
- External/LFS PoC: two independent clones using the same GitHub account observed one lock; the second acquisition was rejected; ID-based unlock was remotely confirmed.
- Security: forbidden destructive/force Git commands are rejected before process start; client contains no GitHub App key.
- Git Task path: strict command allowlist, exact `task/...` branch binding, full-SHA non-force push, remote reachability check, and operation-trailer snapshot retry. A real local bare-remote test confirmed that the Task ref advances while `main` remains unchanged, and that a stale push cannot overwrite a newer Task commit.
- Deployment: not executed.

## Remaining external verification

- Real GitHub Contents CAS, Issue, Check Run, Candidate, PR, Ruleset, and Squash Merge
- Real GitHub Ruleset/Branch Protection is unavailable on this Private + Free repository; GitHub returned HTTP 403. The owner chose not to upgrade or make it public. AT-60 therefore remains unmet under the explicit pilot exception.
- Real Git LFS simultaneous lock behavior has been verified for `Assets/Scenes/VRCDefaultWorldScene.unity`; Device/Session/generation fencing still requires the trusted state service.
- Two physical Windows devices using the same GitHub account
- Unity bridge request/response through the active Editor after script import
- Real build clone and BuildTargetSHA evidence
- Real VRChat Upload result and Deployment-record recovery

These require the unresolved production identities and policies listed in `DESIGN_REVIEW_JA.md`.
