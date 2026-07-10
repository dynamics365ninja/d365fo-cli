using D365FO.Core.Labels;
using Xunit;

namespace D365FO.Core.Tests;

public class LabelFileWriterTests
{
    private static string NewTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"d365fo-label-{Guid.NewGuid():N}.label.txt");
        return path;
    }

    [Fact]
    public void CreateOrUpdate_creates_file_when_missing()
    {
        var path = NewTempFile();
        try
        {
            var r = LabelFileWriter.CreateOrUpdate(path, "Vehicle", "Vehicle");
            Assert.Equal(WriteOutcome.Created, r.Outcome);
            Assert.Equal("Vehicle", r.NewValue);
            Assert.Null(r.OldValue);
            var text = File.ReadAllText(path);
            Assert.Contains("Vehicle=Vehicle", text);
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void CreateOrUpdate_fails_when_key_exists_without_overwrite()
    {
        var path = NewTempFile();
        try
        {
            LabelFileWriter.CreateOrUpdate(path, "Vehicle", "One");
            var r = LabelFileWriter.CreateOrUpdate(path, "Vehicle", "Two");
            Assert.Equal(WriteOutcome.KeyExists, r.Outcome);
            Assert.Equal("One", r.OldValue);
            Assert.Contains("Vehicle=One", File.ReadAllText(path));
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void CreateOrUpdate_overwrites_value_when_flag_set()
    {
        var path = NewTempFile();
        try
        {
            LabelFileWriter.CreateOrUpdate(path, "Vehicle", "One");
            var r = LabelFileWriter.CreateOrUpdate(path, "Vehicle", "Two", overwrite: true);
            Assert.Equal(WriteOutcome.Updated, r.Outcome);
            Assert.Equal("One", r.OldValue);
            Assert.Equal("Two", r.NewValue);
            Assert.Contains("Vehicle=Two", File.ReadAllText(path));
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void Rename_moves_key_preserving_value_and_comments()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "; translator note\nVehicle=My Vehicle\n");
            var r = LabelFileWriter.Rename(path, "Vehicle", "Car");
            Assert.Equal(WriteOutcome.Renamed, r.Outcome);
            Assert.Equal("My Vehicle", r.NewValue);
            var text = File.ReadAllText(path);
            Assert.Contains("; translator note", text);
            Assert.Contains("Car=My Vehicle", text);
            Assert.DoesNotContain("Vehicle=My Vehicle", text);
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void Rename_returns_KeyMissing_when_source_absent()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "A=B\n");
            var r = LabelFileWriter.Rename(path, "X", "Y");
            Assert.Equal(WriteOutcome.KeyMissing, r.Outcome);
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void Rename_returns_KeyExists_when_target_present_without_overwrite()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "A=1\nB=2\n");
            var r = LabelFileWriter.Rename(path, "A", "B");
            Assert.Equal(WriteOutcome.KeyExists, r.Outcome);
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void Delete_removes_key_and_keeps_surrounding_lines()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "A=1\nB=2\n");
            var r = LabelFileWriter.Delete(path, "A");
            Assert.Equal(WriteOutcome.Deleted, r.Outcome);
            Assert.Equal("1", r.OldValue);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("A=1", text);
            Assert.Contains("B=2", text);
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void New_keys_are_inserted_in_ordinal_case_insensitive_order()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "Alpha=1\nGamma=3\n");
            LabelFileWriter.CreateOrUpdate(path, "beta", "2");
            var lines = File.ReadAllLines(path).Where(l => l.Contains('=')).ToArray();
            Assert.Equal(new[] { "Alpha=1", "beta=2", "Gamma=3" }, lines);
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void Sorted_insert_keeps_comment_blocks_attached_to_their_entry()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "Alpha=1\n; translator note for Gamma\nGamma=3\n");
            LabelFileWriter.CreateOrUpdate(path, "Beta", "2");
            var lines = File.ReadAllLines(path);
            Assert.Equal("Alpha=1", lines[0]);
            Assert.Equal("Beta=2", lines[1]);
            Assert.Equal("; translator note for Gamma", lines[2]);
            Assert.Equal("Gamma=3", lines[3]);
        }
        finally { SafeDelete(path); }
    }

    [Fact]
    public void Keys_sorting_after_all_existing_append_at_end()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "Alpha=1\n");
            LabelFileWriter.CreateOrUpdate(path, "Zulu", "26");
            var lines = File.ReadAllLines(path).Where(l => l.Contains('=')).ToArray();
            Assert.Equal(new[] { "Alpha=1", "Zulu=26" }, lines);
        }
        finally { SafeDelete(path); }
    }

    [Theory]
    [InlineData("Base_Extension", true)]
    [InlineData("Base_ExtensionContoso", true)]
    [InlineData("base_extension", true)]
    [InlineData(@"C:\Pkg\M\M\AxLabelFile\LabelResources\en-us\Foo_Extension.en-us.label.txt", true)]
    [InlineData("Contoso", false)]
    [InlineData("ExtensionPoints", false)]
    [InlineData("/pkg/M/M/AxLabelFile/LabelResources/en-us/Contoso.en-us.label.txt", false)]
    [InlineData("", false)]
    public void IsExtensionLabelFile_detects_extension_marker(string input, bool expected)
    {
        Assert.Equal(expected, LabelFileWriter.IsExtensionLabelFile(input));
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); } catch { }
    }
}
