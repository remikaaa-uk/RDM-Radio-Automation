using System.Security.Cryptography;

namespace RDM.Core.Services;

/// <summary>Random password generation shared by first-run admin seeding and account management UI.</summary>
public static class PasswordGenerator
{
    private const string Chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$";

    public static string Generate(int length = 16)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(b => Chars[b % Chars.Length]).ToArray());
    }
}
