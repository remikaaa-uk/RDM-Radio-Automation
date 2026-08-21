using RDM.Core.Hardware;

namespace RDM.Core.Entities;

public class MacroStep
{
    public Guid      Id           { get; set; }
    public Guid      MacroId      { get; set; }
    public int       StepOrder    { get; set; }

    // Jedna z dwóch ścieżek wykonania:
    // A) wywołanie zarejestrowanej akcji
    public ActionId? ActionId     { get; set; }
    public string?   Parameter    { get; set; }   // trafia do ParameterizedPayload.Parameter

    // B) bezpośrednia komenda wyjściowa (MIDI / serial / D&R GPO)
    public string?   OutputDeviceType { get; set; }
    public string?   OutputDeviceId   { get; set; }
    public string?   OutputCommand    { get; set; }  // np. "GPO01 ON" lub "ROUTE 1 2"

    public int       DelayAfterMs { get; set; }   // opóźnienie po wykonaniu kroku
}
