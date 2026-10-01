# ProjectSync v1.1 Implementation Design Proposal

> Historical pre-implementation proposal. For current code, test, and pilot status, see `IMPLEMENTATION_STATUS.md` and `PRIVATE_FREE_PILOT_EXCEPTION.md`. Statements below about an uninitialized repository or production code not yet existing describe the earlier design phase.

## Status

`REDIRECT TO RISK FIRST POC — PRODUCTION UNCHANGED`

This is a design proposal for user approval. It does not authorize production code, `git init`, a GitHub repository change, Ruleset configuration, LFS lock mutation, merge, build, upload, or deployment.

The canonical v1.1 invariants are implementable, but the GitHub plan and authority boundary, trusted GitHub App operation, actual LFS behavior, and VRChat upload evidence must be proven before production implementation.

## Safety Decision

| Field | Decision |
|---|---|
| Gate | `REDIRECT` |
| Mode after approval | `FEATURE`, beginning with an isolated test-repository PoC |
| Risk | `RISK-HIGH` |
| Primary reason | Multi-user authority, credentials, concurrency, cross-system partial failure, merge, unlock, and upload are involved |
| Deployment boundary | No production GitHub or VRChat operation until the listed PoCs pass |
| First behavioral slice | Same-account two-device Main Scene Task start with exactly one valid Session |

### Behavioral slice

**Given** PC1 and PC2 use the same GitHub account and both request the same Main Scene Task, **when** LFS lock acquisition and coordination-state CAS execute concurrently, **then** exactly one Device ID / Session ID / generation becomes active; the loser cannot receive edit, save, or submit permission.

Failure clauses:

- If Issue creation succeeds and LFS or coordination-state update fails, the Task becomes `RecoveryRequired`.
- Restarting either client with the same operation ID must not create another Task, lock, or Session.
- A transport timeout is `OutcomeUnknown`, not failure; remote facts are read before any retry.
- The losing device may preserve local data but cannot push, submit, or become automatically adopted.

Required evidence dimensions: Domain, Persistence, Transport, Failure/Recovery, Workflow, GUI, External, Security, Concurrency, and Deployment for later Build slices.

## Proposed system boundary

```text
ProjectSync Windows App
  ├─ Local workflow engine and durable journal
  ├─ Git CLI and Git LFS CLI adapters
  ├─ Build clone manager
  ├─ Windows credential and device identity adapters
  ├─ Named local connection to Unity Editor Bridge
  └─ Authenticated commands to Trusted GitHub App Service

Unity Editor Bridge
  ├─ Scene and Asset save on the Unity main thread
  ├─ Project validation
  └─ Operation-bound acknowledgements only

Trusted GitHub App Service
  ├─ projectsync-state CAS and command serialization
  ├─ Issue, candidate ref, PR, Check, merge and Deployment projection
  └─ Reconciliation using remote read-back

GitHub and Git LFS
  ├─ main, Task refs and Candidate refs
  ├─ Task Issues and Pull Requests
  ├─ LFS path locks
  ├─ Required Checks and Rulesets
  └─ Deployment records
```

### Selected architecture

Use a Windows desktop orchestrator, a small Unity 2022.3 Editor-only bridge, and a separately trusted GitHub App service.

Rejected families:

- Unity Editor-only: cannot reliably manage closed/broken Editor states, independent Build clones, installation identity, or recovery outside Unity.
- Desktop-only: cannot see or save in-memory Unity Scene and Asset changes.
- Pure local client with embedded GitHub App credentials: cannot protect the App private key or establish a trusted Check/state writer.
- Reimplementing Git: outside the specification and would diverge from Git/Git LFS behavior.

Recommended implementation technology, pending approval:

- Windows app: .NET 10 LTS WPF.
- Domain/application core: UI-independent .NET libraries.
- Local durable journal: SQLite or an equivalent transactional embedded store under `%LOCALAPPDATA%\ProjectSync`.
- Unity bridge: Editor-only assembly/package compatible with Unity 2022.3; no Unity 6 pipeline dependency.
- Local IPC: authenticated per-user named pipe with operation ID and Editor-session nonce.
- Git integration: installed Git and Git LFS command-line clients through a strict command allowlist.
- GitHub integration: versioned REST contracts through the trusted service.

