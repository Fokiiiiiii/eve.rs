using Eve.Characters;

namespace Eve.Host.PublicGateway;

/// <summary>
/// The character title namespace of the public gateway (eve_public.cosmetic.character.title.api): the titles a character owns, the one it
/// wears, and wearing and taking off (EveJS achievementGatewayService.js). A title is the SDE's (its id must be a character title of the
/// SDE); an owned title comes from a reward (not yet claimed through the gateway, so a character owns the titles it was granted). Equipping
/// a title the character does not own is refused with 409, as the reference does. The definition's CDN checkpoint is not served yet.
/// </summary>
internal static class TitleGateway
{
    private const string Prefix = "eve_public.cosmetic.character.title.api.";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static IReadOnlyDictionary<string, GatewayHandler> Handlers(CharacterTitleStore store, IReadOnlySet<Guid> knownTitles)
    {
        var handlers = new Dictionary<string, GatewayHandler>(StringComparer.Ordinal);

        Add(handlers, "GetOwnedRequest", "GetOwnedResponse", async (request, ct) =>
        {
            var page = SkillPlanWire.Message(request.Payload, 1) ?? ReadOnlyMemory<byte>.Empty;
            var size = SkillPlanWire.VarintOf(page, 1) is > 0 and var requested ? (int)Math.Min(requested, MaxPageSize) : DefaultPageSize;
            var offset = int.TryParse(SkillPlanWire.StringOf(page, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var start) ? start : 0;
            // one more than the page tells whether there is a next page
            var titles = await store.OwnedAsync(request.CharacterId!.Value, offset, size + 1, ct);
            var writer = new ProtoWriter();
            foreach (var title in titles.Take(size))
                writer.Message(1, SkillPlanWire.EncodeIdentifier(title));
            if (titles.Count > size)
            {
                var next = new ProtoWriter();
                next.String(1, (offset + size).ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.Message(2, next.ToArray());
            }
            return writer.ToArray();
        });

        Add(handlers, "EquipRequest", "EquipResponse", async (request, ct) =>
        {
            var title = SkillPlanWire.IdentifierOf(SkillPlanWire.Message(request.Payload, 1) ?? ReadOnlyMemory<byte>.Empty);
            if (!knownTitles.Contains(title) || !await store.EquipAsync(request.CharacterId!.Value, title, ct))
                throw new TitleRefusal("title equip failed");
            return [];
        });

        Add(handlers, "UnequipRequest", "UnequipResponse", async (request, ct) =>
        {
            await store.UnequipAsync(request.CharacterId!.Value, ct);
            return [];
        });

        Add(handlers, "GetEquippedForCharacterRequest", "GetEquippedForCharacterResponse", async (request, ct) =>
        {
            // the character named in the request (its Identifier's sequential), else the active character
            var named = SkillPlanWire.Message(request.Payload, 1) is { } character ? SkillPlanWire.VarintOf(character, 1) : 0;
            var characterId = named > 0 ? named : request.CharacterId!.Value;
            var writer = new ProtoWriter();
            if (await store.EquippedAsync(characterId, ct) is { } equipped)
                writer.Message(1, SkillPlanWire.EncodeIdentifier(equipped));
            else
                writer.Bool(2, true);
            return writer.ToArray();
        });

        return handlers;
    }

    /// <summary>A refusal the client reads as a 409 (EveJS: the title is not owned).</summary>
    private sealed class TitleRefusal(string message) : Exception(message);

    private static void Add(Dictionary<string, GatewayHandler> handlers, string request, string response,
        Func<GatewayRequest, CancellationToken, Task<byte[]>> body)
    {
        var responseType = Prefix + response;
        handlers[Prefix + request] = async (gateway, cancellationToken) =>
        {
            if (gateway.CharacterId is null)
                return new GatewayAnswer(400, "no active character", responseType, []);
            try
            {
                return new GatewayAnswer(200, "", responseType, await body(gateway, cancellationToken));
            }
            catch (TitleRefusal error)
            {
                return new GatewayAnswer(409, error.Message, responseType, []);
            }
        };
    }
}
