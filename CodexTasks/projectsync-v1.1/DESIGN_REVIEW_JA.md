# ProjectSync v1.1 実装構成案・確認事項

> 実装前に作成した設計資料です。現在の実装・検証状態とPrivate＋Freeの例外は `IMPLEMENTATION_STATUS.md` と `PRIVATE_FREE_PILOT_EXCEPTION.md` を参照してください。以下の「Git未初期化」等は設計時点の記録です。

## 現在の判定

`REDIRECT TO RISK FIRST POC — PRODUCTION UNCHANGED`

本書は、正本「VRChat_ProjectSync_全体運用仕様書_v1.1.docx」を実装可能な境界へ分解した、承認前の設計案です。ProjectSync本体コード、Git初期化、GitHub設定、LFS Lock、Build、Upload、Deploymentにはまだ変更を加えていません。

仕様の中核は成立可能です。ただし、認証・排他・GitHub Ruleset・VRChat Upload結果の取得には、実装前に決定またはPoCで実証すべき項目があります。未決部分を独自判断で別方式へ置き換えてはいません。

## 確認したプロジェクト条件

- 対象は Unity 2022.3.22f1 / VRChat Worlds SDK 3.10.5 / Built-in Render Pipeline / Linear Color Space のWorldプロジェクトです。
- Visible Meta Files と Force Text は既に有効です。
- 現在の `projectsync` はGitリポジトリではなく、remote、Git LFS、`.gitattributes`、GitHub設定、ProjectSync実装、テストはまだありません。
- Unity Editorは対象プロジェクトを開いています。Editorメモリ上の未保存状態は外部プロセスから断定できません。
- Unity 6以降を前提とするUnity CLI / `com.unity.pipeline` は、このUnity 2022.3 VRChatプロジェクトには導入しません。

## 実装構成案

### 1. Windows Desktop Orchestrator

利用者が操作するWindowsアプリです。候補は .NET 10 LTS + WPF とします。

担当範囲:

- 「新しい作業」「再開」「保存」「提出」「保留」「正式中止」のUI
- Task/Session/Lock/Buildの状態機械
- ローカル永続Journalと冪等なOperation ID
- Git / Git LFS CLIの安全な実行
- Working Tree、未追跡ファイル、未送信Commitの保全判定
- Build専用CloneとBuildAttemptの管理
- Unity Editor Bridgeとのローカル通信
- Trusted GitHub App Serviceへの認証済み要求

### 2. Unity Editor Bridge

対象Unityプロジェクト内のEditor専用コンポーネントです。Unityメインスレッドでのみ実行し、外部アプリがUnityの未保存メモリを推測しないようにします。

担当範囲:

- 開いているSceneの保存
- Asset保存
- compile/import中、play mode、保存失敗の検出
- Missing Script、Missing Material、GUID候補などUnity依存Validation
- 保存要求と応答をOperation IDで対応付ける

ProjectSyncは、Unity Bridgeから明示的な成功応答を受け取る前にGit Snapshotへ進みません。

### 3. Trusted GitHub App Service

GitHub App秘密鍵を各制作PCへ配布せず、権限の強い操作を信頼境界内で実行するサービスです。

担当範囲:

- GitHub上の協調状態に対するcompare-and-swap更新
- Check Runの作成・更新
- Candidateの作成と検証ID管理
- PRとSubmitted Commitの固定関係の確認
- Ruleset条件を満たしたSquash Merge
- BuildAttemptとDeployment記録の連携

GitHub App秘密鍵をDesktopアプリやUnityプロジェクトへ同梱しません。通常メンバーのGit操作はGit Credential Manager等のユーザー認証を使用します。

### 4. GitHub上の正本

- `main`: 次の作業を開始してよい正式基準
- Task branch: 1 Taskにつき1本の短命Branch
- GitHub Issue: 人が読むTask記録、状態遷移履歴、復旧案内
- `projectsync-state`: 機械が権限判定に使うCAS更新可能な協調状態
- PR: Submitted Commitを審査・統合する器
- Check Run: Candidateの正確なIDに対する検証結果
- BuildAttempt record: Upload結果を含む公開作業の正本
- GitHub Deployment: BuildAttemptを投影する補助記録。Upload成功の正本にはしない

Issue更新API単独には期待revisionによる更新条件がないため、Issue本文だけをSession/Lock権限の機械正本にはしません。Issueと`projectsync-state`の不一致は自動コピーで直さず、`RecoveryRequired`にします。

## 状態モデル

Task lifecycleの主状態:

`Draft -> Starting -> Active -> Saving -> SavedRemote -> Submitted -> Validating -> MergeReady -> Merged -> Completed`

