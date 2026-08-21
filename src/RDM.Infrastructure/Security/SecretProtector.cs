using System.Security.Cryptography;
using System.Text;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Security;

/// <summary>
/// Windows DPAPI secret protection.
///
/// Scope is <see cref="DataProtectionScope.LocalMachine"/>, not CurrentUser, on purpose: RDM.UI runs
/// as the interactive user while RDM.API may run under a service account, and both must be able to
/// read the same stored secret. CurrentUser scope would silently fail to decrypt in whichever process
/// runs under a different Windows account.
///
/// The trade-off is explicit and must not be oversold: LocalMachine means any local administrator or
/// interactive user on this machine can decrypt these blobs. That is acceptable for a single
/// broadcast box; it is not isolation from other users of the same machine.
/// </summary>
public sealed class SecretProtector : ISecretProtector
{
    public byte[]? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        return ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.LocalMachine);
    }

    public string? Unprotect(byte[]? cipher)
    {
        if (cipher is null || cipher.Length == 0) return null;

        var plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }
}
