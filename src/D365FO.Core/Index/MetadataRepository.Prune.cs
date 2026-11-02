using Dapper;

namespace D365FO.Core.Index;

public sealed partial class MetadataRepository
{
    /// <summary>One indexed model and a source path that shows where it was extracted from.</summary>
    /// <param name="Model">Model name.</param>
    /// <param name="IsCustom">Whether the last extract classified it as custom.</param>
    /// <param name="SamplePath">One object's <c>SourcePath</c>, or null when no row of the model carries one.</param>
    public sealed record ModelLocation(string Model, bool IsCustom, string? SamplePath);

    /// <summary>
    /// Every indexed model with one sample source path, read across the object tables that
    /// record a path. A model only labels or EDTs live in has no path and comes back with null.
    /// </summary>
    public IReadOnlyList<ModelLocation> GetModelLocations()
    {
        using var conn = OpenReadOnly();
        return conn.Query<(string Name, long IsCustom, string? SamplePath)>(@"
            SELECT m.Name, m.IsCustom, MIN(s.SourcePath)
              FROM Models m
              LEFT JOIN (
                    SELECT ModelId, SourcePath FROM Classes          WHERE SourcePath IS NOT NULL AND SourcePath <> ''
                    UNION ALL
                    SELECT ModelId, SourcePath FROM Tables           WHERE SourcePath IS NOT NULL AND SourcePath <> ''
                    UNION ALL
                    SELECT ModelId, SourcePath FROM Forms            WHERE SourcePath IS NOT NULL AND SourcePath <> ''
                    UNION ALL
                    SELECT ModelId, SourcePath FROM ObjectExtensions WHERE SourcePath IS NOT NULL AND SourcePath <> ''
                    UNION ALL
                    SELECT ModelId, SourcePath FROM Queries          WHERE SourcePath IS NOT NULL AND SourcePath <> ''
                    UNION ALL
                    SELECT ModelId, SourcePath FROM DataEntities     WHERE SourcePath IS NOT NULL AND SourcePath <> '') s
                ON s.ModelId = m.ModelId
             GROUP BY m.ModelId, m.Name, m.IsCustom
             ORDER BY m.Name")
            .Select(r => new ModelLocation(r.Name, r.IsCustom != 0, r.SamplePath))
            .ToList();
    }

    /// <summary>
    /// Remove a model and every row it owns from the index. Returns false when no such model is indexed.
    /// </summary>
    /// <param name="model">Model name.</param>
    /// <param name="modelFolder">
    /// The folder the model was extracted from. Label files are keyed by file id rather than by
    /// model, so this is how the model's label rows are found. Null leaves labels alone.
    /// </param>
    /// <remarks>
    /// <para>
    /// The object rows go through <see cref="ApplyExtract(ExtractBatch)"/> with an empty batch, so
    /// "what a model owns" has one definition: whatever a re-extract replaces. Re-extracting a
    /// model into nothing is exactly what removing it means, and a table added to the extract
    /// later is covered here without anyone remembering to.
    /// </para>
    /// <para>
    /// Then what the extract deliberately keeps is cleared too: the model's method-body full-text
    /// rows, its label files (only those whose path is inside <paramref name="modelFolder"/>, and
    /// a label file's labels only when no other model still ships a file with that id), and the
    /// <c>Models</c> row itself. <c>ExtractionRuns</c> is history and stays.
    /// </para>
    /// </remarks>
    public bool DeleteModel(string model, string? modelFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        bool isCustom;
        using (var probe = OpenReadOnly())
        {
            var flag = probe.ExecuteScalar<long?>("SELECT IsCustom FROM Models WHERE Name=@n", new { n = model });
            if (flag is null) return false;
            isCustom = flag.Value != 0;
        }

        // One transaction: a prune interrupted half-way must not leave a Models row whose object
        // rows are gone, because with no SourcePath left nothing would ever report it again.
        ApplyExtract(ExtractBatch.Empty(model) with { IsCustom = isCustom }, sourceFingerprint: null,
            indexMethodSource: false, beforeCommit: (conn, tx) =>
        {
            try { conn.Execute("DELETE FROM MethodSourceFts WHERE Model=@n", new { n = model }, tx); }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* no FTS5 in this SQLite build: nothing to clear */ }

            if (!string.IsNullOrWhiteSpace(modelFolder))
            {
                var prefix = Path.TrimEndingDirectorySeparator(modelFolder) + Path.DirectorySeparatorChar;
                var files = conn.Query<(long Id, string LabelFile, string SourcePath)>(
                        "SELECT LabelFileId, LabelFile, SourcePath FROM LabelFiles", transaction: tx)
                    .Where(f => f.SourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var f in files)
                    conn.Execute("DELETE FROM LabelFiles WHERE LabelFileId=@id", new { id = f.Id }, tx);
                foreach (var labelFile in files.Select(f => f.LabelFile).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var stillShipped = conn.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM LabelFiles WHERE LabelFile=@f", new { f = labelFile }, tx) > 0;
                    if (!stillShipped)
                        conn.Execute("DELETE FROM Labels WHERE LabelFile=@f", new { f = labelFile }, tx);
                }
            }

            conn.Execute("DELETE FROM Models WHERE Name=@n", new { n = model }, tx);
        });
        return true;
    }
}
