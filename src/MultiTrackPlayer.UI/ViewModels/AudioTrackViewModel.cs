using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class AudioTrackViewModel : ObservableObject
{
    private readonly Action<int, float> _setVolume;
    private readonly Action<int, bool> _setMute;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private int _trackNumber;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private double _volume = 100.0;

    public AudioTrackViewModel(AudioTrackInfo info, Action<int, float> setVolume, Action<int, bool> setMute)
    {
        _setVolume = setVolume;
        _setMute = setMute;
        TrackNumber = info.TrackNumber;
        Name = $"#{info.TrackNumber} {info.Name}";
    }

    partial void OnVolumeChanged(double value) => _setVolume(TrackNumber, (float)(value / 100.0));
    partial void OnIsMutedChanged(bool value) => _setMute(TrackNumber, value);

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;
}