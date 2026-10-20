using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>The client's ContractStruct, the CreateContract argument, read by field name.</summary>
public sealed record ContractCreationInfo(
    int ContractType,
    bool IsPrivate,
    long AssignedToId,
    int MinutesExpire,
    int NumDays,
    long StartStationId,
    long DestinationId,
    decimal Price,
    decimal Reward,
    decimal Collateral,
    string Title,
    string Description,
    IReadOnlyList<ContractOfferedItem> ItemList,
    int StartStationDivision,
    IReadOnlyList<ContractRequestedItem> RequestedTypes,
    bool ForCorp)
{
    public static ContractCreationInfo Parse(PyValue value)
    {
        var fields = value switch
        {
            PyObject { Args: PyDict dict } => dict,
            PyObject { Args: PyTuple tuple } => ClientContractFields(tuple),
            PyDict dict => dict,
            _ => throw new ArgumentException("contract info must be a KeyVal or ContractStruct", nameof(value)),
        };
        return new ContractCreationInfo(
            ContractType: (int)Integer(fields, "contractType"),
            IsPrivate: Flag(fields, "isPrivate"),
            AssignedToId: Integer(fields, "assignedToID"),
            MinutesExpire: (int)Integer(fields, "minutesExpire"),
            NumDays: (int)Integer(fields, "numDays"),
            StartStationId: Integer(fields, "startStationID"),
            DestinationId: Integer(fields, "destinationID"),
            Price: Money(fields, "price"),
            Reward: Money(fields, "reward"),
            Collateral: Money(fields, "collateral"),
            Title: Text(fields, "title"),
            Description: Text(fields, "description"),
            ItemList: Pairs(fields, "itemList").Select(pair => new ContractOfferedItem(pair.Key, pair.Value)).ToArray(),
            StartStationDivision: (int)Integer(fields, "startStationDivision"),
            RequestedTypes: Pairs(fields, "requestItemTypeList").Select(pair => new ContractRequestedItem((int)pair.Key, pair.Value)).ToArray(),
            ForCorp: Flag(fields, "forCorp"));
    }

    private static PyDict ClientContractFields(PyTuple tuple)
    {
        string[] names =
        [
            "contractType", "isPrivate", "assignedToID", "minutesExpire", "numDays", "startStationID",
            "destinationID", "price", "reward", "collateral", "title", "description", "itemList",
            "startStationDivision", "requestItemTypeList", "forCorp", "multiContract",
        ];
        if (tuple.Items.Count != names.Length)
        {
            throw new ArgumentException("ContractStruct must contain all V24 fields", nameof(tuple));
        }
        if (tuple.Items[16] is PyBool { Value: true } or PyInt { Value: not 0 } or PyLong { Value: not 0 })
        {
            throw new ArgumentException("multiContract creation is not supported", nameof(tuple));
        }
        return new PyDict(names.Take(16)
            .Select((name, index) => new KeyValuePair<PyValue, PyValue>(new PyStr(name), tuple.Items[index]))
            .ToArray());
    }

    private static PyValue? Field(PyDict fields, string name)
    {
        foreach (var (key, value) in fields.Entries)
        {
            if (key is PyStr text && text.Value == name)
            {
                return value;
            }
        }
        return null;
    }

    private static long Integer(PyDict fields, string name) => Field(fields, name) switch
    {
        PyInt value => value.Value,
        PyLong value => value.Value,
        PyReal value when value.Value == Math.Floor(value.Value) => (long)value.Value,
        null or PyNone => 0,
        _ => throw new ArgumentException($"contract field {name} must be an integer", nameof(fields)),
    };

    private static decimal Money(PyDict fields, string name) => Field(fields, name) switch
    {
        PyInt value => value.Value,
        PyLong value => value.Value,
        PyReal value => decimal.Round((decimal)value.Value, 2),
        null or PyNone => 0m,
        _ => throw new ArgumentException($"contract field {name} must be a number", nameof(fields)),
    };

    private static bool Flag(PyDict fields, string name) => Field(fields, name) switch
    {
        PyBool value => value.Value,
        PyInt value => value.Value != 0,
        PyLong value => value.Value != 0,
        _ => false,
    };

    private static string Text(PyDict fields, string name) => Field(fields, name) switch
    {
        PyStr value => value.Value,
        PyWString value => value.Value,
        PyBytes value => System.Text.Encoding.UTF8.GetString(value.Value.Span),
        _ => string.Empty,
    };

    private static IEnumerable<KeyValuePair<long, long>> Pairs(PyDict fields, string name)
    {
        IReadOnlyList<PyValue> items = Field(fields, name) switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            null or PyNone => [],
            _ => throw new ArgumentException($"contract field {name} must be a list", nameof(fields)),
        };
        foreach (var item in items)
        {
            var pair = item switch
            {
                PyTuple { Items.Count: 2 } tuple => tuple.Items,
                PyList { Items.Count: 2 } list => list.Items,
                _ => throw new ArgumentException($"contract field {name} entries must be pairs", nameof(fields)),
            };
            yield return new KeyValuePair<long, long>(PairInteger(pair[0]), PairInteger(pair[1]));
        }
    }

    private static long PairInteger(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new ArgumentException("contract pair entries must be integers"),
    };
}

/// <summary>The V24 contract capacity table, the client's own counts, enforced on the server.</summary>
public static class ContractCreationLimits
{
    public const int MaxNumContracts = 500;

    // NUM_CONTRACTS_BY_SKILL, NUM_CONTRACTS_BY_ADVSKILL and NUM_CONTRACTS_BY_SKILL_CORP in eve/common/script/util/contractscommon.py.
    private static readonly int[] ByContracting = [7, 16, 17, 18, 19, 20];
    private static readonly int[] ByAdvancedContracting = [21, 22, 23, 24, 25, 26];
    private static readonly int[] ByCorporationContracting = [5, 26, 27, 28, 29, 30];

    public static int Personal(int contractingLevel, int advancedContractingLevel) =>
        ByContracting[Level(contractingLevel)] + ByAdvancedContracting[Level(advancedContractingLevel)];

    public static int Corporation(int corporationContractingLevel) => ByCorporationContracting[Level(corporationContractingLevel)];

    private static int Level(int level) => level is >= 0 and <= 5
        ? level
        : throw new ArgumentOutOfRangeException(nameof(level), "skill level must be 0 to 5");
}