The UI framework, journal library, IPC library, installer, and exact schema field names remain local implementation choices after the authority model is approved.

## Component responsibilities

| Component | Owns | Must not own |
|---|---|---|
| Desktop UI | User intent and presentation of allowed next actions | Authoritative remote state, Git decision prompts |
| Workflow engine | Transition policy, command guards, saga phase progression | GitHub truth inferred from cached UI state |
| Local journal | Durable operation IDs, local phase facts, local-data inventory, cached remote identities | Shared Task or Session authority |
| Git adapter | Exact local/remote Git facts and explicit allowed Git commands | Automatic conflict resolution, stash, reset, force push |
| LFS adapter | Lock/list/verify/unlock and LFS transfer facts | Device/session permission by owner name alone |
| Unity bridge | Unity save and validation facts from the active Editor | Git/GitHub mutation or background polling authority |
| GitHub App service | Serialized remote commands, CAS state, Check producer, candidate and merge control | Local-only data deletion or Unity in-memory assumptions |
| GitHub Issue | Human-readable Task definition, assignment and progress record | Session fencing by itself |
| `projectsync-state` | Machine coordination, active Session fencing, recovery and BuildAttempt records | Unity content or local unpublished data |
| Git LFS server | Path-level lock existence and lock ID/owner | Task, Device, Session or generation identity |
| GitHub Deployment | Projection of an observed BuildAttempt outcome | Proof that VRChat upload succeeded |

## State model

### User-facing Task lifecycle

`NoTask → Starting → Working → Submitted → AwaitingReview → Integrating → Completed`

Additional transitions:

- `AwaitingReview → ChangesRequested → Working → Submitted`
- `Working → OnHold → Working`
- `Working → HandingOff → Working on new Session`
- `Working | OnHold | ChangesRequested → Aborting → Aborted`
- Any external inconsistency affecting permission or adoption → `RecoveryRequired`

The product UI uses Japanese labels from the specification. The English names above are stable design identifiers only.

### Orthogonal state axes

| Axis | Required values |
|---|---|
| Task progress | NotStarted, Working, AwaitingReview, ChangesRequested, OnHold, Integrating, Completed, Aborted |
| Unity save | Dirty, DiskSaved, Unknown |
| Local Git | Unrecorded, Committed, Unknown |
| Remote sync | Pending, Reachable, OutcomeUnknown |
| Validation | NotRun, Passed, Failed, NeedsReview, Invalidated |
| Edit permission | NotAcquired, Held, HandingOff, Released, ReleaseUnconfirmed |
| Adoption | NotSubmitted, Submitted, Adopted, UnadoptedChanges |
| Work Session | NotIssued, Active, HandingOff, Revoked, RecoveryRequired |
| Operation | Planned, Running, OutcomeUnknown, Succeeded, Failed, RecoveryRequired |

`terminal` and `succeeded` are not synonyms. A stopped operation can still have `OutcomeUnknown` until reconciliation.

### Coordination aggregate

Recommended v1.1 layout: one Repository-level coordination aggregate JSON on `projectsync-state`, updated with the existing blob SHA.

It contains:

- schema version and repository identity;
- current state revision and last operation ID;
- Task coordination records;
- active Device ID, Session ID, GitHub user and generation;
- zero or more lock records containing path, LFS lock ID, owner and verification time;
- pending saga/recovery marker;
- Submitted, Candidate and adopted SHA relations;
- pointers to per-attempt Build records.

Rationale: one CAS boundary prevents two files from separately authorizing incompatible Sessions or multi-path locks. A conflicting write is rejected, reread, and re-evaluated. Blind retry is forbidden.

Build attempts are separate CAS records keyed by BuildAttempt ID so build history does not create contention in the live Session aggregate.

### Issue and state field ownership

Proposed interpretation requiring user approval:

- Issue owns human Task description, requested work, assigned human, and visible lifecycle history.
- Coordination aggregate owns edit/save/submit permission, active Session, generation, lock set, recovery state, and exact SHA relations.
- Duplicated display fields are projections with source metadata.
- Any disagreement affecting permission or adoption becomes `RecoveryRequired`; neither side is silently copied over the other.

GitHub Issue update has no expected revision. ProjectSync Issue mutations therefore pass through the serialized trusted service and use append-only comments or narrowly scoped fields where practical.

