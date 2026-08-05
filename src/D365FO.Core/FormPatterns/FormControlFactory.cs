// <copyright file="FormControlFactory.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Xml.Linq;

namespace D365FO.Core.FormPatterns;

/// <summary>
/// Builds a single, AOT-valid <c>&lt;AxFormControl&gt;</c> element for a normalized
/// control type — the <see cref="XElement"/> counterpart of the private string
/// renderers in <c>Scaffolding.FormPatternTemplates</c>.
///
/// Those renderers can only be reached by generating a whole form from a template;
/// anything that edits an <i>existing</i> form (<c>d365fo modify add-control</c>,
/// <c>d365fo form-pattern repair</c>) needs to emit one control at a time into a
/// parsed document. This is that primitive, and it is deliberately the only place
/// that knows the control-shape rules:
/// <list type="bullet">
/// <item><description>controls inside <c>&lt;Design&gt;</c> live in the empty namespace
/// (<c>xmlns=""</c>) even though the document default is
/// <c>Microsoft.Dynamics.AX.Metadata.V6</c>,</description></item>
/// <item><description>the concrete subtype goes on <c>i:type</c> and the same type
/// is repeated in the <c>&lt;Type&gt;</c> element,</description></item>
/// <item><description><c>&lt;FormControlExtension i:nil="true" /&gt;</c> is mandatory —
/// Visual Studio's metadata reader treats its absence as a malformed control,
/// and</description></item>
/// <item><description>container controls must carry a <c>&lt;Controls /&gt;</c> element
/// even when empty.</description></item>
/// </list>
/// </summary>
public static class FormControlFactory
{
    internal static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Normalized type → concrete <c>i:type</c>, for the cases where it is not simply
    /// <c>AxForm{Type}Control</c>. Anything absent falls through to that default, which
    /// is correct for Grid/Group/Tab/TabPage/ActionPane/ButtonGroup/String/Image/….
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AxTypeOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The metadata model has no AxFormMultilineTextControl — a multi-line
            // field is a String control with MultiLine=Yes.
            ["MultilineText"] = "AxFormStringControl",
            ["Integer"] = "AxFormIntControl",
            ["DateTime"] = "AxFormDateTimeControl",
            ["RadioButton"] = "AxFormRadioControl",
            // A plain <AxFormControl> with no i:type is how extension controls
            // (QuickFilterControl, dimension controls, …) are represented.
            ["Control"] = "",
        };

    /// <summary>
    /// Control types that own a <c>&lt;Controls&gt;</c> collection. A container emitted
    /// without one is rejected by the metadata reader.
    /// </summary>
    public static readonly IReadOnlySet<string> ContainerTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ActionPane", "ActionPaneTab", "ButtonGroup", "Group", "Tab", "TabPage", "Grid",
        };

    /// <summary>
    /// Properties that must be emitted <i>after</i> <c>&lt;Controls&gt;</c> on a container.
    /// AxForm's serializer is order-sensitive: the container-layout properties are
    /// declared after the child collection, everything else before it.
    /// </summary>
    private static readonly IReadOnlySet<string> TrailingProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AlignChild", "AlignChildren", "ArrangeMethod", "FrameType", "Style",
            "ViewEditMode", "DataGroup", "DataSource", "MultiSelect", "ShowRowLabels",
            "AlternateRowShading", "Columns", "HeightMode", "WidthMode",
        };

    /// <summary>True when a control of <paramref name="normalizedType"/> holds children.</summary>
    public static bool IsContainer(string normalizedType) => ContainerTypes.Contains(normalizedType);

    /// <summary>
    /// True when <paramref name="normalizedType"/> names a form-control <i>extension</i>
    /// (QuickFilterControl, dimension controls, …) rather than a metamodel control type.
    ///
    /// <see cref="FormDesignWalker.NormalizeControlType"/> strips a trailing
    /// <c>Control</c> from every <c>i:type</c>, so a normalized type that still ends in
    /// <c>Control</c> can only have come from a <c>&lt;FormControlExtension&gt;&lt;Name&gt;</c>.
    /// Those are emitted as a bare <c>&lt;AxFormControl&gt;</c> with no <c>i:type</c> and no
    /// <c>&lt;Type&gt;</c>.
    /// </summary>
    public static bool IsExtensionControl(string normalizedType) =>
        normalizedType.EndsWith("Control", StringComparison.Ordinal);

    /// <summary>Concrete <c>i:type</c> for a normalized control type; empty for extension controls.</summary>
    public static string AxTypeFor(string normalizedType)
    {
        if (string.IsNullOrWhiteSpace(normalizedType)) return "";
        if (AxTypeOverrides.TryGetValue(normalizedType, out var overridden)) return overridden;
        return IsExtensionControl(normalizedType) ? "" : $"AxForm{normalizedType}Control";
    }

    /// <summary>
    /// Build one control element. <paramref name="properties"/> are emitted in the
    /// leading/trailing halves the AOT expects; <paramref name="subPattern"/> declares a
    /// container sub-pattern (<c>&lt;Pattern&gt;</c>/<c>&lt;PatternVersion&gt;</c>).
    /// </summary>
    public static XElement Create(
        string normalizedType,
        string name,
        IReadOnlyDictionary<string, string>? properties = null,
        string? subPattern = null,
        string? subPatternVersion = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));

        var none = XNamespace.None;
        var el = new XElement(none + "AxFormControl");

        var axType = AxTypeFor(normalizedType);
        if (axType.Length > 0) el.SetAttributeValue(Xsi + "type", axType);

        el.Add(new XElement(none + "Name", name));

        if (!string.IsNullOrWhiteSpace(subPattern))
        {
            el.Add(new XElement(none + "Pattern", subPattern));
            el.Add(new XElement(none + "PatternVersion", subPatternVersion ?? DefaultSubPatternVersion(subPattern!)));
        }

        // Extension controls carry no <Type>; their identity is the
        // <FormControlExtension><Name> below.
        if (axType.Length > 0) el.Add(new XElement(none + "Type", normalizedType));

        var props = properties ?? new Dictionary<string, string>();
        foreach (var (key, value) in props.Where(p => !TrailingProperties.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            el.Add(new XElement(none + key, value));

        // An extension control identifies itself through a populated
        // <FormControlExtension>; every other control nils it out.
        el.Add(axType.Length == 0 && IsExtensionControl(normalizedType)
            ? new XElement(none + "FormControlExtension",
                new XElement(none + "Name", normalizedType),
                new XElement(none + "ExtensionComponents"),
                new XElement(none + "ExtensionProperties"))
            : new XElement(none + "FormControlExtension", new XAttribute(Xsi + "nil", "true")));

        if (IsContainer(normalizedType)) el.Add(new XElement(none + "Controls"));

        foreach (var (key, value) in props.Where(p => TrailingProperties.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            el.Add(new XElement(none + key, value));

        return el;
    }

    /// <summary>
    /// Build a data-bound field control — the shape <c>generate form</c> emits for grid
    /// and FastTab fields.
    /// </summary>
    public static XElement CreateBoundField(string normalizedType, string name, string dataSource, string dataField)
        => Create(normalizedType, name, new Dictionary<string, string>
        {
            ["DataField"] = dataField,
            ["DataSource"] = dataSource,
        });

    /// <summary>
    /// Build the control a <see cref="NodeSpec"/> slot requires: first allowed control
    /// type, the spec's conventional name (falling back to the type), and the spec's
    /// expected properties. Used by <see cref="FormPatternRepairer"/> to satisfy FP003.
    /// </summary>
    public static XElement CreateForSpec(NodeSpec spec, string? nameOverride = null)
    {
        var type = spec.ControlTypes.FirstOrDefault(t => t != "*") ?? "Group";
        var name = nameOverride ?? spec.NameHint ?? spec.Id;

        string? subPattern = null;
        if (spec.RequiresSubPattern && spec.AllowedSubPatterns is { Count: 1 })
            subPattern = spec.AllowedSubPatterns[0];

        return Create(type, name, spec.Properties, subPattern);
    }

    /// <summary>Newest catalog version for a sub-pattern; falls back to 1.0 for unknown names.</summary>
    private static string DefaultSubPatternVersion(string subPattern) =>
        FormPatternCatalog.ResolveSubPattern(subPattern)?.Versions.FirstOrDefault() ?? "1.0";
}
