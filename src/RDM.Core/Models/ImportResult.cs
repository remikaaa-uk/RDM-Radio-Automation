using RDM.Core.Entities;

namespace RDM.Core.Models;

public abstract record ImportResult
{
    public sealed record Success(Asset Asset)       : ImportResult;
    public sealed record Duplicate(string AssetId) : ImportResult;
    public sealed record Failed(string Reason)     : ImportResult;
}
