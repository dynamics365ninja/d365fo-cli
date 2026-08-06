using System.Xml;
using System.Xml.Linq;

namespace D365FO.Core.Eval;

/// <summary>One test case from a SysTest run.</summary>
/// <param name="Name">Fully qualified test-case name as the runner reports it.</param>
/// <param name="Passed">The run finished and did not fail.</param>
/// <param name="Skipped">The runner skipped it (an unmet dependency attribute, usually).</param>
/// <param name="Pending">
/// The case was registered and never executed — the run died, or was cut short. Distinct from
/// skipped, and emphatically not a pass.
/// </param>
/// <param name="TimeMs">Reported duration, when the runner recorded one.</param>
/// <param name="FailureMessage">The failure text, when it failed.</param>
public sealed record SysTestCaseResult(
    string Name,
    bool Passed,
    bool Skipped,
    bool Pending,
    int? TimeMs,
    string? FailureMessage);

/// <summary>The outcome of one SysTest run.</summary>
public sealed record SysTestRunResults(
    IReadOnlyList<SysTestCaseResult> Cases,
    int Passed,
    int Failed,
    int Skipped,
    int Pending)
{
    /// <summary>
    /// Every case that was supposed to run did, and none failed.
    /// </summary>
    /// <remarks>
    /// A run with no cases is not clean — it is a run that tested nothing, and reporting that as
    /// a pass is how a broken harness looks healthy. Pending cases are not clean either: the
    /// runner registered them and never got to them.
    /// </remarks>
    public bool Clean => Cases.Count > 0 && Failed == 0 && Pending == 0;

    /// <summary>Cases that failed, for a caller that wants to name them.</summary>
    public IEnumerable<SysTestCaseResult> Failures => Cases.Where(c => !c.Passed && !c.Skipped && !c.Pending);
}

/// <summary>
/// Reads the XML document <c>SysTestConsole.exe /xml:&lt;file&gt;</c> writes.
/// </summary>
/// <remarks>
/// <para>
/// Issue #160 — the L4 oracle's missing half. <c>d365fo test run</c> returned a tail of console
/// text, which is a log, not a result: nothing could say whether a generated object actually
/// runs.
/// </para>
/// <para>
/// <b>The schema is ground truth, not a guess.</b> It is not discoverable from the runner
/// binary — not in its strings, its XML doc, or an embedded stylesheet — but the writer is X++
/// and ships as source on every installation:
/// <c>ApplicationFoundation\AxClass\SysTestListenerXML.xml</c>. Its element names are
/// <c>#define</c>s at the top of the class and its attributes are literal
/// <c>setAttribute</c> calls:
/// </para>
/// <code>
/// #define.TestResults('test-results')   #define.TestSuite('test-suite')
/// #define.Results('results')            #define.TestCase('test-case')
/// #define.Failure('failure')            #define.Message('message')
/// #define.execution('execution')        #define.executionPending('pending')
/// </code>
/// <para>
/// The one trap is the <c>success</c> attribute, which does not mean what the method producing
/// it is called. <c>SysTestListenerXML.isFailure()</c> returns <c>'false'</c> when the status is
/// <c>Failed</c> and <c>'true'</c> otherwise — so <c>success</c> is a plain pass flag, and
/// reading it as "is this a failure?" inverts every verdict in the file.
/// </para>
/// </remarks>
public static class SysTestResults
{
    private const string RootElement = "test-results";
    private const string CaseElement = "test-case";
    private const string PendingExecution = "pending";

    /// <summary>
    /// Parse a result document. Returns null when <paramref name="xml"/> is not one — a caller
    /// holding the wrong file must not receive an empty-but-successful run.
    /// </summary>
    public static SysTestRunResults? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (doc.Root is null || doc.Root.Name.LocalName != RootElement) return null;

        var cases = doc.Root
            .Descendants()
            .Where(e => e.Name.LocalName == CaseElement)
            .Select(ReadCase)
            .ToList();

        return new SysTestRunResults(
            cases,
            Passed: cases.Count(c => c.Passed),
            Failed: cases.Count(c => !c.Passed && !c.Skipped && !c.Pending),
            Skipped: cases.Count(c => c.Skipped),
            Pending: cases.Count(c => c.Pending));
    }

    /// <summary>Parse the document at <paramref name="path"/>, or null when it cannot be read.</summary>
    public static SysTestRunResults? TryParseFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return Parse(File.ReadAllText(path)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static SysTestCaseResult ReadCase(XElement el)
    {
        var pending = string.Equals(Attr(el, "execution"), PendingExecution, StringComparison.OrdinalIgnoreCase);
        var skipped = IsTrue(Attr(el, "skipped"));

        // `success` is a pass flag despite the name of the method that writes it.
        var success = IsTrue(Attr(el, "success"));

        var failure = el.Elements().FirstOrDefault(e => e.Name.LocalName == "failure");
        var message = failure?.Elements().FirstOrDefault(e => e.Name.LocalName == "message")?.Value.Trim()
                      ?? failure?.Value.Trim();

        return new SysTestCaseResult(
            Name: Attr(el, "name") ?? "(unnamed)",
            Passed: success && !skipped && !pending,
            Skipped: skipped,
            Pending: pending,
            TimeMs: int.TryParse(Attr(el, "time"), out var t) ? t : null,
            FailureMessage: string.IsNullOrWhiteSpace(message) ? null : message);
    }

    private static string? Attr(XElement el, string name) =>
        el.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
