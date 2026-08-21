using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RDM.Core.Entities;
using System;

namespace RDM.UI.Services;

/// <summary>
/// Client-side position tracker for the active track.
/// Drives the countdown overlay on the playlist at 10 Hz.
/// Receives track metadata from PlaylistViewModel when TRACK_STARTED fires.
/// </summary>
public sealed partial class CountdownService : ObservableObject, IDisposable
{
    // Countdown configuration (from AudioSettings, hot-applied on Settings save).
    // Defaults match the historical hard-coded behaviour used before the toggles were wired.
    private bool _redEnabled     = true;
    private bool _greenEnabled   = true;
    private uint _redWarningMs   = 30_000;   // outro warning threshold

    private readonly DispatcherTimer _timer;

    // ── Track state (written from UI thread only) ─────────────────────────────

    private DateTime? _startedAt;
    private uint      _durationMs;
    private uint?     _introCueMs;
    private uint?     _outroCueMs;
    // Absolute file offset where playback actually began (CueStart). Cue markers are absolute
    // file positions, so the reported position must be one too — otherwise the waveform
    // playhead and both countdowns trail the audio by the cue-in offset.
    private uint      _startOffsetMs;

    // ── Observable countdown state ────────────────────────────────────────────

    [ObservableProperty] private int  _countdownSeconds;
    [ObservableProperty] private uint _positionMs;
    [ObservableProperty] private bool _isGreenPhase;    // intro countdown
    [ObservableProperty] private bool _isRedPhase;      // outro countdown
    [ObservableProperty] private bool _isCountdownVisible;
    [ObservableProperty] private IBrush _countdownBrush = new SolidColorBrush(Colors.White);

    public CountdownService(AudioSettings settings)
    {
        ApplySettings(settings);

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timer.Tick += OnTick;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the countdown toggles/threshold. Called at construction with the startup
    /// snapshot and again from SettingsViewModel after a save (hot-apply, no restart).
    /// A threshold of 0 s is treated as the historical 30 s default to avoid hiding the
    /// red countdown entirely.
    /// </summary>
    public void ApplySettings(AudioSettings settings)
    {
        _redEnabled   = settings.CountdownRedEnabled;
        _greenEnabled = settings.CountdownGreenEnabled;
        _redWarningMs = settings.CountdownRedThresholdS > 0
            ? settings.CountdownRedThresholdS * 1000u
            : 30_000u;
    }

    /// <summary>
    /// Called by PlaylistViewModel after loading NowPlaying info for the new track.
    /// <paramref name="startOffsetMs"/> is the track's CueStart — playback begins there, not at 0.
    /// </summary>
    public void OnTrackStarted(
        DateTime startedAt, uint durationMs, uint? introCueMs, uint? outroCueMs, uint startOffsetMs)
    {
        _startedAt     = startedAt;
        _durationMs    = durationMs;
        _introCueMs    = introCueMs;
        _outroCueMs    = outroCueMs;
        _startOffsetMs = startOffsetMs;

        _timer.Start();
        Tick();
    }

    /// <summary>
    /// Called when a looping track rewound to <paramref name="loopStartMs"/>. The track did not
    /// end and no TRACK_STARTED follows, so only the clock origin moves — duration and cue
    /// markers belong to the same track and must survive. Without this the position keeps
    /// counting up, pins at _durationMs, and the playhead freezes after the first pass.
    /// </summary>
    public void OnLooped(DateTime restartedAt, uint loopStartMs)
    {
        if (_startedAt is null) return;   // nothing playing — a stale loop event

        _startedAt     = restartedAt;
        _startOffsetMs = loopStartMs;
        _timer.Start();
        Tick();
    }

    /// <summary>Called when TRACK_ENDED or PLAYLIST_STOPPED fires.</summary>
    public void OnTrackEnded()
    {
        _timer.Stop();
        _startedAt     = null;
        _durationMs    = 0;
        _introCueMs    = null;
        _outroCueMs    = null;
        _startOffsetMs = 0;

        IsGreenPhase       = false;
        IsRedPhase         = false;
        IsCountdownVisible = false;
        CountdownSeconds   = 0;
        PositionMs         = 0;
    }

    // ── Timer ─────────────────────────────────────────────────────────────────

    private void OnTick(object? sender, EventArgs e) => Tick();

    private void Tick()
    {
        if (_startedAt is null) return;

        var posMs = (uint)Math.Min(
            _startOffsetMs + (DateTime.UtcNow - _startedAt.Value).TotalMilliseconds,
            _durationMs);

        PositionMs = posMs;

        bool green = false;
        bool red   = false;
        int  secs  = 0;

        // Green phase: position < INTRO cue (intro zone is still active)
        if (_greenEnabled && _introCueMs.HasValue && posMs < _introCueMs.Value)
        {
            green = true;
            secs  = (int)((_introCueMs.Value - posMs) / 1000);
        }
        // Red phase: time remaining to OUTRO (or end) is within warning threshold
        else if (_redEnabled)
        {
            var referenceMs = _outroCueMs ?? _durationMs;
            if (referenceMs > posMs)
            {
                var remaining = referenceMs - posMs;
                if (remaining <= _redWarningMs)
                {
                    red  = true;
                    secs = (int)(remaining / 1000);
                }
            }
        }

        IsGreenPhase       = green;
        IsRedPhase         = red;
        IsCountdownVisible = green || red;
        CountdownSeconds   = secs;

        if (green)
            CountdownBrush = new SolidColorBrush(Color.Parse("#4CAF50")); // green
        else if (red)
            CountdownBrush = new SolidColorBrush(Color.Parse("#F44336")); // red
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
