using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

public sealed partial class SkillHandlerService
{
    /// <summary>
    /// <c>SaveNewQueue(queue, activate=True)</c>: replaces the queue. It is a dict of position to <c>(skill, level)</c>. The client is told
    /// the queue as it was kept; a queue the rules refuse is answered with the message the client shows.
    /// </summary>
    [Method("SaveNewQueue")]
    public async ValueTask<PyValue> SaveNewQueue(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id)
        {
            return None;
        }
        var activate = call.Kwarg("activate") is { } given ? JsSemantics.Truthy(given) : true;
        var outcome = await training.SaveQueueAsync(id, AccountOf(call), QueueInput(call.Arg(0)), activate, await AccountCharacters(call), call.Cancellation);
        return await Answer(call, outcome);
    }

    /// <summary><c>CharStartTrainingSkillByTypeID(typeID, toLevel=None)</c>: puts a skill first in the queue and runs it.</summary>
    [Method("CharStartTrainingSkillByTypeID")]
    public async ValueTask<PyValue> CharStartTrainingSkillByTypeID(ServiceCall call) => await StartAsync(call, call.Arg(0), call.Arg(1));

    /// <summary>
    /// <c>CharStartTrainingSkill(itemID, toLevel=None)</c>: the same for a skill named by its item id (the character's id and the type, see
    /// <see cref="SkillWire.ItemId"/>). An item that is not one of the character's skills starts nothing.
    /// </summary>
    [Method("CharStartTrainingSkill")]
    public async ValueTask<PyValue> CharStartTrainingSkill(ServiceCall call)
    {
        if (CharacterOf(call) is not { } id || IdOf(call.Arg(0)) is not { } item || item / 100_000 != id)
        {
            return None;
        }
        return await StartAsync(call, new PyInt(item % 100_000), call.Arg(1));
    }

    private async ValueTask<PyValue> StartAsync(ServiceCall call, PyValue type, PyValue level)
    {
        if (CharacterOf(call) is not { } id || IdOf(type) is not { } typeId || typeId > int.MaxValue)
        {
            return None;
        }
        var toLevel = IdOf(level) is { } asked and > 0 and <= int.MaxValue ? (int)asked : 0;
        var outcome = await training.StartAsync(id, AccountOf(call), (int)typeId, toLevel, await AccountCharacters(call), call.Cancellation);
        return outcome is null ? None : await Answer(call, outcome);
    }

    private async ValueTask<PyValue> Answer(ServiceCall call, SaveOutcome outcome)
    {
        if (outcome is not QueueSaved saved)
        {
            throw SkillErrors.Of(outcome);
        }
        await announcer.AnnounceSavedAsync(call.Session, saved);
        return None;
    }

    /// <summary><c>CharStopTrainingSkill()</c>: the same as <c>AbortTraining()</c>.</summary>
    [Method("CharStopTrainingSkill")]
    public ValueTask<PyValue> CharStopTrainingSkill(ServiceCall call) => AbortTraining(call);

    /// <summary><c>AbortTraining()</c>: stops the running skill; the queue stays and can be started again.</summary>
    [Method("AbortTraining")]
    public async ValueTask<PyValue> AbortTraining(ServiceCall call)
    {
        if (CharacterOf(call) is { } id && await training.AbortAsync(id, AccountOf(call), call.Cancellation) is { } update)
        {
            await announcer.AnnounceStoppedAsync(call.Session, update);
        }
        return None;
    }

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal real when double.IsFinite(real.Value) &&
            Math.Truncate(real.Value) == real.Value &&
            real.Value >= -9.223372036854776E18 &&
            real.Value < 9.223372036854776E18 => (long)real.Value,
        _ => null,
    };

    /// <summary>
    /// The lines the client sent, in queue order: a dict of position to <c>(skill, level)</c> (a list is taken as positions from 0). A line
    /// that names no skill or no level, or a negative position, is left out.
    /// </summary>
    private static List<QueueEntry> QueueInput(PyValue queue)
    {
        var lines = new List<(long Position, QueueEntry Entry)>();
        void Add(long position, PyValue value)
        {
            var (type, level) = value switch
            {
                PyTuple { Items.Count: >= 2 } pair => (IdOf(pair.Items[0]), IdOf(pair.Items[1])),
                PyList { Items.Count: >= 2 } pair => (IdOf(pair.Items[0]), IdOf(pair.Items[1])),
                PyDict dict => (Named(dict, "trainingTypeID", "typeID"), Named(dict, "trainingToLevel", "toLevel")),
                _ => (null, null),
            };
            if (position >= 0 && type is > 0 and <= int.MaxValue && level is > 0 and <= int.MaxValue)
            {
                lines.Add((position, new QueueEntry((int)type, (int)level)));
            }
        }
        switch (queue)
        {
            case PyDict dict:
                foreach (var (key, value) in dict.Entries)
                {
                    Add(IdOf(key) ?? -1, value);
                }
                break;
            case PyList list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    Add(i, list.Items[i]);
                }
                break;
            case PyTuple tuple:
                for (var i = 0; i < tuple.Items.Count; i++)
                {
                    Add(i, tuple.Items[i]);
                }
                break;
        }
        return [.. lines.OrderBy(l => l.Position).Select(l => l.Entry)];
    }

    private static long? Named(PyDict dict, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var (key, value) in dict.Entries)
            {
                if (JsSemantics.StringValue(key) == name)
                {
                    return IdOf(value);
                }
            }
        }
        return null;
    }

    /// <summary>The proxy node that serves this object: the one the client is connected to.</summary>
