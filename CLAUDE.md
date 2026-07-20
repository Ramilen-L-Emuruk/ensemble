# CLAUDE.md

MultiTrackPlayer（マルチトラック対応 Windows 動画プレイヤー）で作業するときの手順とルール。

## プロジェクト概要

- C# + WPF (.NET 6) 製の Windows 動画プレイヤー。
- 映像エンジン: Sdcb.FFmpeg（D3D11VA ハードウェアデコード対応）。
- 音声: NAudio (WASAPI)。マルチトラック音声の同時再生・個別音量制御に対応。
- MVVM: CommunityToolkit.Mvvm。
- ソリューション構成: Core（モデル・インターフェース）/ Engine（FFmpeg + NAudio）/ UI（WPF）。

## 変更時の基本フロー（必ずこの順で行う）

1. **状況整理・修正方針の確認**（実装に着手する前に必ず行う。自明な軽微修正でも省略しない）
   - 対象のコード・挙動を調査し、現状と問題点（原因・影響範囲）を整理する
   - 整理した内容と修正方針（どこを・なぜ・どう直すか）をユーザーに提示する
   - ユーザーの確認を得てから次のステップに進む。指摘や方針変更があれば整理からやり直す
2. **ワークツリー作成**（修正・機能追加時はワークツリーを作成してから作業する。ブランチ名は必ず `worktree/<type>/<name>` 形式にする: `worktree/fix/〇〇`・`worktree/feat/〇〇`・`worktree/refactor/〇〇`・`worktree/docs/〇〇`・`worktree/chore/〇〇` など）
   - **注意**: `EnterWorktree(name: ...)` はブランチ名を自動変換してしまうため使用しない。必ず以下の 2 ステップで行う:
     ```bash
     # Step 1: 正しいブランチ名でワークツリーを作成
     git worktree add -b worktree/<type>/<name> .claude/worktrees/<name>
     # Step 2: 作成したワークツリーに入る
     EnterWorktree(path: ".claude/worktrees/<name>")
     ```
3. **実装**（プロジェクトでバージョン番号を管理している場合、ワークツリー内ではバージョンフィールドを変更しない。理由は下記「バージョン管理」参照）
4. **検証**（下記「検証」を実施。ビルド必須＋**実行確認必須**）
5. **コミット前確認**（実装・検証が終わったら、コミットする前に必ずユーザーに確認を取る。**省略しない**）
   - 何を・なぜ・どう直したかを簡潔に提示し、コミットしてよいか確認する
   - 確認が取れるまで次のステップに進まない
6. **コミット**（確認が取れたら、下記「コミット」の規約に従いコミットする）
7. **main へのマージ**（**ユーザーから明示的に指示があったときのみ**。マージ前に必ず確認する。必ずマージコミットを作成する（`--no-ff`））
8. **バージョン更新**（プロジェクトでバージョン番号を管理している場合のみ。**main へのマージ直後・main 上で実施**。下記「バージョン管理」参照。マージしない限りこのステップは発生しない）
9. **プッシュ**（**ユーザーから明示的に指示があったときのみ**。自動では行わない）
10. **リリース後のクリーンアップ**（プッシュ完了後に必ず実施する）
    - **ワークツリーの削除**:
      - ワークツリー内にいる場合は先に `ExitWorktree(action: "keep")` で抜けてから削除する
      - `git worktree remove .claude/worktrees/<name>` でワークツリーディレクトリを削除
      - `git branch -d worktree/<type>/<name>` でブランチを削除
    - **起動したサーバー・ツールの停止**: 検証用に起動したサーバーやツールを停止する
      - ポート指定で対象プロセスのみ終了させること（一括停止は他のサービスに影響するため使わない）

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
- プロジェクトでバージョン番号を管理している場合、ワークツリー内のコミットではバージョンフィールドを変更しない（下記「バージョン管理」参照）。
- **コミット前に必ずユーザーに確認を取る。省略しない**。同一セッション内で別の修正のコミット許可をもらっていても、その修正のコミット許可を個別にもらっていない場合は確認する。

## マージ

- **ファストフォワード可能な場合でも、必ずマージコミットを作成する（`git merge --no-ff`）**。
  - 例: `git merge --no-ff worktree/feat/〇〇`
  - 理由: 各機能・修正の単位（ブランチ）を履歴上で明確に残すため。

## バージョン管理（プロジェクトでバージョン番号を管理している場合）

> 現時点で `.csproj` に `<Version>` タグは存在せず、このプロジェクトはバージョン番号を管理していない。将来管理を始める場合に備えた手順として残す。

**バージョン確定は「main へのマージが完了した直後・main 上」でのみ行う。** ワークツリー内やマージ前のブランチではバージョンフィールドを変更しない。

