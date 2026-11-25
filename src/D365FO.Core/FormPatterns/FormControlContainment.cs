// <copyright file="FormControlContainment.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using D365FO.Core.Metadata;

namespace D365FO.Core.FormPatterns;

/// <summary>
/// Which control may sit directly under which parent on a form.
/// </summary>
/// <remarks>
/// <para>
/// Two things have to hold. The parent has to hold children at all: a control whose contract
/// declares no <c>Controls</c> member loses whatever is nested in it on read, so the write
/// "succeeds" and the control is gone. And the compiler has to accept the pairing: a
/// RadioButton under a Grid is written fine and then fails the build (issue #290).
/// </para>
/// <para>
/// The second half is the compiler's own table, generated into the contract catalog by
/// <c>scripts/emit-metadata-contracts.ps1</c>. It judges the direct parent only, with exact
/// types: a Group inside a Grid takes a RadioButton, because the Group is the parent asked.
/// </para>
/// </remarks>
public static class FormControlContainment
{
    /// <summary>The parent type of a control that sits at the root of a form's design.</summary>
    public const string DesignType = "AxFormDesign";

    /// <summary>The type of a control element carrying no <c>i:type</c> (an extension control).</summary>
    public const string UntypedControl = "AxFormControl";

    /// <summary>
    /// False only when the contract catalog knows <paramref name="parentType"/> and it has no
    /// child collection. A type the catalog does not know is not judged.
    /// </summary>
    public static bool HoldsControls(string parentType)
        => MetadataContracts.Find(parentType) is not { } contract || contract.IndexOf("Controls") >= 0;

    /// <summary>
    /// True unless <paramref name="childType"/> is a control type the metadata model declares
    /// and <paramref name="parentType"/> cannot take it. An undeclared child type is left to
    /// the write, which refuses it by name.
    /// </summary>
    public static bool Allows(string parentType, string childType)
    {
        if (MetadataContracts.Find(childType) is null) return true;
        if (!HoldsControls(parentType)) return false;
        if (MetadataContracts.AllowedChildControls.TryGetValue(parentType, out var allowed))
            return allowed.Contains(childType);
        return !MetadataContracts.DisallowedChildControls.TryGetValue(parentType, out var disallowed)
               || !disallowed.Contains(childType);
    }

    /// <summary>
    /// The parents that accept <paramref name="childType"/>, by the names <c>--type</c> takes,
    /// with <c>Design</c> first when the design root does.
    /// </summary>
    public static IReadOnlyList<string> ParentsAccepting(string childType)
    {
        var parents = MetadataContracts.EnumValues("FormControlType")
            .Where(name => FormControlFactory.AxTypeFor(name) is var parent
                           && MetadataContracts.Find(parent) is not null
                           && Allows(parent, childType))
            .ToList();
        if (Allows(DesignType, childType)) parents.Insert(0, "Design");
        return parents;
    }
}
