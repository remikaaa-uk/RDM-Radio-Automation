using System.Collections.Generic;

namespace RDM.Core.Models;

public sealed class MicFxSlot
{
    public int       SlotId { get; }
    public MicFxType FxType { get; }

    /// <summary>
    /// Current parameter values, keyed as in <see cref="MicFxParams"/>. Always populated — a slot
    /// starts from the defaults — so the engine never has to guess what to send to BASS.
    /// </summary>
    public Dictionary<string, float> Parameters { get; }

    public MicFxSlot(int slotId, MicFxType fxType, Dictionary<string, float>? parameters = null)
    {
        SlotId     = slotId;
        FxType     = fxType;
        Parameters = parameters ?? MicFxParams.Defaults(fxType);
    }
}
