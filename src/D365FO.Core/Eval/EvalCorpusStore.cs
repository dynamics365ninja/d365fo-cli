using System.Text.Json;

namespace D365FO.Core.Eval;

/// <summary>
/// One JSON file per run under <c>eval/corpus/runs/</c>. The records are
/// committed: they are the evidence the eval-improver ranks failure clusters
/// from, so they have to survive across machines and CI runs. Mirrors the sibling
/// d365fo-mcp-server repo's "newline-delimited JSON now, one file per run"
/// choice, minus the NDJSON part — one file per record keeps a single
/// malformed/partial write from corrupting the whole corpus.
/// </summary>
public static class EvalCorpusStore
{
    public static void Append(string runsDir, EvalCorpusRecord record)
    {
        Directory.CreateDirectory(runsDir);
        var safeId = new string(record.RunId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        var path = Path.Combine(runsDir, safeId + ".json");
        File.WriteAllText(path, D365Json.Serialize(record, indented: true));
    }

    public static IReadOnlyList<EvalCorpusRecord> ReadAll(string runsDir)
    {
        if (!Directory.Exists(runsDir)) return Array.Empty<EvalCorpusRecord>();

        var records = new List<EvalCorpusRecord>();
        foreach (var file in Directory.GetFiles(runsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize<EvalCorpusRecord>(json, D365Json.Options);
                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                // A malformed/partial corpus record must not take the whole
                // report/cluster read down with it — skip and move on.
            }
        }
        return records;
    }
}
