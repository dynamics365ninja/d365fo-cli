using System.Xml.Linq;
using D365FO.Core.ObjectTypes;
using D365FO.Core.Validation;

namespace D365FO.Core.Eval;

/// <summary>
/// Compiles ONE artefact — anything, not only an eval golden — with the real X++ compiler.
/// </summary>
/// <remarks>
/// <para>
/// The eval goldens compile clean, and they only cover what the catalog covers: every new
/// <c>generate</c> sub-command, form pattern and report flag has to meet the compiler somehow,
/// and until now that meant driving the golden provisioner by hand and remembering three rules
/// that cost an hour each when forgotten — the file name must equal the emitted <c>&lt;Name&gt;</c>,
/// the artefact must sit in the folder named after its ROOT ELEMENT, and a multi-file scaffold
/// leaves its companions beside the one you asked for.
/// </para>
/// <para>
/// This is that recipe as a command. It places each artefact where the compiler expects it,
/// which is what the manual version kept getting wrong.
/// </para>
/// </remarks>
public static class OracleProbe
{
    /// <param name="Source">Where the artefact came from.</param>
    /// <param name="Placed">Where it was put for the compiler.</param>
    /// <param name="AxFolder">The AOT folder its root element demands.</param>
    /// <param name="ObjectName">The name the document declares, which is also its file name.</param>
    public sealed record PlacedArtifact(string Source, string Placed, string AxFolder, string ObjectName);

    /// <param name="WorkDir">The throwaway metadata store that was built.</param>
    /// <param name="ModelName">Name of the throwaway model the artefacts were placed in.</param>
    /// <param name="ContentRoot">The model's content root — the folder the Ax* folders live under.</param>
    /// <param name="Placed">Artefacts copied into the store, ready to compile.</param>
    /// <param name="FixtureFiles">MiniAot fixture files laid down beside them, when the fixture was used.</param>
    /// <param name="Rejected">Files that are not a usable AOT document, with the reason.</param>
    public sealed record Preparation(
        string WorkDir,
        string ModelName,
        string ContentRoot,
        IReadOnlyList<PlacedArtifact> Placed,
        IReadOnlyList<string> FixtureFiles,
        IReadOnlyList<(string File, string Reason)> Rejected);

    /// <summary>
    /// Build a throwaway model around the given artefacts, ready for the compiler.
    /// </summary>
    /// <param name="artifacts">AOT XML files to compile.</param>
    /// <param name="workDir">Metadata store to build in — one directory per package.</param>
    /// <param name="modelName">Throwaway model name.</param>
    /// <param name="withFixture">
    /// Also lay down the MiniAot fixture, so an artefact that references the usual test symbols
    /// resolves. Off when the artefact is meant to stand alone.
    /// </param>
    /// <param name="packagesRoot">
    /// The installation to reference. Given one, the throwaway model references every package on
    /// it, because an artefact that EXTENDS a standard object drags in whatever that object
    /// references and that does not stay inside the usual three modules. Omitted, the model gets
    /// those three.
    /// </param>
    public static Preparation Prepare(
        IReadOnlyList<string> artifacts, string workDir, string modelName, bool withFixture = true,
        string? packagesRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var modelRoot = Path.Combine(workDir, modelName);
        var contentRoot = Path.Combine(modelRoot, modelName);
        Directory.CreateDirectory(contentRoot);

        L3ModelProvisioner.WriteDescriptorFor(
            modelRoot, modelName,
            moduleReferences: packagesRoot is null ? null : L3ModelProvisioner.ModulesIn(packagesRoot));

        var fixtureFiles = withFixture
            ? L3ModelProvisioner.ProvisionFixtureInto(workDir, contentRoot)
            : Array.Empty<string>();

        var placed = new List<PlacedArtifact>();
        var rejected = new List<(string, string)>();

        foreach (var source in artifacts ?? Array.Empty<string>())
        {
            if (!File.Exists(source)) { rejected.Add((source, "no such file")); continue; }

            XDocument doc;
            try { doc = XDocument.Load(source); }
            catch (Exception ex) { rejected.Add((source, $"not readable XML: {ex.Message}")); continue; }

            var root = doc.Root?.Name.LocalName;
            if (string.IsNullOrEmpty(root) || !root.StartsWith("Ax", StringComparison.Ordinal))
            {
                rejected.Add((source, $"root <{root ?? "?"}> is not an AOT element"));
                continue;
            }

            // The name the document declares — not the file name it happens to carry. A
            // scaffolder's class name often differs from the name it was asked for, and the
            // compiler resolves by the declared name.
            var declared = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
            if (string.IsNullOrEmpty(declared))
            {
                rejected.Add((source, "the document declares no <Name>, so the compiler cannot resolve it"));
                continue;
            }

            var axFolder = ObjectTypeRegistry.Find(root)?.AotSubfolder ?? root;
            var targetDir = Path.Combine(contentRoot, axFolder);
            Directory.CreateDirectory(targetDir);

            var target = Path.Combine(targetDir, declared + ".xml");
            File.Copy(source, target, overwrite: true);
            placed.Add(new PlacedArtifact(source, target, axFolder, declared));
        }

        return new Preparation(workDir, modelName, contentRoot, placed, fixtureFiles, rejected);
    }

    /// <param name="Artifact">The artefact the diagnostic was attributed to, or null when it names none.</param>
    /// <param name="Diagnostic">The compiler diagnostic itself.</param>
    public sealed record AttributedDiagnostic(string? Artifact, XppcDiagnostic Diagnostic);

    /// <summary>
    /// Attribute diagnostics to the artefacts they name, so a probe of several says which one
    /// failed rather than that something did.
    /// </summary>
    public static (IReadOnlyList<AttributedDiagnostic> Attributed, IReadOnlyList<XppcDiagnostic> Unattributed)
        Attribute(Preparation preparation, IReadOnlyList<XppcDiagnostic> diagnostics)
    {
        var byName = preparation.Placed.ToDictionary(p => p.ObjectName, StringComparer.OrdinalIgnoreCase);
        var attributed = new List<AttributedDiagnostic>();
        var unattributed = new List<XppcDiagnostic>();

        foreach (var d in diagnostics ?? Array.Empty<XppcDiagnostic>())
        {
            var owner = d.Object is { Length: > 0 } o && byName.ContainsKey(o) ? o : null;
            if (owner is null) unattributed.Add(d);
            else attributed.Add(new AttributedDiagnostic(owner, d));
        }

        return (attributed, unattributed);
    }
}
