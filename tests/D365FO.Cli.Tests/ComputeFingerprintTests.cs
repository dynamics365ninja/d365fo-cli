using D365FO.Cli.Commands.Index;
using D365FO.Core.Index;

namespace D365FO.Cli.Tests;

/// <summary>
/// Verifies that <see cref="IndexExtractCommand.ComputeFingerprint"/> is
/// sensitive to both label-file changes and LabelLanguages config changes.
/// </summary>
public class ComputeFingerprintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-fp-{Guid.NewGuid():N}");

    public ComputeFingerprintTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Fingerprint_changes_when_label_txt_added()
    {
        var fp1 = IndexExtractCommand.ComputeFingerprint(_dir);
        File.WriteAllText(Path.Combine(_dir, "SysLabel.en-US.label.txt"), "Title=Hello");
        var fp2 = IndexExtractCommand.ComputeFingerprint(_dir);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_changes_when_label_txt_modified()
    {
        var txt = Path.Combine(_dir, "SysLabel.en-US.label.txt");
        File.WriteAllText(txt, "Title=Hello");
        var fp1 = IndexExtractCommand.ComputeFingerprint(_dir);

        // Force mtime change (some FS have 1-s resolution)
        File.SetLastWriteTimeUtc(txt, File.GetLastWriteTimeUtc(txt).AddSeconds(2));
        var fp2 = IndexExtractCommand.ComputeFingerprint(_dir);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_changes_when_LabelLanguages_extended()
    {
        File.WriteAllText(Path.Combine(_dir, "SysLabel.en-US.label.txt"), "Title=Hello");

        var fp1 = IndexExtractCommand.ComputeFingerprint(_dir, new[] { "en-us" });
        var fp2 = IndexExtractCommand.ComputeFingerprint(_dir, new[] { "en-us", "cs" });

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_stable_when_nothing_changes()
    {
        File.WriteAllText(Path.Combine(_dir, "SysLabel.en-US.label.txt"), "Title=Hello");
        var langs = new[] { "en-us", "cs" };

        var fp1 = IndexExtractCommand.ComputeFingerprint(_dir, langs);
        var fp2 = IndexExtractCommand.ComputeFingerprint(_dir, langs);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_changes_when_a_file_is_renamed_keeping_count_and_mtime()
    {
        // A rename keeps the file's mtime and the folder's file count, so a count:mtime
        // fingerprint called the model unchanged and refresh kept the old object name.
        Directory.CreateDirectory(Path.Combine(_dir, "AxClass"));
        var before = Path.Combine(_dir, "AxClass", "ConFleetOld.xml");
        File.WriteAllText(before, "<AxClass/>");
        File.SetLastWriteTimeUtc(before, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fp1 = IndexExtractCommand.ComputeFingerprint(_dir);

        File.Move(before, Path.Combine(_dir, "AxClass", "ConFleetNew.xml"));
        var fp2 = IndexExtractCommand.ComputeFingerprint(_dir);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_does_not_depend_on_enumeration_order()
    {
        // The name hash is summed, so two folders with the same files listed in a different
        // order agree. Built here in opposite creation order.
        var other = _dir + "-twin";
        try
        {
            var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var (dir, names) in new[] { (_dir, new[] { "A.xml", "B.xml" }), (other, new[] { "B.xml", "A.xml" }) })
            {
                Directory.CreateDirectory(dir);
                foreach (var n in names)
                {
                    File.WriteAllText(Path.Combine(dir, n), "<x/>");
                    File.SetLastWriteTimeUtc(Path.Combine(dir, n), stamp);
                }
            }

            Assert.Equal(IndexExtractCommand.ComputeFingerprint(_dir), IndexExtractCommand.ComputeFingerprint(other));
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_fingerprint_stored_by_an_older_build_never_matches_so_the_model_is_re_extracted_once()
    {
        // Indexes written before the name hash hold "count:ticks:langs". The refresh only
        // compares strings, so the old value must simply differ (one re-extract), never throw.
        var file = Path.Combine(_dir, "SysLabel.en-US.label.txt");
        File.WriteAllText(file, "Title=Hello");
        var scan = IndexSync.ScanModel(_dir);
        var legacy = $"{scan.FileCount}:{scan.NewestUtc.Ticks}:en-us";

        var current = IndexExtractCommand.ComputeFingerprint(_dir, new[] { "en-us" });

        Assert.StartsWith(legacy + ":", current, StringComparison.Ordinal);
        Assert.NotEqual(legacy, current);
    }

    [Fact]
    public void Fingerprint_langs_order_insensitive()
    {
        var fp1 = IndexExtractCommand.ComputeFingerprint(_dir, new[] { "en-us", "cs" });
        var fp2 = IndexExtractCommand.ComputeFingerprint(_dir, new[] { "cs", "en-us" });

        Assert.Equal(fp1, fp2);
    }
}
