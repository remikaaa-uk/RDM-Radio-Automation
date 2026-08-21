using System.Security.Cryptography;
using RDM.Core.Interfaces;

namespace RDM.Core.Services;

public sealed class ChecksumService : IChecksumService
{
    public async Task<string> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
