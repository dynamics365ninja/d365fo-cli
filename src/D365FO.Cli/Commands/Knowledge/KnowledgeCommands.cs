using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Index;
using D365FO.Core.Knowledge;
using D365FO.Core.Validation;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Knowledge;

/// <summary>
/// <c>d365fo knowledge list</c> — the topic catalog, cheap enough to call before
/// deciding what to fetch.
/// </summary>
public sealed class KnowledgeListCommand : Command<KnowledgeListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var topics = KnowledgeBase.Topics
            .Select(t => new
            {
                id = t.Id,
                description = t.Description,
                appliesWhen = t.AppliesWhen,
                sections = t.Sections.Count,
                approxTokens = t.ApproxTokens,
            })
            .ToList();

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = topics.Count,
            topics,
            usage = "d365fo knowledge get <id> [--section <heading>]  |  d365fo knowledge search \"<question>\"",
        }));
    }
}

/// <summary>
/// <c>d365fo knowledge get</c> — fetch one topic, optionally a single section so
/// the caller pays for the paragraph it needs instead of a whole document.
/// </summary>
public sealed class KnowledgeGetCommand : Command<KnowledgeGetCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<TOPIC>")]
        [System.ComponentModel.Description("Topic id from `d365fo knowledge list` (a unique substring is enough).")]
        public string Topic { get; init; } = "";

        [CommandOption("--section <HEADING>")]
        [System.ComponentModel.Description("Return only the '##' section whose heading contains this text. Omit for the whole topic.")]
        public string? Section { get; init; }

        [CommandOption("--outline")]
        [System.ComponentModel.Description("Return only the section headings, not their text — a cheap table of contents.")]
        public bool Outline { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var topic = KnowledgeBase.Get(settings.Topic);
        if (topic is null)
        {
            var suggestions = KnowledgeBase.Suggest(settings.Topic);
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.TopicNotFound,
                $"No knowledge topic matches '{settings.Topic}'.",
                suggestions.Count > 0
                    ? $"Did you mean: {string.Join(", ", suggestions)}? Run `d365fo knowledge list` for the full catalog."
                    : "Run `d365fo knowledge list` for the full catalog."));
        }

        if (settings.Outline)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                id = topic.Id,
                description = topic.Description,
                sections = topic.Sections.Select(s => new { heading = s.Heading, approxTokens = s.ApproxTokens }).ToList(),
            }));
        }

        if (!string.IsNullOrWhiteSpace(settings.Section))
        {
            var matches = topic.Sections
                .Where(s => s.Heading.Contains(settings.Section!, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    D365FoErrorCodes.TopicNotFound,
                    $"Topic '{topic.Id}' has no section matching '{settings.Section}'.",
                    $"Available sections: {string.Join(" | ", topic.Sections.Select(s => s.Heading))}"));
            }

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                id = topic.Id,
                description = topic.Description,
                sections = matches.Select(s => new { heading = s.Heading, text = s.Text }).ToList(),
            }));
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            id = topic.Id,
            description = topic.Description,
            appliesWhen = topic.AppliesWhen,
            approxTokens = topic.ApproxTokens,
            body = topic.Body,
        }));
    }
}

/// <summary>
/// <c>d365fo knowledge search</c> — rank '##' sections across every topic against a
/// free-text question, so an agent can go straight to the relevant paragraph.
/// </summary>
public sealed class KnowledgeSearchCommand : Command<KnowledgeSearchCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        [System.ComponentModel.Description("Free-text question, e.g. \"how do I add a field to an existing table\".")]
        public string Query { get; init; } = "";

        [CommandOption("--topic <ID>")]
        [System.ComponentModel.Description("Restrict the search to one topic id.")]
        public string? Topic { get; init; }

        [CommandOption("--limit <N>")]
        [System.ComponentModel.Description("Maximum sections to return (default 10).")]
        public int Limit { get; init; } = 10;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var hits = KnowledgeBase.Search(settings.Query, settings.Limit, settings.Topic);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(
            new
            {
                query = settings.Query,
                count = hits.Count,
                hits = hits.Select(h => new
                {
                    topic = h.TopicId,
                    heading = h.Heading,
                    score = h.Score,
                    excerpt = h.Excerpt,
                    fetch = $"d365fo knowledge get {h.TopicId} --section \"{h.Heading}\"",
                }).ToList(),
            },
            hits.Count == 0
                ? new[] { "No section matched. Try `d365fo knowledge list` and fetch a topic directly." }
                : null));
    }
}

