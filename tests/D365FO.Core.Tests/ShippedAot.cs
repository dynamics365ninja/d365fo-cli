using System.Xml.Linq;

namespace D365FO.Core.Tests;

/// <summary>
/// Which packages in a <c>PackagesLocalDirectory</c> count as "what Microsoft ships" for the
/// census tests.
/// </summary>
/// <remarks>
/// A developer VM's package root also holds what its developers wrote: custom models, and the
/// working models of other tools. On 2 September 2026 the sibling MCP server's own model on
/// this host carried a report whose precision design declared a <c>&lt;Caption&gt;</c> the
/// contract does not have — a defect in that tool, faithfully reported by the census as "a rule
/// is wrong about a file Microsoft ships". It was not one. The census asks what Microsoft
/// writes, so it samples only packages whose descriptor names Microsoft as publisher; a custom
/// model's files are judged by <c>d365fo validate</c>, where a finding is a finding.
/// </remarks>
internal static class ShippedAot
{
    public static bool IsMicrosoftPackage(string packageDir)
    {
        var descriptorDir = Path.Combine(packageDir, "Descriptor");
        if (!Directory.Exists(descriptorDir)) return false;
        try
        {
            foreach (var descriptor in Directory.EnumerateFiles(descriptorDir, "*.xml"))
            {
                var publisher = XDocument.Load(descriptor).Root?.Element("Publisher")?.Value?.Trim();
                if (string.Equals(publisher, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
        return false;
    }
}
