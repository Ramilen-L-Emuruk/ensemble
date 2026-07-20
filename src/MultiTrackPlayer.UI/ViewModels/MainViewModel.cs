using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine;
using MultiTrackPlayer.Engine.Diagnostics;
using MultiTrackPlayer.UI.Settings;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string LogDirectory =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "MultiTrackPlayer", "logs");

    public MediaEngine Engine { get; } = new MediaEngine();
    public PlaylistViewModel Playlist { get; } = new PlaylistViewModel();
    public ObservableCollection<AudioTrackViewModel> AudioTracks { get; } = new();
    public ObservableCollection<ChapterViewModel> Chapters { get; } = new();
    public AppSettings Settings { get; } = AppSettings.Load();

    [ObservableProperty] private PlaybackState _playbackState = PlaybackState.Stopped;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private MediaInfo? _currentMedia;
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private double _masterVolume = 80.0;
    [ObservableProperty] private string _title = "MultiTrackPlayer";
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isDebugMode;
    [ObservableProperty] private bool _isMasterMuted;

    public MainViewModel()
    {
        Engine.PositionChanged += (_, pos) =>
        {
            Position = pos;
            if (Duration > TimeSpan.Zero)
                PositionRatio = pos.TotalSeconds / Duration.TotalSeconds;
        };
        Engine.PlaybackEnded += (_, _) => OnPlaybackEnded();
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

    /// <summary>現在の各トラックのミュート状態を、次回以降ファイルを開いたときの既定値として保存する。</summary>
    public void SaveCurrentMutesAsDefault()
    {
        Settings.DefaultMutedTracks = AudioTracks.Where(t => t.IsMuted).Select(t => t.TrackNumber).ToList();
        Settings.Save();
        DiagnosticLog.Write("ui", $"既定ミュート保存 tracks=[{string.Join(",", Settings.DefaultMutedTracks)}]");
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
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        Engine.SetMasterVolume(value ? 0f : (float)(MasterVolume / 100.0));
        DiagnosticLog.Write("ui", $"マスターミュート切替 muted={value}");
    }

    [RelayCommand]
    private void ToggleMute() => IsMasterMuted = !IsMasterMuted;

    public void OpenFile(string path)
    {
        Engine.Open(path);
        var info = Engine.CurrentMedia!;
        CurrentMedia = info;
        Duration = info.Duration;
        Title = System.IO.Path.GetFileName(path) + " - MultiTrackPlayer";
        Playlist.SetCurrentByPath(path);

        AudioTracks.Clear();
        foreach (var track in info.AudioTracks)
        {
            var trackVm = new AudioTrackViewModel(track, Engine.SetTrackVolume, Engine.SetTrackMute);
            // 設定でデフォルトミュート指定されたトラック番号は最初からミュートで開く
            if (Settings.DefaultMutedTracks.Contains(trackVm.TrackNumber))
                trackVm.IsMuted = true;
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
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (PlaybackState == PlaybackState.Playing) { Engine.Pause(); PlaybackState = PlaybackState.Paused; }
        else { Engine.Play(); PlaybackState = PlaybackState.Playing; }
    }

    [RelayCommand]
    private void Stop() { Engine.Stop(); PlaybackState = PlaybackState.Stopped; Position = TimeSpan.Zero; }

    [RelayCommand]
    private void StepForward() => Engine.StepForward();

    [RelayCommand]
    private void StepBackward() => Engine.StepBackward();

    public void Skip(double seconds) => Engine.Seek(Position + TimeSpan.FromSeconds(seconds));

    public void ChangeSpeed(double delta)
    {
        PlaybackSpeed = Math.Clamp(PlaybackSpeed + delta, 0.1, 2.0);
        Engine.SetPlaybackSpeed(PlaybackSpeed);
    }

    public void SetSpeed(double speed)
    {
        PlaybackSpeed = speed;
        Engine.SetPlaybackSpeed(speed);
    }

    public void ToggleChapterAtCurrentPosition()
    {
        var near = Engine.FindUserChapterNear(Position, TimeSpan.FromSeconds(0.5));
        if (near != null)
            Engine.RemoveUserChapter(near);
        else
            Engine.AddUserChapter(new ChapterInfo(0, $"Chapter {Chapters.Count + 1}", Position, true));
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