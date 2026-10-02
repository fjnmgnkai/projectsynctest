# ProjectSync（ローカル主体の試験版）

各メンバーが自分のGitHubアカウントと自分のUnity Cloneを使い、同じRepositoryの短命Task Branchへ保存・提出するWindowsアプリです。常時稼働サーバー、GitHub Actions、GitHub App、ProjectSync独自の利用枠は使いません。管理者がPull Requestを確認して`main`へ統合します。

## 現在動く操作

1. 「新しい作業」: 選択したUnityプロジェクトのEditorを閉じ、ローカル変更がない状態で最新のremote `main`を取得し、そこから新しい`task/...` Branchを作ります。作業中にmainを自動で取り込みません。別のUnityプロジェクトが開いていても構いません。
2. 「作業を再開」: 指定したローカルTask Branchへ戻ります。Branch切替前は選択したUnityプロジェクトのEditorを閉じ、ローカル変更をなくす必要があります。
3. 「作業を保存」: Unityが開いていればScene/Assetを保存し、Task Branchへ対象ファイルだけをCommit/Pushし、remote到達を確認します。Push失敗時はローカルCommitと`UserSettings/ProjectSync/save-state.json`を残し、次回同じCommitのPushだけ再試行します。
4. 「変更を提出」: GitHub CLIでPRを作り、PR本文にSubmitted Commit SHAを記録します。保存後に同じボタンで再提出すると、ProjectSyncが作成した既存PRの本文にある提出SHAを新しいHEADへ更新します。PR本文やHEADに予期しない変更があれば停止します。

通常操作で`main`へCommit/Push/Mergeしません。`reset --hard`、自動stash、force push、変更の自動破棄もしません。保存時にCommitするのは`Assets`、`Packages`、`ProjectSettings`、`.gitattributes`、`.gitignore`だけで、ルートの`Assets.zip`などは含めません。すでに別ファイルがGitにステージされていても、ProjectSyncのCommitは対象パスだけに限定します。

## このPCでの開発用起動（ZIP不要）

Repository直下の`ProjectSync.cmd`をダブルクリックしてください。毎回この作業フォルダーの最新ソースをビルドし、同じ場所にある開発版アプリを起動します。選択ProjectにはこのRepositoryを自動指定します。別のZIPをダウンロード・展開する必要はありません。

更新後はProjectSyncのウィンドウを閉じ、同じ`.cmd`をもう一度起動してください。起動中のアプリをランチャーが勝手に終了したり、未保存データを破棄したりしません。旧ZIPから起動したProjectSyncも検出して停止するので、先にそのウィンドウを閉じてください。開発版はこのPCにインストール済みの.NET 8 SDKを使用します。別PCへの配布用ZIPとは別の運用です。

## 必要な準備

- Windows x64、Unity 2022.3系の対象Project Clone、Git、Git LFS、GitHub CLI (`gh`)
- 各メンバーが自分のGitHubアカウントでGitのPushと`gh auth login`を設定し、Repositoryへの書き込み権限を持つこと
- Unity Projectの`.gitignore`に`/UserSettings/`と、共有しないバックアップ（例: `/Assets.zip`）を設定すること
- Unity Sceneを通常GitのYAMLとして管理すること。SceneをLFSにすると同じSceneの変更を通常のテキストマージで統合できません。
- 100 MiB超の共有素材には、管理者が個別にGit LFS追跡を設定すること。未設定のまま保存しようとするとProjectSyncはCommit前に停止します。GitHub FreeのLFS単体ファイル上限2 GBも検査します。

GitHub FreeのLFS無料枠はRepository所有者側でストレージ10 GiB・ダウンロード10 GiBです。追加料金を絶対に発生させない場合、所有者がGitHubのLFS予算を$0に設定してください。ProjectSyncはGitHubの請求設定を変更せず、無料枠の残量を完全には把握できません。[GitHub公式のLFS説明](https://docs.github.com/en/billing/concepts/product-billing/git-lfs)

## 現在の安全上の限界

- このアプリはローカルTask運用の試験版です。PR作成の実GitHub終端間試験、Unity Editor Bridgeの実機保存試験、複数PC試験は未完了です。
- Submitted SHAはPR本文に記録しますが、GitHub PRのHEADは後続Pushで変わり得ます。管理者は統合直前にHEADとSubmitted SHAを照合してください。再提出後、旧SHAに対する確認・検証結果を流用してはいけません。PR本文の競合更新を完全に原子的に防ぐ仕組みはないため、アプリは更新前後に再読込し、結果が一致しなければ要確認として停止します。
- Private + GitHub FreeではGitHub側の強制Branch Protection/Ruleset品質Gateは利用できません。アプリはmainを書きませんが、Repositoryの書き込み権限を持つ人の外部Git操作まで防げません。
- 旧仕様のLFS Scene Lock/Session協調、管理者Candidate検証、Build/Upload/Publishはこのローカル主体経路に接続していません。完成済みとは扱いません。
- 選択したProjectに既存の100 MiB超の通常Git履歴がある場合、追跡設定の追加だけでは過去のPush拒否を解消できません。履歴移行は自動では行いません。

## 開発と検証

```powershell
dotnet build .\src\ProjectSync.Desktop\ProjectSync.Desktop.csproj
dotnet run --project .\tests\ProjectSync.SpecTests\ProjectSync.SpecTests.csproj
```

仕様テストは一時的なローカルGit remoteを使い、Task開始、main非変更、保存、Push失敗後の同一Commit再試行、バックアップ除外、大容量素材/Scene追跡の拒否を検査します。実Repositoryへの自動Push・PR作成はテストでは行いません。
