# ProjectSync v1.1 Evidence Pack

## Phase and source of truth

- Phase: evidence preparation only.
- Canonical product specification: `C:\Users\19718\Downloads\VRChat_ProjectSync_全体運用仕様書_v1.1.docx`.
- Target Unity project: `C:\Users\19718\AppData\Local\VRChatProjects\projectsync`.
- Production code, Unity assets, repository configuration, GitHub configuration, and runtime state remain unchanged.
- The specification defines operational invariants, not a detailed implementation technology. Architecture choices that affect authority, authentication, or failure semantics remain explicit decisions.

## Project facts

| Item | Observed value | Evidence |
|---|---|---|
| Unity | 2022.3.22f1 | `ProjectSettings/ProjectVersion.txt` |
| Project type | VRChat Worlds only | `Packages/vpm-manifest.json`, world scene |
| VRChat SDK | Base 3.10.5, Worlds 3.10.5, VPM Resolver 0.1.29 | `Packages/vpm-manifest.json` |
| UdonSharp | Integrated into Worlds SDK; independent semantic version not exposed | `Packages/com.vrchat.worlds/Integrations/UdonSharp` |
| Render pipeline | Built-in Render Pipeline | `ProjectSettings/GraphicsSettings.asset`, `QualitySettings.asset` |
| Color space | Linear | `ProjectSettings/ProjectSettings.asset`, `m_ActiveColorSpace: 1` |
| Active local build target | Standalone, from generated local Editor state | `Library/EditorUserBuildSettings.asset` |
| Version control | Not a Git repository; no branch, remote, LFS rules, or GitHub configuration | `git rev-parse`, directory inspection |
| Existing ProjectSync | No implementation, tests, workflows, or configuration | `Assets`, `Packages`, `.github` inspection |
| Existing tests | No project-owned tests | project file inventory |
| Current Editor state | The project is open in Unity; unsaved in-memory state is not externally knowable | Unity processes and `Temp/UnityLockfile` |
| Unity plugin compatibility | Unity Technologies plugin live integration is Unity 6+ and cannot be used here | workspace `AGENTS.md`, `UNITY_PLUGIN_INVENTORY.md` |

## Canonical specification invariants

1. `main` is the formal base for starting new work, not proof of VRChat publication.
2. One Task owns one short-lived branch created from the latest formal `main` at Task start.
3. A running Task does not automatically consume later `main` changes.
4. Any local working-tree change forbids branch switching, main application, or another Task start.
5. `reset --hard`, automatic stash, force push, automatic discard, and one-sided automatic conflict resolution are prohibited.
6. Save is a durable staged workflow: Unity save, local commit, branch push, remote reachability verification. A later failure never revokes an earlier successful phase.
7. Submission identity is the Submitted Commit SHA, not the moving branch head. Resubmission creates a new identity and invalidates old validation.
8. Main Scene permission requires both the LFS path lock and matching Task ID, lock ID, GitHub user, Device ID, Session ID, and generation.
9. Shared Task/Lock/Session coordination lives on the separate `projectsync-state` branch and rejects stale writes.
10. Cross-system partial failure produces `RecoveryRequired`; work start, transfer, and submission remain blocked until remote facts are reconciled.
11. A Scene Task abort is not complete until local data policy, old-session invalidation, unlock, and remote unlock verification have all completed.
12. Integration validates a Candidate identified by Submitted SHA, Base Main SHA, and conflict-resolution result. Head, base, or resolution changes invalidate validation.
13. Normal administrators and integration Apps must not bypass required checks, current-base requirements, or validated-Candidate identity.
14. Build uses an independent clone and a fixed BuildTargetSHA. GitHub Deployment is a record of an observed VRChat upload result, not proof by itself.
15. Local-only working-tree files, uncommitted changes, and unpushed commits are independent preservation targets and are never automatically deleted.

## Existing and required change surfaces

| Surface | Current state | Required responsibility |
|---|---|---|
| Windows UI | Absent | Expose only start, resume, save, submit, hold, handoff, abort, recovery, and role-specific administration/build actions |
| Workflow engine | Absent | Durable command journal, explicit phase/outcome, idempotency, reconciliation, and state-machine guards |
| Git adapter | Project is not a repo | Invoke installed Git CLI without exposing Git decisions; inspect before mutate; never use prohibited operations |
| Git LFS adapter | No repository configuration | Lock/list/verify/unlock by remote facts and lock ID; require locking support and `locksverify` policy |
| GitHub adapter | No repository | Issues, Contents CAS, PR, Check, Merge, Deployment and status operations with typed results |
| Unity bridge | Absent | Execute save and validation inside the open Unity 2022.3 Editor main thread and return operation-ID-bound acknowledgements |
| Local persistence | Absent | Store non-authoritative cache, device identity, operation journal, local-data inventory, and recovery evidence outside Git-tracked production files |
| GitHub trust boundary | Undefined | Separate human user credentials from trusted App credentials that create Checks or merge |
| GitHub protection | Undefined | Independent authority and quality rulesets, strict status checks, PR-only updates, force-push/delete blocking |
| Build workspace | Absent | Separate clone, fixed target SHA, per-platform attempts, recovery branch, and upload/deployment reconciliation |
| Tests | Absent | Domain, persistence, process, Git, LFS, GitHub, Unity Editor, concurrency, failure/recovery, security, and manual qualification suites |

