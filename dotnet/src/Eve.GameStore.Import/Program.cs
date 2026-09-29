using Eve.GameStore;
using Eve.GameStore.Import;

const string Usage = """
    eve-gamestore-import: copy a reference-server gamestore.sqlite into PostgreSQL

      eve-gamestore-import --sqlite <file> --pg <url> [options]

      --sqlite <file>     the gamestore.sqlite to read (never modified)
      --pg <url>          postgres://user:pass@host:port/db, or an Npgsql connection string
      --schema <name>     target schema (default: gamestore)
      --tables a,b,c      only these tables
      --replace           overwrite target tables that already hold rows
      --apply-outbox      apply the source's unacknowledged journaled batches
      --ignore-lease      import although the source shows a live owner lease
      --no-verify         skip the read-back comparison

    Needs exclusive maintenance ownership of the target, so it fails while a server is running on it.
    """;

string? sqlite = null, pg = null, schema = "gamestore";
List<string>? tables = null;
bool replace = false, applyOutbox = false, ignoreLease = false, verify = true;

try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        switch (args[i])
        {
            case "--sqlite": sqlite = Next(); break;
            case "--pg": pg = Next(); break;
            case "--schema": schema = Next(); break;
            case "--tables": tables = [.. Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]; break;
            case "--replace": replace = true; break;
            case "--apply-outbox": applyOutbox = true; break;
            case "--ignore-lease": ignoreLease = true; break;
            case "--no-verify": verify = false; break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException($"unknown argument {args[i]}");
        }
    }
    if (sqlite is null || pg is null)
    {
        throw new ArgumentException("--sqlite and --pg are required");
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}\n\n{Usage}");
    return 2;
}

try
{
    await using var store = new PersistenceStore(PgConnection.ToConnectionString(pg), schema!);
    var report = await Importer.RunAsync(store, new ImportOptions(sqlite, tables, replace, applyOutbox, ignoreLease, verify), Console.WriteLine);
    foreach (var (table, reason) in report.Skipped)
    {
        Console.WriteLine($"skipped {table}: {reason}");
    }
    Console.WriteLine($"done: {report.Tables.Count} tables, {report.Tables.Sum(t => (long)t.Rows)} rows{(verify ? ", verified" : "")}");
    return 0;
}
catch (Exception ex) when (ex is ImportException or PersistenceException or FileNotFoundException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
