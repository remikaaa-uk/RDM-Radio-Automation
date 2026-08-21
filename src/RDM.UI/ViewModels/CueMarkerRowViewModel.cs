using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed class CueMarkerRowViewModel : ObservableObject
{
    private readonly Func<double?>  _getValue;
    private readonly Action         _setAction;
    private readonly Func<Task>     _jumpAction;
    private readonly Func<Task>     _previewAction;
    private readonly Action         _clearAction;
    private readonly Action         _nudgeBackAction;
    private readonly Action         _nudgeForwardAction;

    public string  Name       { get; }
    public string  HexColor   { get; }
    public IBrush  ColorBrush { get; }

    public double? Value    => _getValue();
    public bool    HasValue => _getValue().HasValue;
    public string  TimeText => _getValue() is double v ? FormatSec(v) : "   —";

    public IRelayCommand      SetCommand          { get; }
    public IAsyncRelayCommand JumpCommand         { get; }
    public IAsyncRelayCommand PreviewCommand      { get; }
    public IRelayCommand      ClearCommand        { get; }
    public IRelayCommand      NudgeBackCommand    { get; }
    public IRelayCommand      NudgeForwardCommand { get; }

    public CueMarkerRowViewModel(
        string        name,
        string        hexColor,
        Func<double?> getValue,
        Action        setAction,
        Func<Task>    jumpAction,
        Func<Task>    previewAction,
        Action        clearAction,
        Action        nudgeBackAction,
        Action        nudgeForwardAction)
    {
        Name                = name;
        HexColor            = hexColor;
        ColorBrush          = new SolidColorBrush(Color.Parse(hexColor));
        _getValue           = getValue;
        _setAction          = setAction;
        _jumpAction         = jumpAction;
        _previewAction      = previewAction;
        _clearAction        = clearAction;
        _nudgeBackAction    = nudgeBackAction;
        _nudgeForwardAction = nudgeForwardAction;

        SetCommand          = new RelayCommand(setAction);
        JumpCommand         = new AsyncRelayCommand(jumpAction);
        PreviewCommand      = new AsyncRelayCommand(previewAction);
        ClearCommand        = new RelayCommand(clearAction);
        NudgeBackCommand    = new RelayCommand(nudgeBackAction);
        NudgeForwardCommand = new RelayCommand(nudgeForwardAction);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(TimeText));
    }

    private static string FormatSec(double sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalMinutes >= 60
            ? t.ToString(@"h\:mm\:ss\.f")
            : t.ToString(@"mm\:ss\.f");
    }
}
