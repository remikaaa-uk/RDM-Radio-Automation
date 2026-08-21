using Avalonia.Media;
using RDM.Shared.DTOs;
using System;
using System.Collections.Generic;

namespace RDM.UI.Controls;

/// Canonical color + label mapping for every cue marker in <see cref="CueMarkersDto"/>.
/// Single source of truth shared by <see cref="WaveformControl"/>, the player and the
/// multi-track segue editor so marker colors/labels stay consistent everywhere.
public static class CueMarkerPalette
{
    /// One present marker, positioned in seconds from the track start.
    /// <see cref="Key"/> matches the CueMarkersDto property name (e.g. "StartNext").
    public readonly record struct Entry(double Seconds, Color Color, string Label, string Key);

    private readonly record struct Def(Func<CueMarkersDto, double?> Selector, string Hex, string Label, string Key);

    private static readonly Def[] Order =
    {
        new(m => m.Start,     "#5DDC8A", "START", "Start"),
        new(m => m.Intro,     "#4AB5FF", "INTRO", "Intro"),
        new(m => m.Ramp2,     "#88CCFF", "RAMP2", "Ramp2"),
        new(m => m.Ramp3,     "#BBDDFF", "RAMP3", "Ramp3"),
        new(m => m.HookIn,    "#CC88FF", "HOOK",  "HookIn"),
        new(m => m.HookFade,  "#AA66DD", "HFDE",  "HookFade"),
        new(m => m.HookOut,   "#8844BB", "HOUT",  "HookOut"),
        new(m => m.LoopIn,    "#00FFCC", "LOOP",  "LoopIn"),
        new(m => m.LoopOut,   "#00CCAA", "LOUT",  "LoopOut"),
        new(m => m.Anchor,    "#FFD700", "ANC",   "Anchor"),
        new(m => m.StartNext, "#FFD700", "NEXT",  "StartNext"),
        new(m => m.FadeOut,   "#FF6644", "FADE",  "FadeOut"),
        new(m => m.FadeEnd,   "#CC2200", "FEND",  "FadeEnd"),
        new(m => m.Outro,     "#FF8C42", "OUTRO", "Outro"),
        new(m => m.End,       "#FFFFFF", "END",   "End"),
    };

    /// Returns the short display label for a marker key (e.g. "FadeOut" → "FADE").
    public static string GetLabel(string key)
    {
        foreach (var d in Order)
            if (d.Key == key) return d.Label;
        return key;
    }

    /// Yields every marker that is actually set on <paramref name="markers"/>, in canonical order.
    public static IEnumerable<Entry> Enumerate(CueMarkersDto markers)
    {
        foreach (var d in Order)
            if (d.Selector(markers) is double s)
                yield return new Entry(s, Color.Parse(d.Hex), d.Label, d.Key);
    }
}