## Authority and projection map

| Concept | Authoritative owner required by specification | Projection or cache |
|---|---|---|
| Formal project base | GitHub `main` ref | local `origin/main`, UI status |
| Task human record | GitHub Issue | desktop view and local cache |
| Task content | remote Task branch and exact commit SHAs | working tree, local branch |
| Submitted content | recorded Submitted Commit SHA plus PR relation | branch head and UI label |
| Main Scene path exclusion | Git LFS lock server | local lock cache |
| Task/device/session fencing | CAS-protected record on `projectsync-state` | local current-session cache |
| Validation | trusted GitHub App Check on exact Candidate identity | UI result |
| Merge result | GitHub PR/main state plus recorded adopted and result SHAs | local history view |
| Local unpublished data | local filesystem and local Git | inventory in local journal; never replace with remote inference |
| Build attempt | durable ProjectSync build record keyed by BuildAttempt ID | GitHub Deployment projection |
| Actual upload outcome | observed VRChat SDK/tool result for the attempt and platform | Deployment status; Deployment creation alone is not authority |

## External evidence

### GitHub rules and checks

- Required checks can be strict, requiring the topic branch to be current with the base before merge. A required check can also be constrained to an expected GitHub App source.
  - https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets
- Required checks must apply to the latest relevant SHA. If a test merge commit has status, GitHub evaluates that merge commit; otherwise it evaluates the latest head.
  - https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks
- `skipped` and `neutral` can satisfy a required check. ProjectSync therefore needs a trusted validation contract that reports `success` only after actual validation and never encodes “not run” as a passing conclusion.
- Ruleset bypass is per ruleset. An actor granted bypass can choose to bypass protections in that ruleset, including with “pull requests only.” Authority restriction and quality gates must therefore be separate rulesets, and the quality ruleset must not grant the normal merge actor bypass.
  - https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository
- Repository-level rulesets are editable by repository admins. Organization-level ownership is required if a repository admin must not be able to weaken the quality ruleset.
- Private-repository ruleset availability depends on repository owner, visibility, and GitHub plan.

### GitHub state and merge APIs

- Contents update requires the existing file blob SHA and returns `409 Conflict` for a conflicting update. This supplies per-file optimistic concurrency but not a multi-file or cross-system transaction.
  - https://docs.github.com/en/rest/repos/contents#create-or-update-file-contents
- Issue update does not offer a documented expected revision. Issue body alone cannot safely own stale-write-sensitive machine state; it is suitable as the human record/projection while CAS state owns fencing.
- Check Run creation is a GitHub App operation. It can bind a result to a specific `head_sha` and an external operation identity.
  - https://docs.github.com/en/rest/checks/runs#create-a-check-run
- Pull request merge accepts an expected head `sha` and `merge_method=squash`; a head mismatch returns conflict. Base validity still requires strict protection, a Candidate check, and an immediate pre-merge read.
  - https://docs.github.com/en/rest/pulls/pulls#merge-a-pull-request
- Deployment accepts a SHA as `ref`; `auto_merge` defaults to true and must be explicitly false. Deployment status has no `unknown` value, so the specification's `Unknown` upload outcome needs an independent authoritative attempt record.
  - https://docs.github.com/en/rest/deployments/deployments#create-a-deployment
  - https://docs.github.com/en/rest/deployments/statuses#create-a-deployment-status

### Git LFS locks

- The locking API creates an exclusive path lock and returns lock ID, path, owner, and time. Verify partitions locks into `ours` and `theirs`; unlock uses the lock ID.
  - https://github.com/git-lfs/git-lfs/blob/main/docs/api/locking.md
- Same-user locks from another clone can appear as `ours`; owner and lock ID do not identify the device or session. Device/Session/generation fencing is therefore essential.
  - https://github.com/git-lfs/git-lfs/blob/main/docs/man/git-lfs-locks.adoc
- Locking is a simple single-branch use case; the optional ref is not a Task-scoped authority. The state branch must not reinterpret it as one.
- Lock verification must be explicitly audited. A server that does not implement locking may return 404 without blocking ordinary pushes.

### Unity 2022.3

- `EditorSceneManager.SaveOpenScenes()` saves open scenes and returns a boolean.
  - https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SceneManagement.EditorSceneManager.SaveOpenScenes.html
- `AssetDatabase.SaveAssets()` writes unsaved asset changes but returns no success value and can be affected by `OnWillSaveAssets`.
  - https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetDatabase.SaveAssets.html
