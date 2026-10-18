using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Host;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// <c>/skyhook state</c>'s write (<see cref="PersistenceSkyhookStateWriter"/>) on a sovereignty table held in memory: the row's state is set, staged, flushed
/// and read back; a skyhook already in the state is left alone, and an unknown skyhook is not written.
/// </summary>
public sealed class SkyhookStateWriterTests
{
    private sealed class Table
    {
        public JsonObject Root { get; } = new()
        {
            ["skyhooks"] = new JsonObject
            {
                ["1234"] = new JsonObject { ["state"] = 210, ["resourceVersion"] = "v1", ["productionActive"] = false },
            },
        };

        public List<string> Writes { get; } = [];

        public int Flushes { get; set; }
    }

    private static (PersistenceSkyhookStateWriter Writer, Table Table) Make()
    {
        var table = new Table();
        var writer = new PersistenceSkyhookStateWriter(
            _ => table.Root,
            (name, path, data) =>
            {
                table.Writes.Add($"{name}{path}");
                table.Root["skyhooks"]![path!.Split('/')[2]] = data;
                return new WriteResult(true, null);
            },
            (_, _) =>
            {
                table.Flushes++;
                return Task.FromResult(1);
            },
            () => new SemaphoreSlim(1, 1));
        return (writer, table);
    }

    [Fact]
    public async Task A_new_state_is_written_flushed_and_read_back()
    {
        var (writer, table) = Make();

        var outcome = await writer.SetStateAsync(1234, 214, CancellationToken.None);

        Assert.Equal(Eve.Services.Slash.SkyhookStateOutcome.Changed, outcome);
        Assert.Equal(["sovereignty/skyhooks/1234"], table.Writes);
        Assert.Equal(1, table.Flushes);
        Assert.Equal(214, table.Root["skyhooks"]!["1234"]!["state"]!.GetValue<int>());
        Assert.Equal("v1", table.Root["skyhooks"]!["1234"]!["resourceVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_skyhook_already_in_the_state_is_not_written()
    {
        var (writer, table) = Make();

        Assert.Equal(Eve.Services.Slash.SkyhookStateOutcome.Unchanged, await writer.SetStateAsync(1234, 210, CancellationToken.None));
        Assert.Empty(table.Writes);
        Assert.Equal(0, table.Flushes);
    }

    [Fact]
    public async Task An_unknown_skyhook_is_not_found_and_not_written()
    {
        var (writer, table) = Make();

        Assert.Equal(Eve.Services.Slash.SkyhookStateOutcome.NotFound, await writer.SetStateAsync(999, 214, CancellationToken.None));
        Assert.Empty(table.Writes);
    }

    [Fact]
    public async Task A_failed_write_is_reported_not_claimed()
    {
        var table = new Table();
        var writer = new PersistenceSkyhookStateWriter(
            _ => table.Root,
            (_, _, _) => new WriteResult(false, "disk full"),
            (_, _) => Task.FromResult(0),
            () => new SemaphoreSlim(1, 1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SetStateAsync(1234, 214, CancellationToken.None));
        Assert.Contains("disk full", error.Message);
        Assert.Equal(210, table.Root["skyhooks"]!["1234"]!["state"]!.GetValue<int>());
    }
}
