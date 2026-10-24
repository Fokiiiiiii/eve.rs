namespace Eve.Services.Core;
using Eve.Marshal;
using Eve.Macho;

public sealed class SearchQueryService(ILookupDirectory directory) : IService
{
    private const string QueryMethod = "Query";
    private const int MaxSearchLength = 256;

    public string Name => "search";

    public bool HasMethod(string method) => method == QueryMethod;

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var search = JsSemantics.StringValue(call.Arg(0))?.Trim() ?? string.Empty;
        if (search.Length > MaxSearchLength)
            throw MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Lookup text is too long.")));

        var exactMode = ReadExactMode(call.Kwarg("exact"));
        var resultTypes = ReadGroupIds(call.Arg(1));
        var entries = new List<KeyValuePair<PyValue, PyValue>>(resultTypes.Count);
        foreach (var resultType in resultTypes)
        {
            PyValue[] matches = search.Length == 0
                ? []
                : (await EntriesAsync(resultType, call.Cancellation).ConfigureAwait(false))
                    .Where(entry => entry.Id > 0 && !string.IsNullOrWhiteSpace(entry.Name) && Matches(entry.Name, search, exactMode))
                    .OrderBy(entry => Rank(entry.Name, search))
                    .ThenBy(entry => entry.Name, StringComparer.CurrentCulture)
                    .ThenBy(entry => entry.Id)
                    .Take(500)
                    .Select(entry => (PyValue)new PyInt(entry.Id))
                    .ToArray();
            entries.Add(new KeyValuePair<PyValue, PyValue>(new PyInt(resultType), new PyList(matches)));
        }

        return new PyDict(entries);
    }

    private async ValueTask<IReadOnlyList<SearchEntry>> EntriesAsync(int resultType, CancellationToken cancellationToken)
    {
        switch (resultType)
        {
            case 2:
                return (await directory.CharactersAsync(cancellationToken).ConfigureAwait(false))
                    .Select(row => new SearchEntry(row.Id, row.Name)).ToArray();
            case 3:
                return (await directory.CorporationsAsync(cancellationToken).ConfigureAwait(false))
                    .Select(row => new SearchEntry(row.Id, row.Name)).ToArray();
            case 4:
                return (await directory.AlliancesAsync(cancellationToken).ConfigureAwait(false))
                    .Select(row => new SearchEntry(row.Id, row.Name)).ToArray();
            case 5:
                return (await directory.FactionsAsync(cancellationToken).ConfigureAwait(false))
                    .Select(row => new SearchEntry(row.Id, row.Name)).ToArray();
            case 6:
                return LocationEntries(await directory.LocationsByGroupAsync(4, cancellationToken).ConfigureAwait(false));
            case 7:
                return LocationEntries(await directory.LocationsByGroupAsync(5, cancellationToken).ConfigureAwait(false));
            case 8:
                return LocationEntries(await directory.LocationsByGroupAsync(3, cancellationToken).ConfigureAwait(false));
            case 9:
                return LocationEntries(await directory.LocationsByGroupAsync(15, cancellationToken).ConfigureAwait(false));
            case 10:
                return (await directory.TypesAsync(cancellationToken).ConfigureAwait(false))
                    .Select(row => new SearchEntry(row.Id, row.Name)).ToArray();
            default:
                return [];
        }
    }

    private static IReadOnlyList<SearchEntry> LocationEntries(IReadOnlyList<LookupLocation> locations) =>
        locations.Select(location => new SearchEntry(location.Id, location.Name ?? string.Empty)).ToArray();

    private static IReadOnlyList<int> ReadGroupIds(PyValue value)
    {
        var values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => Array.Empty<PyValue>(),
        };
        var groupIds = new List<int>();
        var seen = new HashSet<int>();
        foreach (var item in values)
        {
            var number = JsSemantics.NumberValue(item);
            if (double.IsFinite(number) && Math.Truncate(number) == number && number is >= 0 and <= int.MaxValue)
            {
                var groupId = (int)number;
                if (seen.Add(groupId))
                    groupIds.Add(groupId);
            }
        }
        return groupIds;
    }

    private static int ReadExactMode(PyValue? value)
    {
        var number = JsSemantics.NumberValue(value ?? PyNone.Instance);
        return double.IsFinite(number) && Math.Truncate(number) is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Truncate(number)
            : 0;
    }

    private static bool Matches(string name, string search, int exactMode)
    {
        var normalizedName = Normalize(name);
        var normalizedSearch = Normalize(search);
        var collapsedName = Collapse(normalizedName);
        var collapsedSearch = Collapse(normalizedSearch);
        if (collapsedSearch.Length == 0)
            return false;

        if (exactMode is 1 or 2 or 3)
            return normalizedName == normalizedSearch || collapsedName == collapsedSearch;

        var terms = normalizedSearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Collapse)
            .Where(term => term.Length > 0)
            .ToArray();
        return terms.Length == 0
            ? collapsedName.Contains(collapsedSearch, StringComparison.Ordinal)
            : terms.All(term => collapsedName.Contains(term, StringComparison.Ordinal));
    }

    private static int Rank(string name, string search)
    {
        var normalizedName = Normalize(name);
        var normalizedSearch = Normalize(search);
        var collapsedName = Collapse(normalizedName);
        var collapsedSearch = Collapse(normalizedSearch);
        if (normalizedName == normalizedSearch || collapsedName == collapsedSearch)
            return 0;
        if (normalizedName.StartsWith(normalizedSearch, StringComparison.Ordinal) ||
            collapsedName.StartsWith(collapsedSearch, StringComparison.Ordinal))
            return 1;
        return 2;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string Collapse(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
                result.Append(character);
        }
        return result.ToString();
    }

    private sealed record SearchEntry(long Id, string Name);
}
