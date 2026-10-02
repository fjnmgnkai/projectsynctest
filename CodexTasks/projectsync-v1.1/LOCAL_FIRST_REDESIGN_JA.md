# ProjectSync v1.1 ローカル主体・設計再審査（履歴）

> 2026-10-03のユーザー決定で、同一Sceneの並行編集、管理者による後続のConflict解決、大容量素材だけのLFS利用、バックアップZIPの除外を採用した。本書のScene LFS Lock・`projectsync-state`・Trusted Check案は現行の実装方針ではない。現行の実装状態と未達要件は`LOCAL_FIRST_IMPLEMENTATION_JA.md`を参照すること。

状態: `設計再審査 — 本番コード・GitHub設定は未変更`
対象: `fjnmgnkai/projectsynctest`（現在は意図的なPUBLICテストリポジトリ）
正本: `C:\Users\19718\Downloads\VRChat_ProjectSync_全体運用仕様書_v1.1.docx`

## 1. 認識の訂正と適用範囲

通常運用は、**各制作メンバーが自分の個人GitHubアカウントで認証し、各PC上の別々のCloneから、同じGitHub Repositoryの自分のTask BranchへCommit・Pushする**。共通アカウント、常時稼働サーバー、GitHub Actions、管理者トークンのメンバー配布は前提にしない。仕様書§15も「各メンバーは個人GitHubアカウントで認証」と定める。

仕様書§6.1とAT-30/56の「同一アカウントの2台」は、通常のチームアカウント構成ではなく、端末移行や誤った二重起動への安全条件である。これを通常運用の前提にした従来の設計説明は誤りだった。一方、ユーザーが明示的に削除を承認していないため、試験条件としては残す。

本書は正本仕様を独断で置き換えない。従来の `IMPLEMENTATION_SPEC.md` にある「常時稼働のTrusted GitHub App Service必須」は正本仕様に書かれていない実装案なので、この再審査では採用しない。保護不能な条件は満たしたことにせず、下記の未解決事項として示す。

## 2. 実在する環境・差分

| 項目 | 読み取りで確認した事実 | 意味 |
|---|---|---|
| Unity | 2022.3.22f1、VRChat Worlds SDK 3.10.5 | Unity 6専用連携は使用しない |
| GitHub | `fjnmgnkai/projectsynctest`、default `main`、2026-10-02時点PUBLIC | PUBLICはテスト用という所有者の意図。公開設定は変更しない |
| GitHub main保護 | Ruleset 0件、Branch Protection APIは404 | 現在のテストRepositoryでもGitHub側の強制Gateは未設定 |
| Shared state | `projectsync-state` Branchはまだ存在しない | 現行コードのメモリ状態は複数PCの正本にならない |
| Scene | `.gitattributes` がMain Sceneを`filter=lfs ... lockable`に設定 | LockだけでなくScene実体もLFS容量を使用する |
| CI | `.github/workflows/projectsync-required-check.yml` がPR/mainでWindows Actionsを実行 | 「Actionsを使わない」要件と衝突。実装段階で廃止・置換が必要。今回は変更しない |
| Desktop | 新規/再開/保存/提出は無効、GitHub service未設定と表示 | 現パッケージは本番ツールではない |

## 3. 再選定する構成

```text
各メンバーのWindows PC（各自のGitHubアカウント）
  ProjectSync Desktop
    ├─ Unity 2022.3 Editor Bridge（開いているScene/Assetを保存）
    ├─ Git / Git LFS CLI（各自のClone・Task Branchだけ）
    ├─ GitHub API（各自のユーザー認証でIssue・状態・PRを操作）
    └─ ローカル永続Journal（操作ID、段階、未送信データ）
                       │
                       ▼
  同じGitHub Repository: main / Task Branch / Issue / PR /
                       projectsync-state Branch / LFS Locks

管理者PC（必要な時だけ起動）
  Candidate作成・Unity確認・Conflict解決・Squash Merge・Build確認
```

