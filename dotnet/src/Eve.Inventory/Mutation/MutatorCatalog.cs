using System.Text.Json;
using System.Text.RegularExpressions;
using Eve.Sde;

namespace Eve.Inventory.Mutation;

/// <summary>What one mutaplasmid does to one input: the output type and the attributes it moves with their intervals.</summary>
public sealed record MutationPlan(int MutatorTypeId, int SourceTypeId, int OutputTypeId, IReadOnlyList<AttributeRange> Ranges);

/// <summary>
/// The mutaplasmids of the static data, read once: which published inputs each takes and to which output type, and the
/// interval of every attribute it moves. The data is the only source; no type id is written here.
/// </summary>
public sealed class MutatorCatalog
{
    private static readonly Regex SkillAttributeName = new(@"^requiredSkill\d+(Level)?$", RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<int, Mutator> _mutators;
    private readonly int _outputTypeCount;

    private sealed record Mutator(IReadOnlyList<AttributeRange> Ranges, IReadOnlyDictionary<int, int> OutputOfInput);

    private MutatorCatalog(IReadOnlyDictionary<int, Mutator> mutators, int outputTypeCount, IReadOnlySet<int> skillAttributeIds)
    {
        _mutators = mutators;
        _outputTypeCount = outputTypeCount;
        SkillAttributeIds = skillAttributeIds;
    }

    /// <summary>The attributes that carry a skill requirement (requiredSkillN and requiredSkillNLevel), from the data.</summary>
    public IReadOnlySet<int> SkillAttributeIds { get; }

    /// <summary>The published mutaplasmids.</summary>
    public int MutatorCount => _mutators.Count;

    /// <summary>The distinct output types of every mutaplasmid mapping.</summary>
    public int OutputTypeCount => _outputTypeCount;

    /// <summary>The published (mutaplasmid, input) pairs.</summary>
    public int PairCount => _mutators.Values.Sum(mutator => mutator.OutputOfInput.Count);

    /// <summary>The distinct published input types.</summary>
    public int InputTypeCount => _mutators.Values.SelectMany(mutator => mutator.OutputOfInput.Keys).Distinct().Count();

    public bool IsMutator(int typeId) => _mutators.ContainsKey(typeId);

    /// <summary>The plan for a mutaplasmid taking a source type, or the refusal (unknown mutator, or input not mappable).</summary>
    public MutationOutcome<MutationPlan> Plan(int mutatorTypeId, int sourceTypeId)
    {
        if (!_mutators.TryGetValue(mutatorTypeId, out var mutator))
        {
            return new Refused<MutationPlan>(MutationRefusal.UnknownMutator);
        }
        if (!mutator.OutputOfInput.TryGetValue(sourceTypeId, out var output))
        {
            return new Refused<MutationPlan>(MutationRefusal.InputNotMappable);
        }
        return new Accepted<MutationPlan>(new MutationPlan(mutatorTypeId, sourceTypeId, output, mutator.Ranges));
    }

    /// <summary>The legal interval of each attribute a mutaplasmid moves, or the refusal for an unknown mutator.</summary>
    public MutationOutcome<IReadOnlyList<AttributeRange>> Intervals(int mutatorTypeId) =>
        _mutators.TryGetValue(mutatorTypeId, out var mutator)
            ? new Accepted<IReadOnlyList<AttributeRange>>(mutator.Ranges)
            : new Refused<IReadOnlyList<AttributeRange>>(MutationRefusal.UnknownMutator);

    /// <summary>
    /// Reads the mutaplasmids from the static data. A mutaplasmid or input that is not published is left out (the client
    /// filters them the same way). The data must be well formed: a malformed row throws, it is never guessed at.
    /// </summary>
    public static async Task<MutatorCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var published = (await sde.ReadTypeSummariesAsync(cancellationToken))
            .Where(type => type.Published)
            .Select(type => type.TypeId)
            .ToHashSet();

        var dogma = await sde.ReadAsync("dogmaAttributes", cancellationToken: cancellationToken);
        var highIsGoodByAttribute = new Dictionary<int, bool>();
        var skillAttributeIds = new HashSet<int>();
        foreach (var row in dogma)
        {
            var attributeId = (int)row.Key;
            highIsGoodByAttribute[attributeId] = row.Doc.TryGetProperty("highIsGood", out var highIsGood) && highIsGood.ValueKind == JsonValueKind.True;
            if (row.Doc.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && SkillAttributeName.IsMatch(name.GetString()!))
            {
                skillAttributeIds.Add(attributeId);
            }
        }

        var mutators = new Dictionary<int, Mutator>();
        var outputTypes = new HashSet<int>();
        foreach (var row in await sde.ReadAsync("dynamicItemAttributes", cancellationToken: cancellationToken))
        {
            var mutatorTypeId = (int)row.Key;
            var ranges = ReadRanges(row.Doc, highIsGoodByAttribute);
            var outputOfInput = new Dictionary<int, int>();
            foreach (var mapping in row.Doc.GetProperty("inputOutputMapping").EnumerateArray())
            {
                var output = mapping.GetProperty("resultingType").GetInt32();
                outputTypes.Add(output);
                foreach (var input in mapping.GetProperty("applicableTypes").EnumerateArray())
                {
                    var inputTypeId = input.GetInt32();
                    if (!published.Contains(inputTypeId))
                    {
                        continue;
                    }
                    if (!outputOfInput.TryAdd(inputTypeId, output))
                    {
                        throw new InvalidDataException($"mutaplasmid {mutatorTypeId} maps input {inputTypeId} to two outputs");
                    }
                }
            }
            if (published.Contains(mutatorTypeId))
            {
                mutators[mutatorTypeId] = new Mutator(ranges, outputOfInput);
            }
        }
        return new MutatorCatalog(mutators, outputTypes.Count, skillAttributeIds);
    }

    private static IReadOnlyList<AttributeRange> ReadRanges(JsonElement doc, IReadOnlyDictionary<int, bool> highIsGoodByAttribute)
    {
        var mutatorTypeId = (int)doc.GetProperty("_key").GetInt64();
        var ranges = new List<AttributeRange>();
        var seen = new HashSet<int>();
        foreach (var entry in doc.GetProperty("attributeIDs").EnumerateArray())
        {
            var attributeId = entry.GetProperty("_key").GetInt32();
            if (!seen.Add(attributeId))
            {
                throw new InvalidDataException($"mutaplasmid {mutatorTypeId} moves attribute {attributeId} twice");
            }
            var min = entry.GetProperty("min").GetDouble();
            var max = entry.GetProperty("max").GetDouble();
            if (!(min > 0 && max > 0 && double.IsFinite(min) && double.IsFinite(max)))
            {
                throw new InvalidDataException($"mutaplasmid {mutatorTypeId} attribute {attributeId} has a non-positive multiplier");
            }
            bool highIsGood;
            if (entry.TryGetProperty("highIsGood", out var rowOverride))
            {
                highIsGood = rowOverride.ValueKind == JsonValueKind.True;
            }
            else if (!highIsGoodByAttribute.TryGetValue(attributeId, out highIsGood))
            {
                throw new InvalidDataException($"attribute {attributeId} of mutaplasmid {mutatorTypeId} is not in the dogma attributes");
            }
            ranges.Add(new AttributeRange(attributeId, min, max, highIsGood));
        }
        return ranges;
    }
}
