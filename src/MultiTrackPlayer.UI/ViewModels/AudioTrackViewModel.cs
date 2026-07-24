using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTrackPlayer.Core.Models;

namespace MultiTrackPlayer.UI.ViewModels;

public partial class AudioTrackViewModel : ObservableObject
{
    private readonly Action<int, float> _setVolume;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private int _trackNumber;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isSolo;
    [ObservableProperty] private double _volume = 100.0;

    public AudioTrackViewModel(AudioTrackInfo info, Action<int, float> setVolume)
    {
        _setVolume = setVolume;
        TrackNumber = info.TrackNumber;
        var langSuffix = string.IsNullOrEmpty(info.Language) ? "" : $" [{info.Language.ToUpperInvariant()}]";
        Name = $"#{info.TrackNumber} {info.Name}{langSuffix}";
    }

    partial void OnVolumeChanged(double value) => _setVolume(TrackNumber, (float)(value / 100.0));

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void ToggleSolo() => IsSolo = !IsSolo;
}