- ツールはGitを再実装しない。各ユーザーの通常のGitHub資格情報でGit PushとLFS操作を実行する。GitHub APIの認証主体とGit Pushの認証主体が一致するよう、初回設定で検査する。共通の管理者資格情報を同梱しない。
- `projectsync-state` は仕様書§4/§6.1どおり同じRepositoryの**別Branch**に置く。DesktopがContents APIで現在のblob SHAを読み、そのSHAを指定して更新する。409 Conflictなら最新状態を読み直して遷移を再判定し、古い状態を上書きしない。Issueは人が読むTask記録、state JSONはSession/Lock/Submitted SHAの機械的判定用とする。
- Scene LockはGit LFS Lockで取得・ID/Path/ownerを照合し、state JSON側のTask/Device/Session/generationとも一致した時だけ操作を許可する。同一アカウントの二重起動にもこの照合を使う。LFS ownerが同じだけでは許可しない。
- ローカルJournalは共有正本ではないが、Unity保存済み・Commit済み・Push応答不明などを再起動後に復元する。ローカル未共有データは自動削除しない。
- 管理者操作とBuild/Publishは同じDesktopの権限別画面または別ローカル実行モードで構成できる。常時稼働サービスは不要。ただしGitHub Checkの信頼できる書き込み主体は別途決定が必要（§5）。

## 4. 利用者の4操作

| 操作 | 必ず行うこと | 失敗時 |
|---|---|---|
| 新しい作業 | local/Unity未保存・未送信確認 → remote最新main取得 → Task ID/Issue・必要なScene LockとCAS Sessionを確定 → そのmain SHAから短命Branch作成・確認 | 途中状態をJournal/共有状態と照合し、復旧要確認。別Taskへ切り替えない |
| 作業を再開 | 既存Task Branch、GitHub Issue、state、LFS Lock、local未送信データを照合。新Branchは作らない | 不一致時は編集・送信を許可しない。ローカルデータは保持 |
| 作業を保存 | Unity保存 → 同一Task Branchへ一つのSnapshot Commit → 非force Push → remote到達確認 | Commit成功/Push失敗ならCommitを残しPushだけ再試行。応答喪失はremoteを再照合 |
| 変更を提出 | 保存到達と検査を確認 → Submitted Commit SHAを固定 → PRとstateを照合 | 二重PRを作らず復旧。後続Commitを既存提出に自動採用しない |

通常UIから`main`のCommit/Push、reset --hard、自動stash、自動破棄、force push、片側優先Conflict解決は実行させない。進行中Taskへ新しいmainを自動適用しない。

## 5. 根拠つき実現性判定

