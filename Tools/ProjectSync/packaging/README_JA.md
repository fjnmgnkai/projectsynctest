# ProjectSync Windows x64 ポータブル版（開発プレビュー）

このZIPを任意のフォルダーへ展開し、`ProjectSync/ProjectSync.Desktop.exe` を起動してください。インストーラーと管理者権限は不要です。Windows x64向けの自己完結型ビルドなので、別途.NET 8ランタイムをインストールする必要はありません。

起動後、「Unityプロジェクトを選択」で対象フォルダーを指定します。現在は `fjnmgnkai/projectsynctest` のCloneを想定しています。`Assets`、`Packages`、`ProjectSettings` を含むUnityプロジェクトのルートを選んでください。コマンドラインからは `ProjectSync.Desktop.exe --project "C:\path\to\UnityProject"` も使用できます。ZIP自体にUnityプロジェクト、Git認証情報、GitHub App秘密鍵は含まれません。

## 現在の制限

これは起動と状態表示を確認するための開発プレビューです。選択先のGitHub remote一致もまだ確認しません。協調状態、GitHub連携、Unity Bridgeの実運用経路がまだ接続されていないため、「新しい作業」「作業を再開」「作業を保存」「変更を提出」は無効です。別PCへコピーしても共同制作フローが有効になるわけではありません。VRChat Build/Uploadやmain統合もできません。

Private＋GitHub Freeでは、GitHub側のmain保護を強制できません。小規模チームの試験運用例外は記録されていますが、仕様書v1.1のGitHub品質Gateを満たした状態ではありません。

このプレビューはコード署名されていません。入手元と同梱の `BUILD-INFO.txt`、ZIPと同じ場所の `.sha256` を確認してから実行してください。チェックサム例: `Get-FileHash .\ProjectSync-preview-win-x64-*.zip -Algorithm SHA256`。
