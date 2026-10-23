using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Eve.Identity;

/// <summary>What the front knows about a login attempt.</summary>
public sealed record LoginAttempt(string UserName, ReadOnlyMemory<byte> PasswordHash, string Language, string Address);

public abstract record LoginResult;

/// <summary>The login was accepted, as this account.</summary>
public sealed record LoggedIn(uint AccountId, ulong Role, string CountryCode) : LoginResult;

/// <summary>A CCP identity whose JWT signature and claims have already been verified.</summary>
public sealed record VerifiedSsoIdentity(string Issuer, string Subject);

/// <summary>Verifies a client-provided CCP SSO access token. Invalid tokens return null; provider failures throw.</summary>
public interface ISsoTokenVerifier
{
    ValueTask<VerifiedSsoIdentity?> VerifyAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Resolves a verified CCP identity through an explicitly linked eve.rs account.</summary>
public interface ISsoAccountDirectory
{
    ValueTask<LoginResult> LoginSsoAsync(VerifiedSsoIdentity identity, CancellationToken cancellationToken);
}

/// <summary>Creates an explicit issuer/subject to account mapping for provisioning and tests.</summary>
public interface ISsoIdentityLinker
{
    ValueTask<bool> LinkSsoIdentityAsync(uint accountId, VerifiedSsoIdentity identity, CancellationToken cancellationToken);
}

internal static class SsoIdentityRules
{
    public static bool IsAcceptable(VerifiedSsoIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Issuer) || identity.Issuer.Length > 512 ||
            identity.Issuer.Contains('\0'))
            return false;
        const string prefix = "CHARACTER:EVE:";
        if (!identity.Subject.StartsWith(prefix, StringComparison.Ordinal) ||
            identity.Subject.Length > 128 ||
            !ulong.TryParse(identity.Subject.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var characterId))
            return false;
        return characterId > 0;
    }
}

/// <summary>The login was refused; <see cref="Reason"/> is the code the client is shown.</summary>
public sealed record LoginRefused(string Reason) : LoginResult
{
    public const string BadCredentials = "LoginAuthFailed";
    public const string Banned = "ACCOUNTBANNED";
    public const string Unavailable = "Unavailable";
}

/// <summary>Who may log in, and as which account.</summary>
public interface IAccountDirectory
{
    ValueTask<LoginResult> LoginAsync(LoginAttempt attempt, CancellationToken cancellationToken);
}

/// <summary>What other modules may know about an account: its stored subscription end time (null when it has none).</summary>
public sealed record AccountInfo(uint Id, string UserName, DateTimeOffset CreatedAt, DateTimeOffset? SubscriptionEndsAt = null);

/// <summary>Reads an account by its id (the character selection screen shows the user name and when it began).</summary>
public interface IAccountLookup
{
    ValueTask<AccountInfo?> FindAsync(uint accountId, CancellationToken cancellationToken);
}

/// <summary>
/// The subscription policy of this server: every account is Omega (no real-money charge) until 2100-01-01T00:00:00Z.
/// The end time is written to an account when it is created and back-filled for accounts that have none. Once stored,
/// it is the authority: an operator may set an earlier end time, and the account lapses to Alpha.
/// </summary>
public static class SubscriptionPolicy
{
    public static readonly DateTimeOffset NewAccountSubscriptionEnd = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The latest end time a purchase may set. A purchase never runs an account (or a training slot) past this horizon, and an end
    /// time at or past it is left as it is: an account written with the policy end time keeps it when it buys more time.
    /// </summary>
    public static DateTimeOffset PurchaseHorizon => NewAccountSubscriptionEnd;

    /// <summary>
    /// The end time after a purchase of <paramref name="extension"/>: counted from the later of the stored end and <paramref name="now"/>,
    /// capped at <see cref="PurchaseHorizon"/>. It never moves an end time earlier; a stored end at or past the horizon is returned unchanged.
    /// </summary>
    public static DateTimeOffset Extend(DateTimeOffset stored, DateTimeOffset now, TimeSpan extension)
    {
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extension), "A purchase extends an end time by a positive duration.");
        }
        if (stored >= PurchaseHorizon)
        {
            return stored;
        }
        var from = stored > now ? stored : now;
        var target = from + extension;
        if (target > PurchaseHorizon)
        {
            target = PurchaseHorizon;
        }
        return target > stored ? target : stored;
    }
}

/// <summary>An account's stored subscription end time: read by every path that answers Omega or Alpha, and set by an operator.</summary>
public interface IAccountSubscriptions
{
    /// <summary>The stored end time, or null when the account has none (or does not exist).</summary>
    DateTimeOffset? SubscriptionEndsAt(uint accountId);

    /// <summary>Sets the stored end time. The next read, by any path, sees it.</summary>
    void SetSubscriptionEndsAt(uint accountId, DateTimeOffset endsAt);
}

public sealed record AccountOptions
{
    /// <summary>When enabled, an unknown user name creates an account with the password hash it presented.</summary>
    public bool AutoCreate { get; init; } = false;

    /// <summary>Accept any password hash. For development only: it makes every account open to anyone.</summary>
    public bool SkipPasswordCheck { get; init; }

    public string CountryCode { get; init; } = "GB";

    /// <summary>The role mask a new account gets (0 is an ordinary player).</summary>
    public ulong NewAccountRole { get; init; }
}

/// <summary>Which user names may exist.</summary>
public static class AccountNames
{
    public const int MaxLength = 128;

