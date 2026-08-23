using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.Engine.Thumbnails;
using MultiTrackPlayer.UI.Settings;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string LogDirectory =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "MultiTrackPlayer", "logs");

    public MediaEngine Engine { get; } = new MediaEngine();
    public PlaylistViewModel Playlist { get; } = new PlaylistViewModel();
    public ThumbnailCacheService Thumbnails { get; } = new ThumbnailCacheService();
    public ObservableCollection<AudioTrackViewModel> AudioTracks { get; } = new();
    public ObservableCollection<ChapterViewModel> Chapters { get; } = new();
    public AppSettings Settings { get; } = AppSettings.Load();

    [ObservableProperty] private PlaybackState _playbackState = PlaybackState.Stopped;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private MediaInfo? _currentMedia;
    [ObservableProperty] private ThumbnailSheet? _thumbnailSheet;
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private double _masterVolume = 80.0;
    [ObservableProperty] private string _title = "MultiTrackPlayer";
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isDebugMode;
    [ObservableProperty] private bool _isMasterMuted;
    [ObservableProperty] private string _osdText = string.Empty;

    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private int _currentChapterIndex = -1;
    // Dispose 後に Dispatcher へ積み残された継続（連続再生の次ファイル送り等）が
    // 破棄済みの Engine を触らないようにするためのガード
    private bool _isDisposed;
    // ファイルを開くたびに進む世代番号。EOF 検出から UI スレッドでの処理までに
    // ユーザーが手動でファイルを切り替えた場合に、古い継続を無効化するために使う
    private int _sessionGeneration;

    public MainViewModel()
    {
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); OsdText = string.Empty; };
        Engine.PositionChanged += (_, pos) =>
        {
            Position = pos;
            if (Duration > TimeSpan.Zero)
                PositionRatio = pos.TotalSeconds / Duration.TotalSeconds;
            UpdateCurrentChapterHighlight(pos);
        };
        // 再生状態はエンジンが唯一の情報源。ここで受けて表示用プロパティへ反映するだけにし、
        // ViewModel 側から直接書き換えないことで「表示は再生中なのに実際は止まっている」類の
        // 食い違いを構造的になくす。状態タイマー（スレッドプール）からも発火するため UI スレッドへ移す
        // 通知の引数ではなく、継続が走る時点の実状態を読み直す。SetState は状態の書き込みを
        // ロック内、通知をロック外で行うため、2 スレッドがほぼ同時に遷移させると
        // 「書き込み順」と「通知順」がずれうる。実値を読めば最終的に必ず正しい値へ収束する
        Engine.StateChanged += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(() => PlaybackState = Engine.State);
        Engine.PlaybackEnded += (_, _) => OnPlaybackEnded();
        // 音声出力が異常停止すると、音が消えるだけでなく audio-master クロックも止まるため
        // 映像まで停止する。原因が分からないままフリーズしたように見えるので必ず画面に出す
        Engine.PlaybackFailed += (_, message) =>
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // ウィンドウを閉じた後に積み残された継続で、破棄済みの OSD タイマーを
                // 再始動させない（OnPlaybackEnded と同じ流儀）
                if (_isDisposed) return;
                ShowOsd(message);
            });
        Thumbnails.ThumbnailsReady += (_, sheet) =>
            Application.Current.Dispatcher.Invoke(() => ThumbnailSheet = sheet);
        Engine.StatisticsUpdated += (_, stats) =>
        {
            int total = stats.DroppedFrames + stats.DisplayedFrames;
            double dropRate = total > 0 ? stats.DroppedFrames * 100.0 / total : 0.0;
            StatusText = $"表示 {stats.DisplayedFrames} / ドロップ {stats.DroppedFrames} ({dropRate:F1}%)  映像遅延 {stats.VideoLagSec * 1000:F0}ms";
        };

        // 読み込んだ値はプロパティ経由で入れない。OnIsDebugModeChanged が走って、いま読んだ値を
        // そのまま書き戻すことになる（無駄な書き込みであり、書き込みが失敗する環境では
        // ウィンドウが出る前に保存失敗の案内を出して、誰にも見られないまま消える）。
        // バインドはまだ張られていないので、ここは変更通知を出さなくてよい
        _isDebugMode = Settings.DebugMode;
        if (_isDebugMode) DiagnosticLog.Enable(LogDirectory);
    }

    partial void OnIsDebugModeChanged(bool value)
    {
        if (value) DiagnosticLog.Enable(LogDirectory);
        else DiagnosticLog.Disable();
        Settings.DebugMode = value;
        // 保存できなくても切り替え自体は効いている（次回起動時に元へ戻るだけ）ので、
        // 操作を巻き戻さずに知らせるだけにする
        if (!Settings.Save()) ShowOsd("設定を保存できませんでした");
    }

    /// <summary>現在の各トラックのミュート状態を、このファイルが置かれたフォルダの既定値として保存する。</summary>
    public void SaveCurrentMutesAsDefault()
    {
        // メニューはファイル未読み込みでも選べる。黙って何もしないと故障と区別できない
        if (CurrentMedia == null)
        {
            ShowOsd("ファイルを開いてから実行してください");
            return;
        }

        string directory = System.IO.Path.GetDirectoryName(CurrentMedia.FilePath) ?? string.Empty;
        var mutedTracks = AudioTracks.Where(t => t.IsMuted).Select(t => t.TrackNumber).ToList();
        bool hadPrevious = Settings.DefaultMutedTracksByDirectory.TryGetValue(directory, out var previous);
        Settings.DefaultMutedTracksByDirectory[directory] = mutedTracks;
        // 成否を見ずに「保存した」と記録すると、失敗したときログが嘘をつく
        if (!Settings.Save())
        {
            // 「保存できませんでした」と伝えるなら、メモリ上も元へ戻す。反映したままだと
            // 同じフォルダの別ファイルを開いたときに、保存できなかった値が効いてしまう
            if (hadPrevious) Settings.DefaultMutedTracksByDirectory[directory] = previous!;
            else Settings.DefaultMutedTracksByDirectory.Remove(directory);
            ShowOsd("既定ミュートを保存できませんでした");
            return;
        }
        DiagnosticLog.Write("ui", $"既定ミュート保存 dir={directory} tracks=[{string.Join(",", mutedTracks)}]");
        ShowOsd("既定ミュートを保存しました");
    }

    /// <summary>ファイルを開き直すまで解除されない縮退の種類。</summary>
    private enum ReopenRequiredCause
    {
        /// <summary>縮退していない。</summary>
        None,
        /// <summary>パイプラインのスレッドが停止しきらず、資源を解放できないまま取り残された。</summary>
        PipelineQuarantined,
        /// <summary>音声出力（WASAPI）が異常停止した。</summary>
        AudioOutputFailed
    }

    /// <summary>
    /// ファイルを開き直すまで解除されない縮退の有無と種類。**検出はここだけで行う。**
    /// </summary>
    /// <remarks>
    /// 検疫中は、停止時に仕掛けたミキサー出力の保留（消音）が解けないまま <c>Play()</c> も拒否される。
    /// 音声出力が死んでいる場合も音は出ず、位置クロックが音声出力基準のため再生も進まない。
    /// どちらも <c>Open()</c> でのみ解除される。
    /// この 2 つ以外の「一時的に音が出ない状態」を混ぜないこと。シーク中のミキサー出力の保留は
    /// すぐ解けるので、それを理由に案内すると正常な操作へ誤った警告を出すことになる。
    /// 文面は種類ごとに場面で決める（停止直後と操作時では言うべきことが違う。<see cref="Stop"/> と
    /// <see cref="ReopenRequiredMessage"/> を参照）。ここで文面まで共有すると、後始末が完了している
    /// 音声出力の失敗に対して「後始末を完了できませんでした」と言うような嘘になる。
    /// </remarks>
    private ReopenRequiredCause ReopenRequired =>
        Engine.IsPipelineQuarantined ? ReopenRequiredCause.PipelineQuarantined
        : Engine.IsAudioOutputFailed ? ReopenRequiredCause.AudioOutputFailed
        : ReopenRequiredCause.None;

    /// <summary>操作しても音が変わらない・位置が進まない理由。正常なら null。
    /// 再生・シーク・チャプター移動・ミキサー操作で共通の文面を使う
    /// （どれも復旧手段は開き直すことなので、場面ごとに言い換えても伝わる内容が増えない）。</summary>
    private string? ReopenRequiredMessage => ReopenRequired switch
    {
        ReopenRequiredCause.PipelineQuarantined => "前回の停止処理が完了していません。ファイルを開き直してください",
        ReopenRequiredCause.AudioOutputFailed => "音声出力が停止しています。ファイルを開き直してください",
        _ => null
    };

    /// <summary>
    /// 縮退のため操作が効かない場合に、理由を利用者へ伝える。
    /// </summary>
    /// <remarks>
    /// <para>
    /// エンジンは縮退中の要求を例外ではなく黙って捨てる。<c>Seek</c> は demux スレッドが無ければ
    /// 何もせず戻る。<c>SetTrackVolume</c>/<c>SetTrackMute</c> は書き込み自体は成功するが、
    /// 検疫時の停止が仕掛けたミキサー出力の保留（消音）と音声出力の停止のせいで聴感上は何も
    /// 起きない。どちらも <see cref="InvokeEngine"/> は成功と判定するため、放っておくと操作が
    /// 無言で消える。
    /// </para>
    /// <para>
    /// **UI からエンジンを呼ぶ経路を新設したら、ここを通すか否かを必ず判断すること。**
    /// 現在通しているのは 再生 / シーク / チャプター移動 / 再生速度 / トラック音量 /
    /// ミュート・ソロ / マスター音量 / マスターミュート。停止は文面が違うため
    /// <see cref="Stop"/> が自前で案内する。
    /// </para>
    /// <para>OSD は数秒で消えるため、後から報告と突き合わせられるよう操作名をログにも残す。</para>
    /// <para>
    /// 音声出力の失敗に対する免除（<paramref name="worksWhilePaused"/>）を
    /// <see cref="PlaybackState.Paused"/> だけに限る理由。一時停止は「パイプラインが生きていて
    /// （再生中と同じ）、かつ位置クロックの停止に足を取られない（再生中と違う）」状態であり、
    /// シークが着地フレームを見せられると言い切れるのはここだけ。再生中を免除しないのは、
    /// 位置クロックが音声出力基準で止まっていてシークしても位置も映像も進まないため。
    /// </para>
    /// <para>
    /// 「再生中でなければ」と広げてはいけない。<see cref="PlaybackState.Stopped"/> は
    /// EOF 到達（パイプラインは生きている）と明示的な停止（畳み済み）の両方を指し、
    /// <see cref="PlaybackState"/> からは区別できない。畳んだ後にシークを通すと
    /// <c>MediaEngine.Seek</c> が demux スレッド不在で要求を捨て、この案内が塞ごうとしている
    /// 「無言で消える」に戻る（実際にそう書いて指摘された）。
    /// **既知のトレードオフ**: その代償として、EOF 到達で停止した直後に音声出力が死んでいる場合は、
    /// 実際には動くシークまで案内で止める。区別するにはエンジン側でパイプラインの生存を
    /// 公開する必要があり、ここだけでは直せない。緩める方向へ直すなら上記の事故が再発しないことを
    /// 先に確かめること。検疫中はパイプラインそのものが無いので、免除は一切効かせない。
    /// </para>
    /// </remarks>
    /// <param name="worksWhilePaused">
    /// 一時停止中なら、音声出力が死んでいても意味のある動作になる操作か（シーク・チャプター移動）。
    /// true なら音声出力の失敗については一時停止中に限って案内を省く。パイプラインが生きているので
    /// シーク要求は捨てられず、着地フレームを表示できる（映像側のプリロールは音声の消費に依存しない）。
    /// 一律に止めると動く操作を塞ぐことになる。
    /// </param>
    /// <returns>縮退していて案内した場合 true（呼び出し側は操作を中止してよい）。</returns>
    private bool NotifyIfReopenRequired(string operationName, bool worksWhilePaused = false)
    {
        var cause = ReopenRequired;
        if (cause == ReopenRequiredCause.None) return false;
        if (cause == ReopenRequiredCause.AudioOutputFailed && worksWhilePaused
            && Engine.State == PlaybackState.Paused)
            return false;
        if (ReopenRequiredMessage is not string reason) return false;
        DiagnosticLog.Write("ui", $"{operationName} は縮退中のため効かない: {reason}");
        ShowOsd(reason);
        return true;
    }

    private void OnAudioTrackMuteOrSoloChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AudioTrackViewModel.IsMuted) && e.PropertyName != nameof(AudioTrackViewModel.IsSolo))
            return;
        UpdateSoloMuting();
        // 縮退中は反映しても音が変わらない。無言だと故障と区別できないので理由を伝える。
        // 案内をここに置くのは、UpdateSoloMuting がファイルを開くときにも呼ばれるため
        // （利用者の操作に限って出す）。
        // UpdateSoloMuting が失敗を通知していてもこちらで上書きする。「N 件失敗」だけでは
        // 次に何をすればよいか分からないため、復旧手段の案内を優先する
        NotifyIfReopenRequired("トラックミュート切替");
    }

    /// <summary>いずれかのトラックがソロ中なら、ソロ対象以外を実効的にミュートしてエンジンへ反映する。</summary>
    private void UpdateSoloMuting()
    {
        bool anySolo = AudioTracks.Any(t => t.IsSolo);
        // 失敗ごとに OSD を出すとトラック数ぶん上書きし合うため、件数だけまとめて 1 回知らせる
        int failed = 0;
        foreach (var t in AudioTracks)
        {
            if (!InvokeEngine("ミュート切替", () => Engine.SetTrackMute(t.TrackNumber, anySolo ? !t.IsSolo : t.IsMuted), notifyUser: false))
                failed++;
        }
        if (failed > 0) ShowOsd($"ミュート切替に {failed} 件失敗しました");
    }

    [ObservableProperty] private double _positionRatio;

    // PositionRatio は再生位置の表示専用。ここからシークを起こすと、シークバー操作 1 回につき
    // 「バインド経由」と「Seeking イベント経由」で 2 回シークが走る（過去に UI が固まる原因になった）。
    // シーク要求は SeekBarControl.Seeking → SeekTo(TimeSpan) の経路に一本化している

    partial void OnMasterVolumeChanged(double value)
    {
        // スライダー操作でマスター音量を変えたら、ミュート状態と実際に聞こえる音を一致させるため自動的に解除する
        if (IsMasterMuted)
        {
            // セッターが同期的に OnIsMasterMutedChanged を呼び、Engine への反映と OSD 表示まで
            // そちらで完結する。ここで続けて OSD を出すと、失敗表示を上書きしてしまう
            IsMasterMuted = false;
            return;
        }
        if (!InvokeEngine("音量変更", () => Engine.SetMasterVolume((float)(value / 100.0)))) return;
        // 縮退中に「音量 80%」と出すと、操作が効いたように見えて実際には何も聞こえない
        if (!NotifyIfReopenRequired("音量変更")) ShowOsd($"音量 {value:0}%");
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        if (!InvokeEngine("ミュート切替", () => Engine.SetMasterVolume(value ? 0f : (float)(MasterVolume / 100.0)))) return;
        DiagnosticLog.Write("ui", $"マスターミュート切替 muted={value}");
        if (!NotifyIfReopenRequired("マスターミュート切替")) ShowOsd(value ? "ミュート" : "ミュート解除");
    }

    [RelayCommand]
    private void ToggleMute() => IsMasterMuted = !IsMasterMuted;

    /// <summary>
    /// 描画側から報告された映像の不具合を OSD へ出す。<c>Engine.PlaybackFailed</c> の購読と同じく、
    /// ウィンドウを閉じた後に積み残された継続で破棄済みの OSD タイマーを再始動させないよう守る。
    /// </summary>
    public void ReportVideoFailure(object? sender, string message) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_isDisposed) return;
            ShowOsd(message);
        });

    /// <summary>操作内容を一瞬だけ画面に表示する（何をしたか分かりにくいという声を受けて追加）。</summary>
    public void ShowOsd(string text)
    {
        OsdText = text;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    /// <summary>単一ファイルを開く操作（ファイルを開くダイアログ・単一ファイルのドラッグ&amp;ドロップ）の入口。
    /// 同じフォルダ内の対応拡張子ファイルを自動的にプレイリストへ読み込むことで、
    /// 明示的にプレイリストを組んでいなくても次の動画へ自動的に進めるようにする。</summary>
    public void OpenFileWithFolderPlaylist(string path)
    {
        string directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        var siblings = System.IO.Directory.Exists(directory)
            ? System.IO.Directory.EnumerateFiles(directory)
                .Where(f => SupportedVideoExtensions.Extensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        Playlist.Clear();
        Playlist.AddFiles(siblings.Count > 0 ? siblings : new[] { path });
        // 列挙結果に開くファイル自身が含まれないことがある（パス表記の違い等）。
        // 含まれないままだとプレイリスト内の位置が確定せず、自動送りの基準が狂う
        if (!Playlist.Files.Contains(path)) Playlist.AddFiles(new[] { path });
        OpenFile(path);
    }

    /// <summary>アプリ未起動の状態から動画ファイルを1件開いて起動する場合の入口。
    /// 同じフォルダの他ファイルは自動追加せず、渡された1件だけのプレイリストにする。</summary>
    public void OpenSingleFile(string path)
    {
        Playlist.Clear();
        Playlist.AddFiles(new[] { path });
        OpenFile(path);
    }

    /// <summary>
    /// ファイルを開いて再生を開始する。破損ファイル・非対応形式・フォルダの誤指定などで
    /// 開けなかった場合は、開きかけの状態を閉じて表示を「何も開いていない」状態へ戻し
    /// （<see cref="ResetMediaState"/>）、ユーザーへ通知して false を返す。
    /// </summary>
    /// <returns>開けた場合 true。</returns>
    public bool OpenFile(string path)
    {
        if (_isDisposed) return false;
        DiagnosticLog.Write("open", $"OpenFile path={System.IO.Path.GetFileName(path)}");
        // 現在地は開く前に移す。開けなかった場合もここに残ることで、「次へ」を押し直したときに
        // 同じファイルを再試行し続けない。ファイルを開く経路はすべてここを通るため、
        // 経路ごとに現在地を動かすことを覚えておく必要がない
        Playlist.SetCursor(path);
        try
        {
            OpenFileCore(path);
            return true;
        }
        catch (Exception ex)
        {
            // ここで受け止めないと、8 つあるファイルオープン経路のいずれからでも
            // 未処理例外としてアプリ全体が落ちる（フォルダを D&D しただけでも起きる）。
            // Engine.Open 自体は自分で巻き戻すが、その後の Play() が失敗した場合は
            // ファイルを開いたままパイプラインだけ止まった状態が残るため明示的に閉じる。
            // 放置すると「画面は未読み込みなのに再生ボタンで前のファイルが鳴り出す」ことになる
            try { Engine.Close(); }
            catch (Exception closeEx) { DiagnosticLog.WriteFatal("error", $"オープン失敗後の後始末で二次例外: {closeEx}"); }
            ResetMediaState();
            DiagnosticLog.WriteFatal("error", $"ファイルを開けなかった path={path}: {ex}");
            MessageBox.Show(
                $"ファイルを開けませんでした。\n\n{System.IO.Path.GetFileName(path)}\n\n{ex.Message}",
                "MultiTrackPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// 画面に出しているメディア情報を「何も開いていない」状態へ戻す。
    /// Engine 側は Open/Play の失敗時に自分で巻き戻すため、こちらは表示状態だけを扱う。
    /// </summary>
    private void ResetMediaState()
    {
        CurrentMedia = null;
        Duration = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        Title = "MultiTrackPlayer";
        ThumbnailSheet = null;
        AudioTracks.Clear();
        Chapters.Clear();
        // PositionRatio は表示専用（値の変更からシークは起こらない）。
        // ここを省くとシークバーのつまみだけ前のファイルの位置に残る
        PositionRatio = 0.0;
    }

    /// <summary>
    /// Engine の操作を実行し、例外はユーザーへ通知したうえで握る。
    /// App は DispatcherUnhandledException で e.Handled を立てない方針のため、
    /// UI スレッドから Engine を直接呼ぶ経路でここを通さないと、操作 1 つでアプリ全体が落ちる。
    /// </summary>
    /// <returns>操作が成功した場合 true。</returns>
    /// <param name="notifyUser">false にすると OSD を出さない（呼び出し側でまとめて通知したい場合に使う）。</param>
    private bool InvokeEngine(string operationName, Action action, bool notifyUser = true)
    {
        if (_isDisposed) return false;
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFatal("engine", $"{operationName} に失敗: {ex}");
            if (notifyUser) ShowOsd($"{operationName}に失敗しました");
            return false;
        }
    }

    // UI から Engine を呼ぶ経路はすべてここへ集約する。View 側から Engine を直接叩くと、
    // 例外が未処理のまま Dispatcher へ抜けてアプリ全体が落ちる（App は e.Handled を立てない方針）。
    // JumpTo 系・StepBackward は内部で Seek を呼ぶため、シークと同じ失敗経路を持つ点に注意

    /// <summary>指定位置へシークする。UI からのシーク経路はすべてここを通すこと。</summary>
    /// <returns>シークを要求できた場合 true。縮退中は要求せず false を返す。</returns>
    public bool SeekTo(TimeSpan position)
    {
        if (NotifyIfReopenRequired("シーク", worksWhilePaused: true))
        {
            // シークバーのつまみは指の位置へ楽観的に動いている（SeekBarControl.SeekTo）。
            // 縮退中は位置の通知が二度と来ないため訂正されず、動いたように見えたまま残る
            RestorePositionRatio();
            return false;
        }
        return InvokeEngine("シーク", () => Engine.Seek(position));
    }

    /// <summary>シークバーのつまみを現在位置から描き直す。位置の通知が来ない状況で、
    /// 楽観的に動かした表示を戻すために使う。尺が不明なときに何もしないのも含め、
    /// 算出はコンストラクタの位置通知の購読と同じにそろえる（片方だけ 0 へ倒すと、
    /// 同じ状況でつまみの行き先が経路によって変わる）。</summary>
    private void RestorePositionRatio()
    {
        if (Duration > TimeSpan.Zero) PositionRatio = Position.TotalSeconds / Duration.TotalSeconds;
    }

    /// <summary>トラック個別の音量を反映する。正常時は OSD を出さない（スライダー操作のたびに
    /// 出すと連続して上書きし合う）が、縮退中は音が変わらない理由を伝える。</summary>
    public void SetTrackVolume(int trackNumber, float volume)
    {
        if (!InvokeEngine("音量変更", () => Engine.SetTrackVolume(trackNumber, volume))) return;
        NotifyIfReopenRequired("トラック音量変更");
    }

    public void AttachVideoOutput(IntPtr hwnd)
        => InvokeEngine("映像出力の接続", () => Engine.AttachVideoOutput(hwnd));

    // チャプター移動は内部で Seek を呼ぶため、縮退中は同じく無言で消える。
    // 呼び出し側は戻り値を見て「次のチャプター」等の OSD を出すので、ここで false を返さないと
    // 移動していないのに移動したように見える

    public bool JumpToNextChapter()
        => !NotifyIfReopenRequired("次のチャプターへ移動", worksWhilePaused: true)
           && InvokeEngine("次のチャプターへ移動", Engine.JumpToNextChapter);

    public bool JumpToPreviousChapter()
        => !NotifyIfReopenRequired("前のチャプターへ移動", worksWhilePaused: true)
           && InvokeEngine("前のチャプターへ移動", Engine.JumpToPreviousChapter);

    public bool JumpToChapter(int index)
        => !NotifyIfReopenRequired("チャプターへ移動", worksWhilePaused: true)
           && InvokeEngine("チャプターへ移動", () => Engine.JumpToChapter(index));

    public bool RemoveUserChapter(ChapterInfo chapter)
        => InvokeEngine("チャプター削除", () => Engine.RemoveUserChapter(chapter));

    private void OpenFileCore(string path)
    {
        _sessionGeneration++;
        Engine.Open(path);
        var info = Engine.CurrentMedia!;
        CurrentMedia = info;
        Duration = info.Duration;
        Title = System.IO.Path.GetFileName(path) + " - MultiTrackPlayer";

        ThumbnailSheet = null;
        Thumbnails.RequestForFile(path, info.Duration, info.Width, info.Height);

        string directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        bool hasSavedDefault = Settings.DefaultMutedTracksByDirectory.TryGetValue(directory, out var mutedTracks);

        AudioTracks.Clear();
        foreach (var track in info.AudioTracks)
        {
            var trackVm = new AudioTrackViewModel(track, (n, v) => SetTrackVolume(n, v));
            // このフォルダに保存済みの既定ミュートがあればそれを、無ければトラック1のみ再生する既定値を適用する
            trackVm.IsMuted = hasSavedDefault
                ? mutedTracks!.Contains(trackVm.TrackNumber)
                : trackVm.TrackNumber != 1;
            trackVm.PropertyChanged += OnAudioTrackMuteOrSoloChanged;
            AudioTracks.Add(trackVm);
        }
        UpdateSoloMuting();

        RefreshChapters();
        Engine.Play();
        ShowOsd(System.IO.Path.GetFileName(path));
    }

    public void RefreshChapters()
    {
        Chapters.Clear();
        foreach (var ch in Engine.GetChapters())
            Chapters.Add(new ChapterViewModel(ch));
        _currentChapterIndex = -1;
        UpdateCurrentChapterHighlight(Position);
    }

    /// <summary>
    /// 現在再生位置が属するチャプターの行だけを IsCurrent 切り替えでハイライトする。
    /// PositionChanged は再生中高頻度で発火するため、一覧全体の再構築は避ける。
    /// </summary>
    private void UpdateCurrentChapterHighlight(TimeSpan position)
    {
        int idx = -1;
        for (int i = 0; i < Chapters.Count; i++)
        {
            if (Chapters[i].Chapter.StartTime <= position) idx = i;
            else break;
        }
        if (idx == _currentChapterIndex) return;
        if (_currentChapterIndex >= 0 && _currentChapterIndex < Chapters.Count)
            Chapters[_currentChapterIndex].IsCurrent = false;
        if (idx >= 0)
            Chapters[idx].IsCurrent = true;
        _currentChapterIndex = idx;
    }

    public void RenameChapter(ChapterViewModel chapter, string newTitle)
    {
        if (!chapter.IsUserDefined || string.IsNullOrWhiteSpace(newTitle)) return;
        if (!InvokeEngine("チャプター名の変更", () => Engine.RenameUserChapter(chapter.Chapter, newTitle))) return;
        RefreshChapters();
    }

    [RelayCommand]
    private void PlayPause()
    {
        // 分岐はエンジンの実状態で判断する（表示用プロパティは通知経由で遅れて追従するため）
        if (Engine.State == PlaybackState.Playing)
        {
            if (!InvokeEngine("一時停止", Engine.Pause)) return;
            ShowOsd("一時停止");
        }
        else
        {
            // 縮退中は Play() が必ず失敗する（検疫中はパイプラインの再構築を拒否し、音声出力が
            // 死んでいる場合は位置クロックが進まない）。InvokeEngine の汎用 OSD
            // （「再生に失敗しました」）だけでは原因も復旧手段も伝わらず、「ボタンを押しても
            // 何も起きない」ように見えるため個別に案内する。
            // OSD の一度きりの通知を見逃した場合でも、操作のたびにここで再案内される
            if (NotifyIfReopenRequired("再生")) return;
            // Play() はパイプライン構築に失敗すると（自身を巻き戻したうえで）例外を送出する
            if (!InvokeEngine("再生", Engine.Play)) return;
            ShowOsd("再生");
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (!InvokeEngine("停止", Engine.Stop)) return;
        Position = TimeSpan.Zero;
        // 縮退している場合に「停止」とだけ出すと正常に停止したように見え、気づく手段が
        // 「もう一度再生を押す」以外になくなる。文面は種類ごとに変える。検疫では音声出力・
        // バッファ・クロックの後始末が省略されている（消音のみ）が、音声出力の失敗では
        // 後始末そのものは完了しているため、同じ文言では嘘になる
        ShowOsd(ReopenRequired switch
        {
            ReopenRequiredCause.PipelineQuarantined => "停止（後始末を完了できませんでした。ファイルを開き直してください）",
            ReopenRequiredCause.AudioOutputFailed => "停止（音声出力が停止しています。ファイルを開き直してください）",
            _ => "停止"
        });
    }

    [RelayCommand]
    private void StepForward()
    {
        if (Engine.State != PlaybackState.Paused) return;
        if (!InvokeEngine("コマ送り", Engine.StepForward)) return;
        ShowOsd("コマ送り");
    }

    [RelayCommand]
    private void StepBackward()
    {
        if (Engine.State != PlaybackState.Paused) return;
        if (!InvokeEngine("コマ戻し", Engine.StepBackward)) return;
        ShowOsd("コマ戻し");
    }

    public void Skip(double seconds)
    {
        if (!SeekTo(Position + TimeSpan.FromSeconds(seconds))) return;
        ShowOsd(seconds >= 0 ? $"+{seconds:0}秒" : $"{seconds:0}秒");
    }

    public void ChangeSpeed(double delta)
    {
        PlaybackSpeed = Math.Clamp(PlaybackSpeed + delta, 0.1, 2.0);
        if (!InvokeEngine("速度変更", () => Engine.SetPlaybackSpeed(PlaybackSpeed))) return;
        // Engine.SetPlaybackSpeed は状態を見ずに値を書くだけなので、縮退中でも成功して見える。
        // 音も映像も進んでいない状態で「速度 1.25x」と出すと、効いたように誤解させる
        if (!NotifyIfReopenRequired("速度変更")) ShowOsd($"速度 {PlaybackSpeed:0.00}x");
    }

    public void SetSpeed(double speed)
    {
        PlaybackSpeed = speed;
        if (!InvokeEngine("速度変更", () => Engine.SetPlaybackSpeed(speed))) return;
        if (!NotifyIfReopenRequired("速度変更")) ShowOsd($"速度 {PlaybackSpeed:0.00}x");
    }

    public void ToggleChapterAtCurrentPosition()
    {
        var near = Engine.FindUserChapterNear(Position, TimeSpan.FromSeconds(0.5));
        if (near != null)
        {
            if (!InvokeEngine("チャプター削除", () => Engine.RemoveUserChapter(near))) return;
            ShowOsd("チャプター削除");
        }
        else
        {
            if (!InvokeEngine("チャプター追加", () => Engine.AddUserChapter(new ChapterInfo(0, $"Chapter {Chapters.Count + 1}", Position, true)))) return;
            ShowOsd("チャプター追加");
        }
        RefreshChapters();
    }

    public void PlayNext()
    {
        var next = Playlist.PeekNext();
        if (next != null) OpenFile(next);
    }

    public void PlayPrevious()
    {
        var prev = Playlist.PeekPrevious();
        if (prev != null) OpenFile(prev);
    }

    // PlaybackEnded はスレッドプールのタイマーから発火する。PlaybackState は DebugWindow が
    // バインドしており、UI スレッド以外から更新するとバインディングが例外を投げてプロセスごと落ちる。
    // 次ファイルの決定も UI 操作と同じスレッドで行いたいので、
    // 分岐ごとではなくメソッド全体を UI スレッドへ移す。
    // 積んだ継続はウィンドウが閉じた後に実行されることがあるため _isDisposed で弾く
    private void OnPlaybackEnded()
    {
        // 継続が実行されるまでの間に手動で別のファイルを開かれると、古いセッション由来の
        // この継続が新しい再生の状態を上書きしてしまう（末尾ファイルの終了直後に別ファイルを
        // 開くと、再生中なのに PlaybackState だけ Stopped に巻き戻る）。
        // 発火時点の世代を捕まえておき、変わっていたら何もしない
        int generation = _sessionGeneration;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_isDisposed || generation != _sessionGeneration) return;
            // 自動送りは「意図せず同じ動画が繰り返される」等の問い合わせが出やすい箇所なので、
            // どの位置を基準に何を選んだかを追えるようにしておく
            DiagnosticLog.Write("playlist",
                $"自動送り判定 cursorIndex={Playlist.CursorIndex} fileCount={Playlist.Files.Count} " +
                $"cursorPath={Playlist.CursorPath ?? "(なし)"} hasOrigin={Playlist.HasAdvanceOrigin}");
            // 起点が未確定のまま次送りすると、PeekNext が先頭のファイル
            // （＝いま再生し終えたファイル自身になりうる）を返し、延々と再生を繰り返す。
            // 現在地のファイルをプレイリストから削除した場合は、空いた位置が起点として
            // 残るため、CursorIndex が -1 でも自動送りは続く
            if (!Playlist.HasAdvanceOrigin) return;
            // 次が無い場合（末尾）の停止状態はエンジンが通知するので、ここでは何もしない。
            // 次はあるが開けなかった場合は OpenFile がユーザーへ通知し、現在地は
            // 開けなかったファイルへ移る（「次へ」でその先に進める）
            var next = Playlist.PeekNext();
            DiagnosticLog.Write("playlist", $"自動送り結果 next={next ?? "(なし＝停止)"}");
            if (next != null) OpenFile(next);
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        // 破棄後にティックが後追いで発生し、閉じたウィンドウのバインド先を触るのを防ぐ
        _osdTimer.Stop();
        // ネイティブ資源の解放を含むため例外を投げうる。ここで漏らすとウィンドウを
        // 閉じている最中にアプリが落ちるので、記録に留めて終了処理は続行する
        try { Engine.Dispose(); }
        catch (Exception ex) { DiagnosticLog.WriteFatal("engine", $"エンジンの破棄に失敗: {ex}"); }
    }
}