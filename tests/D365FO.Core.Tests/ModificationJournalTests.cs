using D365FO.Core.Journal;
using Xunit;

namespace D365FO.Core.Tests;

public sealed class ModificationJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static JournalEntry Entry(string name, DateTimeOffset? ts = null) => new(
        Id: Guid.NewGuid().ToString("N"),
        TimestampUtc: ts ?? DateTimeOffset.UtcNow,
        Command: "generate table",
        TargetType: JournalTargetType.AotObject,
        Kind: "table",
        ObjectName: name,
        SecondaryKey: null,
        Model: "MyModel",
        Operation: JournalOperation.Create,
        WritePath: JournalWritePath.Disk,
        TargetPath: $@"C:\pkg\MyModel\MyModel\AxTable\{name}.xml",
        PreImage: null,
        IsTombstone: true,
        RnrProjDelta: null);

    [Fact]
    public void Append_then_List_returns_most_recent_first()
    {
        var journal = new ModificationJournal(_dir);
        var now = DateTimeOffset.UtcNow;
        journal.Append(Entry("First", now));
        journal.Append(Entry("Second", now.AddMilliseconds(10)));
        journal.Append(Entry("Third", now.AddMilliseconds(20)));

        var list = journal.List();

        Assert.Equal(3, list.Count);
        Assert.Equal("Third", list[0].ObjectName);
        Assert.Equal("Second", list[1].ObjectName);
        Assert.Equal("First", list[2].ObjectName);
    }

    [Fact]
    public void List_honours_limit()
    {
        var journal = new ModificationJournal(_dir);
        for (var i = 0; i < 5; i++)
            journal.Append(Entry($"T{i}", DateTimeOffset.UtcNow.AddMilliseconds(i)));

        var list = journal.List(2);

        Assert.Equal(2, list.Count);
        Assert.Equal("T4", list[0].ObjectName);
        Assert.Equal("T3", list[1].ObjectName);
    }

    [Fact]
    public void Peek_returns_the_most_recent_entry_or_null_when_empty()
    {
        var journal = new ModificationJournal(_dir);
        Assert.Null(journal.Peek());

        journal.Append(Entry("Only"));
        Assert.Equal("Only", journal.Peek()!.ObjectName);
    }

    [Fact]
    public void Remove_deletes_the_entry_and_is_idempotent()
    {
        var journal = new ModificationJournal(_dir);
        var appended = journal.Append(Entry("Gone"));

        Assert.True(journal.Remove(appended.Id));
        Assert.Empty(journal.List());
        Assert.False(journal.Remove(appended.Id)); // already gone
    }

    [Fact]
    public void Append_roundtrips_all_fields_including_preimage_and_rnrproj_delta()
    {
        var journal = new ModificationJournal(_dir);
        var delta = new RnrProjDelta(@"C:\pkg\MyModel\MyModel.rnrproj", "Compile", @"AxTable\Foo.xml", WasAdded: true);
        var entry = new JournalEntry(
            Id: Guid.NewGuid().ToString("N"),
            TimestampUtc: DateTimeOffset.UtcNow,
            Command: "generate table",
            TargetType: JournalTargetType.AotObject,
            Kind: "table",
            ObjectName: "Foo",
            SecondaryKey: null,
            Model: "MyModel",
            Operation: JournalOperation.Update,
            WritePath: JournalWritePath.Disk,
            TargetPath: @"C:\pkg\MyModel\MyModel\AxTable\Foo.xml",
            PreImage: "<AxTable><Name>Foo</Name></AxTable>",
            IsTombstone: false,
            RnrProjDelta: delta);

        journal.Append(entry);
        var read = Assert.Single(journal.List());

        Assert.Equal(entry.ObjectName, read.ObjectName);
        Assert.Equal(entry.PreImage, read.PreImage);
        Assert.Equal(entry.Operation, read.Operation);
        Assert.Equal(entry.WritePath, read.WritePath);
        Assert.NotNull(read.RnrProjDelta);
        Assert.Equal(delta.Include, read.RnrProjDelta!.Include);
        Assert.True(read.RnrProjDelta.WasAdded);
    }

    [Fact]
    public void FIFO_pruning_by_max_entries_drops_the_oldest_first()
    {
        var journal = new ModificationJournal(_dir, maxBytes: long.MaxValue, maxEntries: 3);
        for (var i = 0; i < 5; i++)
            journal.Append(Entry($"T{i}", DateTimeOffset.UtcNow.AddMilliseconds(i)));

        var list = journal.List();

        Assert.Equal(3, list.Count);
        // Newest three survive; T0 and T1 were pruned.
        Assert.Equal(new[] { "T4", "T3", "T2" }, list.Select(e => e.ObjectName));
    }

    [Fact]
    public void FIFO_pruning_by_max_bytes_drops_the_oldest_first()
    {
        // Each entry's JSON is comfortably a few hundred bytes; cap tight enough that
        // pruning must kick in well before 20 entries accumulate.
        var journal = new ModificationJournal(_dir, maxBytes: 800, maxEntries: int.MaxValue);
        for (var i = 0; i < 20; i++)
            journal.Append(Entry($"T{i:D2}", DateTimeOffset.UtcNow.AddMilliseconds(i)));

        var list = journal.List();

        Assert.True(list.Count < 20, "expected size-based pruning to have removed older entries");
        Assert.Equal("T19", list[0].ObjectName); // the newest entry always survives
        var totalBytes = Directory.EnumerateFiles(_dir, "*.json").Sum(f => new FileInfo(f).Length);
        Assert.True(totalBytes <= 800 + 512, $"journal directory grew past the cap: {totalBytes} bytes");
    }

    [Fact]
    public void Corrupt_entry_file_is_skipped_not_thrown()
    {
        var journal = new ModificationJournal(_dir);
        journal.Append(Entry("Good"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "0000000000000000001_corrupt.json"), "{ not valid json");

        var list = journal.List();

        Assert.Single(list);
        Assert.Equal("Good", list[0].ObjectName);
    }

    [Fact]
    public void Count_reflects_entries_on_disk()
    {
        var journal = new ModificationJournal(_dir);
        Assert.Equal(0, journal.Count());
        journal.Append(Entry("A"));
        journal.Append(Entry("B"));
        Assert.Equal(2, journal.Count());
    }
}
