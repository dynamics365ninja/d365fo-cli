using D365FO.Core.Journal;
using D365FO.Core.Labels;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Correcting a label, and asking whether a model and its project agree.
/// </summary>
public class LabelUpdateAndVerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-w4-{Guid.NewGuid():N}");

    public LabelUpdateAndVerifyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------- labels

    [Fact]
    public void Update_corrects_an_existing_entry_in_place()
    {
        var path = Path.Combine(_dir, "ConFleet.en-us.label.txt");
        LabelFileWriter.CreateOrUpdate(path, "ConVehicle", "Vehicel");

        var result = LabelFileWriter.Update(path, "ConVehicle", "Vehicle");

        Assert.Equal(WriteOutcome.Updated, result.Outcome);
        Assert.Equal("Vehicel", result.OldValue);
        Assert.Contains("ConVehicle=Vehicle", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole reason update exists as its own verb: a mistyped key in a correction must not
    /// become a second label reported as success, which is what create-with-overwrite does.
    /// </summary>
    [Fact]
    public void Update_refuses_a_key_that_does_not_exist_and_leaves_the_file_alone()
    {
        var path = Path.Combine(_dir, "ConFleet.en-us.label.txt");
        LabelFileWriter.CreateOrUpdate(path, "ConVehicle", "Vehicle");
        var before = File.ReadAllText(path);

        var result = LabelFileWriter.Update(path, "ConVehcile", "Vehicle");

        Assert.Equal(WriteOutcome.KeyMissing, result.Outcome);
        Assert.Equal(before, File.ReadAllText(path));

        // And the contrast that motivates it.
        LabelFileWriter.CreateOrUpdate(path, "ConVehcile", "Vehicle", overwrite: true);
        Assert.Contains("ConVehcile=", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Update_on_a_file_that_does_not_exist_reports_the_key_as_missing()
    {
        var result = LabelFileWriter.Update(Path.Combine(_dir, "absent.en-us.label.txt"), "ConVehicle", "Vehicle");
        Assert.Equal(WriteOutcome.KeyMissing, result.Outcome);
    }

    // ------------------------------------------------------------- verify

    private string Model(params (string AxFolder, string Name)[] objects)
    {
        var model = Path.Combine(_dir, "ConFleet", "ConFleet");
        foreach (var (axFolder, name) in objects)
        {
            var dir = Path.Combine(model, axFolder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".xml"), $"<{axFolder}><Name>{name}</Name></{axFolder}>");
        }
        Directory.CreateDirectory(model);
        return model;
    }

    private static void WriteProject(string modelFolder, params string[] includes)
    {
        var items = string.Join("\n", includes.Select(i => $"    <Compile Include=\"{i}\" />"));
        File.WriteAllText(Path.Combine(modelFolder, "ConFleet.rnrproj"),
            $"<Project><ItemGroup>\n{items}\n</ItemGroup></Project>");
    }

    private static (int IssueCount, string[] Findings) Issues(ToolResult<object> result)
    {
        var data = result.Data!;
        var count = (int)data.GetType().GetProperty("issueCount")!.GetValue(data)!;
        var issues = (IEnumerable<ProjectVerifier.Issue>)data.GetType().GetProperty("issues")!.GetValue(data)!;
        return (count, issues.Select(i => i.Finding).ToArray());
    }

    [Fact]
    public void An_object_the_project_does_not_list_is_reported_as_uncompiled()
    {
        var model = Model(("AxTable", "ConFleetVehicle"), ("AxClass", "ConFleetPosting"));
        WriteProject(model, "AxTable\\ConFleetVehicle.xml");

        var (count, findings) = Issues(ProjectVerifier.Verify(model));

        Assert.Equal(1, count);
        Assert.Equal("UNREGISTERED", findings[0]);
    }

    [Fact]
    public void A_project_entry_whose_file_is_gone_is_reported_too()
    {
        var model = Model(("AxTable", "ConFleetVehicle"));
        WriteProject(model, "AxTable\\ConFleetVehicle.xml", "AxClass\\ConFleetDeleted.xml");

        var (count, findings) = Issues(ProjectVerifier.Verify(model));

        Assert.Equal(1, count);
        Assert.Equal("MISSING_FILE", findings[0]);
    }

    /// <summary>
    /// A project that globs its content lists nothing, and calling every file unregistered there
    /// would be a wall of findings that are all wrong.
    /// </summary>
    [Fact]
    public void A_project_with_no_item_list_is_not_judged()
    {
        var model = Model(("AxTable", "ConFleetVehicle"));
        File.WriteAllText(Path.Combine(model, "ConFleet.rnrproj"), "<Project><PropertyGroup /></Project>");

        var result = ProjectVerifier.Verify(model);

        Assert.Equal(0, Issues(result).IssueCount);
        Assert.Contains(result.Warnings!, w => w.Contains("no explicit item list", StringComparison.Ordinal));
    }

    [Fact]
    public void With_no_project_at_all_the_answer_says_only_disk_was_checked()
    {
        var model = Model(("AxTable", "ConFleetVehicle"));

        var result = ProjectVerifier.Verify(model);

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings!, w => w.Contains("No .rnrproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Expected_objects_are_answered_by_name_in_both_spellings()
    {
        var model = Model(("AxTable", "ConFleetVehicle"));
        WriteProject(model, "AxTable\\ConFleetVehicle.xml");

        var result = ProjectVerifier.Verify(model, ["ConFleetVehicle", "AxTable/ConFleetVehicle", "ConGone"]);

        var expected = (System.Collections.IEnumerable)result.Data!.GetType().GetProperty("expected")!.GetValue(result.Data)!;
        var rows = expected.Cast<object>().Select(o => new
        {
            OnDisk = (bool)o.GetType().GetProperty("onDisk")!.GetValue(o)!,
            Registered = (bool)o.GetType().GetProperty("registered")!.GetValue(o)!,
        }).ToList();

        Assert.True(rows[0].OnDisk && rows[0].Registered);
        Assert.True(rows[1].OnDisk && rows[1].Registered);
        Assert.False(rows[2].OnDisk);
    }

    [Fact]
    public void A_folder_that_is_not_there_is_refused_rather_than_reported_as_clean()
    {
        var result = ProjectVerifier.Verify(Path.Combine(_dir, "no-such-model"));
        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }
}
