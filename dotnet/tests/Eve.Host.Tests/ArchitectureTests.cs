using System.Xml.Linq;

namespace Eve.Host.Tests;

/// <summary>
/// The shape of the solution that docs/adr/0003 decides, kept by reading the project files: a domain module does not
/// know the wire or the legacy store, and nothing but the legacy importer builds on the legacy store.
/// </summary>
public class ArchitectureTests
{
    /// <summary>The domain modules and what they may build on: each other (in this order) and the database helpers.</summary>
    private static readonly string[] Modules = ["Eve.Data", "Eve.Sde", "Eve.Identity", "Eve.Inventory", "Eve.Characters"];

    private static string Source
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EveRs.slnx")))
                {
                    return Path.Combine(directory.FullName, "src");
                }
            }
            throw new InvalidOperationException("EveRs.slnx was not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Each source project and the projects it references, by name.</summary>
    private static Dictionary<string, string[]> References() =>
        Directory.GetFiles(Source, "*.csproj", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetFileNameWithoutExtension(file),
            file => XDocument.Load(file).Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')))
                .ToArray());

    [Fact]
    public void The_projects_are_found()
    {
        var all = References();
        Assert.True(all.Count >= 14, string.Join(", ", all.Keys));
        Assert.All(Modules, module => Assert.Contains(module, all.Keys));
    }

    [Fact]
    public void A_domain_module_builds_on_no_protocol_and_no_service()
    {
        foreach (var (project, references) in References().Where(p => Modules.Contains(p.Key)))
        {
            var foreign = references.Where(r => !Modules.Contains(r)).ToArray();
            Assert.True(foreign.Length == 0, $"{project} references {string.Join(", ", foreign)}: a domain module depends on domain modules only");
        }
    }

    [Fact]
    public void Domain_modules_do_not_depend_on_each_other_in_a_circle()
    {
        var all = References();
        foreach (var (project, references) in all.Where(p => Modules.Contains(p.Key)))
        {
            foreach (var reference in references)
            {
                Assert.True(Array.IndexOf(Modules, reference) < Array.IndexOf(Modules, project), $"{project} references {reference}, which comes after it");
            }
        }
    }

    [Fact]
    public void Nothing_but_the_legacy_importer_builds_on_the_legacy_store()
    {
        foreach (var (project, references) in References())
        {
            if (project is "Eve.GameStore.Import")
            {
                Assert.Contains("Eve.GameStore", references);
                continue;
            }
            Assert.DoesNotContain("Eve.GameStore", references);
            Assert.DoesNotContain("Eve.GameStore.Import", references);
        }
    }
}
