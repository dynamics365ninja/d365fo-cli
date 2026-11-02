using System.Xml.Linq;
using D365FO.Cli.Commands.Generate;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>generate datasource-method</c> / <c>generate control-method</c>, <c>generate role --add-to</c> and
/// <c>generate duty</c> / <c>privilege --into-role</c> edit an existing file in place. They kept their own copy
/// of the write that issue #158 fixed in the scaffolder: a fixed <c>&lt;file&gt;.tmp</c> sibling
/// shared by every concurrent writer, and a move pair that left the file missing when the second
/// move failed. They now go through <c>ScaffoldFileWriter.SaveInPlace</c>.
/// </summary>
/// <remarks>
/// The probe is a directory squatting on the old fixed staging name: <c>File.Create</c> on it
/// throws, so a path still staging through <c>&lt;file&gt;.tmp</c> fails here, while the shared
/// writer stages under a per-call name and never touches it.
/// </remarks>
public sealed class InPlaceWriteAtomicityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"inplace-atomic-{Guid.NewGuid():N}");

    public InPlaceWriteAtomicityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Role(string name)
    {
        var path = Path.Combine(_dir, name + ".xml");
        File.WriteAllText(path, $"<AxSecurityRole><Name>{name}</Name></AxSecurityRole>");
        Directory.CreateDirectory(path + ".tmp");
        return path;
    }

    [Fact]
    public void Role_add_to_does_not_stage_through_a_fixed_tmp_sibling()
    {
        var path = Role("ConFleetManager");

        var ok = SecurityRoleMerge.AddReferences(path, ["ConFleetMaintain"], null, out _, out var error);

        Assert.True(ok, error);
        Assert.Contains("ConFleetMaintain", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.True(File.Exists(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Form_method_save_does_not_stage_through_a_fixed_tmp_sibling()
    {
        var path = Path.Combine(_dir, "ConFleetVehicle.xml");
        File.WriteAllText(path, "<AxForm><Name>ConFleetVehicle</Name></AxForm>");
        Directory.CreateDirectory(path + ".tmp");

        FormMethodImpl.AtomicSave(XDocument.Parse("<AxForm><Name>ConFleetVehicle</Name><Edited /></AxForm>"), path);

        Assert.NotNull(XDocument.Load(path).Root!.Element("Edited"));
        Assert.True(File.Exists(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
