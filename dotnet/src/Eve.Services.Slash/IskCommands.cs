using System.Globalization;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's wallet commands (EveJS <c>chatCommands.js</c>): <c>/isk</c> and <c>/wallet</c> show the pilot's wallet (the ISK, PLEX, EverMarks and the last ISK change);
/// <c>/addisk &lt;amount&gt;</c> gives ISK (a negative amount takes it away); <c>/setisk &lt;amount&gt;</c> makes the balance the amount. A change is one wallet journal entry in
/// its own transaction, with the ESI reference type of a game master's cash transfer (<see cref="GmCashTransferRefType"/>), and the new balance is announced after the commit.
/// </summary>
public sealed class IskCommands(WalletTransactions wallets, NpgsqlDataSource source, Func<long> nowMs, PgWalletStore balances, LoyaltyPointStore loyalty)
{
    /// <summary>The V24.01 client's <c>appConst.refGMCashTransfer</c> (eve-logic appConst.py), the journal reference the client shows for this.</summary>
    public const int GmCashTransferRefType = 3;

    /// <summary>EVE.js's EverMark issuer corporation (services/corporation/lpWalletState.js): the EverMarks a pilot holds are its LP with it.</summary>
    public const long EverMarkIssuerCorpId = 1000419;

    public const string AddUsage = "Usage: /addisk <amount>";
    public const string SetUsage = "Usage: /setisk <amount>";

    /// <summary>Reads the amount of <c>/addisk</c>: one number with at most two decimals and no zero.</summary>
    public static decimal? ParseAmount(string argument)
    {
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 1) return null;
        if (!decimal.TryParse(words[0], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
        if (amount == 0 || decimal.Round(amount, 2) != amount) return null;
        return amount;
    }

    /// <summary>The wallet of the pilot (EveJS <c>getWalletSummary</c>): <c>/isk</c> and <c>/wallet</c>; the arguments of <c>/isk</c> are not read, as EVE.js does not read them.</summary>
    public async Task<string> WalletAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before checking wallet balance.";
        var balance = await balances.GetAsync(character, cancellationToken);
        var evermarks = (await loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, character), cancellationToken))
            .FirstOrDefault(row => row.IssuerCorpId == EverMarkIssuerCorpId)?.Amount ?? 0;
        var last = await balances.LastIskChangeAsync(character, cancellationToken);
        var change = last == 0 ? "0.00 ISK" : $"{(last > 0 ? "+" : "")}{Isk(last)}";
        return $"Wallet balance: {Isk(balance?.Isk ?? 0m)}. PLEX: {Count(balance?.Plex ?? 0)} PLEX. EverMarks: {Count(evermarks)} EverMarks. Last ISK change: {change}.";
    }

    /// <summary>
    /// <c>/setisk &lt;amount&gt;</c> (EveJS): the pilot's ISK becomes the amount, as one journal entry of the difference. A negative amount is refused as the balance
    /// cannot be negative. The balance is read before the write, in the command's own call: a wallet change between the read and the write would be overwritten (a GM tool is run alone).
    /// </summary>
    public async Task<string> SetAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before changing wallet balance.";
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 1 || !decimal.TryParse(words[0], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var target)
            || decimal.Round(target, 2) != target)
            return SetUsage;
        if (target < 0) return "Wallet change failed: balance cannot be negative.";
        var current = (await balances.GetAsync(character, cancellationToken))?.Isk ?? 0m;
        if (current != target)
        {
            var now = nowMs();
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var balance = await wallets.AdjustAsync(connection, transaction, character, target - current, 0, GmCashTransferRefType, "gm setisk", now,
                $"gm-setisk:{character}:{now}:{Guid.NewGuid():N}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            wallets.AnnounceCommitted(new WalletBalanceChange(character, 1000, balance));
        }
        return $"Wallet balance set to {Isk(target)}.";
    }

    /// <summary>
    /// <c>/addisk &lt;amount&gt;</c> (EveJS): the amount is added to the pilot's ISK (a negative one takes it away). A balance the change would take below zero is refused
    /// as insufficient funds, and nothing is written.
    /// </summary>
    public async Task<string> GiveAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (ParseAmount(argument) is not { } amount) return AddUsage;
        if (session.CharacterId is not { } character) return "Select a character before changing wallet balance.";
        var now = nowMs();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        decimal balance;
        try
        {
            balance = await wallets.AdjustAsync(connection, transaction, character, amount, 0, GmCashTransferRefType, "gm isk", now,
                $"gm-isk:{character}:{now}:{Guid.NewGuid():N}", cancellationToken);
        }
        catch (InsufficientFundsException)
        {
            return "Wallet change failed: insufficient funds.";
        }
        await transaction.CommitAsync(cancellationToken);
        wallets.AnnounceCommitted(new WalletBalanceChange(character, 1000, balance));
        return $"Adjusted wallet by {Isk(amount)}. New balance: {Isk(balance)}.";
    }

    /// <summary>An ISK amount as the client's text has it (EVE.js formatIsk): two decimals, thousands separated, and the currency.</summary>
    private static string Isk(decimal value) => $"{value.ToString("N2", CultureInfo.InvariantCulture)} ISK";

    /// <summary>A whole count with its thousands separators (EVE.js formats PLEX and EverMarks so).</summary>
    private static string Count(long value) => Math.Max(0, value).ToString("N0", CultureInfo.InvariantCulture);
}
