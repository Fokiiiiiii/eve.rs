using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eve.Services.Space;

/// <summary>The password representation used by EveJS starbase access: SHA-256 hex, then scrypt N=16384,r=8,p=1.</summary>
internal static class StarbasePasswordHash
{
    private const int Cost = 16_384;
    private const int ScryptR = 8;
    private const int SalsaWords = 16;
    private const int SalsaBytes = SalsaWords * sizeof(uint);
    private const int ScryptBlockBytes = 128 * ScryptR;

    public static Task<byte[]> DeriveAsync(string password, string salt, CancellationToken cancellationToken = default) =>
        DeriveFromDigestAsync(SHA256.HashData(Encoding.UTF8.GetBytes(password)), salt, cancellationToken);

    /// <summary>
    /// Derives the stored hash from the raw SHA-256 digest of the password. The scrypt input is the lowercase hex text of
    /// the digest, as EveJS passes it, so a digest kept in place of the password is enough to check the password again.
    /// </summary>
    public static async Task<byte[]> DeriveFromDigestAsync(byte[] digest, string salt, CancellationToken cancellationToken = default)
    {
        var passwordBytes = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(digest));
        var saltBytes = Encoding.ASCII.GetBytes(salt);
        try
        {
            return await Task.Run(() => Scrypt(passwordBytes, saltBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static Task<bool> MatchesAsync(string? configuration, string password, CancellationToken cancellationToken = default) =>
        MatchesDigestAsync(configuration, SHA256.HashData(Encoding.UTF8.GetBytes(password)), cancellationToken);

    /// <summary>Checks the SHA-256 digest of a password against one tower's saved hash; the password itself is not needed.</summary>
    public static async Task<bool> MatchesDigestAsync(string? configuration, byte[] digest, CancellationToken cancellationToken = default)
    {
        if (configuration is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(configuration);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("passwordSalt", out var saltProperty) || saltProperty.GetString() is not { } salt ||
                !root.TryGetProperty("passwordHash", out var hashProperty) || hashProperty.GetString() is not { } hash ||
                salt.Length != 32 || hash.Length != 64 || !IsLowerHex(salt) || !IsLowerHex(hash))
                return false;

            var expected = Convert.FromHexString(hash);
            var actual = await DeriveFromDigestAsync(digest, salt, cancellationToken).ConfigureAwait(false);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
                CryptographicOperations.ZeroMemory(expected);
            }
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsLowerHex(string value) => value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] Scrypt(byte[] password, byte[] salt, CancellationToken cancellationToken)
    {
        var blockBytes = ScryptBlockBytes;
        var block = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, blockBytes);
        var memory = new byte[Cost * blockBytes];
        var mixed = new byte[blockBytes];
        var reordered = new byte[blockBytes];
        try
        {
            for (var i = 0; i < Cost; i++)
            {
                if ((i & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                block.CopyTo(memory, i * blockBytes);
                BlockMix(block, mixed, reordered);
            }

            for (var i = 0; i < Cost; i++)
            {
                if ((i & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var row = (int)(BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(blockBytes - SalsaBytes, sizeof(ulong))) & (Cost - 1));
                var selected = memory.AsSpan(row * blockBytes, blockBytes);
                for (var j = 0; j < blockBytes; j++)
                    block[j] ^= selected[j];
                BlockMix(block, mixed, reordered);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Rfc2898DeriveBytes.Pbkdf2(password, block, 1, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
            CryptographicOperations.ZeroMemory(memory);
            CryptographicOperations.ZeroMemory(mixed);
            CryptographicOperations.ZeroMemory(reordered);
        }
    }

    private static void BlockMix(byte[] block, byte[] mixed, byte[] reordered)
    {
        Span<byte> x = stackalloc byte[SalsaBytes];
        block.AsSpan(block.Length - SalsaBytes, SalsaBytes).CopyTo(x);
        for (var i = 0; i < 2 * ScryptR; i++)
        {
            var input = block.AsSpan(i * SalsaBytes, SalsaBytes);
            for (var j = 0; j < SalsaBytes; j++)
                x[j] ^= input[j];
            Salsa208(x);
            x.CopyTo(mixed.AsSpan(i * SalsaBytes, SalsaBytes));
        }

        for (var i = 0; i < ScryptR; i++)
        {
            mixed.AsSpan((2 * i) * SalsaBytes, SalsaBytes).CopyTo(reordered.AsSpan(i * SalsaBytes, SalsaBytes));
            mixed.AsSpan((2 * i + 1) * SalsaBytes, SalsaBytes).CopyTo(reordered.AsSpan((i + ScryptR) * SalsaBytes, SalsaBytes));
        }
        reordered.CopyTo(block, 0);
        CryptographicOperations.ZeroMemory(x);
    }

    private static void Salsa208(Span<byte> block)
    {
        Span<uint> x = stackalloc uint[SalsaWords];
        Span<uint> original = stackalloc uint[SalsaWords];
        for (var i = 0; i < SalsaWords; i++)
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * sizeof(uint), sizeof(uint)));
        x.CopyTo(original);

        for (var round = 0; round < 8; round += 2)
        {
            x[4] ^= RotateLeft(x[0] + x[12], 7); x[8] ^= RotateLeft(x[4] + x[0], 9); x[12] ^= RotateLeft(x[8] + x[4], 13); x[0] ^= RotateLeft(x[12] + x[8], 18);
            x[9] ^= RotateLeft(x[5] + x[1], 7); x[13] ^= RotateLeft(x[9] + x[5], 9); x[1] ^= RotateLeft(x[13] + x[9], 13); x[5] ^= RotateLeft(x[1] + x[13], 18);
            x[14] ^= RotateLeft(x[10] + x[6], 7); x[2] ^= RotateLeft(x[14] + x[10], 9); x[6] ^= RotateLeft(x[2] + x[14], 13); x[10] ^= RotateLeft(x[6] + x[2], 18);
            x[3] ^= RotateLeft(x[15] + x[11], 7); x[7] ^= RotateLeft(x[3] + x[15], 9); x[11] ^= RotateLeft(x[7] + x[3], 13); x[15] ^= RotateLeft(x[11] + x[7], 18);
            x[1] ^= RotateLeft(x[0] + x[3], 7); x[2] ^= RotateLeft(x[1] + x[0], 9); x[3] ^= RotateLeft(x[2] + x[1], 13); x[0] ^= RotateLeft(x[3] + x[2], 18);
            x[6] ^= RotateLeft(x[5] + x[4], 7); x[7] ^= RotateLeft(x[6] + x[5], 9); x[4] ^= RotateLeft(x[7] + x[6], 13); x[5] ^= RotateLeft(x[4] + x[7], 18);
            x[11] ^= RotateLeft(x[10] + x[9], 7); x[8] ^= RotateLeft(x[11] + x[10], 9); x[9] ^= RotateLeft(x[8] + x[11], 13); x[10] ^= RotateLeft(x[9] + x[8], 18);
            x[12] ^= RotateLeft(x[15] + x[14], 7); x[13] ^= RotateLeft(x[12] + x[15], 9); x[14] ^= RotateLeft(x[13] + x[12], 13); x[15] ^= RotateLeft(x[14] + x[13], 18);
        }

        for (var i = 0; i < SalsaWords; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * sizeof(uint), sizeof(uint)), unchecked(x[i] + original[i]));
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(x));
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(original));
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
