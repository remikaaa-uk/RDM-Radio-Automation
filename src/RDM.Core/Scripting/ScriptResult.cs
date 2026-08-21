namespace RDM.Core.Scripting;

public record ScriptResult(bool Success, string? Error = null, long ElapsedMs = 0)
{
    public static ScriptResult Ok(long elapsedMs)    => new(true,  null,  elapsedMs);
    public static ScriptResult Fail(string error)    => new(false, error, 0);
}