    /// <summary>A name with something in it, of sensible length, without a NUL (PostgreSQL text cannot hold one).</summary>
    public static bool IsAcceptable(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxLength && !name.Contains('\0');
}

/// <summary>
/// Accounts kept in memory: for tests and for running without a database. Nothing survives a restart.
/// </summary>
public sealed class InMemoryAccountDirectory : IAccountDirectory, IAccountLookup, IAccountSubscriptions, ISsoAccountDirectory, ISsoIdentityLinker
{
    private sealed class Account(uint id, string userName, byte[] passwordHash, ulong role, DateTimeOffset createdAt)
    {
        public uint Id { get; } = id;

        public string UserName { get; } = userName;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public byte[] PasswordHash { get; } = passwordHash;

        public ulong Role { get; } = role;

        public bool Banned { get; set; }

        public DateTimeOffset SubscriptionEndsAt { get; set; } = SubscriptionPolicy.NewAccountSubscriptionEnd;
    }

    /// <summary>The accounts themselves, shared by the views <see cref="WithOptions"/> makes.</summary>
    private sealed class Store
    {
        public ConcurrentDictionary<string, Account> Accounts { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<(string Issuer, string Subject), uint> SsoLinks { get; } = new();

        public Lock Create { get; } = new();

        public uint LastId { get; set; }
    }

    private readonly AccountOptions _options;
    private readonly Store _store;

    public InMemoryAccountDirectory(AccountOptions options)
        : this(options, new Store())
    {
    }

    private InMemoryAccountDirectory(AccountOptions options, Store store)
    {
        _options = options;
        _store = store;
    }

    /// <summary>The same accounts, seen with other options (two servers over one database, in a test).</summary>
    public InMemoryAccountDirectory WithOptions(AccountOptions options) => new(options, _store);

    public void Ban(string userName)
    {
        if (_store.Accounts.TryGetValue(userName, out var account))
        {
            account.Banned = true;
        }
    }

    public ValueTask<AccountInfo?> FindAsync(uint accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_store.Accounts.Values.FirstOrDefault(a => a.Id == accountId) is { } account
            ? new AccountInfo(account.Id, account.UserName, account.CreatedAt, account.SubscriptionEndsAt)
            : null);

    public DateTimeOffset? SubscriptionEndsAt(uint accountId) =>
        _store.Accounts.Values.FirstOrDefault(a => a.Id == accountId)?.SubscriptionEndsAt;

    public void SetSubscriptionEndsAt(uint accountId, DateTimeOffset endsAt)
    {
        if (_store.Accounts.Values.FirstOrDefault(a => a.Id == accountId) is { } account)
        {
            account.SubscriptionEndsAt = endsAt;
        }
    }

    public ValueTask<bool> LinkSsoIdentityAsync(uint accountId, VerifiedSsoIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SsoIdentityRules.IsAcceptable(identity) || !_store.Accounts.Values.Any(account => account.Id == accountId))
            return ValueTask.FromResult(false);
        var key = (identity.Issuer, identity.Subject);
        if (_store.SsoLinks.TryAdd(key, accountId))
            return ValueTask.FromResult(true);
        return ValueTask.FromResult(_store.SsoLinks.TryGetValue(key, out var linkedAccount) && linkedAccount == accountId);
    }

    public ValueTask<LoginResult> LoginSsoAsync(VerifiedSsoIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SsoIdentityRules.IsAcceptable(identity) ||
            !_store.SsoLinks.TryGetValue((identity.Issuer, identity.Subject), out var accountId))
            return ValueTask.FromResult<LoginResult>(new LoginRefused(LoginRefused.BadCredentials));
        var account = _store.Accounts.Values.FirstOrDefault(candidate => candidate.Id == accountId);
        if (account is null)
            return ValueTask.FromResult<LoginResult>(new LoginRefused(LoginRefused.BadCredentials));
        return ValueTask.FromResult<LoginResult>(account.Banned
            ? new LoginRefused(LoginRefused.Banned)
            : new LoggedIn(account.Id, account.Role, _options.CountryCode));
    }

    public ValueTask<LoginResult> LoginAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        if (!AccountNames.IsAcceptable(attempt.UserName))
        {
            return ValueTask.FromResult<LoginResult>(new LoginRefused(LoginRefused.BadCredentials));
        }
        if (!_store.Accounts.TryGetValue(attempt.UserName, out var account))
        {
            if (!_options.AutoCreate)
            {
                return ValueTask.FromResult<LoginResult>(new LoginRefused(LoginRefused.BadCredentials));
            }
            lock (_store.Create)
            {
                if (!_store.Accounts.TryGetValue(attempt.UserName, out account))
                {
                    account = new Account(++_store.LastId, attempt.UserName, attempt.PasswordHash.ToArray(), _options.NewAccountRole, DateTimeOffset.UtcNow);
                    _store.Accounts[attempt.UserName] = account;
                }
            }
        }
        return ValueTask.FromResult(Decide(account.Banned, account.PasswordHash, account.Id, account.Role, attempt, _options));
    }

    /// <summary>The refusal, or the login, for an account that exists. A ban outranks a wrong password.</summary>
    internal static LoginResult Decide(bool banned, ReadOnlySpan<byte> storedHash, uint id, ulong role, LoginAttempt attempt, AccountOptions options)
    {
        var passwordOk = options.SkipPasswordCheck || CryptographicOperations.FixedTimeEquals(storedHash, attempt.PasswordHash.Span);
        if (banned)
        {
            return new LoginRefused(LoginRefused.Banned);
        }
        return passwordOk ? new LoggedIn(id, role, options.CountryCode) : new LoginRefused(LoginRefused.BadCredentials);
    }
}
