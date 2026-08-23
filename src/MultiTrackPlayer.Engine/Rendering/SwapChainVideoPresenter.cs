using System.Runtime.InteropServices;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MultiTrackPlayer.Engine.Rendering;

/// <summary><see cref="SwapChainVideoPresenter.TryPresent"/> の結果。</summary>
public enum PresentOutcome
{
    /// <summary>提示できた（単発の失敗を継続扱いにした場合も含む）。</summary>
    Presented,
    /// <summary>デバイスが失われた。作り直さない限り以後の提示はできない。</summary>
    DeviceLost,
    /// <summary>デバイス喪失以外の失敗が続いた。提示を畳むべき。</summary>
    RepeatedFailure,
}

/// <summary>
/// 映像子ウィンドウ（HWND）に D3D11 スワップチェーンを張り、GPU リングのスロットテクスチャを
/// バックバッファへコピーして vsync Present する（案Y・段階1）。D3DImage / DWM 合成を経由しないため、
/// 提示レートが vsync（frame latency waitable object）で安定し、CompositionTarget.Rendering のジッタに
/// 起因するフレーム間引きが原理的に消える。
///
/// バックバッファは映像サイズで確保し、ウィンドウへの表示は <see cref="Scaling.Stretch"/> に委ねる。
/// アスペクト維持のレターボックスは swapchain 側では行わず、映像子ウィンドウ（<c>VideoHost</c>）自体を
/// 映像アスペクト比の矩形へリサイズ・中央配置する方式（<c>MainWindow.ComputeLetterbox</c>）で対応済み。
///
/// スレッド契約: 生成は所有スレッド（MediaEngine）から。<see cref="WaitForVBlank"/>・<see cref="Render"/>・
/// <see cref="ClearBackBuffer"/>・<see cref="TryPresent"/> は vout スレッドから呼ぶ。
/// <see cref="Dispose"/> も <b>vout スレッド自身</b>が自分の finally で呼ぶ（停止待ちがタイムアウトしても
/// メインから破棄しないための設計。<c>MediaEngine.StopVideoOutput</c> 参照）。D3D11 デバイスは
/// <see cref="GpuDeviceContext"/>（SetMultithreadProtected(true)）を共有する。
/// </summary>
public sealed class SwapChainVideoPresenter : IDisposable
{
    private const int BufferCount = 2;

    // DXGI のデバイスロスト HRESULT。Vortice の列挙名に依存せず、Microsoft のドキュメント記載値を直接持つ。
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    // バックバッファ用 RenderTargetView のキャッシュ上限。FlipDiscard + BufferCount=2 なので
    // 実際に現れるバックバッファは 2 枚。DXGI が同じ COM オブジェクトを返す保証は契約上ないため、
    // 想定外に増えたら捨てて作り直す（キャッシュが無制限に育つのを防ぐ）
    private const int MaxCachedClearViews = 4;

    private const uint WaitFailed = 0xFFFFFFFF;

    private readonly GpuDeviceContext _gpu;
    private IDXGISwapChain2 _swapChain;
    // FrameLatencyWaitableObject は呼び出し側が CloseHandle する責任を持つ（swapChain の Dispose では閉じない）
    private IntPtr _waitableHandle;
    // 連続失敗が始まった時刻（0 なら失敗していない）。Environment.TickCount64 基準
    private long _presentFailureSinceTicks;
    private bool _presentFailureLogged;
    private bool _waitFailureLogged;
    private bool _clearViewCacheOverflowLogged;
    // 黒塗り用の RenderTargetView。バックバッファのポインタをキーに使い回す（毎 vsync 生成しない）
    private readonly Dictionary<IntPtr, ID3D11RenderTargetView> _clearViews = new();