## Identity model

| Identity | Proposed rule |
|---|---|
| Task ID | Stable server-issued identifier linked to one Issue |
| Operation ID | UUIDv7/ULID-class identifier created before side effects and reused across retries |
| Device ID | Non-secret installation identity stored by the Windows installation; exact scope requires approval |
| Session ID | Cryptographically random ID for each successful activation |
| generation | Starts at 1 and increments whenever an active Session is replaced, transferred, revoked, or recovered |
| Submission ID | Stable ID for one Submitted SHA and its validation generation |
| Candidate ID | Stable ID for Submitted SHA + Base Main SHA + resolution + Candidate generation |
| BuildAttempt ID | Stable ID shared by local journal, CAS record and Deployment payload |

The same GitHub account does not imply the same Device or Session. LFS `ours` is insufficient permission evidence.

Immediate remote revocation cannot stop a physically offline old PC from modifying local files. The enforceable contract is:

- a previously valid offline Session may perform local preservation only;
- after reconnection, stale generation blocks push, submit, handoff and automatic adoption;
- local changes remain preserved for administrator review.

This boundary requires explicit acceptance.

## Git operation contract

ProjectSync uses Git CLI and Git LFS CLI. It does not use `git pull` as a compound decision, does not rebase, and never runs prohibited destructive commands.

Allowed operation families:

- inspect repository identity, config, status and refs;
- `fetch` explicit remotes/refs without applying them to a working branch;
- create a Task branch from an exact observed `origin/main` SHA;
- stage a Task snapshot and commit it with Task/operation metadata;
- push an explicit non-force refspec;
- verify the remote ref/commit after an unknown response;
- list/lock/verify/unlock LFS locks and verify LFS object transfer;
- create isolated administrator candidate and build worktrees/clones;
- create a normal Revert commit for accepted-main recovery;
- delete a branch only after the full safe-delete predicate passes.

Prohibited operations:

- `reset --hard`;
- automatic stash or stash-based switching;
- force push or ref overwrite;
- automatic discard or checkout of user changes;
- automatic one-side conflict resolution;
- automatic main integration into an active member Task;
- deletion of a branch with unpublished or unadopted data.

All machine parsing uses stable porcelain/JSON output, explicit encodings, explicit repository paths, bounded timeouts, and credential-redacted logs.

### New Task start

1. Acquire a local per-repository ProjectSync process lock.
2. Read local unpublished-data inventory and reject a new Task if any conflicting data or active Task exists.
3. Confirm Unity state is safe for repository switching; never switch while dirty.
4. Fetch and observe remote `main` as `StartMainSHA`.
5. Record the operation locally before any remote mutation.
6. Create or find the Task Issue using the operation ID.
7. Create the Task branch from exact `StartMainSHA` and verify the remote ref.
8. Acquire required LFS locks in a deterministic path order and read them back.
9. Create the active coordination record by CAS with Device, Session and generation.
10. Reread Issue, branch, locks and state. Only then expose `Working`.

The unavoidable concurrency boundary must be defined as: `StartMainSHA` is the latest remote main observed by the serialized start command immediately before branch creation. Any later main change is a post-start update and is notification-only.

### Resume

1. Reconcile local branch, remote Task ref, unpublished data, Issue, locks and coordination state.
2. If another Device/Session is active, reject resume.
3. If the same device replaces an old Session, increment generation and issue a new Session ID.
4. CAS the new Session, reread it, then enable work.
5. Never create another Task branch.

### Save

1. Create/reuse the save operation ID and record the requested phase.
2. Validate Task/Session/lock state. If offline, allow only the specification's local-preservation path from the last validated Session.
3. Request Unity save through the bridge and wait for the matching acknowledgement.
4. Inspect the post-save working tree. Never infer success from timeout.
5. If this operation already created a commit, reuse it. Otherwise create one snapshot commit.
6. Push the explicit Task ref without force.
7. Verify the exact commit is reachable from the remote Task ref and required LFS transfer completed.
8. Record `RemoteReachable`. If remote confirmation is unavailable, expose `PC saved, GitHub confirmation pending` and resume from that phase only.

### Submit and resubmit

