using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace RDM.UI.ViewModels;

public sealed partial class CuePointViewModel : ObservableObject
{
    public string  CueId      { get; }
    public string  MarkerType { get; }   // INTRO | OUTRO | HOOK | CUSTOM

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionFormatted))]
    private uint _positionMs;

    [ObservableProperty] private string? _label;

    public string PositionFormatted
    {
        get
        {
            var t = TimeSpan.FromMilliseconds(PositionMs);
            return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        }
    }

    public IRelayCommand RemoveCommand { get; }

    public CuePointViewModel(
        string cueId,
        string markerType,
        uint positionMs,
        string? label,
        Action<CuePointViewModel> onRemove)
    {
        CueId       = cueId;
        MarkerType  = markerType;
        PositionMs  = positionMs;
        Label       = label;
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }
}
