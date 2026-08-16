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
        Engine.PlaybackEnded += (_, _) => OnPlaybackEnded();
        Thumbnails.ThumbnailsReady += (_, sheet) =>
            Application.Current.Dispatcher.Invoke(() => ThumbnailSheet = sheet);
        Engine.StatisticsUpdated += (_, stats) =>
        {
            int total = stats.DroppedFrames + stats.DisplayedFrames;
            double dropRate = total > 0 ? stats.DroppedFrames * 100.0 / total : 0.0;
            StatusText = $"表示 {stats.DisplayedFrames} / ドロップ {stats.DroppedFrames} ({dropRate:F1}%)  映像遅延 {stats.VideoLagSec * 1000:F0}ms";
        };

        IsDebugMode = Settings.DebugMode;
    }

    partial void OnIsDebugModeChanged(bool value)
    {
        if (value) DiagnosticLog.Enable(LogDirectory);
        else DiagnosticLog.Disable();
        Settings.DebugMode = value;
        Settings.Save();
    }

    /// <summary>現在の各トラックのミュート状態を、このファイルが置かれたフォルダの既定値として保存する。</summary>
    public void SaveCurrentMutesAsDefault()
    {
        if (CurrentMedia == null) return;

        string directory = System.IO.Path.GetDirectoryName(CurrentMedia.FilePath) ?? string.Empty;
        var mutedTracks = AudioTracks.Where(t => t.IsMuted).Select(t => t.TrackNumber).ToList();
        Settings.DefaultMutedTracksByDirectory[directory] = mutedTracks;
        Settings.Save();
        DiagnosticLog.Write("ui", $"既定ミュート保存 dir={directory} tracks=[{string.Join(",", mutedTracks)}]");
    }

    private void OnAudioTrackMuteOrSoloChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioTrackViewModel.IsMuted) || e.PropertyName == nameof(AudioTrackViewModel.IsSolo))
            UpdateSoloMuting();
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

    partial void OnPositionRatioChanged(double value)
    {
        if (Duration > TimeSpan.Zero && Math.Abs(value * Duration.TotalSeconds - Position.TotalSeconds) > 1)
            SeekTo(TimeSpan.FromSeconds(value * Duration.TotalSeconds));
    }

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
        ShowOsd($"音量 {value:0}%");
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        if (!InvokeEngine("ミュート切替", () => Engine.SetMasterVolume(value ? 0f : (float)(MasterVolume / 100.0)))) return;
        DiagnosticLog.Write("ui", $"マスターミュート切替 muted={value}");
        ShowOsd(value ? "ミュート" : "ミュート解除");
    }

    [RelayCommand]
    private void ToggleMute() => IsMasterMuted = !IsMasterMuted;

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

        Playlist.Files.Clear();
        Playlist.AddFiles(siblings.Count > 0 ? siblings : new[] { path });
        OpenFile(path);
    }

    /// <summary>アプリ未起動の状態から動画ファイルを1件開いて起動する場合の入口。
    /// 同じフォルダの他ファイルは自動追加せず、渡された1件だけのプレイリストにする。</summary>
    public void OpenSingleFile(string path)
    {
        Playlist.Files.Clear();
        Playlist.AddFiles(new[] { path });
        OpenFile(path);
    }

    /// <summary>
    /// ファイルを開いて再生を開始する。破損ファイル・非対応形式・フォルダの誤指定などで
    /// 開けなかった場合は、現在の再生状態を保ったままユーザーへ通知して false を返す。
    /// </summary>
    /// <returns>開けた場合 true。</returns>
    public bool OpenFile(string path)
    {
        if (_isDisposed) return false;
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
        PlaybackState = PlaybackState.Stopped;
        // Duration を先に 0 にしてあるので、OnPositionRatioChanged のガードにより Seek は発火しない。
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
    public bool SeekTo(TimeSpan position) => InvokeEngine("シーク", () => Engine.Seek(position));

    public void SetTrackVolume(int trackNumber, float volume)
        => InvokeEngine("音量変更", () => Engine.SetTrackVolume(trackNumber, volume));

    public void AttachVideoOutput(IntPtr hwnd)
        => InvokeEngine("映像出力の接続", () => Engine.AttachVideoOutput(hwnd));

    public bool JumpToNextChapter() => InvokeEngine("次のチャプターへ移動", Engine.JumpToNextChapter);

    public bool JumpToPreviousChapter() => InvokeEngine("前のチャプターへ移動", Engine.JumpToPreviousChapter);

    public bool JumpToChapter(int index) => InvokeEngine("チャプターへ移動", () => Engine.JumpToChapter(index));

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
        Playlist.SetCurrentByPath(path);

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
        PlaybackState = PlaybackState.Playing;
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
        if (PlaybackState == PlaybackState.Playing)
        {
            if (!InvokeEngine("一時停止", Engine.Pause)) return;
            PlaybackState = PlaybackState.Paused;
            ShowOsd("一時停止");
        }
        else
        {
            // Play() はパイプライン構築に失敗すると（自身を巻き戻したうえで）例外を送出する
            if (!InvokeEngine("再生", Engine.Play))
            {
                PlaybackState = PlaybackState.Stopped;
                return;
            }
            PlaybackState = PlaybackState.Playing;
            ShowOsd("再生");
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (!InvokeEngine("停止", Engine.Stop)) return;
        PlaybackState = PlaybackState.Stopped;
        Position = TimeSpan.Zero;
        ShowOsd("停止");
    }

    [RelayCommand]
    private void StepForward()
    {
        if (PlaybackState != PlaybackState.Paused) return;
        if (!InvokeEngine("コマ送り", Engine.StepForward)) return;
        ShowOsd("コマ送り");
    }

    [RelayCommand]
    private void StepBackward()
    {
        if (PlaybackState != PlaybackState.Paused) return;
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
        ShowOsd($"速度 {PlaybackSpeed:0.00}x");
    }

    public void SetSpeed(double speed)
    {
        PlaybackSpeed = speed;
        if (!InvokeEngine("速度変更", () => Engine.SetPlaybackSpeed(speed))) return;
        ShowOsd($"速度 {PlaybackSpeed:0.00}x");
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
        var next = Playlist.MoveNext();
        if (next != null) OpenFile(next);
    }

    public void PlayPrevious()
    {
        var prev = Playlist.MovePrevious();
        if (prev != null) OpenFile(prev);
    }

    // PlaybackEnded はスレッドプールのタイマーから発火する。PlaybackState は DebugWindow が
    // バインドしており、UI スレッド以外から更新するとバインディングが例外を投げてプロセスごと落ちる。
    // 次ファイルの決定（CurrentIndex の更新を伴う）も UI 操作と同じスレッドで行いたいので、
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
            var next = Playlist.MoveNext();
            if (next != null) OpenFile(next);
            else PlaybackState = PlaybackState.Stopped;
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