using System.Xml;
using System.Xml.Linq;
using D365FO.Core.Metadata;
using D365FO.Core.ObjectTypes;

namespace D365FO.Core.Validation;

/// <summary>
/// Per-family structural rules for an AOT document's <em>root</em>: is this a real AOT type,
/// is the polymorphism pinned, is the schema-instance namespace declared, is the document in
/// the namespace its contract declares, and does the file sit in the folder its family owns.
/// </summary>
/// <remarks>
/// <para>
/// Issue #163 (audit plan §1.4 / finding G9). <see cref="ContractShapeRules"/> already speaks
/// for every family about what is <em>inside</em> a document — XML007 for a member the type
/// does not declare, XML008 for a value outside its enum. Nothing spoke about the root itself
/// except the write path, which threw from inside <c>ScaffoldFileWriter</c>. That made the
/// knowledge unavailable to anything that is not writing a file: an eval golden, a CI sweep
/// over a directory, an agent asking "would this be accepted?" before it commits.
/// </para>
/// <para>
/// These four rules are the offline approximation of what the bridge's
/// <c>Handlers.WriteArtifact</c> rejects before it ever reaches the provider —
/// <c>TYPE_NOT_FOUND</c> (XML009), <c>ABSTRACT_TYPE</c> (XML010), and the two
/// <c>XML_DESERIALIZE_FAILED</c> shapes that a missing <c>xmlns:i</c> (XML011) or the wrong
/// contract namespace (XML012) produce. They are driven by
/// <see cref="ObjectTypeRegistry"/> and the <see cref="MetadataContracts"/> catalog, so they
/// cover every family the AOT has rather than the AxTable-only set XML001–XML005 covered.
/// </para>
/// <para>
/// <b>What is deliberately not here:</b> a member-order lint. Order genuinely matters — the
/// writer canonicalises it — but shipped Microsoft files deviate from contract order in places
/// and the provider still reads them back without loss, so flagging deviation in other
/// people's files would be asserting a defect the evidence does not support. The reasoning is
/// recorded in full on <see cref="ContractShapeRules"/>.
/// </para>
/// </remarks>
public static class ObjectShapeRules
{
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Root element that names no AOT type — the provider cannot construct anything.</summary>
    public const string RuleUnknownRoot = "XML009";

    /// <summary>Abstract root with no concrete <c>i:type</c> pinned.</summary>
    public const string RuleAbstractRoot = "XML010";

    /// <summary>The XMLSchema-instance namespace is used, or required, but not declared on the root.</summary>
    public const string RuleXsiNamespace = "XML011";

    /// <summary>The document is not in the XML namespace its contract declares.</summary>
    public const string RuleContractNamespace = "XML012";

    /// <summary>The file sits in an AOT folder that belongs to a different family.</summary>
    public const string RuleFolderMismatch = "XML013";