1. Require full save completion and current Task/Session/lock consistency.
2. Fix Submitted SHA and a new Submission ID in CAS state.
3. Create an App-owned candidate ref initially pointing to that exact Submitted SHA.
4. Create or recover the submission PR using the operation ID; the member Task branch is not the mutable PR authority.
5. Block normal edit/save commands while submitted.
6. On changes requested, resume the same Task, create a new Submitted SHA and new Candidate generation, and invalidate all old validation.

The exact PR reuse policy across intentional resubmissions is a local design choice only if it preserves immutable submission history and never treats an accidental retry as a new PR.

### Candidate and main integration

Candidate identity contains:

- Submitted SHA;
- Base Main SHA;
- Candidate SHA;
- conflict-resolution digest or recorded resolution commit identity;
- Candidate generation.

The trusted integration path uses an isolated worktree to combine the Submitted SHA with the exact Base Main SHA. Conflicts always stop for administrator resolution. The resulting Candidate commit/ref is App-controlled and is the PR head validated by Unity and human review.

Merge requires all of the following at the final reread:

- PR head equals Candidate SHA;
- current `main` equals Base Main SHA;
- required Candidate integrity and Unity validation Checks are `success` on the exact Candidate SHA;
- the expected trusted App produced the Checks;
- PR content and recorded conflict resolution match the Candidate record;
- no unadopted change is included;
- the Merge call supplies expected head SHA and uses squash.

If head, base or resolution changes, increment Candidate generation and rerun validation. Merge queue is out of scope for v1.1; integration is serialized.

## GitHub API contract

| Area | Operations | Consistency and recovery requirement |
|---|---|---|
| Issues | Create, read, update narrowly, append audit comments | Search by Task/operation ID before create; serialized writer; mismatch is recovery |
| Contents/state | Read and update JSON with current blob SHA | `409` is stale write; reread and re-evaluate, never blind retry |
| Refs/commits | Read exact refs and SHAs; create Task/Candidate/build refs | Explicit SHA, no force update; read after timeout |
| Pull Requests | Create/read; verify head/base; close superseded PR | Stable Submission/operation metadata prevents accidental duplicates |
| Checks | Create/update Check Run on exact Candidate SHA | Trusted App only; `NotRun`, `neutral`, or `skipped` never represents validated success |
| Merge | Expected head SHA and `squash` | Reread all merge predicates; response loss requires merged-state query |
| Deployments | Create at exact SHA with `auto_merge=false`; create status | BuildAttempt ID in payload; read before retry; record creation is not upload success |
| Rulesets | Provision and audit outside normal runtime App | Runtime App receives no Administration permission |

### GitHub authentication and trust

- End-user Git/LFS uses each person's GitHub identity through Git Credential Manager or an approved equivalent.
- The Desktop authenticates the person to the trusted service; no shared administrator token is distributed.
- The trusted service stores the GitHub App private key in a server-side secret store and uses short-lived installation tokens.
- The runtime App has only the repository permissions required by state, Issue, PR, Check, merge and Deployment operations.
- Administration permission for changing Rulesets is excluded from the runtime App.
- Tokens, authorization headers and signed URLs are never stored in logs or evidence.

## GitHub quality gate

Use two independent active Rulesets so update authority does not imply quality bypass.

### Authority Ruleset

- Target `main`.
- Require Pull Request.
- Restrict direct update/delete authority to the selected administrator/integration identity.
- Block force pushes and deletion.
- This ruleset may need a narrowly scoped bypass actor to express update authority.

### Quality Ruleset

- Target `main`.
- Strict required status checks and branch-current requirement.
- Require Candidate integrity and Unity validation checks.
- Require expected Check source App.
- Dismiss stale approval or otherwise invalidate review after new commits.
- No normal administrator or integration App bypass actor.

Repository admins can edit repository-level Rulesets. If the administrator must also be unable to weaken the quality ruleset, use an organization-owned Ruleset with a separately controlled owner. This depends on GitHub owner type and plan.

## Unity Editor integration

The bridge is an Editor-only component and must not enter the VRChat world runtime or player build.

Request fields include project identity, Unity version, Editor-session nonce, operation ID, Task ID, Session ID and command schema version.

Save execution:

