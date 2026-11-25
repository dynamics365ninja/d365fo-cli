using D365FO.Core.Ops;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// xppc <c>-incremental</c> recompiles only what changed: code that uses a changed table, EDT or
/// enum is not checked, and a recompiled data entity loses its OData actions. Either one needs a
/// full build, whatever was asked.
/// </summary>
public sealed class IncrementalBuildGuardTests : IDisposable
{
    private static readonly DateTime Built = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-incr-{Guid.NewGuid():N}");

    private XppModelBuild.Target Target => new("ConFleet", "ConFleet", _root, null);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private void Source(string type, string name, DateTime written, string content = "<x/>")
        => Put(Path.Combine(_root, "ConFleet", "ConFleet", type, name + ".xml"), written, content);

    private void Metadata(string type, string name, DateTime written)
        => Put(Path.Combine(_root, "ConFleet", "XppMetadata", "ConFleet", type, name + ".xml"), written, "<x/>");

    private static void Put(string path, DateTime written, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, written);
    }

    /// <summary>A model that was built: one table and one class with their compiler metadata.</summary>
    private void BuiltModel()
    {
        Source("AxTable", "ConVehicle", Built.AddHours(-1));
        Metadata("AxTable", "ConVehicle", Built);
        Source("AxEdt", "ConPlate", Built.AddHours(-1));
        Source("AxClass", "ConHelper", Built.AddHours(-1));
        Metadata("AxClass", "ConHelper", Built);
    }

    private string? Reason(DateTime? lastBuild = null) => IncrementalBuildGuard.FullBuildReason(Target, lastBuild);

    [Fact]
    public void Nothing_changed_stays_incremental()
    {
        BuiltModel();
        Assert.Null(Reason());
    }

    [Fact]
    public void A_changed_class_stays_incremental()
    {
        BuiltModel();
        Source("AxClass", "ConHelper", Built.AddHours(1));
        Assert.Null(Reason());
    }

    [Theory]
    [InlineData("AxTable", "ConVehicle")]
    [InlineData("AxTable", "ConNewTable")]
    [InlineData("AxTableExtension", "CustTable.ConFleet")]
    [InlineData("AxEdt", "ConPlate")]
    [InlineData("AxEdtExtension", "Name.ConFleet")]
    [InlineData("AxEnum", "ConColor")]
    [InlineData("AxEnumExtension", "ABC.ConFleet")]
    public void A_changed_type_shape_needs_a_full_build(string type, string name)
    {
        BuiltModel();
        Source(type, name, Built.AddHours(1));

        Assert.Contains(name, Reason());
    }

    [Theory]
    [InlineData("AxTable", "ConVehicle")]
    [InlineData("AxEdt", "ConPlate")]
    public void A_change_an_earlier_build_covered_stays_incremental(string type, string name)
    {
        // xppc rewrites compiler metadata only when its content changes, so the time of the last
        // successful build is a baseline of its own.
        BuiltModel();
        Source(type, name, Built.AddHours(1));

        Assert.Null(Reason(lastBuild: Built.AddHours(2)));
    }

    [Fact]
    public void A_model_never_built_is_left_as_asked()
    {
        Source("AxTable", "ConVehicle", Built);
        Assert.Null(Reason());
    }

    [Theory]
    [InlineData("AxDataEntityView")]
    [InlineData("AxDataEntityViewExtension")]
    [InlineData("AxClass")]
    public void Recompiling_something_with_an_odata_action_needs_a_full_build(string type)
    {
        BuiltModel();
        Source(type, "ConEntity", Built.AddHours(1),
            "<x><![CDATA[ [SysODataActionAttribute('Confirm', true)] public void confirm() {} ]]></x>");

        Assert.Contains("OData action", Reason());
    }

    [Theory]
    [InlineData("// [SysODataActionAttribute('Confirm', true)]")]
    [InlineData("/* [SysODataAction('Confirm', true)] */")]
    public void A_commented_out_action_does_not_count(string source)
    {
        BuiltModel();
        Source("AxClass", "ConHelper", Built.AddHours(1), $"<x><![CDATA[{source}]]></x>");

        Assert.Null(Reason());
    }

    /// <summary>The package's OData resource file: one entity per line, name, tab, resource.</summary>
    private void ODataRuntime(string entity, params string[] actions)
    {
        var items = string.Concat(actions.Select(a =>
            $"<a:KeyValueOfstringAxODataActionLjh4bohd><a:Key>{a}</a:Key><a:Value><Name>{a}</Name></a:Value></a:KeyValueOfstringAxODataActionLjh4bohd>"));
        Put(Path.Combine(_root, "ConFleet", "bin", "Dynamics.AX.CONFleet.ODataExtends.runtime"), Built,
            $"\uFEFF{entity}\t<AxODataResource><Name>{entity}</Name><Actions><Items xmlns:a=\"x\">{items}</Items></Actions></AxODataResource>\r\n");
    }

    private void EntityWithAction()
    {
        Source("AxDataEntityView", "ConEntity", Built.AddHours(-1),
            "<x><![CDATA[ [SysODataActionAttribute('Confirm', true)] public void confirmIt() {} ]]></x>");
        Metadata("AxDataEntityView", "ConEntity", Built);
    }

    [Fact]
    public void An_action_an_earlier_incremental_build_dropped_needs_a_full_build()
    {
        BuiltModel();
        EntityWithAction();
        ODataRuntime("ConEntity");

        var reason = Reason();

        Assert.Contains("ConEntity", reason);
        Assert.Contains("Confirm", reason);
    }

    [Theory]
    [InlineData("ConEntity", "confirm")]   // present, compared without regard to case
    [InlineData("ConOther", "Nothing")]    // no entry: a non-public entity has none
    public void A_runtime_file_that_lacks_nothing_stays_incremental(string entity, string action)
    {
        BuiltModel();
        EntityWithAction();
        ODataRuntime(entity, action);

        Assert.Null(Reason());
    }

    [Fact]
    public void An_unchanged_entity_with_actions_stays_incremental()
    {
        BuiltModel();
        Source("AxDataEntityView", "ConEntity", Built.AddHours(-1), "<x>[SysODataActionAttribute('Confirm', true)]</x>");
        Metadata("AxDataEntityView", "ConEntity", Built);

        Assert.Null(Reason());
    }
}
