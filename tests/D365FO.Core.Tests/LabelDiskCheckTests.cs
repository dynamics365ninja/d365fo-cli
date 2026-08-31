using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The label search/resolve disk check (port of upstream labelDiskCheck.ts): the index is a
/// snapshot never invalidated on delete or rollback, so a phantom row reads as reusable and
/// the failure only surfaces at build time as BPErrorUnknownLabel.
/// </summary>
public class LabelDiskCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-labelcheck-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public LabelDiskCheckTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "index.sqlite");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteLabelFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void FileDeclaresLabel_matches_first_label_behind_a_BOM_and_is_case_insensitive()
    {
        // Every shipped .label.txt starts with a BOM — without tolerating it the FIRST
        // label of every file would read as missing.
        var content = "﻿VehicleId=Vehicle\r\n ;Help text\r\nMileage=Mileage\r\n";
        Assert.True(LabelDiskCheck.FileDeclaresLabel(content, "VehicleId"));
        Assert.True(LabelDiskCheck.FileDeclaresLabel(content, "vehicleid"));
        Assert.True(LabelDiskCheck.FileDeclaresLabel(content, "Mileage"));
        Assert.False(LabelDiskCheck.FileDeclaresLabel(content, "Vehicle"));   // prefix is not a declaration
        Assert.False(LabelDiskCheck.FileDeclaresLabel(content, "Help"));      // comment lines cannot match
    }

    [Fact]
    public void LabelsMissingOnDisk_reports_true_false_and_null()
    {
        var path = WriteLabelFile("Fleet.en-US.label.txt", "﻿VehicleId=Vehicle\n");
        long bytes = 0;

        // Present, missing, and — when no path can be read — no verdict.
        var verdicts = LabelDiskCheck.LabelsMissingOnDisk(["VehicleId", "GhostLabel"], [path], ref bytes);
        Assert.False(verdicts["VehicleId"]);
        Assert.True(verdicts["GhostLabel"]);

        long bytes2 = 0;
        var noFiles = LabelDiskCheck.LabelsMissingOnDisk(["GhostLabel"], [Path.Combine(_dir, "NoSuch.label.txt")], ref bytes2);
        Assert.Null(noFiles["GhostLabel"]);
    }

    [Fact]
    public void LabelsMissingOnDisk_present_in_any_language_file_is_present()
    {
        // A label only translated to one language is normal — the check is about
        // existence, not completeness.
        var en = WriteLabelFile("Fleet.en-US.label.txt", "﻿Other=Other\n");
        var cs = WriteLabelFile("Fleet.cs.label.txt", "﻿VehicleId=Vozidlo\n");
        long bytes = 0;
        var verdicts = LabelDiskCheck.LabelsMissingOnDisk(["VehicleId"], [en, cs], ref bytes);
        Assert.False(verdicts["VehicleId"]);
    }

    [Fact]
    public void Budget_exhaustion_yields_no_verdict_not_missing()
    {
        var path = WriteLabelFile("Fleet.en-US.label.txt", "﻿VehicleId=Vehicle\n");
        // A budget already spent: reporting "missing" off a sweep that read nothing
        // would be a verdict the files never gave.
        long bytes = LabelDiskCheck.MaxTotalReadBytes;
        var verdicts = LabelDiskCheck.LabelsMissingOnDisk(["GhostLabel"], [path], ref bytes);
        Assert.Null(verdicts["GhostLabel"]);
    }

    [Fact]
    public void Annotate_marks_phantoms_and_leaves_verified_and_unverifiable_hits_alone()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var labelPath = WriteLabelFile("Fleet.en-US.label.txt", "﻿VehicleId=Vehicle\n");
        repo.ApplyExtract(ExtractBatch.Empty("Fleet") with
        {
            Labels = new[]
            {
                new ExtractedLabel("Fleet", "en-US", "VehicleId", "Vehicle"),
                // The stale row: indexed, but not in the file any more.
                new ExtractedLabel("Fleet", "en-US", "GhostLabel", "Ghost"),
                // A file the index has no path for — must yield no verdict, no mark.
                new ExtractedLabel("Orphan", "en-US", "SomeKey", "Some"),
            },
            LabelFiles = new[] { new ExtractedLabelFile("Fleet", "en-US", labelPath) },
        });

        var hits = repo.SearchLabels("e", limit: 50);
        Assert.Equal(3, hits.Count);

        var (phantoms, warnings) = LabelDiskCheck.Annotate(repo, hits);
        Assert.Contains("@Fleet:GhostLabel", phantoms);
        Assert.DoesNotContain("@Fleet:VehicleId", phantoms);
        Assert.DoesNotContain("@Orphan:SomeKey", phantoms); // no indexed path → say nothing
        Assert.NotNull(warnings);
        Assert.Contains("BPErrorUnknownLabel", warnings![0]);
    }

    [Fact]
    public void Extractor_round_trip_stores_label_file_paths()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Fleet") with
        {
            LabelFiles = new[]
            {
                new ExtractedLabelFile("Fleet", "en-US", "/x/Fleet.en-US.label.txt"),
                new ExtractedLabelFile("Fleet", "cs", "/x/Fleet.cs.label.txt"),
            },
        });

        var paths = repo.GetLabelFilePaths("fleet"); // case-insensitive, as label tokens are
        Assert.Equal(2, paths.Count);

        // Re-extract replaces rather than accumulates.
        repo.ApplyExtract(ExtractBatch.Empty("Fleet") with
        {
            LabelFiles = new[] { new ExtractedLabelFile("Fleet", "en-US", "/y/Fleet.en-US.label.txt") },
        });
        var refreshed = repo.GetLabelFilePaths("Fleet");
        Assert.Single(refreshed);
        Assert.Equal("/y/Fleet.en-US.label.txt", refreshed[0]);
    }
}
