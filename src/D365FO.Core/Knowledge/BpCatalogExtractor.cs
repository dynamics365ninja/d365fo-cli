// <copyright file="BpCatalogExtractor.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Xml.Linq;

namespace D365FO.Core.Knowledge;

/// <summary>What one extraction run found, so the run can report its own figures.</summary>
/// <param name="RuleSetFiles">How many <c>AxRuleSet/*.xml</c> were read.</param>
/// <param name="CanonicalNames">Distinct monikers those rule sets declare.</param>
/// <param name="ResourceAssemblies">BP rule assemblies scanned for message text.</param>
/// <param name="WithMessage">Entries that ended up with a real message.</param>
/// <param name="NameOnly">Canonical entries the install ships no message for.</param>
/// <param name="NonCanonical">Resource strings that no rule set lists — not BP rules.</param>
public sealed record BpExtractionReport(
    int RuleSetFiles,
    int CanonicalNames,
    int ResourceAssemblies,
    int WithMessage,
    int NameOnly,
    int NonCanonical);

/// <summary>
/// Builds the BP moniker catalog from a real <c>PackagesLocalDirectory</c>.
/// </summary>
/// <remarks>
/// <para>Two independent sources, and they answer different questions.</para>
/// <para>
/// <b>Canonical names</b> come from every model's <c>&lt;Model&gt;/&lt;Model&gt;/AxRuleSet/*.xml</c>,
/// a flat list of the monikers that model's rules can raise. The union is the full name set, and
/// it is the only thing that answers "is this a real BP rule".
/// </para>
/// <para>
/// <b>Message text</b> comes from the resx-backed resources embedded in
/// <c>bin/BPExtensions/*.dll</c>, keyed by the moniker itself. Coverage is high but not total, and
/// the extraction also yields strings that are NOT rules — messages belonging to the upgrade and
/// form-conversion tooling. Those are kept with <c>Canonical = false</c> rather than dropped,
/// because a searchable message is useful even when the string is not a rule; what matters is that
/// the distinction is recorded rather than guessed at read time.
/// </para>
/// <para>
/// The resources are read through <see cref="PEReader"/> rather than by loading the assemblies.
/// Loading a D365FO rule assembly drags in its dependency graph and executes its module
/// initialisers, which is a lot of risk for a string table.
/// </para>
/// </remarks>
public static class BpCatalogExtractor
{
    public static (BpMonikerSnapshot Snapshot, BpExtractionReport Report) Extract(string packagesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesPath);
        if (!Directory.Exists(packagesPath))
            throw new DirectoryNotFoundException($"Packages path not found: {packagesPath}");

        var (canonical, ruleSetFiles) = ReadCanonicalNames(packagesPath);
        var (messages, assemblies) = ReadRuleMessages(packagesPath);

        var names = new SortedSet<string>(canonical, StringComparer.Ordinal);
        foreach (var key in messages.Keys) names.Add(key);

        var monikers = new List<BpMoniker>(names.Count);
        foreach (var name in names)
        {
            messages.TryGetValue(name, out var message);
            messages.TryGetValue(name + "Description", out var description);
            monikers.Add(new BpMoniker(name, canonical.Contains(name), message, description));
        }

        // A "<Moniker>Description" entry is documentation for its rule, not a rule of its own.
        monikers.RemoveAll(m => !m.Canonical
                                && m.Name.EndsWith("Description", StringComparison.Ordinal)
                                && names.Contains(m.Name[..^"Description".Length]));

        var report = new BpExtractionReport(
            RuleSetFiles: ruleSetFiles,
            CanonicalNames: canonical.Count,
            ResourceAssemblies: assemblies,
            WithMessage: monikers.Count(m => m.Message is not null),
            NameOnly: monikers.Count(m => m.Canonical && m.Message is null),
            NonCanonical: monikers.Count(m => !m.Canonical));

        return (new BpMonikerSnapshot(DateTimeOffset.UtcNow, packagesPath, monikers), report);
    }

    private static (HashSet<string> Names, int Files) ReadCanonicalNames(string packagesPath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var files = 0;

        foreach (var ruleSet in Directory.EnumerateFiles(packagesPath, "*.xml", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
        }).Where(p => p.Contains($"{Path.DirectorySeparatorChar}AxRuleSet{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            files++;
            try
            {
                var doc = XDocument.Load(ruleSet);
                foreach (var moniker in doc.Descendants().Where(e => e.Name.LocalName == "Moniker"))
                {
                    var value = moniker.Value.Trim();
                    if (value.Length > 0) names.Add(value);
                }
            }
            catch
            {
                // One unreadable rule set must not cost the other 143 their names.
            }
        }

        return (names, files);
    }

    private static (Dictionary<string, string> Messages, int Assemblies) ReadRuleMessages(string packagesPath)
    {
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        var assemblies = 0;

        var binDir = Path.Combine(packagesPath, "bin", "BPExtensions");
        if (!Directory.Exists(binDir)) return (messages, 0);

        foreach (var dll in Directory.EnumerateFiles(binDir, "*.dll"))
        {
            assemblies++;
            try
            {
                foreach (var (key, value) in ReadEmbeddedStrings(dll))
                    messages[key] = value;
            }
            catch
            {
                // A rule assembly with no managed resources, or one this runtime cannot map, is
                // a gap in message text — never a reason to lose the canonical names.
            }
        }

        return (messages, assemblies);
    }

    /// <summary>Every string in every embedded <c>.resources</c> stream of one assembly.</summary>
    private static IEnumerable<(string Key, string Value)> ReadEmbeddedStrings(string assemblyPath)
    {
        using var file = File.OpenRead(assemblyPath);
        using var pe = new PEReader(file);
        if (!pe.HasMetadata) yield break;

        var metadata = pe.GetMetadataReader();
        var corHeader = pe.PEHeaders.CorHeader;
        if (corHeader is null) yield break;

        var resourcesRva = corHeader.ResourcesDirectory.RelativeVirtualAddress;
        if (resourcesRva == 0) yield break;

        var sectionIndex = pe.PEHeaders.GetContainingSectionIndex(resourcesRva);
        if (sectionIndex < 0) yield break;

        var section = pe.PEHeaders.SectionHeaders[sectionIndex];
        var image = pe.GetEntireImage().GetContent().ToArray();

        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);

            // A resource with an Implementation lives in another file; only embedded ones are here.
            if (!resource.Implementation.IsNil) continue;
            if (!metadata.GetString(resource.Name).EndsWith(".resources", StringComparison.Ordinal)) continue;

            var start = section.PointerToRawData + (resourcesRva - section.VirtualAddress) + (int)resource.Offset;
            if (start < 0 || start + 4 > image.Length) continue;

            var length = BitConverter.ToInt32(image, start);
            if (length <= 0 || start + 4 + length > image.Length) continue;

            List<(string, string)> collected;
            try
            {
                collected = new List<(string, string)>();
                using var stream = new MemoryStream(image, start + 4, length, writable: false);
                using var reader = new ResourceReader(stream);
                foreach (System.Collections.DictionaryEntry entry in reader)
                {
                    if (entry.Key is string key && entry.Value is string value && value.Length > 0)
                        collected.Add((key, value));
                }
            }
            catch
            {
                // Not a v1 .resources stream, or a type this runtime will not deserialise.
                continue;
            }

            foreach (var pair in collected) yield return pair;
        }
    }
}
