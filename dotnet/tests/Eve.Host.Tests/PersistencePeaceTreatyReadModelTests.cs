using System.Text.Json.Nodes;
using Eve.Host;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public sealed class PersistencePeaceTreatyReadModelTests
{
    [Fact]
    public void Reads_only_unexpired_treaties_and_sorts_each_owner_direction_like_EveJS()
    {
        const long now = 134_000_000_000_000_000;
        var state = JsonNode.Parse($$"""
            {
              "peaceTreaties": {
                "10": { "treatyID": 10, "ownerID": 1, "otherOwnerID": 2, "expiryDate": "{{now + 20}}", "peaceReason": 3, "warID": 99, "reasonEnded": 0, "warHQ": 700 },
                "9": { "treatyID": 9, "ownerID": 1, "otherOwnerID": 3, "expiryDate": "{{now + 10}}", "peaceReason": 6, "warID": 95, "reasonEnded": 0 },
                "11": { "treatyID": 11, "ownerID": 2, "otherOwnerID": 1, "expiryDate": "{{now + 30}}", "peaceReason": 4, "warID": 98, "reasonEnded": 0, "warHQID": 701 },
                "12": { "treatyID": 12, "ownerID": 1, "otherOwnerID": 2, "expiryDate": "{{now}}", "peaceReason": 5, "warID": 97 },
                "13": { "treatyID": 13, "ownerID": 1, "otherOwnerID": 2, "expiryDate": "not-a-filetime", "warID": 96 }
              }
            }
            """)!.AsObject();

        var result = PersistencePeaceTreatyReadModel.Read(state, ownerId: 1, now);

        Assert.Equal(
            [
                new PeaceTreaty(2, now + 20, 3, 99, 0, 700),
                new PeaceTreaty(3, now + 10, 6, 95, 0, null),
            ],
            result.Outgoing);
        Assert.Equal(
            [new PeaceTreaty(1, now + 30, 4, 98, 0, 701)],
            result.Incoming);
    }
}
