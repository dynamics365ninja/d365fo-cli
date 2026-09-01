using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Unit tests for Phase 4 lint rules. Uses a synthetic in-memory repo
/// with pre-set extraction flags to verify that each lint query fires on
/// positive fixtures and does not fire on negative fixtures.
/// </summary>
public class LintRuleTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-lint-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public LintRuleTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private void ApplyBatch(D365FO.Core.Index.ExtractBatch batch) => _repo.ApplyExtract(batch);

    // Helper: minimal batch with one custom model
    private static D365FO.Core.Index.ExtractBatch MinimalBatch(
        string model,
        IReadOnlyList<D365FO.Core.Index.ExtractedClass> classes,
        IReadOnlyList<D365FO.Core.Index.ExtractedTable>? tables = null) =>
        new(model, "Contoso", "usr", IsCustom: true,
            Tables: tables ?? Array.Empty<D365FO.Core.Index.ExtractedTable>(),
            Classes: classes,
            Edts: Array.Empty<D365FO.Core.Index.ExtractedEdt>(),
            Enums: Array.Empty<D365FO.Core.Index.ExtractedEnum>(),
            MenuItems: Array.Empty<D365FO.Core.Index.ExtractedMenuItem>(),
            CocExtensions: Array.Empty<D365FO.Core.Index.ExtractedCoc>(),
            Labels: Array.Empty<D365FO.Core.Index.ExtractedLabel>());

    // ---- 4.1 insert-in-loop ----

    [Fact]
    public void InsertInLoop_fires_when_HasInsertInLoop_is_true()
    {
        ApplyBatch(MinimalBatch("ModelA",
            new[] { new D365FO.Core.Index.ExtractedClass("InsertLoopClass", null, false, true, null,
                new[] { new D365FO.Core.Index.ExtractedMethod("badMethod", null, "void", false,
                    HasInsertInLoop: true) }) }));

        var hits = _repo.FindInsertInLoopMethods();
        Assert.Contains(hits, h => h.TargetName == "InsertLoopClass::badMethod");
    }

    [Fact]
    public void InsertInLoop_silent_when_flag_false()
    {
        ApplyBatch(MinimalBatch("ModelB",
            new[] { new D365FO.Core.Index.ExtractedClass("CleanClass", null, false, true, null,
                new[] { new D365FO.Core.Index.ExtractedMethod("goodMethod", null, "void", false) }) }));

        var hits = _repo.FindInsertInLoopMethods();
        Assert.DoesNotContain(hits, h => h.TargetName == "CleanClass::goodMethod");
    }

    // ---- 4.2 nested-select ----

    [Fact]
    public void NestedSelect_fires_when_HasNestedSelect_is_true()
    {
        ApplyBatch(MinimalBatch("ModelC",
            new[] { new D365FO.Core.Index.ExtractedClass("NestedSelectClass", null, false, true, null,
                new[] { new D365FO.Core.Index.ExtractedMethod("query", null, "void", false,
                    HasNestedSelect: true) }) }));

        var hits = _repo.FindNestedSelectMethods();
        Assert.Contains(hits, h => h.TargetName == "NestedSelectClass::query");
    }

    // ---- 4.4 force-literals ----

    [Fact]
    public void ForceLiterals_fires_when_flag_set()
    {
        ApplyBatch(MinimalBatch("ModelD",
            new[] { new D365FO.Core.Index.ExtractedClass("ForceLitClass", null, false, true, null,
                new[] { new D365FO.Core.Index.ExtractedMethod("selectMethod", null, "void", false,
                    HasForceLiterals: true) }) }));

        var hits = _repo.FindForceLiteralsMethods();
        Assert.Contains(hits, h => h.TargetName == "ForceLitClass::selectMethod");
    }

    // ---- 4.5 tts-try-catch ----

    [Fact]
    public void TtsTryCatch_fires_when_flag_set()
    {
        ApplyBatch(MinimalBatch("ModelE",
            new[] { new D365FO.Core.Index.ExtractedClass("TtsClass", null, false, true, null,
                new[] { new D365FO.Core.Index.ExtractedMethod("writeMethod", null, "void", false,
                    HasTryCatchInTts: true) }) }));

        var hits = _repo.FindTtsTryCatchMethods();
        Assert.Contains(hits, h => h.TargetName == "TtsClass::writeMethod");
    }

    // ---- 4.6 batch-no-cango ----

    [Fact]
    public void BatchNoCango_fires_for_RunBaseBatch_without_canGoBatch()
    {
        ApplyBatch(MinimalBatch("ModelF",
            new[] { new D365FO.Core.Index.ExtractedClass("MyBatchJob", null, false, false, null,
                Array.Empty<D365FO.Core.Index.ExtractedMethod>())
            {
                IsRunBaseBatch = true,
                HasCanGoBatch = false,
            } }));

        var hits = _repo.FindRunBaseBatchWithoutCanGoBatch();
        Assert.Contains(hits, h => h.TargetName == "MyBatchJob");
    }

    [Fact]
    public void BatchNoCango_silent_when_HasCanGoBatch_is_true()
    {
        ApplyBatch(MinimalBatch("ModelG",
            new[] { new D365FO.Core.Index.ExtractedClass("GoodBatch", null, false, false, null,
                Array.Empty<D365FO.Core.Index.ExtractedMethod>())
            {
                IsRunBaseBatch = true,
                HasCanGoBatch = true,
            } }));

        var hits = _repo.FindRunBaseBatchWithoutCanGoBatch();
        Assert.DoesNotContain(hits, h => h.TargetName == "GoodBatch");
    }

    // ---- 4.7 public-instance-field ----

    [Fact]
    public void PublicInstanceField_fires_when_flag_set()
    {
        ApplyBatch(MinimalBatch("ModelH",
            new[] { new D365FO.Core.Index.ExtractedClass("BadEncapsulation", null, false, false, null,
                Array.Empty<D365FO.Core.Index.ExtractedMethod>())
            {
                HasPublicInstanceFields = true,
            } }));

        var hits = _repo.FindPublicInstanceFieldClasses();
        Assert.Contains(hits, h => h.TargetName == "BadEncapsulation");
    }
}
