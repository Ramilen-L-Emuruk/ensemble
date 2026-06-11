using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTrackPlayer.Core.Enums;
using MultiTrackPlayer.Core.Models;
using MultiTrackPlayer.Engine;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    public MediaEngine Engine { get; } = new MediaEngine();
    public PlaylistViewModel Playlist { get; } = new PlaylistViewModel();
    public ObservableCollection<AudioTrackViewModel> AudioTracks { get; } = new();
    public ObservableCollection<ChapterViewModel> Chapters { get; } = new();

    [ObservableProperty] private PlaybackState _playbackState = PlaybackState.Stopped;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private double _masterVolume = 80.0;
    [ObservableProperty] private string _title = "MultiTrackPlayer";
    [ObservableProperty] private bool _isFullscreen;

    public MainViewModel()
    {
        Engine.VideoFrameReady += (_, _) => { };
        Engine.PositionChanged += (_, pos) =>
        {
            Position = pos;
            if (Duration > TimeSpan.Zero)
                PositionRatio = pos.TotalSeconds / Duration.TotalSeconds;
        };
        Engine.PlaybackEnded += (_, _) => OnPlaybackEnded();
    }

    [ObservableProperty] private double _positionRatio;

    partial void OnPositionRatioChanged(double value)
    {
        if (Duration > TimeSpan.Zero && Math.Abs(value * Duration.TotalSeconds - Position.TotalSeconds) > 1)
            Engine.Seek(TimeSpan.FromSeconds(value * Duration.TotalSeconds));
    }

    partial void OnMasterVolumeChanged(double value) => Engine.SetMasterVolume((float)(value / 100.0));

    public void OpenFile(string path)
    {
        Engine.Open(path);
        var info = Engine.CurrentMedia!;
        Duration = info.Duration;
        Title = System.IO.Path.GetFileName(path) + " - MultiTrackPlayer";
        Playlist.SetCurrentByPath(path);

        AudioTracks.Clear();
        foreach (var track in info.AudioTracks)
            AudioTracks.Add(new AudioTrackViewModel(track, Engine.SetTrackVolume, Engine.SetTrackMute));

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