例外・分岐状態:

- `OnHold`: Lockを解放しない保留
- `AbortPreparing`: 未保存・未送信・未採用データを確認・保全中
- `UnlockPending`: Session失効後、Lockの解放またはremote確認待ち
- `Aborted`: Lockのremote解放確認後のみ到達
- `RecoveryRequired`: 複数システムの事実が一致せず、開始・保存・提出・引継ぎを止める

Task状態とは別に、次の直交状態を持ちます。

- Local data: clean / dirty / untracked / unpushed / unknown
- Session: none / active / stale / revoked / uncertain
- LFS Lock: none / ours-exact / theirs / owner-only-match / uncertain
- Remote reachability: confirmed / absent / unknown
- Validation: none / running / passed / failed / invalidated
- BuildAttempt: preparing / building / upload success / failed / unknown / record pending / needs review

Main Sceneの編集・保存・提出許可は、少なくともTask ID、Lock ID、GitHub User、Device ID、Session ID、generation、対象pathがすべて現在の協調状態と一致する場合だけ与えます。同じGitHub userのLFS lockが「ours」と見えても、それだけでは許可しません。

## 主要フロー

### 新しい作業

1. Working Treeと未追跡ファイルを検査し、local dataがあれば停止する。
2. remoteの最新`main` SHAを取得する。
3. そのSHAからTask branchを作る。
4. Scene TaskではLFS Lockと協調状態を確立する。
5. Task ID / Device ID / Session ID / generationを照合する。
6. 全remote事実が一致した後だけ`Active`にする。

進行中Taskへ新しいmainを自動適用しません。通知だけ行います。

### 作業を保存

1. Unity Scene / Assetをディスク保存する。
2. 同一Task branchへローカルSnapshot Commitを作る。
3. Task branchへPushする。
4. remoteがそのCommitへ到達可能であることを確認する。

各段階の成功をJournalへ確定してから次へ進みます。Commit成功・Push失敗ならCommitを残し、Pushから再開します。応答喪失は`OutcomeUnknown`としてremoteを読んで照合し、成功・失敗を推測して最初からやり直しません。

### 提出・再提出

- 提出対象はBranch先端ではなく`Submitted Commit SHA`で固定する。
- 提出後にBranchへ追加Commitがあっても、既存提出へ採用しない。
- 再提出は新しいSubmitted SHAと新しいValidation identityを発行する。
- 旧Validationは流用しない。

### Candidateとmain統合

Candidate identityは少なくとも次を含みます。

- Submitted SHA
- Base Main SHA
- Candidate SHA
- Conflict Resolution digest
- generation

PR HEAD、Base Main、Conflict Resolutionのどれかが変化すれば旧Check結果を無効にします。統合直前にもPR HEAD、Base Main、required checks、Candidate identityを再取得し、Squash Mergeします。

GitHub Rulesetは、権限を制御するAuthority Rulesetと、検査を強制するQuality Rulesetに分離する案です。Quality Rulesetには通常運用のbypass actorを置きません。管理者やAppに書込権限があっても、未検査・失敗・古いBase・変更後未再検査を迂回できないことを実リポジトリPoCで確認します。

### 正式中止

1. 未保存・未送信・未採用データを列挙する。
2. 必要データを削除せず保全する。
3. 旧Sessionを失効させる。
4. LFS Lockを解放する。
5. remoteで解放を確認する。
6. 確認後のみ`Aborted`とする。

Unlock失敗・応答不明なら`UnlockPending`または`RecoveryRequired`とし、次のScene Taskを開始させません。保留ではLockを解放しません。

### Build / Publish

- 制作ProjectとBuild専用Cloneを分離する。
- Build開始時の`main HEAD`を`BuildTargetSHA`として固定し、その後のmain更新を取り込まない。
- BuildAttempt ID、BuildTargetSHA、World ID、Platform、担当者、開始/終了時刻、Upload結果を保持する。
- Upload結果は`Success / Failed / Unknown`を区別する。
- GitHub Deploymentを作る場合はrefを固定SHA、`auto_merge=false`とする。
- Deployment作成成功はUpload成功の証拠にしない。
- Upload成功後にDeployment記録だけ失敗した場合、同じBuildAttemptを復旧し、再Uploadしない。
- Build後のProject変更はBuild Recovery Branchへ保全し、Scene変更なら現行Lockとの関係を確認する。

## エラー復旧の共通方式

各外部副作用をDurable Sagaとして扱います。