    public SwapChainVideoPresenter(GpuDeviceContext gpu, IntPtr hwnd, int videoWidth, int videoHeight)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));

        using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)videoWidth,
            Height = (uint)videoHeight,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };

        using var swapChain1 = factory.CreateSwapChainForHwnd(_gpu.Device, hwnd, desc);
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain2>();

        // 1フレームだけ先行させる（低レイテンシ）。waitable object で vsync ごとに 1 回だけ起床する。
        _swapChain.MaximumFrameLatency = 1;
        _waitableHandle = _swapChain.FrameLatencyWaitableObject;

        // DXGI 既定の Alt+Enter フルスクリーン遷移は無効化（フルスクリーンは WPF 側で制御する）。
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        DiagnosticLog.Write("d3dPresenter", $"swapchain 生成 {videoWidth}x{videoHeight}");
    }

    /// <summary>
    /// 次の提示可能タイミング（vsync）まで待つ。UI 合成に依存しない安定した提示ペースを得るための待機点。
    /// </summary>
    /// <returns>
    /// 待機が成立した場合 true。待機オブジェクトが壊れて待てなくなった場合 false
    /// （そのまま回すと無待機のビジーループになるため、呼び出し側は畳むこと）。
    /// </returns>
    public bool TryWaitForVBlank()
    {
        if (_waitableHandle == IntPtr.Zero) return true;

        uint waitResult = WaitForSingleObjectEx(_waitableHandle, 1000, false);
        // WAIT_TIMEOUT はここでは異常扱いにしない。ウィンドウ最小化や合成状態の変化で
        // 信号が来なくなることは正常に起こりうるため、これで映像出力を畳むと新たな不具合になる。
        // 提示が本当に壊れているかどうかは TryPresent 側が経過時間で判定する
        if (waitResult != WaitFailed) return true;

        // WAIT_FAILED を素通りさせると待機なしでループが回り、CPU を 1 コア占有しながら
        // 誰にも気づかれない状態になる（記録も残らない）
        if (!_waitFailureLogged)
        {
            _waitFailureLogged = true;
            DiagnosticLog.WriteFatal("d3dPresenter",
                $"vsync 待機に失敗（映像提示を停止） Win32 error={Marshal.GetLastWin32Error()}");
        }
        return false;
    }

    /// <summary>リングのスロットテクスチャ（BGRA・映像サイズ）をバックバッファへコピーする。</summary>
    public void Render(GpuVideoFrameRing ring, int slotIndex)
    {
        ID3D11Texture2D? src = ring.GetSlotTexture(slotIndex);
        if (src == null) return;

        // FLIP モデルでは Present ごとにバックバッファが入れ替わるため、コピー直前に index 0 を取得する。
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _gpu.ImmediateContext.CopyResource(backBuffer, src);
    }

    /// <summary>
    /// バックバッファを黒で塗る。まだ 1 枚も映像フレームが来ていない間に使う。
    ///
    /// <para>
    /// frame latency waitable object は「待機と Present が 1:1」でないと枯渇してブロックするため、
    /// 表示すべきフレームが無い vsync でも <see cref="TryPresent"/> は呼ばなければならない。
    /// その間 <see cref="Render"/> を省くと、FlipDiscard の未初期化バックバッファ（前回 Present で
    /// 内容が破棄された領域）をそのまま提示してしまう。ファイルを開いた直後の
    /// 最初の due フレームまで必ずこの状態を通るため、黒で塗り潰して隠す。
    /// </para>
    /// </summary>
    public void ClearBackBuffer()
    {
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _gpu.ImmediateContext.ClearRenderTargetView(
            GetOrCreateClearView(backBuffer), new Color4(0.0f, 0.0f, 0.0f, 1.0f));
    }

    /// <summary>
    /// バックバッファ用の RenderTargetView を取得する（初回のみ生成してキャッシュ）。
    /// 表示すべきフレームが無い状態は「ファイルを開いた直後」だけでなく、再生直後に即一時停止した場合や
    /// 一時停止中シークが時間内に着地できなかった場合にも持続する。その間ずっと黒塗りが必要になるため、
    /// 毎 vsync の生成・破棄を避けて使い回す。
    /// RenderTargetView 自身がテクスチャへの参照を持つので、引数の <paramref name="backBuffer"/> を
    /// 呼び出し側が解放してもキャッシュしたビューは有効なまま。
    /// </summary>
    private ID3D11RenderTargetView GetOrCreateClearView(ID3D11Texture2D backBuffer)
    {
        IntPtr key = backBuffer.NativePointer;
        if (_clearViews.TryGetValue(key, out ID3D11RenderTargetView? cached)) return cached;

        // 想定より増えた（DXGI がバックバッファごとに別の COM オブジェクトを返し続ける環境）場合は
        // 溜め込まずに捨てる。使い回せなくなるだけで、動作は毎回生成していた頃と同じ。
        // ただし黙って捨てると「キャッシュが効かず毎 vsync 生成している」ことに気づけないので記録する
        if (_clearViews.Count >= MaxCachedClearViews)
        {
            DisposeClearViews();
            if (!_clearViewCacheOverflowLogged)
            {
                _clearViewCacheOverflowLogged = true;
                DiagnosticLog.Write("d3dPresenter",
                    $"黒塗り用ビューのキャッシュが上限（{MaxCachedClearViews}）に達したため破棄" +
                    "（以降は毎回生成する。この行は記録しない）");
            }
        }

        ID3D11RenderTargetView view = _gpu.Device.CreateRenderTargetView(backBuffer);
        _clearViews[key] = view;
        return view;
    }

    private void DisposeClearViews()
    {
        foreach (ID3D11RenderTargetView view in _clearViews.Values) view.Dispose();
        _clearViews.Clear();
    }

    /// <summary>
    /// vsync 同期で提示する。戻り値を検査しないと、TDR（GPU タイムアウト検出・復旧）やドライバ更新の後に
    /// Present が延々とエラーを返し続け、音声だけが流れて映像が二度と出ない状態になる。
    /// </summary>
    public PresentOutcome TryPresent()
    {
        // 成功でも DXGI_STATUS_OCCLUDED（ウィンドウが隠れている）のような情報コードが返るため、
        // 失敗（負値）だけを見る
        var result = _swapChain.Present(1, PresentFlags.None);
        if (result.Success)
        {
            _presentFailureSinceTicks = 0;
            return PresentOutcome.Presented;
        }

        if (result.Code == DxgiErrorDeviceRemoved || result.Code == DxgiErrorDeviceReset)
        {
            // 既定でも記録する。この経路を無記録にすると「音は出るのに映像だけ消えた」原因不明の不具合になる
            DiagnosticLog.WriteFatal("d3dPresenter",
                $"映像デバイスが失われた（Present HRESULT=0x{result.Code:X8} reason={DescribeDeviceRemovedReason()}）");
            return PresentOutcome.DeviceLost;
        }

        // デバイス喪失以外の失敗。単発なら次の vsync で回復しうるので継続するが、
        // 猶予時間を超えて続くなら畳んでユーザーへ伝える（黙って 1fps へ劣化させない）
        long now = Environment.TickCount64;
        if (_presentFailureSinceTicks == 0) _presentFailureSinceTicks = now;
        long elapsedMs = now - _presentFailureSinceTicks;

        if (elapsedMs < VideoOutputPolicy.FailureGraceMs)
        {
            // 記録は 1 回だけ。WriteFatal はプロセス間ミューテックス待ちで vout スレッドを
            // 数百ms 止めるため、毎 vsync 呼ぶと停止の応答性まで悪化する
            if (!_presentFailureLogged)
            {
                _presentFailureLogged = true;
                DiagnosticLog.Write("d3dPresenter",
                    $"Present が失敗 HRESULT=0x{result.Code:X8}" +
                    $"（{VideoOutputPolicy.FailureGraceMs}ms 継続したら停止する）");
            }
            return PresentOutcome.Presented;
        }

        DiagnosticLog.WriteFatal("d3dPresenter",
            $"Present の失敗が {elapsedMs}ms 続いたため映像提示を停止 HRESULT=0x{result.Code:X8}");
        return PresentOutcome.RepeatedFailure;
    }

    /// <summary>
    /// 共有 D3D11 デバイスが失われているか。<see cref="Render"/> や <see cref="ClearBackBuffer"/> が
    /// 例外を投げた場合に、その原因がデバイス喪失なのか別の不具合なのかを切り分けるために使う
    /// （切り分けずにデバイス喪失と断定すると、ユーザーと開発者の両方を誤った原因へ誘導する）。
    /// </summary>
    public bool IsDeviceLost
    {
        get
        {
            try
            {
                return _gpu.Device.DeviceRemovedReason.Failure;
            }
            catch (Exception ex)
            {
                // この getter が呼ばれるのは「提示中に例外が飛んだ直後」＝デバイスの参照が
                // 不安定になっていて当然の場面。ここを無言で false にすると、実際は
                // デバイス喪失なのに「想定外のエラー」という誤った案内が出て、
                // ユーザーがドライバ更新・再起動という正しい対処へ辿り着けない
                DiagnosticLog.WriteFatal("d3dPresenter",
                    $"デバイス喪失の判定に失敗（喪失していないものとして扱う）: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>デバイス喪失の理由コードを診断用に文字列化する（取得自体が失敗しても記録を止めない）。</summary>
    private string DescribeDeviceRemovedReason()
    {
        try
        {
            return $"0x{_gpu.Device.DeviceRemovedReason.Code:X8}";
        }
        catch (Exception ex)
        {
            return $"取得不能（{ex.GetType().Name}）";
        }
    }

    public void Dispose()
    {
        DisposeClearViews();
        _swapChain?.Dispose();
        _swapChain = null!;
        // Microsoft のドキュメントが CloseHandle を明示的に要求している。閉じないと
        // ファイルを開くたびに 1 ハンドル漏れ、プロセス寿命で積み上がる
        if (_waitableHandle != IntPtr.Zero)
        {
            if (!CloseHandle(_waitableHandle))
                DiagnosticLog.Write("d3dPresenter",
                    $"vsync 待機ハンドルの解放に失敗 Win32 error={Marshal.GetLastWin32Error()}");
            _waitableHandle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObjectEx(IntPtr hHandle, uint dwMilliseconds, bool bAlertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