1. Validate the request against the active project and Session.
2. Reject an unsafe Editor state rather than queueing a later surprise mutation.
3. On the Unity main thread, save open Scenes and Assets.
4. Report Scene save result, exceptions, dirty state before/after, known affected paths and completion time.
5. Persist the acknowledgement by operation ID long enough for the Desktop to query after a timeout.
6. The Desktop rechecks Git status after acknowledgement.

Default safe policy, pending approval:

- compiling/importing/updating: reject with retryable status;
- entering/inside Play Mode: reject;
- Prefab Stage with unsaved changes: reject;
- untitled Scene requiring a path dialog: reject and instruct the user;
- bridge disconnected or response ambiguous: do not commit.

Validation includes Missing Script, Missing Material, principal missing references, Scene load, `.meta` anomalies, package/VPM consistency, LFS object availability, and administrator-only paths. Visual quality, placement intent, Udon experience and VRChat runtime behavior remain human checks.

## Session, lock, handoff and abort

### Permission predicate

Work, full save and submission are allowed only when all are true:

1. Task lifecycle permits the command.
2. Required LFS path locks exist with exact lock IDs.
3. Coordination aggregate Task, user, Device ID, Session ID and generation equal the current client.
4. No relevant recovery operation is unresolved.
5. Local branch and repository identity match the Task.

LFS owner equality alone never grants permission.

### Handoff

1. Require the old owner to complete Unity save, local commit, push and remote verification.
2. CAS state to `HandingOff`; both old and prospective new Sessions are blocked.
3. Revoke the old Session and increment generation.
4. Transfer Task assignment and, when users differ, perform explicit old unlock/new lock steps.
5. Create the new Session only after all remote facts agree.
6. Any partial failure remains `RecoveryRequired`; never enable both Sessions.

### Formal Scene Task abort

1. Inventory unsaved, uncommitted, unpushed and unadopted data.
2. Require an administrator-approved preservation disposition and execute it without deletion.
3. Revoke the old Session and increment generation by CAS.
4. Unlock each recorded LFS lock without force in normal flow.
5. Read the remote LFS state and confirm every target is unlocked.
6. Mark `Aborted` only after verification.

If unlock fails or its outcome is unknown, retain `Aborting`/`RecoveryRequired`, preserve all data, and block another Scene Task.

## Recovery design

Every side-effecting command is a durable saga with one stable operation ID. The local journal is written before the first side effect and after every confirmed phase.

General algorithm:

1. Read local journal and local Git facts.
2. Read Issue, refs, PR, Check, state JSON, LFS locks, and Deployment facts relevant to the operation.
3. Classify each phase as absent, confirmed, conflicting, or outcome unknown.
4. If confirmed, retain it.
5. If absent and safe, execute only that phase.
6. If conflicting, enter `RecoveryRequired` and require a typed recovery decision.
7. Read back after the operation; do not equate HTTP/process completion with business success.

Examples:

| Partial result | Recovery action |
|---|---|
| Unity saved, commit absent | Create the one planned commit from preserved working tree |
| Commit exists, push absent | Push the existing commit only |
| Push response lost | Query the remote ref before any push retry |
| PR created, response lost | Find by Task/Submission/operation identity |
| Merge response lost | Query PR merged state and resulting main SHA |
| Issue succeeded, lock/state failed | Stop in `RecoveryRequired`; complete or compensate only after read-back |
| Unlock response lost | Query by lock ID/path; do not declare abort complete |
| Upload success, Deployment record failed | Never re-upload; retry the same BuildAttempt projection only |

## Build and publish design

Build uses a separate clone and never switches a member's active production working tree.

BuildAttempt record fields:

- BuildAttempt ID;
- BuildTargetSHA;
- World ID;
- Platform;
- operator identity;
- startedAt and endedAt;
- UploadResult: Success, Failed, or Unknown;
- upload evidence and evidence source;
- project-difference status and detection time;
- Recovery Branch and commit when present;
- Deployment projection state and Deployment ID.

Flow:

1. Verify a clean dedicated Build clone with LFS and packages restored.
2. Read `main` once and persist it as immutable BuildTargetSHA.
3. Create local `build/<attempt-id>` from that SHA before Build.
4. Build and upload each platform as a separate result.
5. If project differences occur, preserve them on the Recovery Branch, mark the attempt `NeedsReview`, and never push to main.
6. If a Scene changed, compare with current Scene lock state and prohibit automatic adoption.
7. Only an observed Upload success can project a successful Deployment. Use exact SHA and `auto_merge=false`.
8. If Deployment recording fails, reconcile by BuildAttempt ID and retry only the record.

