using System.Security.Cryptography;
using System.Text;

namespace CloudMusicPresence;

public static class DefaultAppId
{
    private static readonly byte[] Key = Convert.FromBase64String("tRPONWpJbdlo3W5AfilVd9Z75lbTtQpmzGeDUShcI+g=");
    private static readonly byte[] Iv = Convert.FromBase64String("nPJQUsECxwXJLcxryYKQIA==");
    private static readonly byte[] Cipher = Convert.FromBase64String("YLkYjDabSUnXW0lnvAFSob3Npxz6udghvrwdMsASotI=");

    public static string Resolve(string? customInput = null)
    {
        string trimmed = (customInput ?? "").Trim();
        if (trimmed.Length > 0) return trimmed;
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        using var decryptor = aes.CreateDecryptor();
        byte[] plain = decryptor.TransformFinalBlock(Cipher, 0, Cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    public static bool IsValid(string id) => id.Length is >= 17 and <= 22 && id.All(char.IsAsciiDigit);
}
