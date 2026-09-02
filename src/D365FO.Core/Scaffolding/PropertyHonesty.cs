using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>One thing the caller asked for that is not in the document that was written.</summary>
/// <param name="Option">The option the value came from, e.g. <c>--label</c>.</param>
/// <param name="Value">The value as the caller wrote it.</param>
/// <param name="Missing">The part of it that could not be found in the written document.</param>
public sealed record PropertyGap(string Option, string Value, string Missing)
{
    /// <summary>Sentence for a warnings list.</summary>
    public override string ToString()
        => $"{Option} {Value} — \"{Missing}\" is not in the generated object. The scaffolder either " +
           $"does not carry that option onto this AOT type, or the value was dropped on the way to disk.";
}

/// <summary>
/// Reconciles what a generate call requested against what actually reached the document.
/// </summary>
/// <remarks>
/// <para>
/// Issue #161 / R6, ported in spirit from the predecessor's
/// <c>createTablePropertyHonesty.ts</c>. The defect class it exists for is silent: an option is
/// accepted, the command reports success, and the property is simply not in the file — because
/// the scaffolder never carried it onto that AOT type, or because something between the
/// scaffolder and disk discarded it. Every other check in this repo judges the document on its
/// own terms and therefore cannot see this: a document missing a property the caller asked for
/// is a perfectly valid document.
/// </para>
/// <para>
/// The comparison is deliberately blunt — a case-insensitive search of the whole written
/// document for each requested value. It is a claim about presence, not about which member the
/// value should have landed in, because mapping option → AOT member per command is exactly the
/// hand-maintained table that drifts and would have to be right for the check to be worth
/// anything. Blunt makes it wrong in one direction only: it can miss a value that coincidentally
/// appears elsewhere, but it never invents a gap for a value that really is in the file.
/// </para>
/// </remarks>
public static class PropertyHonesty
{
    /// <summary>
    /// Values that carry a request but never survive as text: they select a shape rather than
    /// supply a value, so their absence from the document says nothing.
    /// </summary>
    private static readonly HashSet<string> Untraceable = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "none", "default", "auto",
    };

    private static readonly char[] CompositeSeparators = [':', ',', ';', '|', '=', '/'];

    /// <summary>
    /// Options whose value is written somewhere other than the AOT XML — the label text
    /// <c>generate label-file --entry Key=Text</c> puts into the <c>.label.txt</c> beside the
    /// manifest. The manifest cannot carry it, so its absence there is not a dropped value.
    /// </summary>
    private static readonly HashSet<string> ContentOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--entry",
    };

    /// <summary>
    /// Requested values that did not reach <paramref name="writtenXml"/>.
    /// </summary>
    /// <param name="requested">Option name → value, as supplied on the command line.</param>
    /// <param name="writtenXml">The document as it was written (or handed to the provider).</param>
    public static IReadOnlyList<PropertyGap> Reconcile(
        IEnumerable<(string Option, string Value)> requested, string writtenXml)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (string.IsNullOrWhiteSpace(writtenXml)) return Array.Empty<PropertyGap>();

        var haystack = Haystack(writtenXml);
        var gaps = new List<PropertyGap>();

        foreach (var (option, value) in requested)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (ContentOptions.Contains(option)) continue;
            if (LooksLikeAPath(value)) continue;

            foreach (var part in Parts(value))
            {
                if (haystack.Contains(part, StringComparison.OrdinalIgnoreCase)) continue;
                gaps.Add(new PropertyGap(option, value, part));
            }
        }

        return gaps;
    }

    /// <summary>
    /// Everything the document says, as one string: element names, element text and attribute
    /// values.
    /// </summary>
    /// <remarks>
    /// Names matter as much as values. <c>--field Plate:Name:mandatory</c> asks for three things
    /// and only two of them are text — "mandatory" arrives as the <c>&lt;Mandatory&gt;</c>
    /// element, whose value is "Yes". Searching the raw XML rather than the parsed values covers
    /// both without having to know which is which. It falls back to the raw string when the
    /// document does not parse, because a caller holding unparseable XML has a bigger problem
    /// than this and should not also get a wall of spurious gaps.
    /// </remarks>
    private static string Haystack(string writtenXml)
    {
        try
        {
            var doc = XDocument.Parse(writtenXml);
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException)
        {
            return writtenXml;
        }
    }

    /// <summary>
    /// The traceable pieces of one option value.
    /// </summary>
    /// <remarks>
    /// Repeatable options carry composite specs — <c>--field AccountNum:CustAccount:mandatory</c>,
    /// <c>--relation Vehicle=VehicleId</c>, <c>--constrained Header/Line</c> — and each piece is a
    /// separate request that can be separately dropped. Splitting them is what turns "the field
    /// arrived" into "the field arrived but its EDT did not", which is the interesting half; not
    /// splitting them is what made <c>--constrained Header/Line</c> report a gap for a policy
    /// that had both tables, because no single element holds the path as written.
    /// </remarks>
    /// <summary>
    /// Whether a requested value names a file rather than a property of the generated object.
    /// </summary>
    /// <remarks>
    /// Not every option a command declares describes the object it produces. <c>--from</c> names
    /// a reference form to clone, <c>--add-to</c> and <c>--into-role</c> name documents to merge
    /// into, and the <c>--out-*</c> family names where companion artefacts go. None of those
    /// values can appear inside the AOT XML, so reconciling them reports one gap per path
    /// segment — <c>AosService</c>, <c>PackagesLocalDirectory</c>, <c>CustGroup.xml</c> — and
    /// buries the findings that mean something.
    /// <para>
    /// Rooted-or-has-an-extension rather than "contains a separator", because <c>/</c> is a
    /// meaningful separator in real option values: <c>--constrained Header/Line</c> nests a
    /// policy's constrained-table tree and every segment of it genuinely has to reach the
    /// document.
    /// </para>
    /// </remarks>
    private static bool LooksLikeAPath(string value)
    {
        var trimmed = value.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed)) return true;
        }
        catch (ArgumentException) { return false; }

        return Path.GetExtension(trimmed).Length > 1
               && (trimmed.Contains('/') || trimmed.Contains('\\'));
    }

    private static IEnumerable<string> Parts(string value)
    {
        foreach (var raw in value.Split(CompositeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A label key is written as-is; anything shorter than two characters matches too
            // much to mean anything.
            if (raw.Length < 2) continue;
            if (Untraceable.Contains(raw)) continue;
            yield return raw;
        }
    }
}
