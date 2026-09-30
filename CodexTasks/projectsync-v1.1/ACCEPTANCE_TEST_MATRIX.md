# ProjectSync v1.1 Acceptance Test Matrix

## Classification rule

- `自動可能`: Deterministic pass/fail can be produced by unit, process, Unity Editor, isolated Git/Git LFS, or disposable GitHub integration tests. A real external service may still be required.
- `手動必要`: The specification requires proof of an actual VRChat upload or currently published runtime state that the local/GitHub test harness cannot authoritatively infer.
- Automated tests that use GitHub or Git LFS run only against a disposable test repository. Production upload, merge, unlock, and deployment actions are not test targets.

## Summary

- Automated: 57 tests.
- Manual required: 3 tests (`AT-52`, `AT-54`, `AT-59`).
- `AT-30`, `AT-45`, and `AT-56` are automatable with independent VMs/runners, but should also receive a one-time two-PC qualification because device identity and credential storage are part of the product boundary.
- `AT-52`, `AT-54`, and `AT-59` still require automated tests for all state transitions and GitHub records surrounding the manual VRChat observation.

## Tests 1 through 60

| ID | Specification scenario | Classification | Required harness or manual evidence |
|---:|---|---|---|
| AT-01 | 新規Task開始 | 自動可能 | Disposable GitHub repository; assert one Issue, one branch, captured latest main SHA |
| AT-02 | 既存Task再開 | 自動可能 | Existing remote Task fixture; assert no new branch |
| AT-03 | 通常保存 | 自動可能 | Live Unity Editor bridge plus Git remote; verify all four save phases |
| AT-04 | 保存後に追加編集 | 自動可能 | Working-tree mutation after a verified snapshot |
| AT-05 | Unity未保存Scene | 自動可能 | Unity Editor fixture with a dirty saved Scene |
| AT-06 | Git記録失敗 | 自動可能 | Git command failure injection after Unity acknowledgement |
| AT-07 | Push失敗 | 自動可能 | Rejecting remote or network fault; preserve commit |
| AT-08 | Push再実行 | 自動可能 | Durable operation journal; assert same commit is pushed |
| AT-09 | 作業中にmain更新 | 自動可能 | Concurrent remote main update; assert notification only |
| AT-10 | ローカル変更中の更新 | 自動可能 | Dirty tracked and untracked fixtures; reject branch/main operation |
| AT-11 | Git CleanだがTask作業中 | 自動可能 | Active Task fixture; assert no automatic main integration |
| AT-12 | 通常提出 | 自動可能 | Save complete; assert Submitted SHA and waiting state |
| AT-13 | 提出後の通常編集 | 自動可能 | Submitted session; UI/domain command must reject edit/save |
| AT-14 | 提出後Branchに追加Commit | 自動可能 | External branch advance; assert Submitted SHA remains fixed |
| AT-15 | 差し戻しから再提出 | 自動可能 | New commit and new validation identity; old result rejected |
| AT-16 | 一般メンバーがmainへPush/Merge | 自動可能 | Real protected disposable GitHub repository; expected rejection |
| AT-17 | 一般メンバーがmainへForce Push | 自動可能 | Real protected disposable GitHub repository; expected rejection |
| AT-18 | 管理者による正常Merge | 自動可能 | GitHub integration; required checks, current base, squash result |
| AT-19 | 検証後PR HEAD変更 | 自動可能 | Advance PR head after validation; merge must be blocked |
| AT-20 | 検証後main更新 | 自動可能 | Advance base after validation; strict gate must block |
| AT-21 | Conflict発生 | 自動可能 | Deterministic conflict repository; route to administrator |
| AT-22 | Conflict解決後 | 自動可能 | New Candidate identity and fresh validation |
| AT-23 | AがScene編集権取得 | 自動可能 | Real LFS lock endpoint and coordination-state assertion |
| AT-24 | A/B同時取得要求 | 自動可能 | Concurrent clients against real LFS endpoint; exactly one succeeds |
| AT-25 | AからBへ担当交代 | 自動可能 | Two users/clients; remote-save guard and generation transition |
| AT-26 | 交代後に旧A端末から更新 | 自動可能 | Stale session command; reject adoption and enter review path |
| AT-27 | Scene Task完了または正式中止 | 自動可能 | Separate completion and abort cases; remote unlock verification |
| AT-28 | Scene Task提出済み未Merge | 自動可能 | Retained lock; competing Task start must fail |
| AT-29 | PC1からPC2移行 | 自動可能 | Independent device stores/runners; old session invalidated |
| AT-30 | 同一Taskを2PC同時開始 | 自動可能 | Concurrent VMs/runners with one GitHub user; one valid session only |
| AT-31 | 保存中通信断 | 自動可能 | Fault injection at every remote boundary; local data preserved |
| AT-32 | Push成功後の通信断 | 自動可能 | Drop response after remote update; reconcile without new commit |
| AT-33 | 提出中に異常終了 | 自動可能 | Process kill after PR creation; reuse PR by operation identity |
| AT-34 | Merge直後に通信断 | 自動可能 | Drop response after merge; query merge status before retry |
| AT-35 | ボタン二重押下 | 自動可能 | Concurrent duplicate commands; one durable operation identity |
| AT-36 | ツール強制終了 | 自動可能 | Process kill at each phase; restart and reconcile journal |
| AT-37 | LFS取得失敗 | 自動可能 | Missing object/auth/network fixture; block work start |
| AT-38 | LFS送信失敗 | 自動可能 | LFS pre-push failure; preserve local data and block submission |
| AT-39 | `.meta`欠落 | 自動可能 | Unity/project validation fixture |
| AT-40 | GUID異常候補 | 自動可能 | Requires approved anomaly rules before fixture is final |
| AT-41 | Missing Material | 自動可能 | Requires approved outcome: hard block or administrator review |
| AT-42 | Missing Script | 自動可能 | Unity Editor validation fixture; hard block |
| AT-43 | 管理者専用領域変更 | 自動可能 | Requires approved protected-path policy |
| AT-44 | Fresh Clone | 自動可能 | Clean CI workspace; restore VPM, Packages, and LFS; open Unity |
| AT-45 | 別PCでPackage復元 | 自動可能 | Independent runner/VM; one-time physical-PC smoke recommended |
| AT-46 | A200採用・A201未採用 | 自動可能 | Branch-retention domain and GitHub integration test |
| AT-47 | Task完了・未採用変更なし | 自動可能 | Local and remote safe-delete preconditions; test repository only |
| AT-48 | Task途中中止 | 自動可能 | Preservation, invalidation, unlock, verification, and failure cases |
| AT-49 | Scene Task保留 | 自動可能 | Assert lock and session policy remain unchanged |
| AT-50 | main統合後に重大不具合 | 自動可能 | Test repository; revert commit without history rewrite |
| AT-51 | AがBuild開始 | 自動可能 | Separate clone, clean-state gate, fixed BuildTargetSHA record |
| AT-52 | Build後変更なし・Upload成功 | 手動必要 | Actual VRChat upload success plus matching fixed-SHA Deployment record |
| AT-53 | Build後Project変更あり | 自動可能 | Mutation fixture; Recovery Branch preservation and review state |
| AT-54 | 公開版追跡 | 手動必要 | Compare ProjectSync/GitHub record with actual VRChat platform publication |
| AT-55 | Issue成功・Lock/協調状態更新失敗 | 自動可能 | Cross-system phase fault injection; RecoveryRequired and no dual session |
| AT-56 | 同一アカウント2台で同Task再開 | 自動可能 | Same-user independent VMs/runners; one-time two-PC qualification |
| AT-57 | 引継途中で一部更新失敗 | 自動可能 | Fault at every handoff phase; neither session may continue while inconsistent |
| AT-58 | Build中にmain更新 | 自動可能 | Concurrent main advance; BuildTargetSHA and records remain unchanged |
| AT-59 | Upload成功・Deployment記録失敗 | 手動必要 | Actual upload, injected GitHub-record failure, no re-upload, same attempt recovery |
| AT-60 | 管理者/Appが検査未達でMerge | 自動可能 | Real GitHub ruleset/App test for missing/failed check, stale base, changed PR |

