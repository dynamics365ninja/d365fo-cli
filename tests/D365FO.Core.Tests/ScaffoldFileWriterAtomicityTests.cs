using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Pins the write path's "atomic" claim (issue #158): a <see cref="ScaffoldFileWriter.Write"/>
/// that returns normally has left the file on disk, and concurrent writers do not interfere.
/// </summary>
/// <remarks>
/// The reported symptom was a scaffold write that returned successfully and a file that was
/// gone when the caller looked — three writes, two files. Two things made that possible and
/// both are covered here: the staging sibling used to be the deterministic
/// <c>&lt;target&gt;.tmp</c>, shared by every writer aiming at that target, and nothing checked
/// that the rename had actually published the file.
/// </remarks>
// In the non-parallel Environment collection deliberately, and with the degree of parallelism
// pinned. These tests exist to put the write path under contention, and an unbounded
// Parallel.ForEach grows the shared thread pool — which then makes the *rest* of the suite run
// at a concurrency it was never stable at. Measured: five clean full-suite runs before these
// tests existed, two unrelated SQLite tests going red across seven runs after. Four writers is
// already more than the one-writer-per-target race needs.
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class ScaffoldFileWriterAtomicityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfw-atomic-{Guid.NewGuid():N}");

    public ScaffoldFileWriterAtomicityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Enough writers to race, few enough not to reshape the thread pool.</summary>
    private static readonly ParallelOptions Bounded = new() { MaxDegreeOfParallelism = 4 };

    private static XDocument TableDoc(string name) => XDocument.Parse(
        $"<AxTable xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>{name}</Name></AxTable>");

    [Fact]
    public void A_successful_write_leaves_no_staging_file_behind()
    {
        ScaffoldFileWriter.Write(TableDoc("Solo"), Path.Combine(_dir, "Solo.xml"));

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(_dir, "Solo.xml")));
    }

    [Fact]
    public void Concurrent_writes_into_one_directory_all_land()
    {
        var names = Enumerable.Range(0, 16).Select(i => $"ConVehicle{i:D2}").ToArray();

        Parallel.ForEach(names, Bounded, name =>
            ScaffoldFileWriter.Write(TableDoc(name), Path.Combine(_dir, name + ".xml")));

        WrittenFilesAssert.ExactlyTheseXml(_dir, names.Select(n => n + ".xml").ToArray());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // Every file holds its own document — not a neighbour's, which is what a shared
        // staging name would produce.
        foreach (var name in names)
            Assert.Equal(name, XDocument.Load(Path.Combine(_dir, name + ".xml")).Root!.Element("Name")!.Value);
    }

    [Fact]
    public void Concurrent_writers_racing_one_path_never_publish_a_torn_document()
    {
        var target = Path.Combine(_dir, "Contended.xml");
        File.WriteAllText(target, "<AxTable><Name>Seed</Name></AxTable>");

        // Writers that lose the race are allowed to fail — what is not allowed is a write that
        // reports success while the file holds someone else's half-staged bytes, or no file at
        // all. Before the fix each writer staged through the same "<target>.tmp".
        //
        // Both exception types count as losing. The .bak sibling deliberately keeps a fixed
        // name — it exists so a human can find it — so concurrent overwrites of one path still
        // contend there, and on Windows that contention surfaces as UnauthorizedAccessException
        // rather than IOException. Only the staging file needed to become private.
        Parallel.For(0, 24, Bounded, i =>
        {
            try { ScaffoldFileWriter.Write(TableDoc($"Racer{i:D2}"), target, overwrite: true); }
            catch (IOException) { /* lost the rename race — acceptable */ }
            catch (UnauthorizedAccessException) { /* lost the .bak race — acceptable */ }
        });

        Assert.True(File.Exists(target));
        var name = XDocument.Load(target).Root!.Element("Name")!.Value;
        Assert.True(name == "Seed" || name.StartsWith("Racer", StringComparison.Ordinal),
            $"target holds an unexpected document: {name}");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
