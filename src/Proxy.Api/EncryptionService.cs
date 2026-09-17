using System.Security.Cryptography;
using System.Text;

namespace NovaWallet.ProxyApi;

public sealed class EncryptionService
{
    private readonly byte[] _key;

    public EncryptionService(IConfiguration config)
    {
        var keyStr = config["Encryption:Key"] ?? "NovaWallet2026EncryptionKey32Bytes!";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyStr));
    }

    public string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
        var output = new byte[aes.IV.Length + encrypted.Length];
        aes.IV.CopyTo(output, 0);
        encrypted.CopyTo(output, aes.IV.Length);
        return Convert.ToBase64String(output);
    }

    public string Decrypt(string ciphertext)
    {
        var fullBytes = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        var iv = new byte[16];
        Array.Copy(fullBytes, 0, iv, 0, 16);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var encrypted = new byte[fullBytes.Length - 16];
        Array.Copy(fullBytes, 16, encrypted, 0, encrypted.Length);
        var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
        return Encoding.UTF8.GetString(decrypted);
    }

    public sealed class EncryptedWrapper(string? Data)
    {
        public string? Data { get; set; } = Data;
    }
}
