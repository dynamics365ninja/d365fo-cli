using D365FO.Cli.Commands.Generate;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// The two generate commands that write a file the AOT XML only points at: an
/// <c>AxLabelFile</c>'s <c>.label.txt</c> and an <c>AxResource</c>'s content.
/// </summary>
/// <remarks>
/// Both go to disk outside <c>ScaffoldFileWriter</c>, so neither gets its backup, its journal
/// entry or its "target exists" refusal for free. <c>--overwrite</c> with no <c>--entry</c>
/// used to truncate an existing <c>.label.txt</c> to a bare byte-order mark — a successful
/// generate that destroyed every label in the file, with nothing to recover from.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class GenerateContentFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-content-{Guid.NewGuid():N}");

    public GenerateContentFileTests() => Directory.CreateDirectory(Path.Combine(_dir, "AxLabelFile"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string LabelManifest(string id) => Path.Combine(_dir, "AxLabelFile", $"{id}_en-US.xml");

    private string LabelContent(string id)
        => Path.Combine(_dir, "AxLabelFile", "LabelResources", "en-US", $"{id}.en-US.label.txt");

    private static GenerateLabelFileCommand.Settings Settings(string id, string outPath, params string[] entries)
        => new()
        {
            LabelFileId = id,
            Model = "FleetManagement",
            Labels = entries,
            Out = outPath,
            Overwrite = true,
            Output = "json",
        };

    [Fact]
    public void Overwrite_without_entries_leaves_an_existing_label_file_alone()
    {
        var cmd = new GenerateLabelFileCommand();
        Assert.Equal(0, cmd.Execute(null!, Settings("ConContentA", LabelManifest("ConContentA"),
            "Vehicles=Vehicles", "Setup=Setup")));

        var content = LabelContent("ConContentA");
        var before = File.ReadAllText(content);
        Assert.Contains("Vehicles=Vehicles", before);

        // The manifest is regenerated; the labels beside it are not this command's to discard.
        Assert.Equal(0, cmd.Execute(null!, Settings("ConContentA", LabelManifest("ConContentA"))));

        Assert.Equal(before, File.ReadAllText(content));
    }

    [Fact]
    public void Overwrite_with_entries_replaces_the_content_and_keeps_the_previous_one_as_bak()
    {
        var cmd = new GenerateLabelFileCommand();
        Assert.Equal(0, cmd.Execute(null!, Settings("ConContentB", LabelManifest("ConContentB"), "Vehicles=Vehicles")));
        Assert.Equal(0, cmd.Execute(null!, Settings("ConContentB", LabelManifest("ConContentB"), "Vehicles=Cars")));

        var content = LabelContent("ConContentB");
        Assert.Contains("Vehicles=Cars", File.ReadAllText(content));
        Assert.Contains("Vehicles=Vehicles", File.ReadAllText(content + ".bak"));
    }

    [Fact]
    public void An_existing_resource_content_file_is_a_warning_not_a_failed_command()
    {
        var source = Path.Combine(_dir, "ConLogo.png");
        File.WriteAllBytes(source, [0x89, 0x50, 0x4E, 0x47]);
        var outPath = Path.Combine(_dir, "AxResource", "ConLogo.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var target = Path.Combine(_dir, "AxResource", "ResourceContent", "Images", "ConLogo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, [0x00]);

        // The manifest is already on disk by the time the copy is attempted, so refusing here
        // has to be a warning: a thrown IOException reports a failed command that half ran.
        var exit = new GenerateResourceCommand().Execute(null!, new GenerateResourceCommand.Settings
        {
            Name = "ConLogo",
            Source = source,
            Model = "FleetManagement",
            Out = outPath,
            Overwrite = false,
            Output = "json",
        });

        Assert.Equal(0, exit);
        Assert.True(File.Exists(outPath));
        Assert.Equal([0x00], File.ReadAllBytes(target));
    }
}
