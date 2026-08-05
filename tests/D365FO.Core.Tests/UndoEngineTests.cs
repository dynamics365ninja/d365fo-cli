using System.Text.Json.Nodes;
using D365FO.Core.Bridge;
using D365FO.Core.Journal;
using D365FO.Core.Labels;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Covers the acceptance criteria of issue #113: create→undo removes the file (+ .rnrproj
/// entry), update→undo restores the exact pre-image, delete→undo restores the file, --dry-run
/// makes no changes, and undo behaves identically whether the write went through disk or the
/// bridge (faked via the same in-memory stdio harness <c>BridgeClientTests</c> uses).
/// </summary>
public sealed class UndoEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"undo-{Guid.NewGuid():N}");

    public UndoEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ModificationJournal NewJournal() => new(Path.Combine(_dir, "journal"));

    private string PathIn(string name) => Path.Combine(_dir, name);

    // ---- disk: create -----------------------------------------------------

    [Fact]
    public void Disk_create_undo_deletes_the_file()
    {
        var target = PathIn("NewTable.xml");
        File.WriteAllText(target, "<AxTable><Name>NewTable</Name></AxTable>");

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "NewTable", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Create, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        Assert.False(File.Exists(target));
        Assert.Empty(journal.List()); // entry consumed
    }

    [Fact]
    public void Disk_create_undo_also_removes_the_rnrproj_entry()
    {
        var modelFolder = Path.Combine(_dir, "MyModel", "MyModel");
        var axSubfolder = Path.Combine(modelFolder, "AxTable");
        Directory.CreateDirectory(axSubfolder);
        var target = Path.Combine(axSubfolder, "NewTable.xml");
        File.WriteAllText(target, "<AxTable><Name>NewTable</Name></AxTable>");

        var rnrproj = Path.Combine(modelFolder, "MyModel.rnrproj");
        File.WriteAllText(rnrproj,
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" +
            "<ItemGroup><Compile Include=\"AxTable\\NewTable.xml\" /></ItemGroup></Project>");

        var delta = new RnrProjDelta(rnrproj, "Compile", @"AxTable\NewTable.xml", WasAdded: true);
        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "NewTable", SecondaryKey: null,
            Model: "MyModel", Operation: JournalOperation.Create, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: null, IsTombstone: true, RnrProjDelta: delta));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        Assert.False(File.Exists(target));
        var projXml = File.ReadAllText(rnrproj);
        Assert.DoesNotContain("NewTable.xml", projXml);
    }

    // ---- disk: update -------------------------------------------------------

    [Fact]
    public void Disk_update_undo_restores_the_exact_preimage_bytes()
    {
        var target = PathIn("CustTable.xml");
        var originalBytes = "<AxTable><Name>CustTable</Name><SomeProp>Original\r\nMultiline\tValue</SomeProp></AxTable>";
        File.WriteAllText(target, "<AxTable><Name>CustTable</Name><SomeProp>Changed</SomeProp></AxTable>");

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "CustTable", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Update, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: originalBytes, IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        Assert.Equal(originalBytes, File.ReadAllText(target));
    }

    [Fact]
    public void Disk_update_undo_restores_the_utf8_bom()
    {
        // AOT XML is written WITH a BOM by D365FO, Visual Studio, and this tool's own
        // scaffolder. PreImage is a decoded string, so the BOM is not in it — undo has to
        // re-emit it from the recorded flag or the "restored" file is three bytes short.
        var target = PathIn("BomTable.xml");
        var body = "<AxTable><Name>BomTable</Name></AxTable>";
        File.WriteAllText(target, "<AxTable><Name>BomTable</Name><Changed /></AxTable>",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "BomTable", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Update, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: body, IsTombstone: false, RnrProjDelta: null,
            PreImageHadBom: true));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        var bytes = File.ReadAllBytes(target);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal(body, File.ReadAllText(target));
    }

    [Fact]
    public void Disk_update_undo_does_not_add_a_bom_the_original_lacked()
    {
        var target = PathIn("NoBomTable.xml");
        var body = "<AxTable><Name>NoBomTable</Name></AxTable>";
        File.WriteAllText(target, "<AxTable><Name>NoBomTable</Name><Changed /></AxTable>",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "NoBomTable", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Update, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: body, IsTombstone: false, RnrProjDelta: null,
            PreImageHadBom: false));

        Assert.True(UndoEngine.Undo(journal, 1, dryRun: false).AllOk);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(body), File.ReadAllBytes(target));
    }

    [Fact]
    public void Undo_with_zero_or_negative_steps_reverts_nothing()
    {
        var target = PathIn("Untouched.xml");
        File.WriteAllText(target, "<AxTable><Name>Untouched</Name></AxTable>");

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Untouched", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Create, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        foreach (var steps in new[] { 0, -1 })
        {
            var result = UndoEngine.Undo(journal, steps, dryRun: false);
            Assert.Empty(result.Steps);
            Assert.True(File.Exists(target));   // the write was NOT reverted
            Assert.Single(journal.List());      // the entry is still on the stack
        }
    }

    [Fact]
    public void Disk_replay_refuses_a_target_outside_the_allowed_roots()
    {
        // A journal entry pointing outside packages/workspace/temp must not be replayed —
        // the path comes from the journal store, not from the command line.
        var outside = OperatingSystem.IsWindows()
            ? @"C:\d365fo-undo-boundary-probe.xml"
            : "/d365fo-undo-boundary-probe.xml";

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Probe", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Update, WritePath: JournalWritePath.Disk,
            TargetPath: outside, PreImage: "<AxTable/>", IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.False(result.AllOk);
        Assert.Contains("Path traversal blocked", result.Steps[0].Error);
        Assert.False(File.Exists(outside));
    }

    // ---- disk: delete -------------------------------------------------------

    [Fact]
    public void Disk_delete_undo_restores_the_file()
    {
        var target = PathIn("OldClass.xml");
        var originalBytes = "<AxClass><Name>OldClass</Name></AxClass>";
        // File does not exist — it was deleted by the original write.
        Assert.False(File.Exists(target));

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "delete",
            TargetType: JournalTargetType.AotObject, Kind: "class", ObjectName: "OldClass", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Delete, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: originalBytes, IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        Assert.True(File.Exists(target));
        Assert.Equal(originalBytes, File.ReadAllText(target));
    }

    // ---- dry run --------------------------------------------------------------

    [Fact]
    public void DryRun_makes_no_changes_and_leaves_the_journal_intact()
    {
        var target = PathIn("Untouched.xml");
        File.WriteAllText(target, "<AxTable><Name>Untouched</Name></AxTable>");

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Untouched", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Create, WritePath: JournalWritePath.Disk,
            TargetPath: target, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: true);

        Assert.True(result.DryRun);
        Assert.True(result.AllOk);
        Assert.True(File.Exists(target)); // untouched
        Assert.Single(journal.List());    // entry NOT consumed
        Assert.Contains("would delete", result.Steps[0].Detail);
    }

    // ---- fail-fast ------------------------------------------------------------

    [Fact]
    public void Undo_stops_at_first_failure_and_leaves_older_entries_in_the_journal()
    {
        var journal = NewJournal();
        // Oldest first in append order; newest (broken) entry has no pre-image so it fails.
        var goodTarget = PathIn("Good.xml");
        File.WriteAllText(goodTarget, "<AxTable><Name>Good</Name></AxTable>");
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Good", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Create, WritePath: JournalWritePath.Disk,
            TargetPath: goodTarget, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow.AddMilliseconds(10), Command: "generate table",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Broken", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Update, WritePath: JournalWritePath.Disk,
            TargetPath: PathIn("Broken.xml"), PreImage: null /* missing preimage -> fails */, IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 2, dryRun: false);

        Assert.False(result.AllOk);
        Assert.False(result.Steps[0].Ok);  // newest (Broken) attempted first, fails
        Assert.Single(result.Steps);       // fail-fast: "Good" was never attempted
        // Neither entry is consumed: "Broken" failed (left for retry), "Good" was never
        // attempted (left untouched) — and its file was never deleted.
        Assert.True(File.Exists(goodTarget));
        Assert.Equal(2, journal.List().Count);
        Assert.Equal("Broken", journal.List()[0].ObjectName);
        Assert.Equal("Good", journal.List()[1].ObjectName);
    }

    // ---- labels -----------------------------------------------------------

    [Fact]
    public void Label_create_undo_removes_the_key()
    {
        var file = PathIn("Test.en-us.label.txt");
        LabelFileWriter.CreateOrUpdate(file, "MyLabel", "Hello");

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "labels create",
            TargetType: JournalTargetType.Label, Kind: "label", ObjectName: "MyLabel", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Create, WritePath: JournalWritePath.Disk,
            TargetPath: file, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        var lines = File.ReadAllLines(file);
        Assert.DoesNotContain(lines, l => l.StartsWith("MyLabel="));
    }

    [Fact]
    public void Label_update_undo_restores_the_old_value()
    {
        var file = PathIn("Test2.en-us.label.txt");
        LabelFileWriter.CreateOrUpdate(file, "MyLabel", "Old value");
        LabelFileWriter.CreateOrUpdate(file, "MyLabel", "New value", overwrite: true);

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "labels create",
            TargetType: JournalTargetType.Label, Kind: "label", ObjectName: "MyLabel", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Update, WritePath: JournalWritePath.Disk,
            TargetPath: file, PreImage: "Old value", IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        var lines = File.ReadAllLines(file);
        Assert.Contains("MyLabel=Old value", lines);
    }

    [Fact]
    public void Label_delete_undo_recreates_the_key()
    {
        var file = PathIn("Test3.en-us.label.txt");
        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "labels delete",
            TargetType: JournalTargetType.Label, Kind: "label", ObjectName: "Gone", SecondaryKey: null,
            Model: null, Operation: JournalOperation.Delete, WritePath: JournalWritePath.Disk,
            TargetPath: file, PreImage: "Recovered value", IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        var lines = File.ReadAllLines(file);
        Assert.Contains("Gone=Recovered value", lines);
    }

    [Fact]
    public void Label_rename_undo_renames_back()
    {
        var file = PathIn("Test4.en-us.label.txt");
        LabelFileWriter.CreateOrUpdate(file, "NewKey", "Value");

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "labels rename",
            TargetType: JournalTargetType.Label, Kind: "label", ObjectName: "NewKey", SecondaryKey: "OldKey",
            Model: null, Operation: JournalOperation.Rename, WritePath: JournalWritePath.Disk,
            TargetPath: file, PreImage: null, IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(result.AllOk);
        var lines = File.ReadAllLines(file);
        Assert.Contains(lines, l => l.StartsWith("OldKey="));
        Assert.DoesNotContain(lines, l => l.StartsWith("NewKey="));
    }

    // ---- bridge parity (faked, no real D365FO.Bridge.exe) ---------------------

    [Fact]
    public void Bridge_create_undo_calls_deleteObject()
    {
        string? calledMethod = null;
        using var harness = FakeBridge.Create(req =>
        {
            calledMethod = (string?)req["method"];
            Assert.Equal("Foo", (string?)req["params"]!["name"]);
            return Ok(req, new JsonObject { ["ok"] = true });
        });

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table --install-to",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Foo", SecondaryKey: null,
            Model: "MyModel", Operation: JournalOperation.Create, WritePath: JournalWritePath.Bridge,
            TargetPath: null, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false, bridgeClientFactory: () => harness.Client);

        Assert.True(result.AllOk);
        Assert.Equal("deleteObject", calledMethod);
    }

    [Fact]
    public void Bridge_update_undo_calls_updateObject_with_preimage_xml()
    {
        string? calledMethod = null;
        string? sentXml = null;
        using var harness = FakeBridge.Create(req =>
        {
            calledMethod = (string?)req["method"];
            sentXml = (string?)req["params"]!["xml"];
            return Ok(req, new JsonObject { ["ok"] = true });
        });

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table --install-to",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Foo", SecondaryKey: null,
            Model: "MyModel", Operation: JournalOperation.Update, WritePath: JournalWritePath.Bridge,
            TargetPath: null, PreImage: "<AxTable><Name>Foo</Name></AxTable>", IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false, bridgeClientFactory: () => harness.Client);

        Assert.True(result.AllOk);
        Assert.Equal("updateObject", calledMethod);
        Assert.Equal("<AxTable><Name>Foo</Name></AxTable>", sentXml);
    }

    [Fact]
    public void Bridge_delete_undo_calls_createObject_with_preimage_xml()
    {
        string? calledMethod = null;
        using var harness = FakeBridge.Create(req =>
        {
            calledMethod = (string?)req["method"];
            return Ok(req, new JsonObject { ["ok"] = true });
        });

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "delete --install-to",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Foo", SecondaryKey: null,
            Model: "MyModel", Operation: JournalOperation.Delete, WritePath: JournalWritePath.Bridge,
            TargetPath: null, PreImage: "<AxTable><Name>Foo</Name></AxTable>", IsTombstone: false, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false, bridgeClientFactory: () => harness.Client);

        Assert.True(result.AllOk);
        Assert.Equal("createObject", calledMethod);
    }

    [Fact]
    public void Bridge_failure_response_fails_the_undo_step_and_keeps_the_entry()
    {
        using var harness = FakeBridge.Create(req => Ok(req, new JsonObject
        {
            ["ok"] = false,
            ["error"] = "NOT_FOUND",
            ["message"] = "boom",
        }));

        var journal = NewJournal();
        journal.Append(new JournalEntry(
            Id: Guid.NewGuid().ToString("N"), TimestampUtc: DateTimeOffset.UtcNow, Command: "generate table --install-to",
            TargetType: JournalTargetType.AotObject, Kind: "table", ObjectName: "Foo", SecondaryKey: null,
            Model: "MyModel", Operation: JournalOperation.Create, WritePath: JournalWritePath.Bridge,
            TargetPath: null, PreImage: null, IsTombstone: true, RnrProjDelta: null));

        var result = UndoEngine.Undo(journal, 1, dryRun: false, bridgeClientFactory: () => harness.Client);

        Assert.False(result.AllOk);
        Assert.Contains("NOT_FOUND", result.Steps[0].Error);
        Assert.Single(journal.List()); // entry left in place for retry
    }

    private static JsonObject Ok(JsonObject request, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = request["id"]?.DeepClone(),
        ["result"] = result,
    };

    /// <summary>
    /// Stand-in for <c>D365FO.Bridge.exe</c> backed by in-memory readers/writers — same
    /// technique as <c>BridgeClientTests.FakeBridge</c>, duplicated here (that one is a
    /// private nested type) since <see cref="UndoEngine"/> needs its own harness instance
    /// per test to assert on the specific verb/args it sends.
    /// </summary>
    private sealed class FakeBridge : IDisposable
    {
        public BridgeClient Client { get; }

        private FakeBridge(BridgeClient client) => Client = client;

        public static FakeBridge Create(Func<JsonObject, JsonObject> respondWith)
        {
            var reader = new DeferredResponseReader(respondWith);
            var client = new BridgeClient(writer: new TeeWriter(reader), reader: reader);
            return new FakeBridge(client);
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly DeferredResponseReader signal;
        private readonly System.Text.StringBuilder buffer = new();

        public TeeWriter(DeferredResponseReader signal) => this.signal = signal;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = buffer.ToString();
                buffer.Clear();
                signal.OnRequest(line);
            }
            else
            {
                buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var ch in value) Write(ch);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }

    private sealed class DeferredResponseReader : TextReader
    {
        private readonly Func<JsonObject, JsonObject> respondWith;
        private readonly Queue<string> queued = new();

        public DeferredResponseReader(Func<JsonObject, JsonObject> respondWith) => this.respondWith = respondWith;

        public void OnRequest(string requestLine)
        {
            var parsed = JsonNode.Parse(requestLine) as JsonObject
                ?? throw new InvalidOperationException("bad request JSON");
            queued.Enqueue(respondWith(parsed).ToJsonString());
        }

        public override string? ReadLine() => queued.Count == 0 ? null : queued.Dequeue();
    }
}
