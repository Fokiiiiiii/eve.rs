using System.Text.Json.Nodes;
using Eve.Host;

namespace Eve.Host.Tests;

public sealed class PersistenceCharacterRpcReadModelTests
{
    [Fact]
    public void Jump_timers_match_evejs_expiry_fallback_and_last_updated_rules()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var activation = now.AddMinutes(3).ToFileTime();
        var expiredFatigue = now.AddMinutes(-2).ToFileTime();
        var lastUpdated = now.AddMinutes(-1).ToFileTime();
        var character = new JsonObject
        {
            ["jumpTimers"] = new JsonObject
            {
                ["jumpActivation"] = 0,
                ["jumpActivationExpireDate"] = activation.ToString(),
                ["jumpFatigue"] = expiredFatigue.ToString(),
                ["lastUpdated"] = lastUpdated.ToString(),
            },
            ["jumpFatigueExpireDate"] = now.AddMinutes(5).ToFileTime().ToString(),
        };

        var timers = PersistenceCharacterRpcReadModel.ReadJumpTimers(character, now);

        Assert.Equal(activation, timers?.JumpActivation);
        Assert.Null(timers?.JumpFatigue);
        Assert.Equal(lastUpdated, timers?.LastUpdated);
    }

    [Fact]
    public void Expired_timers_and_missing_injection_state_match_the_empty_wire_values()
    {
        var now = DateTimeOffset.UtcNow;
        var character = new JsonObject
        {
            ["jumpTimers"] = new JsonObject
            {
                ["jumpActivation"] = now.AddMinutes(-2).ToFileTime().ToString(),
                ["jumpFatigue"] = now.AddMinutes(-1).ToFileTime().ToString(),
                ["lastUpdated"] = now.ToFileTime().ToString(),
            },
        };
        var table = new JsonObject
        {
            ["42"] = new JsonObject { ["nonDiminishingInjectionsRemaining"] = 7 },
            ["43"] = new JsonObject { ["nonDiminishingInjectionsRemaining"] = -4 },
        };

        Assert.Null(PersistenceCharacterRpcReadModel.ReadJumpTimers(character, now));
        Assert.Equal(7, PersistenceCharacterRpcReadModel.ReadNonDiminishingInjections(table, 42));
        Assert.Equal(0, PersistenceCharacterRpcReadModel.ReadNonDiminishingInjections(table, 43));
        Assert.Equal(0, PersistenceCharacterRpcReadModel.ReadNonDiminishingInjections(table, 44));
        Assert.Equal(0, PersistenceCharacterRpcReadModel.ReadNonDiminishingInjections(table, 0));
    }
}
