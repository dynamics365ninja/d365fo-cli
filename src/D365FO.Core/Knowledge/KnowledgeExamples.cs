// <copyright file="KnowledgeExamples.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using D365FO.Core.Validation;

namespace D365FO.Core.Knowledge;

/// <summary>One fenced code example from the knowledge corpus.</summary>
/// <param name="TopicId">Topic the example lives in.</param>
/// <param name="Field">Location label — <c>§heading · ```xpp</c>.</param>
/// <param name="CodeType">The <see cref="XppValidator"/> code type this example is checked as.</param>
/// <param name="Code">The example source.</param>
public sealed record KnowledgeExample(string TopicId, string Field, string CodeType, string Code)
{
    /// <summary>Stable key used to pin an intentional wrong-vs-right demonstration.</summary>
    public string Key => $"{TopicId}::{Field}";
}

/// <summary>One offline best-practice violation found in a knowledge example.</summary>
public sealed record KnowledgeExampleViolation(KnowledgeExample Example, string Rule, string Severity, string Fix)
{
    /// <summary>Pin key for the allowlist: <c>topic::field::rule</c>.</summary>
    public string Key => $"{Example.Key}::{Rule}";
}

/// <summary>
/// Routes every code example in <c>skills/_source</c> through the same offline BP validator
/// the <c>validate xpp</c> command runs, so a knowledge edit cannot teach BP-breaking X++
/// (<c>today()</c>, <c>forceLiterals</c>, crossCompany on a joined buffer, default-param CoC
/// wrappers, hardcoded infolog strings, …).
///
/// Port of upstream <c>d365fo-mcp-server</c>'s <c>exampleValidation.test.ts</c>. This is the
/// VM-free half; proving the surrounding X++ actually compiles stays a VM-only gate.
/// </summary>
public static class KnowledgeExamples
{
    private static readonly HashSet<string> XppLanguages = new(StringComparer.OrdinalIgnoreCase) { "xpp", "x++" };

    /// <summary>Collect every X++ / AOT-XML example in the corpus, in topic order.</summary>
    public static IReadOnlyList<KnowledgeExample> Collect(IEnumerable<KnowledgeTopic>? topics = null)
    {
        var examples = new List<KnowledgeExample>();
        foreach (var topic in topics ?? KnowledgeBase.Topics)
        {
            foreach (var block in KnowledgeMarkdown.Blocks(topic.Body))
            {
                if (!block.IsFence) continue;
                string codeType;
                if (XppLanguages.Contains(block.Language))
                {
                    codeType = XppValidator.CodeTypeXpp;
                }
                else if (block.Language == "xml")
                {
                    codeType = block.Text.Contains("<AxTable", StringComparison.Ordinal)
                        ? XppValidator.CodeTypeXmlTable
                        : XppValidator.CodeTypeXmlAny;
                }
                else
                {
                    continue; // shell transcripts, JSON envelopes, ASCII trees — nothing to BP-check
                }

                examples.Add(new KnowledgeExample(topic.Id, block.Field, codeType, block.Text));
            }
        }
        return examples;
    }

    /// <summary>Every error-severity BP violation across the collected examples.</summary>
    public static IReadOnlyList<KnowledgeExampleViolation> Validate(
        IReadOnlyList<KnowledgeExample> examples,
        IPropertyStatsProvider? stats = null)
    {
        var found = new List<KnowledgeExampleViolation>();
        foreach (var ex in examples)
        {
            foreach (var v in XppValidator.Validate(ex.Code, ex.CodeType, stats))
            {
                if (v.Severity != "error") continue;
                found.Add(new KnowledgeExampleViolation(ex, v.Rule, v.Severity, v.Fix));
            }
        }
        return found;
    }

    /// <summary>
    /// The gate proper: violations that are not pinned, plus pins that no longer fire. A dead
    /// pin fails too — otherwise an example could quietly stop demonstrating the anti-pattern
    /// it was excused for.
    /// </summary>
    public static (IReadOnlyList<KnowledgeExampleViolation> Unexpected, IReadOnlyList<string> DeadPins) Gate(
        IReadOnlyList<KnowledgeExample> examples,
        IReadOnlyDictionary<string, string> pinned,
        IPropertyStatsProvider? stats = null)
    {
        var violations = Validate(examples, stats);
        var fired = violations.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        return (
            violations.Where(v => !pinned.ContainsKey(v.Key)).ToList(),
            pinned.Keys.Where(k => !fired.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }
}
