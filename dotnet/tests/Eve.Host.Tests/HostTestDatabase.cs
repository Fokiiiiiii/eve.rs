using Npgsql;

namespace Eve.Host.Tests;

internal static class HostTestDatabase
{
    internal static async Task AttachSdeAsync(NpgsqlDataSource target, NpgsqlConnectionStringBuilder source)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var user = $"user {Literal(source.Username!)}";
        if (!string.IsNullOrEmpty(source.Password)) user += $", password {Literal(source.Password)}";
        await using var setup = target.CreateCommand($"""
            CREATE EXTENSION postgres_fdw;
            CREATE SERVER sde_source FOREIGN DATA WRAPPER postgres_fdw
                OPTIONS (host {Literal(source.Host!)}, port {Literal(source.Port.ToString())}, dbname {Literal(source.Database!)});
            CREATE USER MAPPING FOR CURRENT_USER SERVER sde_source OPTIONS ({user});
            CREATE SCHEMA sde;
            IMPORT FOREIGN SCHEMA sde FROM SERVER sde_source INTO sde;
            CREATE SCHEMA sde_mission;
            IMPORT FOREIGN SCHEMA sde_mission FROM SERVER sde_source INTO sde_mission;
            """);
        await setup.ExecuteNonQueryAsync();
    }
}
