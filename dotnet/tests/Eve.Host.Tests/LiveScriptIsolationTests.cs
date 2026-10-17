using System.Text.RegularExpressions;

namespace Eve.Host.Tests;

/// <summary>
/// The live script (<c>tools/dev/run-eve-live.sh</c>) gives each run its own PostgreSQL schemas and drops them at the end.
/// Every schema the host reads from the environment must be one of them. Otherwise a run inherits rows from the last one:
/// the game store's mailboxes did (slice C), because the script did not scope the game store.
/// </summary>
public class LiveScriptIsolationTests
{
    private static readonly Regex SchemaVariable = new(@"EVE_[A-Z_]*_SCHEMA", RegexOptions.CultureInvariant);

    private static string RepoRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EveRs.slnx")))
                {
                    return directory.Parent?.FullName ?? throw new InvalidOperationException("EveRs.slnx has no repository above it");
                }
            }
            throw new InvalidOperationException("EveRs.slnx was not found above " + AppContext.BaseDirectory);
        }
    }

    private static string LiveScript() => File.ReadAllText(Path.Combine(RepoRoot, "tools", "dev", "run-eve-live.sh"));

    /// <summary>The schema variables the host reads, from its sources.</summary>
    private static IReadOnlySet<string> SchemaVariablesOfTheHost()
    {
        var source = Path.Combine(RepoRoot, "dotnet", "src");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match match in SchemaVariable.Matches(File.ReadAllText(file)))
            {
                names.Add(match.Value);
            }
        }
        return names;
    }

    [Fact]
    public void The_game_store_schema_is_scratch_in_the_live_script_and_dropped_at_its_end()
    {
        var script = LiveScript();

        Assert.Contains("EVE_GAMESTORE_SCHEMA=\"${SCRATCH}_gamestore\"", script, StringComparison.Ordinal);
        Assert.Contains("\"$EVE_GAMESTORE_SCHEMA\"", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The static data schema (<c>EVE_SDE_SCHEMA</c>) is not scratch: <c>eve-sde ensure</c> imports the pinned data once, and the host
    /// only reads it (<see cref="The_shared_static_data_reader_writes_nothing"/>), so no run can inherit rows from another.
    /// </summary>
    private static readonly string[] SharedStaticSchemas = ["EVE_SDE_SCHEMA"];

    [Fact]
    public void The_shared_static_data_reader_writes_nothing()
    {
        var reader = File.ReadAllText(Path.Combine(RepoRoot, "dotnet", "src", "Eve.Sde", "SdeReader.cs"));

        Assert.DoesNotMatch(new Regex(@"\b(INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|TRUNCATE)\b", RegexOptions.CultureInvariant), reader);
    }

    [Fact]
    public void Every_schema_the_host_reads_is_scratch_in_the_live_script_and_dropped_at_its_end()
    {
        var script = LiveScript();
        var names = SchemaVariablesOfTheHost().Where(name => !SharedStaticSchemas.Contains(name)).ToHashSet(StringComparer.Ordinal);

        Assert.True(names.Count >= 20, $"only {names.Count} schema variables were found in the host");
        var unisolated = names
            .Where(name => !script.Contains(name + "=\"${SCRATCH}_", StringComparison.Ordinal) ||
                           !script.Contains("\"$" + name + "\"", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(unisolated.Length == 0, "not scratch in the live script, or not dropped in its cleanup: " + string.Join(", ", unisolated));
    }
}