- **理由**: 複数のワークツリーを並行して進めている場合、各セッションがワークツリー作成時点の（まだ更新されていない）古いバージョンを見て同じ番号を選んでしまい、結果的にバージョンが正しく積み上がらない事故が起きる（例: 1.0.0 から2つのセッションが同時にパッチ修正すると、両方とも 1.0.1 を選んでしまい最終的に 1.0.1 のままになる）。マージは順番に処理されるため、マージ直後の main 上でバージョンを決めれば常に最新の値を見て +1 できる。
- **手順**（main へのマージ後、毎回省略せず行う）:
  1. バージョンを上げるか（メジャー/マイナー/パッチ/上げない）をユーザーに確認する
  2. 上げる場合、main 上でバージョンファイル（`.csproj` の `<Version>` / `<AssemblyVersion>` 等）を更新する
  3. バージョン表示箇所が複数ある場合は、可能な限り**単一の情報源**（バージョンファイル）から参照する形にし、手動での二重更新を避ける
- プッシュ時（ユーザーの明示的指示があったとき）はバージョン更新で作成したタグも一緒に送る: `git push && git push --tags`（または `git push --follow-tags`）。

## コメント・ドキュメント整合性チェック

コードを変更した際は、以下の照合を必ず行う。変更が小さくても省略しない。

### 変更前後に確認すること
- **数値・閾値の一致**: コメントに書かれた秒数・件数・スケール値・インデックス範囲が実コードの値と一致するか。
- **列挙の網羅性**: コメントが列挙する要素（トラック種別・レイヤー数・ペイン名など）が実装のすべての要素を網羅しているか。
- **UI 説明文と実データの照合**: ユーザーに表示する説明文が、それが参照するデータの実値と一致するか。

### コメント腐敗を防ぐパターン
- 処理の「単位」「対象範囲」「条件」を書いたコメントは、実装変更時に必ず追従させる。
- 「A または B」「A の場合のみ」のような条件を述べるコメントは、実コードの条件式と突き合わせて確認する。
- 古い識別子・旧実装の痕跡（クラス名・変数名・関数名）がコメントに残っていないか確認する。

## 補助コマンド

| コマンド | 用途 |
|----------|------|
| `dotnet build` | ビルド（全プロジェクト） |
| `dotnet build -c Release` | リリースビルド |
| `dotnet run --project src/MultiTrackPlayer.UI` | アプリ起動 |
| `dotnet test tests/MultiTrackPlayer.Tests/MultiTrackPlayer.Tests.csproj` | テスト実行（xUnit） |

## 構成メモ

- `src/MultiTrackPlayer.Core/`: モデル・インターフェース（依存なし）
- `src/MultiTrackPlayer.Engine/`: FFmpeg デコード・NAudio ミキサー（unsafe コード有）
  - `Pipeline/`: ffplay 型スレッド分離パイプライン。`DemuxThread` が `AVFormatContext` を唯一専有し、
    `VideoDecodeThread`/`AudioDecodeThread` が `VideoPacketQueue`/`AudioPacketQueue`（serial・Flush/EOF
    番兵付き有界キュー）経由でデコードする。シークは `DemuxThread.RequestSeek` でコアレスされ非同期処理される
  - `Video/VideoFrameRing.cs`: 4スロットのネイティブ BGRA リング。`TryLeaseDue`/`TryLeaseOldest` +
    `ReturnLease` の真のリース方式（`IMediaEngine.TryGetFrame`/`ReturnFrame` の裏側）
  - `Sync/PlaybackClock.cs`: audio-master クロック（mixer 出力サンプル軸のセグメントマップ）。
    `WasapiPositionSource`（`IWavePosition` ベース、異常検知で `FallbackPositionSource` へ自動切替）と組み合わせて
    `MediaEngine.Position` を算出する
  - 映像描画は push（イベント）ではなく pull 型: UI 側の `CompositionTarget.Rendering` が毎フレーム
    `TryGetFrame`/`ReturnFrame` を呼ぶ
- `src/MultiTrackPlayer.UI/`: WPF アプリ・MVVM ViewModel・XAML ビュー
- `tests/MultiTrackPlayer.Tests/`: xUnit。純ロジック（`BoundedSerialQueue`/`PlaybackClock`/
  `PrerollCalculator`/`FrameSelector`）のみを対象とし、unsafe/FFmpeg 依存のパイプライン本体は対象外
- チャプター永続化: `%APPDATA%\MultiTrackPlayer\chapters\{MD5}.json`
- キーバインド設定: `%APPDATA%\MultiTrackPlayer\keybindings.json`
- ファイルオープン（`avformat_open_input`/`avformat_find_stream_info`）は UI スレッドで同期実行される
  （3-4GB ファイルで数百ms かかることがある。非同期化は未対応・既知の TODO）
