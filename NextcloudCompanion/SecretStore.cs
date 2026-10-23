using System.Security.Cryptography;
using System.Text;

namespace NextcloudCompanion;

/// <summary>Windows DPAPI, current user. App passwords never sit in settings.json in the clear.</summary>
internal static class SecretStore
{
    public static string Protect(string plain)
    {
        byte[] data = Encoding.UTF8.GetBytes(plain);
        byte[] protectedBytes = ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string stored)
    {
        byte[] data = Convert.FromBase64String(stored);
        byte[] plain = ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
