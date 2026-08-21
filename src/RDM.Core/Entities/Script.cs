namespace RDM.Core.Entities;

public class Script
{
    public Guid   Id          { get; set; }
    public string Name        { get; set; } = string.Empty;
    public string ScriptBody  { get; set; } = string.Empty;
    public bool   IsEnabled   { get; set; } = true;
    public string Language    { get; set; } = "js";  // "js" (Jint) — extensible
}
