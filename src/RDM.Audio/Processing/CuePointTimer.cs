using System.Runtime.InteropServices;
using RDM.Audio.Engine;
using RDM.Core.Entities;
using RDM.Shared.Enums;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;

namespace RDM.Audio.Processing;

/// <summary>
/// Registers BASS_Mixer_ChannelSetSync callbacks for cue points on a playlist stream.
/// Each SYNCPROC delegate is pinned via GCHandle to prevent garbage collection.
///
/// Call Register() after adding the stream to the mixer.
/// Call Release() before BASS_Mixer_ChannelRemove() to avoid stale callbacks.
/// </summary>
internal sealed class CuePointTimer
{
    private readonly EventBridge _eventBridge;
    private readonly List<Registration> _syncs = new();

    // Last registration, kept so a looping track can re-arm its markers for the next pass.
    private int                          _streamHandle;
    private string                       _assetId = string.Empty;
    private IReadOnlyList<AssetCuePoint> _cuePoints = Array.Empty<AssetCuePoint>();

    private record struct Registration(int SyncHandle, GCHandle GcHandle);

    public CuePointTimer(EventBridge eventBridge) => _eventBridge = eventBridge;

    /// <summary>
    /// Registers BASS_SYNC_POS callbacks for all cue points and a BASS_SYNC_END
    /// callback for TRACK_ENDED. Replaces any previously registered syncs.
    /// </summary>
    public void Register(int streamHandle, string assetId, IReadOnlyList<AssetCuePoint> cuePoints)
    {
        Release();

        _streamHandle = streamHandle;
        _assetId      = assetId;
        _cuePoints    = cuePoints;

        foreach (var cue in cuePoints)
        {
            long posBytes = Bass.BASS_ChannelSeconds2Bytes(streamHandle, cue.PositionMs / 1000.0);
            if (posBytes < 0) continue;

            SyncMessageType msgType = cue.MarkerType switch
            {
                MarkerType.Intro     => SyncMessageType.TrackIntroReached,
                MarkerType.StartNext => SyncMessageType.TrackStartNextReached,
                MarkerType.FadeOut   => SyncMessageType.TrackFadeOutReached,
                MarkerType.End       => SyncMessageType.TrackCueEndReached,
                _                    => SyncMessageType.CuePointReached
            };

            string          capturedAssetId = assetId;
            SyncMessageType capturedType    = msgType;
            MarkerType      capturedMarker  = cue.MarkerType;
            uint            capturedPosMs   = cue.PositionMs;

            SYNCPROC proc = (handle, channel, data, user) =>
                _eventBridge.Post(new SyncMessage(
                    Type:       capturedType,
                    AssetId:    capturedAssetId,
                    Marker:     capturedMarker,
                    PositionMs: capturedPosMs));

            PinAndRegister(proc, streamHandle,
                BASSSync.BASS_SYNC_POS | BASSSync.BASS_SYNC_ONETIME, posBytes);
        }

        RegisterEndSync(streamHandle, assetId);
    }

    /// <summary>
    /// Re-arms the markers of the current track for another pass. The syncs are one-shot, so
    /// after a looping track rewinds they would never fire again — leaving a track that, once
    /// the loop is switched off, has no StartNext/FadeOut/End left and could only run to EOF.
    /// No-op when nothing is registered.
    /// </summary>
    public void ReArm()
    {
        if (_streamHandle == 0) return;
        Register(_streamHandle, _assetId, _cuePoints);
    }

    /// <summary>
    /// Removes the registered BASS syncs and frees all pinned SYNCPROC delegates.
    /// Must be called before BASS_Mixer_ChannelRemove to avoid callbacks firing
    /// after the stream is disconnected, and whenever the syncs must stop firing
    /// while the stream itself keeps playing (e.g. the outgoing side of a crossfade).
    /// </summary>
    public void Release()
    {
        foreach (var s in _syncs)
        {
            if (_streamHandle != 0)
                BassMix.BASS_Mixer_ChannelRemoveSync(_streamHandle, s.SyncHandle);
            if (s.GcHandle.IsAllocated) s.GcHandle.Free();
        }
        _syncs.Clear();
        _streamHandle = 0;
        _assetId      = string.Empty;
        _cuePoints    = Array.Empty<AssetCuePoint>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RegisterEndSync(int streamHandle, string assetId)
    {
        string capturedAssetId = assetId;

        SYNCPROC endProc = (handle, channel, data, user) =>
            _eventBridge.Post(new SyncMessage(
                Type:    SyncMessageType.TrackEnded,
                AssetId: capturedAssetId));

        PinAndRegister(endProc, streamHandle,
            BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_ONETIME, 0);
    }

    private void PinAndRegister(SYNCPROC proc, int streamHandle, BASSSync syncType, long param)
    {
        var gcHandle = GCHandle.Alloc(proc, GCHandleType.Normal);

        int syncHandle = BassMix.BASS_Mixer_ChannelSetSync(
            handle: streamHandle,
            type:   syncType,
            param:  param,
            proc:   proc,
            user:   IntPtr.Zero);

        _syncs.Add(new Registration(syncHandle, gcHandle));
    }
}
