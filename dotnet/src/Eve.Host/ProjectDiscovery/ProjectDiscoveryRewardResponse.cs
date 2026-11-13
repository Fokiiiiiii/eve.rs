using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eve.Host.ProjectDiscovery;

// Game economy fields must describe committed local receipts, not upstream claims.
internal static class ProjectDiscoveryRewardResponse
{
    public static JsonElement DecorateScientific(JsonElement response, long xp, long analysisKredits,
        long totalXp, long totalAnalysisKredits, int bonusSamplesRemaining, bool gotBonusXp,
        bool finishedTutorial, int tutorialLevel)
    {
        var root = JsonNode.Parse(response.GetRawText()) as JsonObject ?? throw new InvalidDataException("Invalid classification response.");
        var rank = RankFor(totalXp);
        root["XP_Reward"] = xp;
        root["ISK_Reward"] = 0;
        root["AK_Reward"] = analysisKredits;
        root["loot_crates"] = new JsonArray();
        root["tier_reward"] = null;
        root["gotBonusXP"] = gotBonusXp;
        root["bonusSamplesAfterClassification"] = bonusSamplesRemaining;
        root["playerState"] = new JsonObject
        {
            ["rank"] = rank,
            ["experience"] = totalXp,
            ["analysisKredits"] = totalAnalysisKredits,
            ["finishedTutorial"] = finishedTutorial,
            ["tutorialLevel"] = tutorialLevel,
        };
        return JsonSerializer.SerializeToElement(root);
    }

    public static JsonElement Decorate(JsonElement response, long bonusXp, string protocol = "covid", long totalXp = 0, int remaining = 0, bool finished = false, int tutorialLevel = 0)
    {
        var root = JsonNode.Parse(response.GetRawText()) as JsonObject ?? throw new InvalidDataException("Invalid classification response.");
        string[] prohibited = ["XP_Reward", "ISK_Reward", "gotBonusXP", "requiredSkillPoints", "playerState", "reward", "rewards", "analysisKredits", "AK_Reward", "loot_crates", "tier_reward", "bonusSamplesAfterClassification"];
        foreach (var key in root.Select(pair => pair.Key).ToArray())
            if (prohibited.Contains(key, StringComparer.OrdinalIgnoreCase)) root.Remove(key);
        if (root["player"] is JsonObject player)
            foreach (var key in player.Select(pair => pair.Key).Where(key => !string.Equals(key, "score", StringComparison.Ordinal)).ToArray()) player.Remove(key);
        if (protocol == "covid")
        {
            if (bonusXp > 0) { root["XP_Reward"] = bonusXp; root["gotBonusXP"] = true; }
        }
        else
        {
            var rank = (long)Math.Floor((1 + Math.Sqrt(1 + totalXp / 12.5)) / 2);
            root["playerState"] = new JsonObject { ["rank"] = rank, ["experience"] = totalXp, ["analysisKredits"] = 0, ["finishedTutorial"] = finished, ["tutorialLevel"] = tutorialLevel };
            root["XP_Reward"] = bonusXp; root["ISK_Reward"] = 0; root["AK_Reward"] = 0;
            root["loot_crates"] = 0; root["tier_reward"] = false; root["gotBonusXP"] = bonusXp > 0; root["bonusSamplesAfterClassification"] = remaining;
        }
        return JsonSerializer.SerializeToElement(root);
    }

    private static long RankFor(long experience)
    {
        var rank = Math.Max(1L, (long)Math.Floor((1 + Math.Sqrt(1 + Math.Max(0, experience) / 12.5)) / 2));
        while (rank < long.MaxValue && (50d * rank * (rank + 1)) <= experience) rank++;
        while (rank > 1 && (50d * (rank - 1) * rank) > experience) rank--;
        return rank;
    }
}
