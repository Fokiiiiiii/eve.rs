using System.Security.Cryptography;
using System.Text;

namespace Eve.Host.PublicGateway;

/// <summary>Creates the opaque 64-character key used by the V24 SKINR design-data API.</summary>
internal static class ShipSkinrDesignId
{
    public static string Create(long characterId, string name, string lineName, int tier, ReadOnlySpan<byte> layout)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(characterId);
            writer.Write(name);
            writer.Write(lineName);
            writer.Write(tier);
            writer.Write(layout.Length);
            writer.Write(layout);
        }

        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }
}