/// <summary>
/// <c>d365fo knowledge audit</c> — prove the corpus itself, rather than only the code it
/// helps generate. Two halves, both ported from upstream <c>d365fo-mcp-server</c>:
/// <list type="number">
/// <item><description>every named AOT type/API in <c>skills/_source</c> resolves against the
/// symbol index (live when a full standard index is present, otherwise against the committed
/// snapshot, so CI can refuse un-audited knowledge edits), and</description></item>
/// <item><description>every X++/AOT-XML example passes the same offline BP validator
/// <c>validate xpp</c> runs.</description></item>
/// </list>
/// Exit codes: 0 = clean, 1 = command failure, 2 = defects found.
/// </summary>
public sealed class KnowledgeAuditCommand : Command<KnowledgeAuditCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--capture")]
        [System.ComponentModel.Description("Re-capture eval/knowledge-audit.snapshot.json from a live full standard index.")]
        public bool Capture { get; init; }

        [CommandOption("--verify")]
        [System.ComponentModel.Description("Force the snapshot gate even when a live index is available (what CI runs).")]
        public bool Verify { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var repoRoot = EvalPaths.FindRepoRoot();
        if (repoRoot is null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.SourceUnreadable,
                "knowledge audit needs a checkout of this repo (eval/knowledge-audit.*.json live there).",
                "Run it from within the d365fo-cli source tree."));
        }

        var allow = KnowledgeAuditStore.LoadAllow(EvalPaths.KnowledgeAllowPath(repoRoot));
        var snapshotPath = EvalPaths.KnowledgeSnapshotPath(repoRoot);
        var refs = KnowledgeRefExtractor.ExtractAll();

        // Half 2 runs unconditionally — it needs no index at all.
        var examples = KnowledgeExamples.Collect();
        var (exampleViolations, deadPins) = KnowledgeExamples.Gate(examples, allow.Examples);

        MetadataRepository? repo = null;
        try { repo = RepoFactory.Create(); } catch { /* no index — snapshot gate below */ }
        var live = repo is not null && !settings.Verify && KnowledgeAudit.IsFullStandardIndex(repo);

        if (settings.Capture && !live)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.NoIndex,
                "--capture needs a full standard symbol index; this machine has none.",
                $"Run `d365fo index extract` against a real PackagesLocalDirectory first (sentinels: {string.Join(", ", KnowledgeAudit.Sentinels)})."));
        }

        object symbols;
        int symbolDefects;
        if (live)
        {
            var result = KnowledgeAudit.Audit(refs, repo!, allow);
            symbolDefects = result.Findings.Count;
            if (settings.Capture)
            {
                var indexedAt = repo!.GetNewestExtractTimestampUtc()?.ToString("o") ?? "unknown";
                KnowledgeAuditStore.SaveSnapshot(
                    snapshotPath, KnowledgeAudit.BuildSnapshot(refs, result, indexedAt, DateTimeOffset.UtcNow));
            }
            symbols = new
            {
                mode = settings.Capture ? "capture" : "live",
                checkedCount = result.Checked,
                resolved = result.Resolved,
                allowlisted = result.Allowed,
                defects = result.Findings.Select(f => new
                {
                    topic = f.Ref.TopicId,
                    field = f.Ref.Field,
                    refKind = f.Ref.Kind,
                    status = f.Status,
                    detail = f.Detail,
                }).ToList(),
                snapshot = settings.Capture ? snapshotPath : null,
            };
        }
        else
        {
            var snapshot = KnowledgeAuditStore.LoadSnapshot(snapshotPath);
            if (snapshot is null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    D365FoErrorCodes.NoIndex,
                    "No full standard index and no committed snapshot — the audit has nothing to prove against.",
                    "Run `d365fo knowledge audit --capture` on a machine with a real index and commit the snapshot."));
            }

            var uncovered = KnowledgeAudit.VerifyAgainstSnapshot(refs, snapshot, allow);
            var stale = KnowledgeAudit.StaleSnapshotKeys(refs, snapshot);
            symbolDefects = uncovered.Count;
            symbols = new
            {
                mode = "snapshot",
                checkedCount = refs.Count,
                capturedAt = snapshot.CapturedAt,
                indexedAt = snapshot.IndexedAt,
                uncovered = uncovered.Select(r => new { topic = r.TopicId, field = r.Field, refKind = r.Kind, name = r.Name, member = r.Member }).ToList(),
                staleSnapshotKeys = stale,
            };
        }

        var defects = symbolDefects + exampleViolations.Count + deadPins.Count;
        var envelope = ToolResult<object>.Success(new
        {
            topics = KnowledgeBase.Topics.Count,
            symbols,
            examples = new
            {
                count = examples.Count,
                defects = exampleViolations.Select(v => new
                {
                    topic = v.Example.TopicId,
                    field = v.Example.Field,
                    rule = v.Rule,
                    fix = v.Fix,
                }).ToList(),
                deadPins,
            },
            verdict = defects == 0
                ? "Knowledge corpus audited clean."
                : $"{defects} knowledge defect(s) — fix the topic, or add a reviewed entry to eval/knowledge-audit.allow.json.",
        });

        var rc = RenderHelpers.Render(kind, envelope);
        return rc != 0 ? rc : defects > 0 ? 2 : 0;
    }
}

