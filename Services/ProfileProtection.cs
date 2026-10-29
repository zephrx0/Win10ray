using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XrayMetro.Services;

public sealed class ProfileLockedException : Exception
{
    public ProfileLockedException() : base(LocalizationService.Pick(
        "Не удалось расшифровать профиль. Он принадлежит другому пользователю Windows или повреждён. Создать чистый профиль? Исходный файл будет сохранён отдельно.",
        "The profile could not be decrypted. It belongs to another Windows user or is damaged. Create a clean profile? The original file will be kept separately.")) { }
}

public static class ProfileProtection
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("Win10ray.DPAPI.v1\n");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Win10ray/profile/current-user/v1");
    public static bool IsProtected(ReadOnlySpan<byte> data) => data.StartsWith(Header);
    public static byte[] Protect(byte[] data)
    {
        byte[] encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        byte[] result = new byte[Header.Length + encrypted.Length];
        Header.CopyTo(result, 0); encrypted.CopyTo(result, Header.Length);
        CryptographicOperations.ZeroMemory(encrypted);
        return result;
    }
    public static byte[] Unprotect(byte[] data)
    {
        if (!IsProtected(data)) throw new ProfileLockedException();
        try { return ProtectedData.Unprotect(data[Header.Length..], Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new ProfileLockedException(); }
    }
    public static void RemoveBackup(string path)
    {
        // No plaintext backup is retained. Erasure on SSDs cannot be guaranteed by overwriting.
        if (File.Exists(path)) File.Delete(path);
    }
}
