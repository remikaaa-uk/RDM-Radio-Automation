using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IRdmFileWriter
{
    Task WriteAsync(Asset asset, CancellationToken ct = default);
}