/// <summary>
/// <c>d365fo explain-error</c> — run the scored <see cref="XppcFixHints"/> matcher over
/// pasted xppc / build output and return the ranked fixes plus the knowledge topic
/// behind each. This is the offline half of the build-error help upstream serves from
/// its knowledge subsystem: it needs no VM, so an agent can triage a build log it was
/// handed rather than one it produced.
/// </summary>
public sealed class ExplainErrorCommand : Command<ExplainErrorCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[MESSAGE]")]
        [System.ComponentModel.Description("A compiler message or a whole xppc log. Omit to read the log from stdin.")]
        public string? Message { get; init; }

        [CommandOption("--file <PATH>")]
        [System.ComponentModel.Description("Read the xppc log from a file instead of the argument/stdin.")]
        public string? File { get; init; }

        [CommandOption("--all")]
        [System.ComponentModel.Description("Return every matching rule per message, not just the best-scoring one.")]
        public bool All { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        string input;
        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            if (!System.IO.File.Exists(settings.File))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    D365FoErrorCodes.SourceUnreadable, $"Log file not found: {settings.File}"));
            }
            input = System.IO.File.ReadAllText(settings.File);
        }
        else if (!string.IsNullOrWhiteSpace(settings.Message))
        {
            input = settings.Message!;
        }
        else
        {
            input = Console.In.ReadToEnd();
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                "No input. Pass a message argument, --file <PATH>, or pipe an xppc log on stdin."));
        }

        // A full log parses into structured diagnostics; a bare message pasted by a
        // user won't match the xppc line grammar, so fall back to scoring it as-is.
        var parsed = XppcDiagnostics.Parse(input);
        var messages = parsed.Count > 0
            ? parsed.Select(d => (d.Severity, d.Object, d.Member, d.Line, d.Message)).ToList()
            : [("error", (string?)null, (string?)null, (int?)null, input.Trim())];

        var explained = messages.Select(m =>
        {
            var matches = XppcFixHints.Match(m.Message);
            var chosen = settings.All ? matches : matches.Take(1).ToList();
            return new
            {
                severity = m.Item1,
                obj = m.Item2,
                member = m.Item3,
                line = m.Item4,
                message = m.Message,
                hints = chosen.Select(h => new
                {
                    rule = h.RuleId,
                    hint = h.Hint,
                    score = h.Score,
                    knowledge = h.Knowledge,
                    read = h.Knowledge is null ? null : $"d365fo knowledge get {h.Knowledge}",
                }).ToList(),
            };
        }).ToList();

        var unexplained = explained.Count(e => e.hints.Count == 0);
        var warnings = new List<string>();
        if (XppcDiagnostics.IndicatesStaleSymbols(input))
            warnings.Add("Log indicates stale incremental-build symbols — do a full build before trusting these errors.");
        if (unexplained > 0)
            warnings.Add($"{unexplained} message(s) matched no hint rule — they are reported verbatim rather than guessed at.");

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = explained.Count,
            explained = explained.Count - unexplained,
            diagnostics = explained,
        }, warnings.Count > 0 ? warnings : null));
    }
}
