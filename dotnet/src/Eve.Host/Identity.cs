using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Eve.Host;

/// <summary>What the front knows about a login attempt.</summary>
public sealed record LoginAttempt(string UserName, ReadOnlyMemory<byte> PasswordHash, string Language, string Address);

/// <summary>Decides who may log in, and as which account.</summary>
public interface IIdentityDirectory
{
    ValueTask<AuthOutcome> AuthenticateAsync(LoginAttempt attempt, CancellationToken cancellationToken);
}

public sealed record DevIdentityOptions
{
    /// <summary>An unknown user name creates an account with the password hash it presented.</summary>
    public bool AutoCreate { get; init; } = true;

    /// <summary>Accept any password hash. For development only: it makes every account open to anyone.</summary>
    public bool SkipPasswordCheck { get; init; }

    public string CountryCode { get; init; } = "GB";

    /// <summary>The role mask new accounts get (0 is an ordinary player).</summary>
    public ulong Role { get; init; }
}

/// <summary>
/// An in-memory account directory for development: accounts are created on first login and then need the
/// same password hash. It is replaced by the identity module (S2), which keeps accounts in the database.
/// </summary>
public sealed class DevIdentityDirectory(DevIdentityOptions options) : IIdentityDirectory
{
    private sealed record Account(uint Id, byte[] PasswordHash);

    public const string LoginFailed = "LoginAuthFailed";

    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly object _create = new();
    private uint _lastId;

    public ValueTask<AuthOutcome> AuthenticateAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(attempt.UserName, out var account))
        {
            if (!options.AutoCreate)
            {
                return ValueTask.FromResult<AuthOutcome>(new Rejected(LoginFailed));
            }
            lock (_create)
            {
                if (!_accounts.TryGetValue(attempt.UserName, out account))
                {
                    account = new Account(++_lastId, attempt.PasswordHash.ToArray());
                    _accounts[attempt.UserName] = account;
                }
            }
        }
        if (!options.SkipPasswordCheck && !CryptographicOperations.FixedTimeEquals(account.PasswordHash, attempt.PasswordHash.Span))
        {
            return ValueTask.FromResult<AuthOutcome>(new Rejected(LoginFailed));
        }
        return ValueTask.FromResult<AuthOutcome>(new Accepted(account.Id, options.Role, options.CountryCode));
    }
}
