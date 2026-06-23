using System;
using System.Security.Cryptography;
using System.Text;

namespace PgBackupManager.Core.Services;

public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PgBackupManager:v1:profile-password");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return "";
        var cipher = Convert.FromBase64String(cipherBase64);
        var bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
