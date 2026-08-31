using System.Xml.Linq;
using D365FO.Core.Eval;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// The CDATA gate over the whole generate surface.
///
/// Every AxClass/AxTable/AxForm file the platform ships wraps its X++ payload —
/// <c>&lt;Declaration&gt;</c> and each method's <c>&lt;Source&gt;</c> — in CDATA, so the doc
/// comments inside can use literal <c>&lt;summary&gt;</c>/<c>&lt;param&gt;</c> brackets. A
/// scaffolder that emits the same text as a plain text node produces a file that parses,
/// round-trips and compares identically, but forces the next hand-edit to XML-escape those
/// brackets — and D365FO does not decode the entities back, so the literal
/// <c>&amp;lt;summary&amp;gt;</c> reaches Visual Studio and the compiled X++.
///
/// The eval scorer cannot see this: <see cref="XElement.Value"/> is identical for a CDATA
/// node and a text node, so <c>eval run</c> reports goldenMatch either way. That blind spot
/// is exactly how the gap survived — this test closes it by asserting the node type across
/// every captured golden, which is real tool output for every case in the catalog.
/// </summary>
public class GoldenCDataTests
{
    private static readonly string RepoRoot =
        EvalPaths.FindRepoRoot() ?? throw new InvalidOperationException("Could not locate repo root for tests.");

    [Fact]
    public void Every_golden_wraps_its_X_plus_plus_payload_in_CDATA()
    {
        var goldensDir = EvalPaths.GoldensDir(RepoRoot);
        Assert.True(Directory.Exists(goldensDir), $"Goldens directory not found: {goldensDir}");

        var files = Directory.GetFiles(goldensDir, "*.xml", SearchOption.AllDirectories);
        Assert.True(files.Length > 0, "no golden files found — the gate would pass vacuously");

        var offenders = new List<string>();
        var checkedNodes = 0;

        foreach (var file in files)
        {
            var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);

            foreach (var el in doc.Descendants()
                         .Where(e => e.Name.LocalName is "Declaration" or "Source"))
            {
                // An element the scaffolder deliberately left empty carries no X++ to protect.
                if (string.IsNullOrWhiteSpace(el.Value)) continue;

                checkedNodes++;
                if (el.Nodes().Count() == 1 && el.FirstNode is XCData) continue;

                offenders.Add(
                    $"  {Path.GetRelativePath(RepoRoot, file)} · <{el.Name.LocalName}> is a " +
                    $"{el.FirstNode?.NodeType.ToString() ?? "empty"} node, not CDATA");
            }
        }

        Assert.True(checkedNodes > 0, "no <Declaration>/<Source> payloads found — the gate would pass vacuously");
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} golden payload(s) are not CDATA-wrapped — the scaffolder that produced " +
            "them emits a plain text node:\n" + string.Join("\n", offenders));
    }
}
