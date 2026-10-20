namespace Eve.Host.PublicGateway;

/// <summary>
/// Who a public gateway request is for. The identity the client puts in the request (the active character) is not trusted on its own: the
/// account is the subject of the bearer token the client sends (the login JWT, <c>USER:EVE:&lt;account&gt;</c>, the subject the client's own
/// token decoder reads), and the active character must belong to that account. Host-issued tokens have a verified HMAC signature and expiry.

/// </summary>
internal sealed class GatewayAuthority(Func<long, CancellationToken, Task<long?>> accountOfCharacter, Func<DateTimeOffset> now)
{
    /// <summary>The account a bearer token belongs to, or null when the token is missing, not a JWT, not a user's, or expired.</summary>
    public long? AccountOf(string? authorization) =>
        GatewayTokens.TryReadAccount(authorization, now(), out var account) ? account : null;

    /// <summary>
    /// The refusal of a request for a character: 401 without a user's token, 403 when the character is not on the token's account (or does not
    /// exist). Null when the request may proceed.
    /// </summary>
    public async Task<GatewayAnswer?> RefusalAsync(long? account, long? character, CancellationToken cancellationToken)
    {
        if (account is null)
            return new GatewayAnswer(401, "a user token is required", "", []);
        if (character is { } id && await accountOfCharacter(id, cancellationToken) != account)
            return new GatewayAnswer(403, "the character is not on the token's account", "", []);
        return null;
    }

    /// <summary>Whether a character belongs to an account (for the notice stream, which only routes what the identity may see).</summary>
    public async Task<bool> OwnsAsync(long account, long character, CancellationToken cancellationToken) =>
        await accountOfCharacter(character, cancellationToken) == account;

}
