# CLAUDE.md

MultiTrackPlayer（マルチトラック対応 Windows 動画プレイヤー）で作業するときの手順とルール。

## プロジェクト概要

- C# + WPF (.NET 6) 製の Windows 動画プレイヤー。
- 映像エンジン: Sdcb.FFmpeg（D3D11VA ハードウェアデコード対応）。
- 音声: NAudio (WASAPI)。マルチトラック音声の同時再生・個別音量制御に対応。
- MVVM: CommunityToolkit.Mvvm。
- ソリューション構成: Core（モデル・インターフェース）/ Engine（FFmpeg + NAudio）/ UI（WPF）。

## 変更時の基本フロー（必ずこの順で行う）

1. **ワークツリー作成**（修正・機能追加時は必ず `EnterWorktree` でワークツリーに入ってから作業する。ワークツリー名にはコミット種別に合わせたプレフィックスを付ける: `fix/〇〇`・`feat/〇〇`・`refactor/〇〇`・`docs/〇〇`・`chore/〇〇` など）
2. **実装**
3. **検証**（下記「検証」を実施。ビルド必須＋**実行確認必須**）
4. **コミット**（下記「コミット」の規約に従い、確認を求めず自動で行う）
5. **main へのマージ**（**ユーザーから明示的に指示があったときのみ**。ワークツリーの変更を main にマージする前に必ず確認する。必ずマージコミットを作成する（`--no-ff`））
6. **プッシュ**（**ユーザーから明示的に指示があったときのみ**。自動では行わない）

## 検証

> **コードを修正した場合は、ビルドだけでなく必ずアプリを起動して実行確認まで行う。**

- **ビルド（必須）**: `dotnet build`。エラー 0 を確認する。
- **実行確認**: `dotnet run --project src/MultiTrackPlayer.UI` でアプリを起動し、対象の変更が正常に動作することを確認する。
- **大きめの変更時**: Release ビルドも確認: `dotnet build -c Release`。

## プランモード

- プランモードに入ったとき、前回のプランが**完了済み**（実装・コミット済み）の場合は、既存プランファイルを修正せず**新規プランファイルを作成**する。
- 前回プランが未完了（作業途中）の場合のみ、既存プランファイルを引き続き更新してよい。

## コミット

- **Conventional Commits**（`feat` / `fix` / `refactor` / `docs` / `chore` / `perf` / `ci`）。説明は日本語。
- コミットメッセージ末尾に必ず付与:
  ```
  Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
  ```
- Windows 環境のため `LF will be replaced by CRLF` の警告が出るが正常（無視してよい）。

## マージ

- **ファストフォワード可能な場合でも、必ずマージコミットを作成する（`git merge --no-ff`）**。
  - 例: `git merge --no-ff feat/〇〇`
  - 理由: 各機能・修正の単位（ブランチ）を履歴上で明確に残すため。

## 補助コマンド

| コマンド | 用途 |
|----------|------|
| `dotnet build` | ビルド（全プロジェクト） |
| `dotnet build -c Release` | リリースビルド |
| `dotnet run --project src/MultiTrackPlayer.UI` | アプリ起動 |
| `dotnet test` | テスト実行（テストプロジェクト追加後） |

## 構成メモ

- `src/MultiTrackPlayer.Core/`: モデル・インターフェース（依存なし）
- `src/MultiTrackPlayer.Engine/`: FFmpeg デコード・NAudio ミキサー（unsafe コード有）
- `src/MultiTrackPlayer.UI/`: WPF アプリ・MVVM ViewModel・XAML ビュー
- チャプター永続化: `%APPDATA%\MultiTrackPlayer\chapters\{MD5}.json`
- キーバインド設定: `%APPDATA%\MultiTrackPlayer\keybindings.json`
