using System.Text.Json.Nodes;
using Eve.GameStore;

namespace Eve.Services.Core;

/// <summary>
/// Reads an account's Omega expiry from the game store's <c>newEdenStoreRuntime</c> table
/// (<c>accounts[id].omegaExpiryFileTime</c>). The table must have been loaded into the cache.
/// </summary>
public sealed class GameStoreOmegaAccounts(GameStoreCache cache) : IOmegaAccountStore
{
    public const string Table = "newEdenStoreRuntime";

    public string? GetOmegaExpiry(long accountId)
    {
        var result = cache.Read(Table, $"/accounts/{accountId}/omegaExpiryFileTime");
        // Only a non-blank string counts; it is used trimmed.
        return result is { Success: true, Data: JsonValue value }
            && value.TryGetValue<string>(out var text)
            && text.Trim() is { Length: > 0 } trimmed
                ? trimmed
                : null;
    }
}
