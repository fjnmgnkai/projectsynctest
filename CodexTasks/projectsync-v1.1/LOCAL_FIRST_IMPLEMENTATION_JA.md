# ProjectSync ローカル主体・実装記録

状態: `IMPLEMENTED — VERIFICATION INCOMPLETE`（2026-10-03）
対象: `fjnmgnkai/projectsynctest` の試験用PUBLIC Repository
作業Branch: `task/projectsync-main-guard`

## ユーザーが選んだ運用

- 各メンバーは各自のGitHubアカウント、各自のPC/Cloneで同じRepositoryへTask BranchをPushする。
- 1 Task = 1短命Branch。新Taskは最新remote mainから始め、進行中Taskへmainを自動取り込みしない。
- 同じUnity Sceneを別Branchで並行編集してよい。管理者が先行Branchをmainへ入れ、後続BranchのConflictを人間が解決して統合する。したがってSceneはLFS Lockの排他対象ではなく、通常GitのYAMLとして扱う。
- 100 MiB超の共有素材は必要に応じてGit LFSで扱う。バックアップZIPはProjectSyncのCommitに含めない。
- 常時稼働サーバー、GitHub Actions、有料プランは使わない。Private + GitHub FreeではGitHub側の強制品質Gateは未達のまま、管理者が手動で統合を判断する。

これは添付仕様書v1.1のScene排他、共有Session状態、GitHub品質Gate、Build/Publish要件からの明示的な方針変更と段階的実装であり、仕様書の全要件を達成したという意味ではない。

## Change safety契約

| 項目 | 現行Task経路 |
|---|---|
| Gate / Mode / Risk | `PROCEED` / `FEATURE`と旧経路の`MIGRATE` / `RISK-HIGH`（Git remote・PRの外部変更） |
| Production path | WPFボタン → `LocalTaskWorkspace` → Git CLI / Git LFS / Unity Editor pipe / GitHub CLI |
| Authority | CommitとBranchの事実はGit、PRと提出SHA表示はGitHub、未完了保存の段階はClone内`UserSettings/ProjectSync/save-state.json` |
| Trust boundary | 選択したローカルUnity Cloneのみ操作。Git/GitHubの認証は各ユーザーの既存設定。App秘密鍵・共通Tokenは配布しない |
| Side effects | Unity Scene/Asset保存、Task Branchの作成・Commit・非force Push、PR作成/同じTaskの再提出更新。mainのCommit/Push/Merge、VRChat Upload、Deploymentは行わない |
| Concurrency | 同じCloneのProjectSync操作はローカル排他ファイルで直列化。Gitの非fast-forward拒否とRemote SHA再読込を使う。ProjectSync以外のGit操作は強制禁止できない |
| Recovery | 保存段階をディスクへ記録。Commit後のPush失敗・応答喪失時はRemote到達を読み直し、同一Commitだけ再送する。自動reset/stash/破棄なし |
| Deployment | ソースはこの作業Branch。実利用PCの実行ファイル・GitHub mainは別の状態として確認する。まだ本番配布確定ではない |

## 素材の規模検証

参考Project `7d1mtest` の読み取り結果: `Assets`約3.21 GiB、100 MiB超のファイル6個で計約1.39 GiB。最大Sceneは約30.8 MiB。ルートの`Assets.zip`は約2.13 GBで共有対象外。ProjectSyncの保存対象は`Assets`/`Packages`/`ProjectSettings`/`.gitattributes`/`.gitignore`に限定し、Root ZIPは既にステージされていてもCommitしない。Branch切替時は未記録ファイルを含むローカル変更があれば停止するので、管理者は対象Cloneの`.gitignore`でバックアップZIPを除外する。

既存Main Scene `Assets/Scenes/VRCDefaultWorldScene.unity` は、HEADではLFS pointer、作業ツリーではSHA-256 `876bf604...ba9`の29,320 byte Unity YAMLだった。`.gitattributes`を通常テキスト管理に変え、Git indexをrenormalizeした。Git indexは29,320 byte YAMLになったが、旧履歴のLFS objectは自動削除していない。

## 実施した検証

- .NET Core/Infrastructure/WPFビルド成功、警告0。
- 17/17 specification tests成功。一時的なbare Git remoteで、新Taskのmain SHA固定、TaskだけのCommit/Push、バックアップZIP非混入、Push失敗後の同一Commit再送、100 MiB超の非LFS素材とLFS SceneのCommit前拒否を確認。PR再提出の本文変換と不正形式拒否も検査。
- `gh auth status`で現在のPCの個人アカウント設定を確認し、`gh pr list`の実JSONフィールドを読み取り検証。開発PR #2の本文更新は実行したが、アプリ経由の新規PR作成・再提出は未検証。
- GitHub上の旧`ProjectSync Required Check`は`disabled_manually`を確認。ソースのActions workflowも削除。
- Unity Editor Bridgeの実保存、別PCからの同時利用、大容量LFS実Push、実PR提出/再提出、管理者Conflict解決、Build/Uploadは未検証。

## 未達・運用上の注意

- GitHub FreeのLFS無料枠はストレージ/ダウンロード各10 GiB。各PCの再Cloneや大型素材の更新で消費する。ProjectSyncは残量もGitHubの課金予算も完全には制御できない。追加課金禁止のため、所有者はGitHubのLFS予算を$0に設定する必要がある。
- PR本文のSubmitted SHAとPR HEADを統合直前に管理者が一致確認する必要がある。後続PushでPR HEADは動く。再提出時は新SHAをPR本文へ記録し、旧検証結果を流用しない。PR本文編集はGitHub CLI経由で原子的CASではないため、アプリは更新前後に読み直して不一致時に停止する。
- `7d1mtest`はGit Repositoryではないため、そのフォルダを直接選択してもTask操作はできない。GitHub Cloneへの初期導入と大容量素材のLFS追跡設定は別の管理者準備が必要。
- Private + GitHub Freeのmain強制保護不可、同一アカウント2台Session fencing、Scene Lock、仕様書の受け入れ試験全60件、Build/Publishは未達。現版を仕様書v1.1全適合の本番完成品とは呼ばない。