    /// <summary>
    /// Appends any root-shape violations found in <paramref name="xml"/>.
    /// </summary>
    /// <param name="xml">The document as it would be written.</param>
    /// <param name="violations">Collector.</param>
    /// <param name="sourcePath">
    /// Where the document lives, when that is known. Only used by <see cref="RuleFolderMismatch"/>,
    /// which has nothing to say about a document that came from stdin.
    /// </param>
    public static void Check(string xml, List<XppViolation> violations, string? sourcePath = null)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return; // Not well-formed — the parser and the other rules report that.
        }

        if (doc.Root is not null) Check(doc.Root, violations, sourcePath);
    }

    /// <summary>Root-shape rules against an already-parsed document.</summary>
    public static void Check(XElement root, List<XppViolation> violations, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(violations);

        var name = root.Name.LocalName;

        // Only AOT documents are in scope. A form-pattern fragment, a .rnrproj, an arbitrary
        // XML file the user pointed at — none of those name an Ax* type, and guessing at them
        // would make the rule useless noise.
        if (!name.StartsWith("Ax", StringComparison.Ordinal)) return;

        var line = (root as IXmlLineInfo)?.LineNumber;
        var contract = MetadataContracts.Find(name);
        var registered = ObjectTypeRegistry.Find(name);

        if (!CheckRootIsReal(name, contract, registered, line, violations)) return;

        CheckAbstractRoot(root, name, contract, registered, line, violations);
        CheckXsiDeclaration(root, name, registered, line, violations);
        CheckContractNamespace(root, name, contract, registered, line, violations);
        CheckFolder(name, registered, sourcePath, line, violations);
    }

    /// <summary>
    /// The root has to name a type the metadata assemblies actually define.
    /// </summary>
    /// <remarks>
    /// Two authorities, because they answer different questions. The contract catalog knows all
    /// 565 MetaModel types including the ones that are never a file's root
    /// (<c>AxTableField</c>) and the concrete subtypes a registry family collapses
    /// (<c>AxEdtString</c>). The registry knows which of those are top-level AOT families, and
    /// carries <c>ExistsInStandardAot</c> — the flag that exists because
    /// <c>AxWorkflowType</c> was read for years and matches no folder on any AOS (finding G1).
    /// A name unknown to both is the failure the bridge reports as <c>TYPE_NOT_FOUND</c>.
    /// </remarks>
    private static bool CheckRootIsReal(
        string name, MetadataContract? contract, ObjectTypeInfo? registered, int? line, List<XppViolation> violations)
    {
        if (contract is null && registered is null)
        {
            violations.Add(new XppViolation(
                RuleUnknownRoot, "error", line, $"<{name}>",
                $"No AOT type is named {name}. The metadata provider cannot construct a root it does " +
                $"not know, so the file is unreadable end to end. {NearestRoots(name)}"));
            return false;
        }

        if (registered is not null && !registered.ExistsInStandardAot)
        {
            violations.Add(new XppViolation(
                RuleUnknownRoot, "error", line, $"<{name}>",
                $"{name} is a plausible-looking name that matches no folder on any shipped D365FO " +
                $"installation, so an object written as one is invisible to the index and to Visual " +
                $"Studio. Use the real root for this family instead."));
            return false;
        }

        return true;
    }

    /// <summary>
    /// An abstract root is only writable when the concrete subtype is pinned via <c>i:type</c> —
    /// the escape hatch <c>Handlers.WriteArtifact</c> honours for
    /// <c>&lt;AxEdt i:type="AxEdtString"&gt;</c>.
    /// </summary>
    private static void CheckAbstractRoot(
        XElement root, string name, MetadataContract? contract, ObjectTypeInfo? registered,
        int? line, List<XppViolation> violations)
    {
        var isAbstract = contract?.IsAbstract == true || registered?.AbstractRoot == true;
        var pinned = root.Attribute(XName.Get("type", XsiNamespace))?.Value;

        if (isAbstract && string.IsNullOrWhiteSpace(pinned))
        {
            violations.Add(new XppViolation(
                RuleAbstractRoot, "error", line, $"<{name}>",
                $"{name} is an abstract MetaModel type. Opening the file throws \"Cannot create an " +
                $"abstract class\" unless a concrete subtype is pinned. Either write the concrete root " +
                $"directly or add i:type — {ConcreteSubtypes(name)}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(pinned)) return;

        if (MetadataContracts.Find(pinned) is null)
        {
            violations.Add(new XppViolation(
                RuleAbstractRoot, "error", line, $"<{name} i:type=\"{pinned}\">",
                $"No AOT type is named {pinned}, so the discriminator resolves to nothing and the " +
                $"read fails. {ConcreteSubtypes(name)}"));
            return;
        }

        // A discriminator that names a real type from a different branch is worse than an
        // unknown one: it deserializes into the wrong shape rather than failing.
        var subtypes = MetadataContracts.DerivedFrom(name);
        if (subtypes.Count > 0 && !subtypes.Any(s => string.Equals(s.Name, pinned, StringComparison.Ordinal))
            && !string.Equals(pinned, name, StringComparison.Ordinal))
        {
            violations.Add(new XppViolation(
                RuleAbstractRoot, "error", line, $"<{name} i:type=\"{pinned}\">",
                $"{pinned} does not derive from {name}, so the discriminator cannot select it. " +
                $"{ConcreteSubtypes(name)}"));
        }
    }

    /// <summary>
    /// Roots that carry <c>i:type</c> anywhere, and the families the registry marks as
    /// requiring it, need the XMLSchema-instance namespace declared on the root element.
    /// </summary>
    /// <remarks>
    /// Two reasons, and both were shipped defects. A discriminator with no namespace in scope
    /// is not a discriminator at all — it is an unknown attribute, and the polymorphic member it
    /// was pinning silently reads as its base type (issue #91, every <c>AxTableField</c>). And
    /// for <c>AxEnum</c> Visual Studio's reader refuses the file outright when the declaration
    /// is absent even though nothing in it uses the prefix (issue #70), which is why the policy
    /// is a per-family registry flag and not something derived from the document alone.
    /// </remarks>
    private static void CheckXsiDeclaration(
        XElement root, string name, ObjectTypeInfo? registered, int? line, List<XppViolation> violations)
    {
        var usesXsi = root.DescendantsAndSelf()
            .Any(e => e.Attributes().Any(a => a.Name.NamespaceName == XsiNamespace));
        var requiredByFamily = registered?.RequiresXsiNamespace == true
                               || name.StartsWith("AxEdt", StringComparison.Ordinal);
        if (!usesXsi && !requiredByFamily) return;

        // Any prefix bound to the URI counts; Visual Studio always writes "i", but the prefix
        // carries no meaning of its own.
        var declared = root.Attributes()
            .Any(a => a.IsNamespaceDeclaration && string.Equals(a.Value, XsiNamespace, StringComparison.Ordinal));
        if (declared) return;

        violations.Add(new XppViolation(
            RuleXsiNamespace, "error", line, $"<{name}>",
            $"Declare xmlns:i=\"{XsiNamespace}\" on the root. " +
            (usesXsi
                ? "Without it the i:type discriminators in this document are just unknown attributes, " +
                  "and every polymorphic member reads back as its base type with its extra properties gone."
                : $"Visual Studio's metadata reader refuses to open an {name} file whose root does not " +
                  "declare it, whether or not the document uses the prefix.")));
    }

    /// <summary>
    /// The document has to be in the XML namespace its DataContract declares.
    /// </summary>
    /// <remarks>
    /// Finding G3: menu items and tiles contract into <c>…Metadata.V1</c>, reports and workflow
    /// objects into <c>V2</c>, forms into <c>V6</c>, and everything else into the empty
    /// namespace. Getting it wrong is not cosmetic — the reader fails with "Expecting element X
    /// from namespace Y" before it looks at a single property, which is how menu items, reports
    /// and workflow objects shipped unreadable until the namespaces were ground-truthed.
    /// </remarks>
    private static void CheckContractNamespace(
        XElement root, string name, MetadataContract? contract, ObjectTypeInfo? registered,
        int? line, List<XppViolation> violations)
    {
        var expected = contract?.Namespace ?? registered?.ContractNamespace ?? string.Empty;
        var actual = root.Name.NamespaceName;
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;

        violations.Add(new XppViolation(
            RuleContractNamespace, "error", line, $"<{name} xmlns=\"{actual}\">",
            expected.Length == 0
                ? $"{name} contracts into the empty namespace, so a default namespace on the root makes " +
                  $"the reader fail with \"Expecting element {name} from namespace ''\". Drop the xmlns."
                : $"{name} contracts into \"{expected}\". The reader fails with \"Expecting element {name} " +
                  $"from namespace {expected}\" before it reads a single property. Set xmlns=\"{expected}\" " +
                  $"on the root."));
    }

    /// <summary>
    /// A family owns exactly one AOT folder, and the folder is how the extractor, Visual Studio
    /// and the build tools decide what a file is.
    /// </summary>
    /// <remarks>
    /// Path-aware, so it only speaks when the caller knows where the document lives. The parent
    /// folder has to be an AOT folder some family owns before this says anything — a scaffold
    /// parked at an arbitrary <c>--out</c> path is not in the AOT at all and is not a defect.
    /// </remarks>
    private static void CheckFolder(
        string name, ObjectTypeInfo? registered, string? sourcePath, int? line, List<XppViolation> violations)
    {
        if (registered is null || string.IsNullOrWhiteSpace(sourcePath)) return;

        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(sourcePath)));
        if (string.IsNullOrEmpty(folder)) return;

        var owner = ObjectTypeRegistry.Find(folder);
        if (owner is null || !string.Equals(owner.AotSubfolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(owner.AotSubfolder, registered.AotSubfolder, StringComparison.OrdinalIgnoreCase)) return;

        violations.Add(new XppViolation(
            RuleFolderMismatch, "error", line, $"{folder}/{Path.GetFileName(sourcePath)}",
            $"A <{name}> document is sitting in the {folder} folder, which belongs to " +
            $"{owner.RootElement}. The extractor and the build tools read a file's type from its " +
            $"folder, so this object is indexed as the wrong kind — or not at all. Move it to " +
            $"{registered.AotSubfolder}."));
    }

    private static string ConcreteSubtypes(string abstractName)
    {
        var concrete = MetadataContracts.DerivedFrom(abstractName)
            .Where(c => !c.IsAbstract)
            .Select(c => c.Name)
            .Take(8)
            .ToList();

        return concrete.Count == 0
            ? "the metadata catalog lists no concrete subtype for it."
            : $"concrete subtypes: {string.Join(", ", concrete)}.";
    }

    private static string NearestRoots(string written)
    {
        var prefix = written[..Math.Min(5, written.Length)];
        var near = MetadataContracts.All.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToList();

        return near.Count > 0
            ? $"Did you mean one of: {string.Join(", ", near)}?"
            : "Run `d365fo get object-types` for the AOT families this tool knows.";
    }
}
