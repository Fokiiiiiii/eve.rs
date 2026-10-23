namespace Eve.Host.PublicGateway;

/// <summary>
/// Who a public gateway request is for. The identity the client puts in the request (the active character) is not trusted on its own: the
/// account is the subject of the bearer token the client sends (the login JWT, <c>USER:EVE:&lt;account&gt;</c>, the subject the client's own
/// token decoder reads), and the active character must belong to that account. Host-issued tokens have a verified HMAC signature and expiry.

/// </summary>
internal sealed class GatewayAuthority(Func<long, CancellationToken, Task<long?>> accountOfCharacter, Func<DateTimeOffset> now)
{
    /// <summary>The verified identity in a bearer token, or null when the token is absent, invalid, forged, or expired.</summary>
    public GatewayTokenIdentity? IdentityOf(string? authorization) =>
        GatewayTokens.TryReadIdentity(authorization, now(), out var identity) ? identity : null;

    /// <summary>The account of a valid bearer token.</summary>
    public long? AccountOf(string? authorization) => IdentityOf(authorization)?.AccountId;

    /// <summary>
    /// The refusal of a request for a character: 401 without a valid user token, 403 when its character claim or stored account does not match.
    /// Null when the request may proceed.
    /// </summary>
    public async Task<GatewayAnswer?> RefusalAsync(GatewayTokenIdentity? identity, long? character, CancellationToken cancellationToken)
    {
        if (identity is not { } verified)
            return new GatewayAnswer(401, "a valid user token is required", "", []);
        if (character is { } id)
        {
            if (verified.CharacterId is { } claimedCharacter && claimedCharacter != id)
                return new GatewayAnswer(403, "the character does not match the token identity", "", []);
            if (await accountOfCharacter(id, cancellationToken) != verified.AccountId)
                return new GatewayAnswer(403, "the character is not on the token's account", "", []);
        }
        return null;
    }

    /// <summary>Whether a character is both named by the token when scoped and owned by its verified account.</summary>
    public async Task<bool> OwnsAsync(GatewayTokenIdentity identity, long character, CancellationToken cancellationToken) =>
        (identity.CharacterId is null || identity.CharacterId == character)
        && await accountOfCharacter(character, cancellationToken) == identity.AccountId;
}
