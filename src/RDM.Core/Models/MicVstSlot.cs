namespace RDM.Core.Models;

public sealed class MicVstSlot
{
    public int    SlotId     { get; }
    public string DllPath    { get; }
    public string PluginName { get; set; }

    /// <summary>
    /// The plugin's own settings as an opaque blob (BASS_VST_GetChunk), captured while it was
    /// loaded and restored the next time it is. Null for plugins that do not support chunk mode —
    /// their raw parameter list is deliberately not used as a substitute: Stereo Tool alone
    /// exposes 8042 parameters, some of them infinity, which is neither serialisable nor a
    /// meaningful snapshot. Such plugins keep their own settings (Stereo Tool uses an INI file).
    /// </summary>
    public byte[]? StateChunk { get; set; }

    public MicVstSlot(int slotId, string dllPath, string pluginName)
    {
        SlotId     = slotId;
        DllPath    = dllPath;
        PluginName = pluginName;
    }
}