## First PoC execution order

1. `AT-24`: Git LFS simultaneous acquisition and non-LFS `.unity` path behavior.
2. `AT-30` and `AT-56`: same-account device/session/generation fencing.
3. `AT-55`: Issue success followed by lock/state failure and reconciliation.
4. `AT-48` and `AT-27`: formal abort, unlock, and remote unlock confirmation.
5. `AT-60`: non-bypass quality rules for administrators and integration App.
6. `AT-58`: fixed BuildTargetSHA under concurrent main updates.
7. `AT-59`: successful upload followed by Deployment-record failure.

## Test suite boundaries

| Suite | Evidence produced |
|---|---|
| Domain unit tests | State axes, guards, operation identity, invalid transitions, retry decisions |
| Persistence tests | Journal write, process restart, reload, and semantic equivalence |
| Git process integration | Exact commands, porcelain parsing, branch/ref/commit facts, prohibited-command assertion |
| Git LFS integration | Lock exclusivity, same-user verification, unlock, object transfer, network failure |
| GitHub integration | CAS conflict, Issue/PR/Check/Merge/Deployment reconciliation, ruleset rejection |
| Unity EditMode tests | Save bridge, dirty Scenes/Assets, validation fixtures, compile/import/play guards |
| End-to-end Windows tests | Actual WPF command through orchestrator, Unity bridge, Git, and disposable remote |
| Failure/recovery tests | Timeout, lost response, duplicate command, process kill, stale revision, partial success |
| Security tests | Token redaction, protected path, role checks, expected Check source, stale session rejection |
| Manual VRChat qualification | Actual upload result and published platform identity for `AT-52`, `AT-54`, `AT-59` |
