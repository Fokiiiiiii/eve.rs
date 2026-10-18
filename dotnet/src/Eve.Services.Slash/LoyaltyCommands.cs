using System.Globalization;
using Eve.Wallet;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's loyalty point commands (EveJS <c>chatCommands.js</c>): <c>/lp</c> lists the pilot's LP per corporation (or one corporation's, when one is named),
/// <c>/addlp &lt;amount&gt; &lt;corporation&gt;</c> gives LP (a negative amount takes it away) and <c>/setlp &lt;amount&gt; &lt;corporation&gt;</c> makes the balance the amount.
/// The issuer is a corporation of the directory (its ID or exact name). Each change is one commit of the store, which announces it to the pilot; a change the store refuses
/// (a balance below zero) is answered as EVE.js answers it.
/// </summary>
public sealed class LoyaltyCommands(LoyaltyPointStore loyalty, IStandingDirectory directory, Func<long> nowMs)
{
    public const string AddUsage = "Usage: /addlp <amount> <npc corp name|corpID>";
    public const string SetUsage = "Usage: /setlp <amount> <npc corp name|corpID>";

    /// <summary><c>/lp [corporation]</c>: every balance, or the one of the corporation named (EVE.js <c>getLoyaltyPointSummary</c>).</summary>
    public async Task<string> SummaryAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before checking LP.";
        var holder = new LoyaltyHolder(LoyaltyHolderKind.Character, character);
        var rows = await loyalty.ReadAsync(holder, cancellationToken);
        var token = argument.Trim();
        if (token.Length > 0)
        {
            if (await directory.FindOwnerAsync(token, cancellationToken) is not { IsCorporation: true } corporation) return NotFound(token);
            var amount = rows.FirstOrDefault(row => row.IssuerCorpId == corporation.Id)?.Amount ?? 0;
            return $"{Label(corporation)} LP: {Lp(amount)}.";
        }
        if (rows.Count == 0) return "No LP balances.";
        var balances = new List<string>();
        foreach (var row in rows)
        {
            var corporation = await directory.FindOwnerAsync(row.IssuerCorpId.ToString(CultureInfo.InvariantCulture), cancellationToken)
                ?? new StandingOwner(row.IssuerCorpId, "");
            balances.Add($"{Label(corporation)}: {Lp(row.Amount)}");
        }
        return $"LP balances: {string.Join("; ", balances)}.";
    }

    public Task<string> AddAsync(ClientSession session, string argument, CancellationToken cancellationToken) =>
        ChangeAsync(session, argument, AddUsage, adding: true, cancellationToken);

    public Task<string> SetAsync(ClientSession session, string argument, CancellationToken cancellationToken) =>
        ChangeAsync(session, argument, SetUsage, adding: false, cancellationToken);

    private async Task<string> ChangeAsync(ClientSession session, string argument, string usage, bool adding, CancellationToken cancellationToken)
    {
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || !long.TryParse(words[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)) return usage;
        if (adding && amount == 0) return usage;
        if (session.CharacterId is not { } character) return "Select a character before changing LP.";
        var corporationToken = string.Join(' ', words.Skip(1));
        if (await directory.FindOwnerAsync(corporationToken, cancellationToken) is not { IsCorporation: true } corporation) return NotFound(corporationToken);
        // a set below zero is the store's refusal, as a balance below zero is for the client
        if (!adding && amount < 0) return "LP change failed.";

        var holder = new LoyaltyHolder(LoyaltyHolderKind.Character, character);
        var rows = await loyalty.ReadAsync(holder, cancellationToken);
        var before = rows.FirstOrDefault(row => row.IssuerCorpId == corporation.Id)?.Amount ?? 0;
        var delta = adding ? amount : amount - before;
        try
        {
            var after = before;
            if (delta != 0)
            {
                var now = nowMs();
                var change = await loyalty.ApplyAndCommitAsync(holder, corporation.Id, delta, adding ? "GM /addlp" : "GM /setlp",
                    $"gm-lp:{character}:{now}:{Guid.NewGuid():N}", cancellationToken);
                after = change?.After ?? before;
            }
            return adding
                ? $"Adjusted {Label(corporation)} LP by {Signed(delta)}. New balance: {Lp(after)}."
                : $"{Label(corporation)} LP set to {Lp(after)}.";
        }
        catch (InsufficientLoyaltyPointsException)
        {
            return "LP change failed.";
        }
    }

    /// <summary>The name of a corporation as EVE.js shows it: its name and its ID in brackets, or the ID alone when the name is not known.</summary>
    private static string Label(StandingOwner corporation) =>
        corporation.Name.Length == 0 ? $"corporation {corporation.Id}" : $"{corporation.Name}({corporation.Id})";

    private static string NotFound(string token) => $"NPC corporation not found: {token}.";

    private static string Lp(long value) => $"{Math.Max(0, value).ToString("N0", CultureInfo.InvariantCulture)} LP";

    private static string Signed(long value) => $"{(value > 0 ? "+" : "")}{value.ToString("N0", CultureInfo.InvariantCulture)} LP";
}
