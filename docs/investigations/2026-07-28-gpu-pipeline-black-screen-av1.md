# GPU 描画パイプラインでの黒画面（AV1 動画）調査メモ

- 調査日: 2026-07-28
- 対象ブランチ: `worktree/chore/dotnet10-migration`
- 状態: 上記の黒画面は修正済み（コミット参照）。ただし修正の副作用で
  **CPU 経路再生時にオーバーレイ（OSD・シークバー）が表示されなくなる既知の問題が残っている**
  （「追記」参照・未修正）。

## 現象

以下のような縦動画（2160×3840）を再生すると、映像が黒画面のまま進行しない。音声は再生される。

```
E:\Videos\Captures\REV\Replay 2026-07-28 10-39-55.mp4
```

## 再現ログ

`%APPDATA%\MultiTrackPlayer\logs\session-20260728-171838.log` より抜粋。再生開始直後からこの3行が延々と繰り返される：

```
[gpuConvert] 映像リング=GPU ゼロコピー経路（HW デコード + VideoProcessor）
[d3dPresenter] swapchain 生成 2160x3840
[d3dPresenter] vout スレッド開始
[gpuConvert] HW テクスチャ取得失敗 fmt=Yuv420p（GPU 経路に SW フレーム到達。破棄）
```

比較対象として、正常に再生できた 1920×1080 の H.264 ファイルでは以下のログが出ている（今回の対象ファイルでは一度も出現しない）：

```
[hwDecode] get_format 候補=[Dxva2Vld, D3d11vaVld, D3d11, Cuda, Yuvj420p] 選択=D3d11 (D3D11=HWデコード有効)
```

## 原因

