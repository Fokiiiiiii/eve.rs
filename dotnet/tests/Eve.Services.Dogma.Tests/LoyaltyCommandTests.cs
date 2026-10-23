using Eve.Marshal;
using Eve.Services.Slash;
using Eve.Wallet;

namespace Eve.Services.Dogma.Tests;

/// <summary><c>/lp</c>, <c>/addlp</c> and <c>/setlp</c>: the balances the store keeps per corporation, the pilot's announcement and the refusals.</summary>
public class LoyaltyCommandTests
{
    [PgFact]
    public async Task A_game_master_gives_sets_and_takes_loyalty_points_per_corporation_and_the_pilot_is_told_each_balance()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Loyal Pilot");
        var session = Rig.Session(pilot.CharacterId);
        var holder = new LoyaltyHolder(LoyaltyHolderKind.Character, pilot.CharacterId);
        var announced = new List<LoyaltyPointChange>();
        rig.Loyalty.BalanceCommitted += announced.Add;

        Assert.Equal("No LP balances.", await rig.SlashAsync(session, "/lp"));
        Assert.Equal("Adjusted Caldari Test Corp(1000035) LP by +500 LP. New balance: 500 LP.", await rig.SlashAsync(session, "/addlp 500 1000035"));
        Assert.Equal("Adjusted Caldari Test Corp(1000035) LP by -200 LP. New balance: 300 LP.", await rig.SlashAsync(session, "/addlp -200 1000035"));
        Assert.Equal("Caldari Test Corp(1000035) LP set to 1,000 LP.", await rig.SlashAsync(session, "/setlp 1000 1000035"));
        Assert.Equal("Caldari Test Corp(1000035) LP set to 1,000 LP.", await rig.SlashAsync(session, "/setlp 1000 1000035"));
        Assert.Equal("LP balances: Caldari Test Corp(1000035): 1,000 LP.", await rig.SlashAsync(session, "/lp"));

        var balance = Assert.Single(await rig.Loyalty.ReadAsync(holder));
        Assert.Equal((FakeStandingDirectory.CaldariCorp, 1000L), (balance.IssuerCorpId, balance.Amount));
        Assert.Equal(new long[] { 500, 300, 1000 }, announced.Select(change => change.After));
    }

    [PgFact]
    public async Task Loyalty_points_are_refused_when_the_issuer_or_the_amount_or_the_balance_cannot_stand()
    {
        await using var rig = await Rig.CreateAsync();
        var pilot = await rig.NewCharacterAsync("Refused Loyal");
        var session = Rig.Session(pilot.CharacterId);
        var announced = new List<LoyaltyPointChange>();
        rig.Loyalty.BalanceCommitted += announced.Add;

        Assert.Equal(LoyaltyCommands.AddUsage, await rig.SlashAsync(session, "/addlp abc 1000035"));
        Assert.Equal(LoyaltyCommands.AddUsage, await rig.SlashAsync(session, "/addlp 0 1000035"));
        Assert.Equal("LP change failed.", await rig.SlashAsync(session, "/setlp -1 1000035"));
        Assert.Equal("NPC corporation not found: nobody.", await rig.SlashAsync(session, "/addlp 5 nobody"));
        Assert.Equal("NPC corporation not found: 500001.", await rig.SlashAsync(session, "/addlp 5 500001"));
        Assert.Equal("LP change failed.", await rig.SlashAsync(session, "/addlp -5 1000035"));
        Assert.Empty(announced);
        Assert.Empty(await rig.Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, pilot.CharacterId)));

        Assert.Equal("Select a character before changing LP.", await rig.SlashAsync(Rig.Session(null), "/addlp 5 1000035"));
        var plain = Rig.Session(pilot.CharacterId);
        plain.Set("accountRole", new PyLong(0));
        Assert.Equal(SlashService.NotGameMaster, await rig.SlashAsync(plain, "/setlp 5 1000035"));
    }
}
