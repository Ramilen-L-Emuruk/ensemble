# MultiTrackPlayer

マルチトラック音声対応の Windows 動画プレイヤー。複数の音声トラックを同時再生し、トラックごとに音量を個別調整できる。

## 主な機能

- **マルチトラック音声再生**: 複数の音声トラックを同時にデコード・再生し、トラックごとに音量を個別制御（ミキサーウィンドウ）
- **ハードウェアデコード**: FFmpeg（D3D11VA）によるハードウェアアクセラレーテッドな映像デコード
- **audio-master クロック同期**: 音声出力位置を基準に映像フレームを同期する再生クロック
- **シークバーサムネイルプレビュー**: シークバーホバー時に該当位置のサムネイルを表示
- **チャプター機能**: チャプターの作成・編集・永続化
- **プレイリスト**: 複数ファイルの連続再生
- **キーバインドのカスタマイズ**: ショートカットキーの設定・確認
- **デバッグウィンドウ**: 再生パイプラインの内部状態確認用

## 動作環境

- Windows 10 / 11 (x64)
- [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)（ソースからビルドする場合は SDK が必要）

## 技術スタック

| 領域 | 使用技術 |
|------|----------|
| UI | WPF (.NET 6), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| 映像デコード | [Sdcb.FFmpeg](https://github.com/sdcb/Sdcb.FFmpeg)（D3D11VA ハードウェアデコード対応） |
| 音声再生 | [NAudio](https://github.com/naudio/NAudio)（WASAPI） |
| テスト | xUnit |

## プロジェクト構成

```
src/
├── MultiTrackPlayer.Core/    モデル・インターフェース（依存なし）
├── MultiTrackPlayer.Engine/  FFmpeg デコード・NAudio ミキサー（unsafe コード有）
│   ├── Pipeline/             ffplay 型スレッド分離パイプライン（Demux/Decode スレッド + 有界キュー）
│   ├── Video/                ネイティブ BGRA フレームリングバッファ
│   ├── Sync/                 audio-master クロック
│   └── Thumbnails/           シークバー用サムネイル生成・キャッシュ
└── MultiTrackPlayer.UI/       WPF アプリ・MVVM ViewModel・XAML ビュー
    ├── Controls/              シークバー等のカスタムコントロール
    ├── Windows/               ミキサー・プレイリスト・チャプター・ショートカット・デバッグ各ウィンドウ
    └── Settings/              アプリ設定・キーバインド設定

tests/
└── MultiTrackPlayer.Tests/    xUnit（純ロジックのみ。unsafe/FFmpeg 依存のパイプライン本体は対象外）
```

## ビルド

```bash
dotnet build
```

Release ビルド:

```bash
dotnet build -c Release
```

## 実行

```bash
dotnet run --project src/MultiTrackPlayer.UI
```

## テスト

```bash
dotnet test tests/MultiTrackPlayer.Tests/MultiTrackPlayer.Tests.csproj
```

## 発行（配布用 exe 作成）

ローカルで発行物を確認する場合:

```bash
dotnet publish src/MultiTrackPlayer.UI/MultiTrackPlayer.UI.csproj -c Release -o publish
```

FFmpeg のネイティブ DLL（`avcodec-60.dll` 等）や NAudio・CommunityToolkit.Mvvm の依存 DLL が必要なため、`publish/` フォルダ一式を配布する形式になっている（single-file 化は未対応）。

### GitHub Actions によるリリース

`v` から始まるタグを push すると、GitHub Actions（`.github/workflows/release.yml`）が自動的にビルド・zip 化・GitHub Release への公開まで行う。

```bash
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

## 設定ファイルの保存先

| 内容 | パス |
|------|------|
| チャプター | `%APPDATA%\MultiTrackPlayer\chapters\{MD5}.json` |
| キーバインド | `%APPDATA%\MultiTrackPlayer\keybindings.json` |

## 既知の制約

- ファイルオープン（`avformat_open_input` / `avformat_find_stream_info`）は UI スレッドで同期実行される。3-4GB 級のファイルでは数百ms かかる場合がある（非同期化は未対応）。
