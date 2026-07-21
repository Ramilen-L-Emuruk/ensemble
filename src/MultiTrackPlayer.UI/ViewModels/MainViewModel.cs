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

    [ObservableProperty] private double _positionRatio;

    partial void OnPositionRatioChanged(double value)
    {
        if (Duration > TimeSpan.Zero && Math.Abs(value * Duration.TotalSeconds - Position.TotalSeconds) > 1)
            Engine.Seek(TimeSpan.FromSeconds(value * Duration.TotalSeconds));
    }

    partial void OnMasterVolumeChanged(double value)
    {
        // スライダー操作でマスター音量を変えたら、ミュート状態と実際に聞こえる音を一致させるため自動的に解除する
        if (IsMasterMuted) IsMasterMuted = false;
        else Engine.SetMasterVolume((float)(value / 100.0));
        ShowOsd($"音量 {value:0}%");
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        Engine.SetMasterVolume(value ? 0f : (float)(MasterVolume / 100.0));
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

    public void OpenFile(string path)
    {
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
            var trackVm = new AudioTrackViewModel(track, Engine.SetTrackVolume, Engine.SetTrackMute);
            // このフォルダに保存済みの既定ミュートがあればそれを、無ければトラック1のみ再生する既定値を適用する
            trackVm.IsMuted = hasSavedDefault
                ? mutedTracks!.Contains(trackVm.TrackNumber)
                : trackVm.TrackNumber != 1;
            AudioTracks.Add(trackVm);
        }

        RefreshChapters();
        Engine.Play();
        PlaybackState = PlaybackState.Playing;
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
        Engine.RenameUserChapter(chapter.Chapter, newTitle);
        RefreshChapters();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (PlaybackState == PlaybackState.Playing)
        {
            Engine.Pause();
            PlaybackState = PlaybackState.Paused;
            ShowOsd("一時停止");
        }
        else
        {
            Engine.Play();
            PlaybackState = PlaybackState.Playing;
            ShowOsd("再生");
        }
    }

    [RelayCommand]
    private void Stop()
    {
        Engine.Stop();
        PlaybackState = PlaybackState.Stopped;
        Position = TimeSpan.Zero;
        ShowOsd("停止");
    }

    [RelayCommand]
    private void StepForward()
    {
        if (PlaybackState != PlaybackState.Paused) return;
        Engine.StepForward();
        ShowOsd("コマ送り");
    }

    [RelayCommand]
    private void StepBackward()
    {
        if (PlaybackState != PlaybackState.Paused) return;
        Engine.StepBackward();
        ShowOsd("コマ戻し");
    }

    public void Skip(double seconds)
    {
        Engine.Seek(Position + TimeSpan.FromSeconds(seconds));
        ShowOsd(seconds >= 0 ? $"+{seconds:0}秒" : $"{seconds:0}秒");
    }

    public void ChangeSpeed(double delta)
    {
        PlaybackSpeed = Math.Clamp(PlaybackSpeed + delta, 0.1, 2.0);
        Engine.SetPlaybackSpeed(PlaybackSpeed);
        ShowOsd($"速度 {PlaybackSpeed:0.00}x");
    }

    public void SetSpeed(double speed)
    {
        PlaybackSpeed = speed;
        Engine.SetPlaybackSpeed(speed);
        ShowOsd($"速度 {PlaybackSpeed:0.00}x");
    }

    public void ToggleChapterAtCurrentPosition()
    {
        var near = Engine.FindUserChapterNear(Position, TimeSpan.FromSeconds(0.5));
        if (near != null)
        {
            Engine.RemoveUserChapter(near);
            ShowOsd("チャプター削除");
        }
        else
        {
            Engine.AddUserChapter(new ChapterInfo(0, $"Chapter {Chapters.Count + 1}", Position, true));
            ShowOsd("チャプター追加");
        }
        RefreshChapters();
    }

    public void PlayNext()
    {
        var next = Playlist.MoveNext();
        if (next != null) { OpenFile(next); ShowOsd("次のファイル"); }
    }

    public void PlayPrevious()
    {
        var prev = Playlist.MovePrevious();
        if (prev != null) { OpenFile(prev); ShowOsd("前のファイル"); }
    }

    private void OnPlaybackEnded()
    {
        var next = Playlist.MoveNext();
        if (next != null)
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OpenFile(next));
        else
            PlaybackState = PlaybackState.Stopped;
    }

    public void Dispose() => Engine.Dispose();
}