- A separate batch-mode Unity instance cannot open the same project while it is open in the Editor. The active Editor needs an in-process bridge for save and validation.
  - https://docs.unity3d.com/2022.3/Documentation/Manual/EditorCommandLineArguments.html

### Authentication

- User Git/LFS authentication can use Git Credential Manager. REST user operations can use GitHub App user access tokens or another explicitly approved user credential model.
- A GitHub App private key must not be shipped in a native desktop client. Installation-token, Check-writing, and bot-merge operations require a trusted backend/Actions boundary.
  - https://docs.github.com/en/apps/creating-github-apps/about-creating-github-apps/best-practices-for-creating-a-github-app

## Candidate solution families without selection

1. External Windows desktop application plus a small Unity Editor bridge, with Git CLI/Git LFS CLI adapters and a separate trusted GitHub App service.
2. Unity Editor-only tool plus the same trusted GitHub App service. This reduces IPC but cannot safely govern closed-Editor operations, build clone lifecycle, installer/device identity, or recovery independently of Unity.
3. External desktop-only tool without an Editor bridge. This cannot prove or perform the first save stage for in-memory Unity state and does not satisfy the specification.

For coordination state, the viable families are:

- one CAS-protected JSON record per coordination aggregate on `projectsync-state`, with Issue as a human projection; or
- a branch-level commit/ref CAS ledger.

Issue body as the only stale-write-sensitive state owner is not viable because Issue update has no expected revision.

## Preliminary risk factors

- Multi-user authority and concurrency across GitHub, Git LFS, local Git, and Unity.
- Irreversible or externally visible operations: unlock, merge, VRChat upload, Deployment publication, branch deletion.
- Credential and GitHub App private-key boundaries.
- Partial success across systems without distributed transactions.
- Preservation of local-only data across crashes, aborts, handoffs, and recovery.
- Live Unity in-memory state and single-instance constraints.
- GitHub plan and organization policy may change whether the required quality gate is enforceable.

## Open Decisions

1. GitHub repository owner type, visibility, and plan.
2. Whether an organization owner, distinct from the normal repository administrator, owns the non-bypass quality ruleset.
3. Trusted GitHub App hosting boundary and operator. A desktop-embedded App private key is not acceptable.
4. Exact Candidate representation: dedicated candidate commit/ref versus GitHub test merge commit, and the SHA to which validation is attached.
5. Single-record coordination aggregate schema and path cardinality for multiple lockable assets.
6. Whether Task Issue is strictly a human projection or contains fields that must be updated together with CAS state.
7. Device ID installation scope: physical machine, Windows user profile, or ProjectSync installation.
8. Target platforms for Build/Publish: PC only or PC plus Android/Quest.
9. VRChat upload success evidence exposed by the installed SDK and how `Unknown` is derived after timeout or crash.
10. Main Scene path and any additional lockable Prefab/Lighting assets.
11. Administrator-only path policy and the exact validation checks required before submission.
12. Whether Unity save during Play Mode, compilation, import, Prefab Stage, or an untitled Scene is rejected or queued.

## Evidence Conflicts

1. Specification: Required Check not executed must block merge. GitHub: required `skipped` or `neutral` can count as passing. The validation producer must narrow success semantics.
2. Specification: `UploadResult=Unknown`. GitHub Deployment Status: no `unknown` state. The build attempt ledger must remain authoritative, with Deployment as a projection.
3. Specification: stale shared-state writes must be rejected. GitHub Issue PATCH: no expected revision. Machine fencing state must use CAS-protected repository content or an equivalent service.
4. Specification: same GitHub user on two devices permits only one session. Git LFS: both clones can see the same user's lock as `ours`. Custom fencing is mandatory.
5. Specification: ProjectSync alone updates coordination state. A pure user-token desktop client cannot cryptographically prove tool-only writes, while a trusted installation App cannot keep its private key in the desktop. A separate trusted writer boundary is required if “ProjectSync only” is an enforced security property.

## Missing Evidence

- Repository URL, default branch, initial canonical `main`, collaboration model, and migration policy.
- GitHub owner/plan/visibility, member roles, Apps, Actions availability, and ruleset administration boundary.
- Actual GitHub.com LFS locking behavior for this repository and account configuration.
- Git/LFS executable and credential deployment policy for end-user PCs.
- VRChat SDK upload result and retry behavior under success-with-lost-response conditions.
- Approved Windows UI framework and installer/update/signing requirements.
- Saved versus unsaved Unity state at implementation start.

## Direct Files High May Need

- `ProjectSettings/ProjectVersion.txt`
- `ProjectSettings/ProjectSettings.asset`
- `ProjectSettings/EditorSettings.asset`
- `ProjectSettings/GraphicsSettings.asset`
- `ProjectSettings/QualitySettings.asset`
- `Packages/manifest.json`
- `Packages/packages-lock.json`
- `Packages/vpm-manifest.json`
- `Assets/Scenes/VRCDefaultWorldScene.unity`
- `C:\Users\19718\Downloads\VRChat_ProjectSync_全体運用仕様書_v1.1.docx`
