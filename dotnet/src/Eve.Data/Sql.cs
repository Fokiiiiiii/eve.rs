using Npgsql;
using NpgsqlTypes;

namespace Eve.Data;

/// <summary>
/// The small helpers every PostgreSQL-backed module wants: a command with positional parameters (<c>$1</c>, <c>$2</c>, …) in which <c>null</c> is SQL NULL and an array is a
/// PostgreSQL array, and a schema name that is checked before it is put into SQL text. Used by the shared foundations (scheduler, events, relations, content, read side).
/// </summary>
public static class Sql
{
    /// <summary>A command for <paramref name="text"/>; <paramref name="values"/> become <c>$1</c>, <c>$2</c>, … (null is NULL; <c>string[]</c>, <c>long[]</c>, <c>int[]</c> are arrays).</summary>
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string text, params object?[] values)
    {
        var command = new NpgsqlCommand(text, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(Parameter(value));
        }
        return command;
    }

    private static NpgsqlParameter Parameter(object? value) => value switch
    {
        null => new NpgsqlParameter { Value = DBNull.Value },
        string[] array => new NpgsqlParameter { Value = array, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
        long[] array => new NpgsqlParameter { Value = array, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint },
        int[] array => new NpgsqlParameter { Value = array, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer },
        _ => new NpgsqlParameter { Value = value },
    };

    /// <summary>Runs a statement and returns the number of rows it changed.</summary>
    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string text, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, text, values);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Runs a query and returns its first column of its first row, or <c>null</c>.</summary>
    public static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string text, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, text, values);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull ? null : result;
    }

    /// <summary>Throws unless <paramref name="schema"/> is a name that is safe to put into SQL text (lower case letters, digits and underscores).</summary>
    public static string CheckSchema(string schema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        return schema;
    }

    /// <summary>The SQL text with <c>{schema}</c> replaced by the (checked) schema name.</summary>
    public static string In(string schema, string text) => text.Replace("{schema}", schema);
}
