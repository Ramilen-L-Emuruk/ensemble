# MultiTrackPlayer

マルチトラック音声対応の Windows 動画プレイヤー。複数の音声トラックを同時再生し、トラックごとに音量を個別調整できる。

## 主な機能

- **マルチトラック音声再生**: 複数の音声トラックを同時にデコード・再生し、トラックごとに音量を個別制御（ミキサーウィンドウ）
- **ハードウェアデコード**: FFmpeg（D3D11VA）によるハードウェアアクセラレーテッドな映像デコード。AV1 など既定デコーダがソフトウェアのコーデックは、D3D11VA 対応の内蔵デコーダを自動選択してハードウェア経路に載せる
- **GPU ゼロコピー描画**: ハードウェアデコード出力の D3D11 テクスチャを CPU 転送せず、専用スレッドが映像子ウィンドウのスワップチェーンへ vsync で直接提示する（フレーム間引きを抑制）。ソフトウェアデコード時は D3DImage / WriteableBitmap 経路へ自動フォールバックする
- **audio-master クロック同期**: 音声出力位置を基準に映像フレームを同期する再生クロック
- **シークバーサムネイルプレビュー**: シークバーホバー時に該当位置のサムネイルを表示
- **チャプター機能**: チャプターの作成・編集・永続化
- **プレイリスト**: 複数ファイルの連続再生
- **コマ送り・先頭/末尾ジャンプ**: 一時停止中の 1 フレーム単位の前送り・後送り、Home/End での先頭・末尾シーク
- **キーバインドのカスタマイズ**: ショートカットキーの設定・確認
- **デバッグウィンドウ**: 再生パイプラインの内部状態確認用

## 動作環境

- Windows 10 / 11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（ソースからビルドする場合は SDK が必要）

## 技術スタック

| 領域 | 使用技術 |
|------|----------|
| UI | WPF (.NET 10), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| 映像デコード | [Sdcb.FFmpeg](https://github.com/sdcb/Sdcb.FFmpeg)（D3D11VA ハードウェアデコード対応） |
| GPU 描画 | [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)（Direct3D11 / D3D9 / DXGI）によるスワップチェーン提示・D3DImage ブリッジ |
| 音声再生 | [NAudio](https://github.com/naudio/NAudio)（WASAPI） |
| テスト | xUnit |

## プロジェクト構成

```
src/
├── MultiTrackPlayer.Core/    モデル・インターフェース（依存なし。ViewModel・エンジンから切り出した純ロジックを含む）
├── MultiTrackPlayer.Engine/  FFmpeg デコード・NAudio ミキサー・GPU 描画（unsafe コード有）
│   ├── Decoding/             映像/音声デコーダ（VideoDecoder は D3D11VA 対応デコーダを優先選択）
│   ├── Pipeline/             ffplay 型スレッド分離パイプライン（Demux/Decode スレッド + 有界キュー）と
│   │                         映像書き込み戦略（GpuFrameSink / CpuFrameSink）
│   ├── Rendering/            GPU ゼロコピー描画の中核（共有 D3D11 デバイス・VideoProcessor 色変換・
│   │                         スワップチェーンの vsync 提示）
│   ├── Video/                映像フレームリング（GPU/CPU）とスロット状態機械（SlotSequencer）・due フレーム選択
│   ├── Sync/                 audio-master 再生クロック・シーク後のプリロール待ち合わせ
│   ├── Audio/                マルチトラックミキサー・トラック状態
│   ├── Thumbnails/           シークバー用サムネイル生成・キャッシュ
│   ├── Utilities/            ハードウェアデバイス生成等のユーティリティ
│   └── Diagnostics/          診断ログ・滞留検出（音声・映像の停止を経過時間で検出）
└── MultiTrackPlayer.UI/       WPF アプリ・MVVM ViewModel・XAML ビュー
    ├── Controls/              シークバー等のカスタムコントロール
    ├── Rendering/             D3DImage ブリッジ・映像子ウィンドウ（HwndHost）
    ├── ViewModels/            MVVM ViewModel
    ├── Windows/               ミキサー・プレイリスト・チャプター・ショートカット・デバッグ・透過オーバーレイ各ウィンドウ
    └── Settings/              アプリ設定・キーバインド設定

tests/
└── MultiTrackPlayer.Tests/    xUnit（FFmpeg・D3D11 に依存しないロジックが対象。デコード・描画パイプライン本体は対象外）
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

FFmpeg のネイティブ DLL（`avcodec-60.dll` 等）や NAudio・CommunityToolkit.Mvvm・Vortice の依存 DLL が必要なため、`publish/` フォルダ一式を配布する形式になっている（single-file 化は未対応）。

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
- AV1 等のハードウェアデコードは GPU・ドライバ・FFmpeg ビルドの D3D11VA 対応に依存する。対応しない環境では自動的にソフトウェアデコード（CPU 描画経路）へフォールバックする。
