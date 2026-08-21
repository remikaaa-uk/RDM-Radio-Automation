using System.Runtime.InteropServices;
using RDM.Audio.Engine;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;

namespace RDM.Audio.Sweeper;

/// <summary>
/// Registers a one-shot BASS_SYNC_POS callback on the active playlist stream.
/// When the stream reaches the scheduled byte position, a SweeperTrigger message
/// is posted to EventBridge, which creates and plays the sweeper stream on the
/// drain task thread (safe for BASS calls).
///
/// Zero database access — caller supplies the resolved file path.
/// </summary>
internal sealed class SweeperTrigger
{
    private readonly EventBridge    _eventBridge;
    private readonly List<SweeperSchedule> _schedules = new();

    private record struct SweeperSchedule(int StreamHandle, int SyncHandle, GCHandle GcHandle);

    public SweeperTrigger(EventBridge eventBridge) => _eventBridge = eventBridge;

    /// <summary>
    /// Schedules <paramref name="sweeperFilePath"/> to be played when
    /// <paramref name="streamHandle"/> reaches <paramref name="triggerAtBytes"/>.
    /// </summary>
    public void Schedule(int streamHandle, long triggerAtBytes, string sweeperFilePath)
    {
        string capturedPath = sweeperFilePath;

        SYNCPROC proc = (handle, channel, data, user) =>
            _eventBridge.Post(new SyncMessage(
                Type:     SyncMessageType.SweeperTrigger,
                FilePath: capturedPath));

        var gc = GCHandle.Alloc(proc, GCHandleType.Normal);

        int syncHandle = BassMix.BASS_Mixer_ChannelSetSync(
            streamHandle,
            BASSSync.BASS_SYNC_POS | BASSSync.BASS_SYNC_ONETIME,
            triggerAtBytes,
            proc,
            IntPtr.Zero);

        _schedules.Add(new SweeperSchedule(streamHandle, syncHandle, gc));
    }

    /// <summary>
    /// Explicitly removes all registered BASS syncs and unpins GCHandles.
    /// Call this when the playlist advances or stops BEFORE the sync fires —
    /// prevents the sweeper from playing over the next track. (Bug 2 fix)
    /// </summary>
    public void Cancel()
    {
        foreach (var s in _schedules)
        {
            BassMix.BASS_Mixer_ChannelRemoveSync(s.StreamHandle, s.SyncHandle);
            if (s.GcHandle.IsAllocated) s.GcHandle.Free();
        }
        _schedules.Clear();
    }

    /// <summary>
    /// Unpins GCHandles only. Called from FreePlaylistStream where
    /// BASS_Mixer_ChannelRemove already cancels any registered syncs.
    /// </summary>
    public void Release()
    {
        foreach (var s in _schedules)
            if (s.GcHandle.IsAllocated) s.GcHandle.Free();
        _schedules.Clear();
    }
}
