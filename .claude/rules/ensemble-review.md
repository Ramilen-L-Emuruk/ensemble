# MultiTrackPlayer 固有のレビュー観点

汎用的な C#/.NET のチェックリストは `~/.claude/rules/dotnet/code-review.md` を参照。本ファイルは**このプロジェクトで実際に事故が起きた箇所**だけをまとめる。各項目には根拠となる修正コミットを添えてあるので、疑わしいときは `git show <hash>` で当時の変更を確認すること。

## 1. スレッド待ち合わせの取りこぼし（最頻・最悪）

過去に少なくとも 6 件の「再生が固まる」不具合を出している。原因はいずれも **待機スレッドを起こし忘れた** こと。

| コミット | 症状 | 原因 |
|---|---|---|
| `b5bff31` | 一時停止→再生で映像が固まる | `TryLeaseDue` が drop でスロットを Free にしたのに `PulseAll` を呼んでいなかった |
| `e1e2164` | シーク直後にフリーズ | HoldOutput 中に音声デコードスレッドが永久ブロック |
| `63309ca` `437a631` `742bd94` | 連続シーク・連打で固まる | 待ち合わせの取りこぼし |
| `50dcd31` | 連続巻き戻しでクロックが固まる | 同上 |

チェック項目:

- [ ] **待機側が前進できる状態へ遷移させる経路は、1 つ残らず `Monitor.PulseAll` を呼んでいること**。`SlotSequencer` では Free へ戻す経路が `CommitWrite`（世代不一致）・`AbortWrite`・`TryLeaseDue`（drop）・`ReturnLease`・`Flush` の 5 つある。1 つでも漏らすとデコードスレッドが寝たまま起きず、映像が止まる
- [ ] `Monitor.Wait` のループ条件に、Close と Flush（serial 世代の変化）の脱出条件が含まれていること。含めないとシーク時に起床できない
- [ ] 新しく待機を追加したら、**シーク中・一時停止中・EOF 到達時・操作の連打時**の 4 状況で確実に起床するか机上で追うこと
- [ ] `lock` は 9 ファイル・63 箇所に分布している。ロック内から外部コールバックを呼ぶ箇所（`SlotSequencer` の `onAcquired` / `onFreePayload`）では、コールバック側で別のロックを取らないこと（デッドロック源）

## 2. GPU / CPU 二経路の非対称

映像提示は `MediaEngine.IsVideoOutputActive`（実体は `_swapPresenter != null`）で二分岐する。**片方だけ直して他方を壊す**事故を繰り返している。

| 経路 | 提示方法 | UI 側の動作 |
|---|---|---|
| GPU（HW デコード時） | 専用 vout スレッドが `SwapChainVideoPresenter` で vsync 直接提示 | `CompositionTarget.Rendering` での映像プルを**行わない** |
| CPU（SW デコード時） | `D3DImagePresenter`（D3D9Ex↔D3D11 共有）で `D3DImage` へ | 毎フレーム `TryGetFrame` / `ReturnFrame` を呼ぶ |

チェック項目:

- [ ] **映像の見え方に関わる変更は、GPU 経路と CPU 経路の両方で確認すること**（`ebe6981` `18ad568` は GPU 側、`cf35a44` `1a91a9f` `d975e78` は CPU 側の事故）
- [ ] UI 要素（OSD・シークバー等）を追加したら、GPU 経路で airspace に隠れないか確認すること。隠れる場合は透過の `Windows/AirspaceOverlayWindow` に載せる
- [ ] アスペクト比・解像度の異なる動画へ切り替えたときに、前の映像が残らないこと（`8f14d79`）
- [ ] 新しいコーデックを扱う場合、HW デコード非対応時の SW フォールバックで黒画面にならないこと。`VideoDecoder` は既定デコーダが D3D11VA 非対応なら同一 codec_id の対応デコーダを `avcodec_get_hw_config` で探す（`1c98833` の AV1 対策）

## 3. ネイティブ・GPU リソースの解放順序

`Dispose` / `unsafe` / `CancellationTokenSource` は 28 ファイルに分布する。解放順序を誤るとプロセスごと落ちる。

- [ ] ペイロード（ネイティブバッファ・GPU テクスチャ）の確保・解放は `SlotSequencer` のコールバック内（＝ロック内）で行い、状態遷移との原子性を保つこと
- [ ] ファイル切替・連続 D&D では「**パイプラインの完全停止 → 解放 → 新規構築**」の順序を守ること。停止前に解放するとデコードスレッドが解放済み領域を触りネイティブヒープが壊れる（`2d257ea`）
- [ ] `CancellationTokenSource` を Dispose した後に参照しないこと（`26c03a5`。サムネイル生成で実際にクラッシュした）
- [ ] 例外が飛ぶ経路でも解放されること。`try`/`finally` か `using` で保証する

## 4. キー入力の経路が 4 つに分散している

キー操作の不具合を 5 件出している。入力経路が複数あり、片方にだけ実装して漏れるのが原因。

経路: `MainWindow.xaml.cs` の `PreviewKeyDown` / `MainWindow.xaml` の `InputBindings` / `Settings/KeyBindings.cs`（`%APPDATA%` の設定） / `Windows/SubWindowKeyHandling.cs`（子ウィンドウ）

- [ ] キー操作を追加・変更したら**全経路を確認**すること（`2615034` `748a7f0` `8027cf4` はいずれも「ショートカットが効かない」）
- [ ] テキスト編集中（チャプタータイトル等）は Enter / Esc をグローバル操作に横取りさせないこと（`595696c` `d13f1c7`）
- [ ] メニュー・ComboBox・キーボードのどの経路から操作しても、UI の状態表示が追従すること（`dc1ab6c`）

## 5. テスト可能性の設計

テスト対象は純ロジックのみ（`BoundedSerialQueue` / `PlaybackClock` / `PrerollCalculator` / `FrameSelector` / `SlotSequencer`）。unsafe / FFmpeg 依存のパイプライン本体はテストしていない。

- [ ] 同期ロジック・状態機械を新規に追加する場合は、**FFmpeg 依存から切り離した純ロジックとして実装し、テストを書く**こと。`SlotSequencer`（状態機械）と `GpuVideoFrameRing`（ペイロード管理）の分離がその手本
- [ ] 上記 1 の待ち合わせ不具合は、純ロジックとして切り出してあればテストで捕捉できる種類のもの
