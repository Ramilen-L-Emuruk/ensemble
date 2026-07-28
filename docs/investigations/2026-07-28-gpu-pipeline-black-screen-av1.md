# GPU 描画パイプラインでの黒画面（AV1 動画）調査メモ

- 調査日: 2026-07-28
- 対象ブランチ: `worktree/chore/dotnet10-migration`
- 未修正（調査結果のみ。開発機側で修正予定）

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
