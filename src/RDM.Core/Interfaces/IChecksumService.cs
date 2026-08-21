namespace RDM.Core.Interfaces;

public interface IChecksumService
{
    /// Computes the SHA-256 checksum of the given file, returned as a lowercase
    /// hex string. This is the canonical audio-file fingerprint used across the
    /// import pipeline and the new-file scanner to detect duplicates.
    Task<string> ComputeAsync(string filePath, CancellationToken ct = default);
}
