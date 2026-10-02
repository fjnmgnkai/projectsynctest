# ProjectSync Windows x64 ポータブル版（ローカル主体の試験版）

ZIPを展開し、`ProjectSync/ProjectSync.Desktop.exe`を起動してください。.NETランタイムの別途インストールは不要です。Unity Project、Git認証情報、GitHub App秘密鍵はZIPに含まれません。この版はコード署名されていません。

## 初回準備

1. このPCにGit、Git LFS、GitHub CLI (`gh`)を用意します。各ユーザーは自分のGitHubアカウントでGitのPushと`gh auth login`を設定します。
2. `Assets`、`Packages`、`ProjectSettings`、`.git`を持つUnity ProjectのCloneを選択します。`--project "C:\path\to\UnityProject"`でも指定できます。
3. 管理者はSceneを通常GitのYAMLにし、100 MiB超の共有素材を個別にLFS追跡します。バックアップZIPは選択Projectの`.gitignore`で除外してください。例: `/Assets.zip`。
4. GitHubでLFSの追加課金を避ける場合、Repository所有者のLFS予算を$0にします。ProjectSyncはGitHubの請求設定を変更しません。

## 日常操作

- 新しい作業: 選択したUnityプロジェクトのEditorを閉じ、ローカル変更がない状態で作業名を入力して押します。最新remote `main`から短命Task Branchを作ります。別のUnityプロジェクトは開いたままで構いません。
- 作業を再開: 再開したいローカルTask Branchを指定して押します。Branchを切り替える場合は選択したUnityプロジェクトのEditorを閉じ、ローカル変更をなくしてください。
- 作業を保存: Unityが開いていればScene/Assetを保存し、Task BranchへCommit・Pushして到達を確認します。通信失敗時はCommitを残すので、同じボタンで再試行します。
- 変更を提出: 保存後に押すとPRを作り、本文にSubmitted Commit SHAを記録します。同じTaskで後から保存した場合は再度押して新SHAを再提出します。管理者はそのSHAとPR HEADを確認してから統合してください。

ProjectSyncは`main`へCommit/Push/Mergeしません。ルートの`Assets.zip`はProjectSyncのCommit対象にしません。GitHub Actionsと常時稼働サーバーは使いません。

## 試験版としての限界

実GitHub PR作成・再提出、Unity Editor経由の保存、複数PCでの終端間試験はまだ完了していません。Private + GitHub FreeではGitHub側の強制品質Gateを作れないため、管理者による確認が必要です。再提出はProjectSyncが作成した形式のPRだけに許可し、更新前後にPRを読み直します。競合更新は完全には防げません。Build/Upload/Publishも未接続です。本番確認済みと誤認しないでください。

ZIPのSHA-256を隣の`.sha256`ファイルと照合し、`BUILD-INFO.txt`でソースCommitを確認してください。