[Method("IsSkillAvailableForPurchase")]
    public PyValue IsSkillAvailableForPurchase(ServiceCall call) =>
        IdOf(call.Arg(0)) is { } id && id is >= 0 and <= int.MaxValue
            && catalog.Get((int)id) is { Published: true, AvailableForPurchase: true }
                ? PyBool.True
                : PyBool.False;

    [Method("GetDirectPurchasePrice")]
    public PyValue GetDirectPurchasePrice(ServiceCall call)
    {
        if (IdOf(call.Arg(0)) is not { } id || id is < 0 or > int.MaxValue || catalog.Get((int)id) is not { Published: true } skill)
            throw MachoErrors.ThrowUserError("ItemNotASkill", ("skillName", call.Arg(0)));
        if (!skill.AvailableForPurchase)
            throw MachoErrors.ThrowUserError("SkillUnavailableForPurchase", ("type", new PyInt(skill.TypeId)));
        return Py.Number((double)(skill.BasePrice * 1.3m));
    }

    [Method("PurchaseSkills")]
    public async ValueTask<PyValue> PurchaseSkills(ServiceCall call)
    {
        if (CharacterOf(call) is not { } character) return None;
        if (walletTransactions is null)
            throw MachoErrors.ThrowUserError("SkillPurchaseDisabled");
        var ids = call.Arg(0) switch
        {
            PyList list => list.Items.Select(IdOf).OfType<long>().Distinct().ToArray(),
            PyTuple tuple => tuple.Items.Select(IdOf).OfType<long>().Distinct().ToArray(),
            _ => [],
        };
        if (ids.Length == 0) return Py.Tuple();
        var requested = new List<SkillType>(ids.Length);
        foreach (var id in ids)
        {
            if (id is < 0 or > int.MaxValue || catalog.Get((int)id) is not { Published: true } skill)
                throw MachoErrors.ThrowUserError("ItemNotASkill", ("skillName", Py.Number(id)));
            if (!skill.AvailableForPurchase)
                throw MachoErrors.ThrowUserError("SkillUnavailableForPurchase", ("type", new PyInt(skill.TypeId)));
            requested.Add(skill);
        }
        var cost = requested.Sum(skill => decimal.Round(skill.BasePrice * 1.3m, 2, MidpointRounding.AwayFromZero));
        if (cost <= 0)
            throw MachoErrors.ThrowUserError("SkillPurchaseUnknownError");
        var operationKey = $"skill-purchase:{character}:{call.Session.SessionId}:{IdOf(call.CallId) ?? nowUnixMs()}";
        (IReadOnlyList<CharacterSkill> Before, CharacterSkill[] Granted, decimal Balance) committed;
        try
        {
            committed = await store.InTransactionAsync(async (connection, transaction, token) =>
            {
                await using (var characterLock = new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
                {
                    characterLock.Parameters.Add(new NpgsqlParameter { Value = $"skills:{character}" });
                    await characterLock.ExecuteScalarAsync(token);
                }
                var current = await store.ForCharacterAsync(connection, transaction, character, token);
                if (requested.Any(skill => current.Any(have => have.TypeId == skill.TypeId)))
                    throw MachoErrors.ThrowUserError("CharacterAlreadyKnowsSkill", ("skillName", new PyInt(requested.First(skill => current.Any(have => have.TypeId == skill.TypeId)).TypeId)));
                var balance = await walletTransactions.AdjustAsync(connection, transaction, character, -cost, 0, 57, "Direct skill purchase",
                    nowUnixMs(), operationKey, token);
                var granted = requested.Select(skill => new CharacterSkill(skill.TypeId, 0, 0)).ToArray();
                await store.GrantAsync(connection, transaction, character, granted, token);
                return (Before: current, Granted: granted, Balance: balance);
            }, call.Cancellation);
        }
        catch (Eve.Wallet.InsufficientFundsException)
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Not enough ISK to purchase these skills.")));
        }
        walletTransactions.AnnounceCommitted(new Eve.Wallet.WalletBalanceChange(character, 1000, committed.Balance));
        await announcer.AnnouncePurchasedAsync(call.Session, character, committed.Before, committed.Granted);
        return Py.Tuple([.. committed.Granted.Select(skill => (PyValue)new PyInt(skill.TypeId))]);
    }
}
