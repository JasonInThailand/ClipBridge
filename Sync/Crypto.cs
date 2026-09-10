using System.Security.Cryptography;
using System.Text;

namespace ClipBridge.Sync;

/// <summary>AES-256-GCM framing keyed from the shared passphrase. Each frame = 12-byte nonce | ciphertext | 16-byte tag.</summary>
public sealed class FrameCrypto
{
    private readonly byte[] _key;
    public string Fingerprint { get; }

    public FrameCrypto(string secret)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("ClipBridge-v1:" + secret));
        Fingerprint = Convert.ToHexString(SHA256.HashData(_key), 0, 4);
    }

    public byte[] Seal(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, ct, tag);
        var frame = new byte[12 + ct.Length + 16];
        Buffer.BlockCopy(nonce, 0, frame, 0, 12);
        Buffer.BlockCopy(ct, 0, frame, 12, ct.Length);
        Buffer.BlockCopy(tag, 0, frame, 12 + ct.Length, 16);
        return frame;
    }

    public byte[]? Open(byte[] frame)
    {
        if (frame.Length < 28) return null;
        try
        {
            var nonce = frame.AsSpan(0, 12);
            var tag = frame.AsSpan(frame.Length - 16, 16);
            var ct = frame.AsSpan(12, frame.Length - 28);
            var plain = new byte[ct.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, ct, tag, plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}
