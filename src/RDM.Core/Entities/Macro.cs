namespace RDM.Core.Entities;

public class Macro
{
    public Guid                  Id        { get; set; }
    public string                Name      { get; set; } = string.Empty;
    public bool                  IsEnabled { get; set; } = true;
    public IReadOnlyList<MacroStep> Steps  { get; set; } = [];
}
