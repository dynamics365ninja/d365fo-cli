using System.Xml.Linq;
using D365FO.Core.Journal;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Verifies that <see cref="ScaffoldFileWriter"/> — the single choke point every
/// <c>generate *</c> CLI command and the MCP <c>generate_object</c> tool funnel disk writes
/// through — journals every write (issue #113) without the caller having to opt in.
/// </summary>
[Collection("ScaffoldFileWriter")]
public sealed class ScaffoldFileWriterJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfw-journal-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string? _prevIndexDb;

    public ScaffoldFileWriterJournalTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "index", "d365fo-index.sqlite");
        _prevIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _dbPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _prevIndexDb);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ModificationJournal Journal() => ModificationJournal.ForIndex();

    private static XDocument TableDoc(string name) => XDocument.Parse(
        $"<AxTable xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>{name}</Name></AxTable>");

    [Fact]
    public void Write_of_a_new_file_journals_a_tombstone_create()
    {
        var path = Path.Combine(_dir, "AxTable", "BrandNew.xml");
        ScaffoldFileWriter.Write(TableDoc("BrandNew"), path);

        var entry = Journal().Peek();

        Assert.NotNull(entry);
        Assert.Equal(JournalOperation.Create, entry!.Operation);
        Assert.True(entry.IsTombstone);
        Assert.Null(entry.PreImage);
        Assert.Equal("BrandNew", entry.ObjectName);
        Assert.Equal("Table", entry.Kind);
        Assert.Equal(JournalWritePath.Disk, entry.WritePath);
        Assert.Equal(Path.GetFullPath(path), entry.TargetPath);
    }

    [Fact]
    public void Write_over_an_existing_file_journals_an_update_with_the_preimage()
    {
        var path = Path.Combine(_dir, "AxTable", "Existing.xml");
        ScaffoldFileWriter.Write(TableDoc("Existing"), path);
        var firstWriteText = File.ReadAllText(path);

        ScaffoldFileWriter.Write(TableDoc("Existing"), path, overwrite: true);

        var entry = Journal().Peek();

        Assert.NotNull(entry);
        Assert.Equal(JournalOperation.Update, entry!.Operation);
        Assert.False(entry.IsTombstone);
        Assert.Equal(firstWriteText, entry.PreImage);
    }

    [Fact]
    public void Undo_of_a_generate_style_create_removes_the_file()
    {
        var path = Path.Combine(_dir, "AxTable", "Scratch.xml");
        ScaffoldFileWriter.Write(TableDoc("Scratch"), path);
        Assert.True(File.Exists(path));

        var journal = Journal();
        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        Assert.False(File.Exists(path));
    }
}
