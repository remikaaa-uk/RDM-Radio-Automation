namespace RDM.Core.Interfaces;

/// <summary>
/// Encrypts secrets that must be stored but never held in plaintext (e.g. cast-server passwords).
/// Implementations are machine-bound: a blob produced on one machine cannot be read on another.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a plaintext secret. Returns null for null/empty input.</summary>
    byte[]? Protect(string? plaintext);

    /// <summary>
    /// Decrypts a blob produced by <see cref="Protect"/>. Returns null for null/empty input,
    /// and throws if the blob is corrupt or was produced on a different machine.
    /// </summary>
    string? Unprotect(byte[]? cipher);
}