1. 副作用の前にOperation ID、期待状態、対象SHA/IDをJournalへ書く。
2. 外部操作を一度実行する。
3. 応答の有無にかかわらず、確認可能なremote事実を読む。
4. remote事実が期待結果と一致すれば段階成功とする。
5. 不一致が安全に再試行可能と証明できる場合だけ、同じOperation IDで再試行する。
6. 不明または複数正本が不一致なら`RecoveryRequired`とし、人に観測事実と復旧候補を提示する。

`reset --hard`、自動stash、force push、自動破棄、片側優先の自動Conflict解決はコマンド層でも禁止します。

## 受け入れ試験1〜60の分類

- 自動テスト可能: 57件
- 手動テスト必要: 3件
  - AT-52: 実VRChat Upload成功と固定SHAのDeployment記録
  - AT-54: 記録と実際のVRChat公開状態の照合
  - AT-59: 実Upload成功後にDeployment記録を故障注入し、再Uploadせず復旧

AT-30、AT-45、AT-56は独立VM/runnerで自動化できますが、Device IDやCredential Managerの実機差を確認するため、リリース前にWindows物理2台で一度Qualificationすることを推奨します。

## 最初に行うPoC

本番リポジトリではなく、破棄可能なGitHubテストリポジトリと独立クライアントで次の順に行います。

1. LFS Lock同時取得と`.unity` pathの実挙動（AT-24）
2. 同一GitHubアカウント2台で一方だけSession有効（AT-30 / AT-56）
3. Issue成功・Lock/協調状態更新失敗と再起動復旧（AT-55）
4. Task正式中止、Unlock失敗、remote解放確認（AT-48 / AT-27）
5. 管理者/Appでも検査未達Merge拒否（AT-60）
6. BuildTargetSHA固定（AT-58）
7. Upload成功・Deployment記録失敗（AT-59、実Uploadを伴うため後段）

## 実装前に確認が必要な点

次の項目は結果を変え得るため、承認または情報提供が必要です。

1. **GitHubリポジトリ**: 新規作成か既存か、owner、repository名、default branchを`main`へする時期。
2. **GitHub契約とRuleset管理境界**: organizationか個人repositoryか、利用プラン、organization rulesetを使用できるか。repository adminがRuleset自体を変更できる点を脅威範囲に含めるか。
3. **Trusted GitHub App Serviceの配置**: organization管理のサーバー、GitHub Actions、クラウドサービス等のどこへ置けるか。Desktopへ秘密鍵を配る案は採用不可。
4. **協調状態の粒度**: Task/Scene Lock/Sessionを一つのCAS aggregateとして原子的に更新する案を承認するか。BuildAttemptは別CAS recordとする。
5. **Issueと`projectsync-state`の責任分担**: Issueは人向け記録、`projectsync-state`は権限正本とし、不一致時は自動修復せずRecoveryRequiredとする案を承認するか。
6. **Main Scene path**: Lock対象となる`.unity`ファイル、複数Sceneや関連Prefabを同じ排他集合へ含めるか。
7. **LFSポリシー**: `.unity`をLFS対象とする正本の`.gitattributes`、`locksverify`、Lock対象pathの一覧。実サーバーでPoC後に確定する。
8. **Session失効の現実的境界**: オフラインの旧PCによるローカル編集そのものは即時停止できない。再接続後のpush/submit/adoptをgeneration不一致で拒否し、ローカルデータは保全する、という保証範囲でよいか。
9. **Unity保存時の方針**: compile/import中、play mode中、無題Scene、読み取り専用Asset、保存ダイアログが必要な場合をすべて停止扱いにするか。
10. **Validation定義**: 必須Check一覧、Missing Materialの扱い、GUID異常規則、管理者専用path、Candidate検証の内容。
11. **管理者Conflict Resolution**: 解決用branch/refの命名、誰が解決できるか、解決内容をCandidate identityへどう署名・記録するか。
12. **Build対象**: Windows/Androidの対象範囲、World IDの管理、VRChat Upload成功を取得する正式手段、実アカウントを使う手動試験環境。
13. **Device ID保管**: Windows DPAPI/資格情報ストア等の利用、PC再セットアップ・複製・紛失時の失効手続き。
14. **ローカル保持期限**: 完了・中止Taskのworking tree、branch、未追跡保全物を削除可能とする条件と管理者承認。
15. **UI言語と運用ロール**: 一般メンバー、管理者、Build担当Aの権限割当、Gitを直接操作した場合のサポート範囲。

この確認が終わるまでは、Production実装、GitHub Ruleset変更、LFS対象変更、実Uploadには進みません。承認後も、まず上記PoCで技術的成立性を実証し、失敗した場合は仕様へ勝手に代替案を差し込まず、観測事実と選択肢を再提示します。
