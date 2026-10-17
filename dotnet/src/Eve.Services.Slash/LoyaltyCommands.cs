using System.Globalization;
using Eve.Wallet;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's loyalty point commands (EveJS <c>chatCommands.js</c>): <c>/lp</c> lists the pilot's LP per corporation, <c>/addlp &lt;amount&gt; &lt;corporation&gt;</c>
/// gives LP (a negative amount takes it away, the store refuses a balance below zero) and <c>/setlp &lt;amount&gt; &lt;corporation&gt;</c> makes the balance the amount.
/// The issuer is a corporation of the directory (its ID or exact name). Each change is one commit of the store, which announces it to the pilot.
/// </summary>
public sealed class LoyaltyCommands(LoyaltyPointStore loyalty, IStandingDirectory directory, Func<long> nowMs)
{
    public const string AddUsage = "Usage: /addlp <amount> <npc corp name|corpID>";
    public const string SetUsage = "Usage: /setlp <amount> <npc corp name|corpID>";

    public async Task<string> SummaryAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before checking LP.";
        var rows = await loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, character), cancellationToken);
        return rows.Count == 0
            ? "LP: none."
            : "LP: " + string.Join(", ", rows.Select(row => $"corporation {row.IssuerCorpId}: {row.Amount.ToString(CultureInfo.InvariantCulture)}")) + ".";
    }

    public Task<string> AddAsync(ClientSession session, string argument, CancellationToken cancellationToken) =>
        ChangeAsync(session, argument, AddUsage, adding: true, cancellationToken);

    public Task<string> SetAsync(ClientSession session, string argument, CancellationToken cancellationToken) =>
        ChangeAsync(session, argument, SetUsage, adding: false, cancellationToken);

    private async Task<string> ChangeAsync(ClientSession session, string argument, string usage, bool adding, CancellationToken cancellationToken)
    {
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || !long.TryParse(words[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)) return usage;
        if (adding ? amount == 0 : amount < 0) return usage;
        if (session.CharacterId is not { } character) return "Select a character before changing LP.";
        var corporationToken = string.Join(' ', words.Skip(1));
        if (await directory.FindOwnerAsync(corporationToken, cancellationToken) is not { IsCorporation: true } corporation)
            return $"Could not resolve an NPC corporation from \"{corporationToken}\".";

        var holder = new LoyaltyHolder(LoyaltyHolderKind.Character, character);
        var rows = await loyalty.ReadAsync(holder, cancellationToken);
        var before = rows.FirstOrDefault(row => row.IssuerCorpId == corporation.Id)?.Amount ?? 0;
        var delta = adding ? amount : amount - before;
        var label = corporation.Name.Length == 0 ? $"corporation {corporation.Id}" : $"{corporation.Name}({corporation.Id})";
        if (delta == 0)
            return $"{label} LP is already {before.ToString(CultureInfo.InvariantCulture)}.";
        try
        {
            var now = nowMs();
            var change = await loyalty.ApplyAndCommitAsync(holder, corporation.Id, delta, adding ? "GM /addlp" : "GM /setlp",
                $"gm-lp:{character}:{now}:{Guid.NewGuid():N}", cancellationToken);
            var after = change?.After ?? before;
            return adding
                ? $"{label} LP changed by {delta.ToString(CultureInfo.InvariantCulture)}. Balance: {after.ToString(CultureInfo.InvariantCulture)}."
                : $"{label} LP set to {after.ToString(CultureInfo.InvariantCulture)}.";
        }
        catch (InsufficientLoyaltyPointsException)
        {
            return $"LP change failed: {label} LP is {before.ToString(CultureInfo.InvariantCulture)}, not enough to take {(-delta).ToString(CultureInfo.InvariantCulture)}.";
        }
    }
}
