using System.Xml.Linq;
using D365FO.Core.ObjectTypes;

namespace D365FO.Core.Eval;

/// <summary>One golden file placed into the throwaway model, and where it landed.</summary>
/// <param name="Name">
/// The object's declared AOT name — the key the compiler blames diagnostics on.
/// Carried explicitly rather than derived from the file name: an extension object is
/// named <c>Target.Suffix</c>, and taking the file's stem would silently truncate it
/// at the dot, so every diagnostic about it would land as unattributed.
/// </param>
public sealed record ProvisionedArtifact(string CaseId, string SourcePath, string RelativePath, string RootElement, string Name);

/// <summary>
/// A throwaway model on disk holding every golden the L3 oracle will compile.
/// <see cref="Skipped"/> is part of the result rather than a silent omission: a
/// case dropped without a word would make the oracle's "all clean" mean less than
/// it appears to.
/// </summary>
public sealed record ProvisionedModel(
    string ModelName,
    string ModelRoot,
    IReadOnlyList<ProvisionedArtifact> Artifacts,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Materialises reviewed goldens into a real model layout
/// (<c>&lt;root&gt;/&lt;Model&gt;/Descriptor/&lt;Model&gt;.xml</c> plus
/// <c>&lt;root&gt;/&lt;Model&gt;/&lt;Model&gt;/Ax&lt;Kind&gt;/&lt;Name&gt;.xml</c>) so a
/// compiler can be pointed at them — the provisioning half of the L3 build oracle
/// (plan item 4.2), and the half that needs no VM and is therefore tested offline.
/// </summary>
/// <remarks>
/// The folder for each artifact comes from <see cref="ObjectTypeRegistry"/>, so a
/// golden whose root element the registry does not know is skipped loudly instead
/// of being written where no compiler would look — the exact failure mode behind
/// audit finding G1, where an artifact sat for years in a folder that exists on no
/// AOS and every tool reported success.
/// </remarks>
public static class L3ModelProvisioner
{
    /// <summary>
    /// The three modules every custom model needs on the reference list. Referencing
    /// only <c>ApplicationSuite</c> was the obvious guess and it does not work: the
    /// compiler then reports the standard <c>Name</c> EDT and the <c>ModuleAxapta</c>
    /// enum as nonexistent, because they live in ApplicationFoundation and
    /// ApplicationPlatform.
    /// </summary>
    public static readonly IReadOnlyList<string> StandardModuleReferences =
        ["ApplicationPlatform", "ApplicationFoundation", "ApplicationSuite"];

    /// <summary>
    /// Every package in an installation, for goldens that extend a standard object.
    /// </summary>
    /// <remarks>
    /// Extending a shipped element pulls in what that element references, and it does not stay
    /// inside the three modules above: a view extension over <c>CustAccountName</c> needs
    /// <c>DirPartyTable</c>, which is in <c>Directory</c>, and a data-entity extension over
    /// <c>CustCustomerV3Entity</c> reaches a dozen packages further. Guessing the transitive set
    /// per case is a maintenance trap — the list here is read off the installation, and a
    /// package on the reference list that nothing uses costs the compiler nothing.
    /// </remarks>
    public static IReadOnlyList<string> ModulesIn(string packagesRoot)
    {
        if (string.IsNullOrWhiteSpace(packagesRoot) || !Directory.Exists(packagesRoot))
            return StandardModuleReferences;

        try
        {
            // A package is a directory with a bin — which is what the compiler resolves a module
            // reference to. The root also holds DataStack, GeneratedXppSource, InstallationRecords,
            // Plugins, StaticMetadata, the shared bin, and (on this machine) a directory whose name
            // is a path with its separators removed; referencing any of them earns a warning per
            // compile and resolves nothing.
            var all = Directory.EnumerateDirectories(packagesRoot)
                .Where(d => Directory.Exists(Path.Combine(d, "bin")))
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return all.Count == 0 ? StandardModuleReferences : all;
        }
        catch
        {
            return StandardModuleReferences;
        }
    }

    public static ProvisionedModel Provision(
        IReadOnlyList<EvalCase> cases,
        string goldensDir,
        string targetRoot,
        string modelName,
        string publisher = "d365fo-cli",
        string layer = "usr",
        IReadOnlyList<string>? moduleReferences = null)
    {
        moduleReferences ??= StandardModuleReferences;

        var modelRoot = Path.Combine(targetRoot, modelName);
        var contentRoot = Path.Combine(modelRoot, modelName);
        Directory.CreateDirectory(contentRoot);

        WriteDescriptor(modelRoot, modelName, publisher, layer, moduleReferences);

        var artifacts = new List<ProvisionedArtifact>();
        var skipped = new List<string>();

        foreach (var @case in cases.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (@case.GoldenPending)
            {
                skipped.Add($"{@case.Id}: golden_pending — no reviewed artifact to compile.");
                continue;
            }

            var caseDir = Path.Combine(goldensDir, @case.GoldenPath);
            if (!Directory.Exists(caseDir))
            {
                skipped.Add($"{@case.Id}: no golden directory at {caseDir}.");
                continue;
            }

            var files = Directory.GetFiles(caseDir, "*.xml").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count == 0)
            {
                skipped.Add($"{@case.Id}: golden directory contains no XML.");
                continue;
            }

            files.AddRange(Companions(caseDir));

            foreach (var file in files)
            {
                var (root, name, error) = ReadIdentity(file);
                if (error is not null)
                {
                    skipped.Add($"{@case.Id}: {error}");
                    continue;
                }

                var type = ObjectTypeRegistry.Find(root!);
                if (type is null || !type.ExistsInStandardAot)
                {
                    skipped.Add($"{@case.Id}: root element <{root}> maps to no AOT folder that exists on an AOS.");
                    continue;
                }

                var relative = Path.Combine(type.AotSubfolder, name + ".xml");
                var destination = Path.Combine(contentRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyWritable(file, destination);
                artifacts.Add(new ProvisionedArtifact(@case.Id, file, relative, root!, name!));
            }
        }

        return new ProvisionedModel(modelName, modelRoot, artifacts, skipped);
    }

    /// <summary>
    /// The folder inside a case's golden directory holding the artifacts the case's
    /// command also produces but the case does not score.
    /// </summary>
    public const string CompanionFolder = "_companions";

    /// <summary>
    /// The sibling artifacts a case's command emits alongside the one artifact the
    /// case scores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A case scores exactly one file — <c>EvalScorer</c> rejects a golden directory
    /// holding more, and rightly so: a case asserts one artifact. But a single
    /// generate command usually ships several. <c>generate custom-service</c> writes
    /// an <c>AxService</c> <em>and</em> the class it names; <c>generate
    /// business-event</c> writes the event class and its contract. Compiling the
    /// scored file alone made those references dangle, and the oracle reported five
    /// cases red for objects the tool does in fact generate — a false alarm that
    /// buried the real defects underneath it.
    /// </para>
    /// <para>
    /// Companions live in a <c>_companions</c> subfolder, invisible to the scorer
    /// (which enumerates the case directory non-recursively) and compiled here. They
    /// are captured from the same command run as the golden, so they stay honest:
    /// a companion that drifts from what the tool emits shows up as a build failure,
    /// exactly like the golden itself.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Companions(string caseDir)
    {
        var dir = Path.Combine(caseDir, CompanionFolder);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.xml").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
    }

    /// <summary>
    /// The object's name comes from the file's own <c>&lt;Name&gt;</c> element, not
    /// the golden's file name: the compiler resolves references by AOT name, and a
    /// file whose name disagrees with its content is a defect the oracle should
    /// surface rather than paper over by renaming.
    /// </summary>
    private static (string? Root, string? Name, string? Error) ReadIdentity(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return (null, null, $"{Path.GetFileName(path)} has no root element.");

            var name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
            if (string.IsNullOrEmpty(name))
                return (null, null, $"{Path.GetFileName(path)} (<{root.Name.LocalName}>) declares no <Name>.");

            return (root.Name.LocalName, name, null);
        }
        catch (System.Xml.XmlException ex)
        {
            return (null, null, $"{Path.GetFileName(path)} is not well-formed XML: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the checked-in mini-AOT fixture's objects into the throwaway model
    /// <em>itself</em>, so the tables the goldens bind to (<c>FmVehicle</c>,
    /// <c>FmVehicleLine</c>, …) exist during the compile. Returns the relative paths
    /// that were added.
    /// </summary>
    /// <remarks>
    /// Provisioning the fixture as its own module and referencing it does not work:
    /// the fixture module compiles clean, and the goldens compile still reports
    /// every view and entity field as referring to "a nonexistent table or view
    /// named 'FmVehicle'". Cross-module metadata visibility needs more than a
    /// <c>ModuleReferences</c> entry, and none of it is worth reproducing for a
    /// disposable model — one module has no visibility question to get wrong.
    /// </remarks>
    public static IReadOnlyList<string> ProvisionFixtureInto(string fixtureRoot, string contentRoot)
    {
        var added = new List<string>();
        if (!Directory.Exists(fixtureRoot)) return added;

        foreach (var modelDir in Directory.GetDirectories(fixtureRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var descriptor = Directory.Exists(Path.Combine(modelDir, "Descriptor"))
                ? Directory.GetFiles(Path.Combine(modelDir, "Descriptor"), "*.xml").FirstOrDefault()
                : null;
            if (descriptor is null) continue;

            var name = XDocument.Load(descriptor).Root?.Element("Name")?.Value?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var source = Path.Combine(modelDir, name);
            if (!Directory.Exists(source)) continue;

            foreach (var file in Directory.GetFiles(source, "*.xml", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(contentRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyWritable(file, destination);
                added.Add(relative);
            }
        }

        return added;
    }

    /// <summary>
    /// Copies a golden into the throwaway model with the read-only attribute cleared.
    /// The compiler rewrites a form's metadata header in place while validating it, and
    /// a read-only copy makes it report "Failed to write metadata back to the file …
    /// often due to the file being read-only" — a property of the copy, not of the
    /// golden, and one that would otherwise be scored against the case.
    /// </summary>
    private static void CopyWritable(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        var info = new FileInfo(destination);
        if (info.IsReadOnly) info.IsReadOnly = false;
    }

    /// <summary>Layer ordinal the descriptor contract serialises — <c>usr</c> is 14, not the string "usr".</summary>
    public const int UsrLayer = 14;

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Arrays = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

    /// <summary>
    /// Writes the descriptor in the shape the metadata provider's own
    /// <c>ModelDescriptorPointers</c> reads, ground-truthed against the descriptors
    /// shipped in <c>PackagesLocalDirectory</c>.
    /// </summary>
    /// <remarks>
    /// A four-element descriptor (name/publisher/layer/module references) is enough
    /// for this repo's <em>extractor</em> and was therefore the obvious thing to
    /// write — but the compiler dies on it before compiling anything:
    /// <c>ModelKey..ctor</c> throws <c>ArgumentNullException: moduleName</c> because
    /// <c>&lt;ModelModule&gt;</c> is absent. <c>Layer</c> is also an ordinal there,
    /// not the layer's name, and the string collections live in the DataContract
    /// arrays namespace. Members are emitted in contract (alphabetical) order for
    /// the same reason every other writer in this repo does.
    /// </remarks>
    /// <summary>
    /// Write the model descriptor for a throwaway model built around arbitrary artefacts.
    /// </summary>
    /// <remarks>
    /// Same descriptor the golden provisioner writes, exposed because the single-artefact probe
    /// builds its model the same way and a second descriptor writer would be a second place for
    /// the module reference list to go stale.
    /// </remarks>
    public static void WriteDescriptorFor(
        string modelRoot, string modelName, string publisher = "d365fo-cli", string layer = "usr",
        IReadOnlyList<string>? moduleReferences = null)
        => WriteDescriptor(modelRoot, modelName, publisher, layer, moduleReferences ?? StandardModuleReferences);

    private static void WriteDescriptor(
        string modelRoot, string modelName, string publisher, string layer, IReadOnlyList<string> moduleReferences)
    {
        var descriptorDir = Path.Combine(modelRoot, "Descriptor");
        Directory.CreateDirectory(descriptorDir);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("AxModelInfo",
                new XAttribute(XNamespace.Xmlns + "i", Xsi),
                new XElement("AppliedUpdates", new XAttribute(XNamespace.Xmlns + "d2p1", Arrays)),
                new XElement("Customization", "DoNotAllow"),
                new XElement("Description", $"Throwaway model holding d365fo-cli eval goldens for compilation."),
                new XElement("DisplayName", modelName),
                new XElement("Id", StableModelId(modelName)),
                new XElement("InternalsVisibleTo", new XAttribute(XNamespace.Xmlns + "d2p1", Arrays)),
                new XElement("Layer", LayerOrdinal(layer)),
                new XElement("Locked", "false"),
                new XElement("ModelModule", modelName),
                new XElement("ModelReferences",
                    new XAttribute(XNamespace.Xmlns + "d2p1", Arrays),
                    new XAttribute(Xsi + "nil", "true")),
                new XElement("ModuleReferences",
                    new XAttribute(XNamespace.Xmlns + "d2p1", Arrays),
                    moduleReferences.Select(m => new XElement(Arrays + "string", m))),
                new XElement("Name", modelName),
                new XElement("Publisher", publisher),
                new XElement("SolutionId", Guid.Empty.ToString())));

        doc.Save(Path.Combine(descriptorDir, modelName + ".xml"));
    }

    /// <summary>Only <c>usr</c> is ever provisioned; anything else is a caller mistake worth naming.</summary>
    private static int LayerOrdinal(string layer) => layer.Equals("usr", StringComparison.OrdinalIgnoreCase)
        ? UsrLayer
        : throw new ArgumentException($"Only the usr layer is provisioned for eval builds; got '{layer}'.", nameof(layer));

    /// <summary>
    /// Deterministic, so re-running the oracle does not present the same model under
    /// a new identity to anything that caches by model id. Kept well above the
    /// ranges shipped models occupy.
    /// </summary>
    private static int StableModelId(string modelName)
    {
        var hash = 17;
        foreach (var c in modelName) hash = unchecked(hash * 31 + c);
        return 900_000_000 + Math.Abs(hash % 1_000_000);
    }
}