1. 対象ファイルの映像コーデックは **AV1**（`av01` fourcc、ファイル先頭バイナリから確認）。
2. この環境の FFmpeg デコーダは AV1 に対して D3D11VA の候補を一切提示しない
   （`VideoDecoder.SelectPixelFormat`（[VideoDecoder.cs:99](../../src/MultiTrackPlayer.Engine/Decoding/VideoDecoder.cs#L99)）＝ `get_format` コールバック自体が呼ばれていない）。
   結果として完全ソフトウェアデコードになる。
3. `VideoDecoder._useHw` は `hw_device_ctx` を注入した時点で `true` になるだけで、
   実際に `get_format` で D3D11 が選択されたかどうかは反映していない
   （[VideoDecoder.cs:65-74](../../src/MultiTrackPlayer.Engine/Decoding/VideoDecoder.cs#L65-L74)）。そのため `IsHardwareAccelerated` は
   このファイルでも楽観的に `true` を返し、上位は GPU 経路（`GpuFrameSink`）を選び続ける。
4. `GpuFrameSink.WriteFrame`（[GpuFrameSink.cs:29-37](../../src/MultiTrackPlayer.Engine/Pipeline/GpuFrameSink.cs#L29-L37)）は
   `TryGetHwTexture` が失敗した場合（＝ソフトウェアフレームが来た場合）、ログを出して
   **そのフレームを黙って破棄するだけ**でフォールバック経路を持たない。
   コード中のコメントは「稀: D3D11非対応コーデック等」としているが、AV1 ファイルでは常時発生する。
5. 結果、映像リングに一枚もフレームが積まれず、スワップチェーンは初期状態の黒いバックバッファを
   Present し続ける。音声は別スレッド（mixer）で進行するため、「音は鳴るが映像だけ黒い」という
   症状になる。

旧ビルド（この GPU パイプライン導入前の main）で同じ縦動画が問題なく再生できていたのは、
旧経路が `sws_scale` + `WriteableBitmap` の CPU 変換一本槍で、そもそも SW フレームを前提に
作られていたため。今回の GPU 専用パイプラインには **ソフトウェアデコードへのフォールバックが
存在しない** のが本質的な問題。

## 影響範囲

D3D11VA ハードウェアデコードの候補が提示されない、または実際に SW デコードへ落ちる
すべてのコーデック／ファイルで発生しうる（今回確認したのは AV1 だが、他コーデックでも
ドライバやプロファイルの組み合わせによっては同様の可能性がある）。

## 修正方針（案・要選定）

- **案A（低リスク・お勧め）**: ファイルを開いた直後、実際に HW デコードが選択されたか
  （`get_format` の結果）を見て、SW フォールバックだったら vout / スワップチェーンを
  起動せず、最初から旧来の `WriteableBitmap` + `CompositionTarget.Rendering` 経路に
  切り替える。1ファイル単位の判定で済むため実装範囲が狭い。
  - `VideoDecoder.IsHardwareAccelerated` を「`hw_device_ctx` を注入したか」ではなく
    「`get_format` で実際に D3D11 が選ばれたか」を反映するよう修正する必要がある
    （現状は注入時点で楽観的に `true` を返しており、これ自体が誤り）。
- **案B（工数大）**: 再生中に動的にフレーム単位で GPU⇔CPU を切り替えられるようにする。
  滑らかだが複雑さとバグリスクが増える。

## 参考: 再現ファイルの特定方法

コーデック確認は `ffprobe` が手元になかったため、ファイル先頭 2MB を走査して
`av01` fourcc の出現を確認する簡易チェックで行った（`av1C` 設定ボックスも同時に確認）。

## 追記（同日）: 黒画面の修正と、新たに判明した副作用

### 黒画面の一次修正

上記「原因」の 2〜3 に対応する修正を実施（`fix: HWデコード非対応コーデック(AV1等)で黒画面になる問題を解消`）。
`VideoDecoder.IsHardwareAccelerated` が `get_format` の実際の選択結果（D3D11 が選ばれたか）を
反映するようになり、AV1 のような SW フォールバックのファイルは最初から CPU 経路（`CpuFrameSink`）
が選ばれるようになった。

### 二次的に発覚した問題: airspace で VideoHost が VideoImage を覆い隠す

上記修正後も実機確認で「デコードは進んでいるのに画面が黒い」という報告があり、再調査したところ
別原因が見つかった。

- `VideoHost`（[MainWindow.xaml.cs](../../src/MultiTrackPlayer.UI/MainWindow.xaml.cs) 内 `VideoHwndHost`）は
  airspace の性質上、XAML の重ね順に関わらず常に WPF 要素（`VideoImage`）より手前に描画される。
- vout（GPU スワップチェーン提示）が動いているときは `VideoHost` 自体に映像が描画されるので問題ないが、
  CPU 経路（`vout` 非稼働）のときは `VideoImage` に描画された映像が `VideoHost`（何も描かれていない
  ネイティブウィンドウ）に覆い隠され、黒画面に見えていた。

**この分は修正済み**（`fix: CPU経路映像がairspaceでVideoHostに隠れ黒画面になる問題を解消`）。
`OnRendering` で `Engine.IsVideoOutputActive`（vout 稼働中＝GPU 経路）の状態に追従して
`VideoHost.Visibility` を Visible/Collapsed で切り替えるようにした
（`SyncVideoHostVisibility`、[MainWindow.xaml.cs](../../src/MultiTrackPlayer.UI/MainWindow.xaml.cs)）。

### 既知の問題（未修正）: CPU 経路でオーバーレイ（OSD・シークバー）が出なくなった

上記の `VideoHost.Visibility = Collapsed` の副作用として、CPU 経路の動画再生中は
**フルスクリーンの OSD・シークバーオーバーレイ（`AirspaceOverlayWindow`）が表示されなくなっている**。

原因: `UpdateOverlayBounds()`（[MainWindow.xaml.cs:224](../../src/MultiTrackPlayer.UI/MainWindow.xaml.cs#L224)）は
`VideoHost.ActualWidth` / `ActualHeight` を見てオーバーレイの位置・サイズを追従させており、
`width <= 0 || height <= 0` のときはオーバーレイを `Hide()` する実装になっている
（[MainWindow.xaml.cs:230-236](../../src/MultiTrackPlayer.UI/MainWindow.xaml.cs#L230-L236)）。
`VideoHost` を `Collapsed` にすると `ActualWidth`/`ActualHeight` が 0 になるため、
CPU 経路のときは常にこの条件に該当してオーバーレイが隠れたままになる。

**修正方針（案）**: オーバーレイの位置・サイズ計算の基準を `VideoHost` ではなく `VideoArea`
（またはレターボックス矩形を保持する別の値）に変更する。`VideoHost` の可視状態と
オーバーレイの表示可否を結び付けている現状の実装が、今回の Visibility 切り替えと衝突している。

上記オーバーレイの問題は開発機側で修正済み（`fix: CPU経路でオーバーレイ(OSD・シークバー)が表示されない問題を解消`
→ 呼び出しトリガーの不足分をこちらで追加修正: `fix: CPU経路でオーバーレイの再計算が発火せず表示されない問題を解消`）。
両方とも実機確認済み。

### 新たに判明した問題（未調査・未修正）: アスペクト比の異なる動画へ切り替えると前の映像が残る

実機確認中に報告があった症状: アスペクト比の異なる動画（例: 16:9 の GPU 経路の動画 → 別アスペクト比の
動画）へ切り替えて再生すると、**前に再生していた映像の内容が画面から消えずに残ってしまう**。
スクリーンショットでは、ウィンドウが3段に分かれてそれぞれ別々の（過去の）映像フレームらしき内容が
同時に表示された状態が確認できた（単純に前の1フレームが背景に残る、というより複数レイヤーぶんの
残像が重なって見える）。

現時点では未調査。ここ数日で触っている `VideoHost`（ネイティブ子ウィンドウ・GPU 経路）/ `VideoImage`
（WPF Image・CPU 経路）/ `AirspaceOverlayWindow`（透過オーバーレイ・OSD）の3層の重なりと、
ファイル切替時にそれぞれのレイヤーが古い内容をクリアせず残す作りになっていないか、という点を
疑うべきところから調査を始めるのが妥当と思われる（`VideoHost.Visibility` の Visible/Collapsed
切り替え自体は前述の修正で入れたばかりのため、そこが絡んでいる可能性も含めて確認する）。