If the VRChat SDK cannot provide authoritative success evidence after a lost response, record `Unknown` and require human review. GitHub's deployment status enum is a projection; the CAS BuildAttempt record retains the three-valued specification result.

## Test architecture

The complete classification is in `ACCEPTANCE_TEST_MATRIX.md`.

Required suites:

- pure domain transition and policy tests;
- durable-journal restart tests;
- real Git CLI and Git LFS CLI integration tests;
- disposable GitHub repository and Ruleset integration tests;
- Unity 2022.3 EditMode save/validation tests;
- Windows end-to-end UI/IPC/process tests;
- fault-injection tests at every external phase;
- security and credential-redaction tests;
- three manual VRChat qualification tests for actual upload/publication evidence.

Risk-first PoC order:

1. LFS simultaneous acquisition, including the non-LFS `.unity` path.
2. Same-account two-device fencing.
3. Issue success followed by lock/state failure.
4. Formal abort and remote unlock confirmation.
5. Administrator/App non-bypass quality gate.
6. BuildTargetSHA fixed while main advances.
7. Upload success followed by Deployment-record failure.

## Fixed by High

- External Windows orchestrator plus Unity Editor bridge plus trusted GitHub App service.
- App private key never enters the desktop client.
- One CAS coordination aggregate for live Task/Lock/Session fencing.
- Stable operation IDs, durable saga and fail-closed reconciliation.
- Dedicated App-owned Candidate identity and exact Candidate SHA validation.
- Separate authority and quality Rulesets.
- BuildAttempt authority distinct from Deployment projection.
- Event/command-driven Editor behavior; no world runtime, Udon, GPU, VR stereo, or mirror impact.
- All canonical prohibited Git/data-loss operations remain prohibited.

These decisions are fixed only after the user approves this proposal and the required platform PoCs do not invalidate them.

## Medium May Decide

- WPF internal presentation pattern and visual design.
- SQLite library and internal table names.
- Named-pipe framing and JSON serialization library.
- Exact class/module names and folder layout.
- Branch/ref display names and safe normalization.
- Bounded retry/backoff values that preserve the stated failure contract.
- Test framework, installer technology, structured log format and non-secret telemetry.
- PR reuse mechanics across intentional resubmission, provided submission identity and validation invalidation remain exact.

## Do Not Change

- Do not apply new `main` to an active member Task.
- Do not replace Submitted SHA with a moving branch head.
- Do not permit work from LFS owner equality alone.
- Do not treat `neutral`, `skipped`, missing Check, or Deployment creation as business success.
- Do not place trusted App secrets in the desktop.
- Do not delete unpublished/unadopted data automatically.
- Do not let normal administrators or the integration App bypass the quality gate.
- Do not change BuildTargetSHA after attempt start.
- Do not use Unity 6-only plugin instructions or add `com.unity.pipeline` to this Unity 2022.3 project.

## SPEC BLOCKER

Implementation must stop and return for decision if any of the following is unresolved or changes:

1. Repository URL, initial canonical `main`, migration freeze, or backup policy is missing.
2. GitHub owner type, visibility, plan, or Ruleset administrator is unknown.
3. The trusted GitHub App host/operator/secret boundary is not approved.
4. The Issue versus coordination-state field ownership proposal is not approved.
5. The offline stale-device contract is not accepted or the threat model requires physical prevention of local edits.
6. Device ID scope and generation increment policy are not approved.
7. Main Scene path, additional lock paths, or multi-lock policy is unknown.
8. Candidate/ref model or exact validation Check producer is not approved.
9. Required validation set, administrator-only paths, `.meta` anomaly policy, or Missing Material outcome is unknown.
10. Target platforms and authoritative VRChat upload-success evidence are unknown.
11. Unity policy for Play Mode, compile/import, Prefab Stage, or untitled Scenes is not approved.
12. A PoC disproves GitHub non-bypass rules, LFS exclusivity/verification, CAS fencing, or upload reconciliation.
13. Repository/runtime identity changes between approval and implementation.

No production implementation begins until the user explicitly clears these blockers or narrows the initial milestone so the unresolved boundary is outside it.