| 要求 | 判定 | 根拠と境界 |
|---|---|---|
| 個人アカウント・別PCから同一Repositoryの各Task BranchへCommit/Push | **可能** | 個人所有RepositoryのcollaboratorはPushできる [GitHubの権限仕様](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/repository-access-and-collaboration/permission-levels-for-a-personal-account-repository)。各人に書き込み権限と認証が必要。仕様書§15にも合致 |
| サーバー・Actions・追加料金なしで日常4操作 | **可能（実装・実機試験は未完了）** | Git CLI、Git LFS CLI、GitHub REST APIをローカルから呼ぶ。GitHub APIのContents更新は既存blob SHA、対象Branch、409 Conflictを定義している [GitHub Contents API](https://docs.github.com/en/rest/repos/contents#create-or-update-file-contents) |
| 同一アカウント2台のProjectSync同士で、片方だけSession有効 | **設計上可能、未実証** | 同一JSONへのSHA付きCASと再読込で競合を解決。LFS Lockのowner一致だけでは許可しない。物理2台・通信断試験が必要 |
| 書き込み権限を持つ利用者が、ProjectSync以外からstate/mainを絶対に変更できない | **Private + GitHub Freeでは保証不可** | 同じRepositoryへの書き込み権限を与える一方、Private FreeにはBranch Protection/Rulesetsがない。[GitHub Protected Branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches)・[Rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)。既承認の信頼ベース例外はここにも及ぶか、確認が必要 |
| 一般メンバーが偽造できないGitHub CheckをActions/サーバーなしで作る | **管理者PCでだけ動くGitHub Appなら技術的には可能、方式未承認** | Check書き込みはGitHub Appの権限が必要。[Checks API](https://docs.github.com/en/rest/guides/using-the-rest-api-to-interact-with-checks)。App秘密鍵を一般メンバーのDesktopに同梱せず、管理者PCの保護領域だけに置く案は可能だが、鍵保管・紛失・ローテーションを決める必要がある [GitHub App key管理](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps) |
| Private + GitHub FreeのままAT-16/17/60をGitHub側で強制 | **不可** | Protected Branches/Rulesetsの対象プランにPrivate Freeは含まれない。ProjectSync側のガードと管理者確認は迂回防止の代わりにならない。既承認の例外を維持し、AT-60を合格扱いしない |
| 「費用ゼロ、かつ外部上限で一切止まらない」を無条件に保証 | **不可** | GitHub REST APIには[利用頻度制限](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)、Git LFSには[無料容量・転送量と超過時の拒否または課金](https://docs.github.com/en/billing/concepts/product-billing/git-lfs)がある。現RepositoryはScene自体をLFS対象にしている。ProjectSync独自の回数・期間制限は設けないが、GitHubが拒否した送信を成功とは表示できない。ローカルCommitを保持して再試行可能にする |
| VRChat Upload成功を自動判定してDeploymentへ厳密に記録 | **未実証** | Unity/VRChat SDK 3.10.5で取得できる確定的なUpload結果と、応答喪失時の照合手段をPoCする必要がある。Deployment作成だけをUpload成功と扱わない |

判定の「可能」は、完成済みという意味ではない。現Desktopの4ボタンは無効で、実GitHub/Unityの終端間試験はまだない。

## 6. 無料・無上限という要望の実装上の意味

ProjectSyncはGitHub Actionsを起動せず、ProjectSync自身の操作回数・日数・容量上限も設けない。追加料金を発生させるサービスを勝手に導入しない。一方、GitHub自体の制限を消すことはできない。GitHubがAPI/LFS/Pushを拒否したら、作業を消さず「PC保存済み・GitHub送信待ち」と正確に示し、復旧後に続きから送る。これは任意の枠による停止ではなく、外部サービスの失敗を隠さない安全動作である。

現行`.gitattributes`はMain Scene実体をLFS化しており、仕様書§11の「Lockは使うがScene自体のLFS化は不要」という運用案より容量面で不利。Sceneを通常Git＋LFS Lockへ移すかは、既存履歴・実体復元・チームのCloneを調べてから別途判断する。今回、追跡方式や履歴は変更しない。

## 7. 実装前に必要な決定・試験

1. 管理者PC限定のGitHub App（Check作成用）を採用するか。採用しない場合、仕様書の信頼済みGitHub Check要件は満たせず、管理者による手動確認に仕様変更する必要がある。
2. 最終RepositoryをPrivate + GitHub Freeにする際、AT-16/17/60とstate Branchの外部書き換え防止がGitHub側で保証できないことを、既存パイロット例外として正式運用まで許容するか。テストPUBLICで保護に成功してもPrivate化後の保証にはならない。
3. Git LFSを実際に使うAsset種類と容量方針。無料枠超過時に、費用ゼロと送信継続の両方は保証できない。Main Sceneの既存LFS追跡も含めて決める。
4. 異なる個人アカウントの2PC、同一アカウントの2PC、LFS同時Lock、Issue成功/Lock失敗、保存応答喪失、Task中止/Unlock失敗を破棄可能なテストTaskでPoCする。
5. Build/Uploadの実結果判定、Platform、World ID、Build担当のローカル専用Clone運用を決める。

上記1〜3は機能・安全性の意味を変えるため、推測でコードを有効化しない。先に各ユーザーの個人認証・同一Repository/Task Branchへの通常操作と、ローカルデータ保全の実装を進めることは可能。ただし現パッケージを本番用と呼ぶ条件には含めない。
