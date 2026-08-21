namespace RDM.Core.Models;

public sealed record ImportReaderFlags(
    bool ReadRdm  = true,
    bool ReadMmd  = true,
    bool ReadWfrm = true,
    bool ReadId3  = true);
