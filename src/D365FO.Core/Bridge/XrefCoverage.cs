// <copyright file="XrefCoverage.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using D365FO.Core.Index;

namespace D365FO.Core.Bridge;

/// <summary>
/// Whether the cross-reference database can answer for a symbol at all: the module that
/// declares it has to have been compiled with cross-references.
/// </summary>
/// <remarks>
/// <para>
/// <c>DYNAMICSXREFDB</c> is written by the compiler, and only when a build asks for it
/// (<c>xppc -xref</c>). A developer machine's database therefore holds the standard modules it
/// shipped with and nothing for the custom models built on it, so <c>find refs --xref</c> on a
/// custom symbol came back empty, or timed out, with nothing to say that the database had simply
/// never seen the module. An empty answer from a database that does not cover the symbol is not
/// "no references", and reporting it as one is the degraded zero this repository refuses
/// elsewhere.
/// </para>
/// <para>
/// The check costs one indexed existence query per module, through the same bridge that ran the
/// reference query. The decision itself (<see cref="Assess"/>) is a pure function, so the cases
/// are tested without a database.
/// </para>
/// </remarks>
public static class XrefCoverage
{
    /// <summary>The model and module (package) that declare a symbol.</summary>
    /// <param name="Model">Model name, the one <c>d365fo build --model</c> takes.</param>
    /// <param name="Module">
    /// Package folder, which is what the cross-reference database keys on, or null when the index
    /// records no source path for the model to read it from (coverage is then unknown).
    /// </param>
    /// <param name="Kinds">
    /// Every object kind the index knows the name as (<c>table</c>, <c>form</c>, …). A table and its
    /// form often share one. The xref query uses them to seek a bare name instead of scanning.
    /// </param>
    public sealed record Owner(string Model, string? Module, IReadOnlyList<string>? Kinds = null);

    /// <summary>
    /// The type a symbol names: <c>SalesTable</c> for <c>SalesTable</c>,
    /// <c>SalesTable.initValue</c> and <c>/Tables/SalesTable/Methods/initValue</c>. Null for a
    /// label, which no module owns in a way the index records.
    /// </summary>
    public static string? OwnerName(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var s = symbol.Trim();
        if (s.StartsWith('@') || s.StartsWith("/Labels/", StringComparison.OrdinalIgnoreCase)) return null;

        if (s.StartsWith('/'))
        {
            var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : null;
        }

        var dot = s.IndexOf('.');
        return dot > 0 ? s[..dot] : s;
    }

    /// <summary>
    /// The package folder of an AOT source path, <c>&lt;root&gt;\&lt;Package&gt;\&lt;Model&gt;\Ax&lt;Kind&gt;\&lt;Name&gt;.xml</c>,
    /// or null when the path is not in that layout.
    /// </summary>
    public static string? PackageOf(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        var normalised = sourcePath.Replace('\\', '/');
        var parts = normalised.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // ... / Package / Model / AxKind / Name.xml
        if (parts.Length < 4) return null;
        var axFolder = parts[^2];
        if (!axFolder.StartsWith("Ax", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[^4];
    }

    /// <summary>
    /// The model and module that declare <paramref name="symbol"/>, from the local index, or null
    /// when the index does not know it.
    /// </summary>
    public static Owner? OwnerOf(string symbol, MetadataRepository repo)
    {
        var name = OwnerName(symbol);
        if (name is null) return null;

        string? model = null;
        string? path = null;
        var kinds = new List<string>();
        try
        {
            // The first hit decides the model and path, as before. Every hit is a kind.
            if (repo.GetClassDetails(name) is { } c) { (model, path) = (c.Class.Model, c.Class.SourcePath); kinds.Add("class"); }
            if (repo.GetTableDetails(name) is { } t) { if (model is null) (model, path) = (t.Table.Model, t.Table.SourcePath); kinds.Add("table"); }
            if (repo.GetForm(name) is { } f) { if (model is null) (model, path) = (f.Form.Model, f.Form.SourcePath); kinds.Add("form"); }
            if (repo.GetEdt(name) is { } e) { model ??= e.Model; kinds.Add("edt"); }
            if (repo.GetEnum(name) is { } en) { model ??= en.Enum.Model; kinds.Add("enum"); }
        }
        catch
        {
            return null; // an unreadable index is no reason to fail the reference query
        }
        if (string.IsNullOrEmpty(model)) return null;

        var module = PackageOf(path);
        if (module is null)
        {
            try
            {
                module = repo.GetModelSamplePaths()
                    .Where(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase))
                    .Select(p => PackageOf(p.SourcePath))
                    .FirstOrDefault(p => p is not null);
            }
            catch { /* the package stays unknown */ }
        }

        // No guess from the model name: a package named otherwise would probe as "no rows" and
        // turn a genuine empty answer into a refusal. An unknown package means unknown coverage.
        return new Owner(model!, module, kinds);
    }

    /// <summary>What a reference answer means once coverage is known.</summary>
    /// <param name="Failure">Refuse the answer: it cannot be told apart from "not covered".</param>
    /// <param name="Warning">Keep the answer, but say what it is missing.</param>
    public sealed record Verdict(ToolResult<object>? Failure, string? Warning);

    /// <summary>
    /// Judge a cross-reference answer of <paramref name="count"/> rows for
    /// <paramref name="symbol"/> against whether the database covers its owner.
    /// </summary>
    /// <param name="count">Rows the reference query returned.</param>
    /// <param name="symbol">The symbol asked about.</param>
    /// <param name="owner">Its model and module, or null when the index does not know it.</param>
    /// <param name="moduleHasRows">
    /// Whether the database holds any row for <paramref name="owner"/>'s module, or null when
    /// that could not be checked.
    /// </param>
    public static Verdict Assess(int count, string symbol, Owner? owner, bool? moduleHasRows)
    {
        if (owner is null) return new Verdict(null, null);

        if (owner.Module is null)
        {
            // Coverage could not be checked; an empty answer stays an answer, with the one
            // reason it could be empty that the caller cannot see.
            return new Verdict(null, count == 0
                ? $"Could not tell whether DYNAMICSXREFDB covers model {owner.Model}, which declares '{symbol}' " +
                  "(the index records no package folder for it). If that model was never compiled with " +
                  $"cross-references, this empty answer is not \"no references\": `d365fo build --model {owner.Model} --xref`."
                : null);
        }

        if (moduleHasRows is not false) return new Verdict(null, null);

        var build = $"d365fo build --model {owner.Model} --xref";
        if (count == 0)
        {
            return new Verdict(ToolResult<object>.Fail(
                D365FoErrorCodes.XrefUnavailable,
                $"DYNAMICSXREFDB has 0 rows for module {owner.Module}, which declares '{symbol}', so an empty " +
                "answer would only mean the module was never compiled with cross-references.",
                $"Build it with `{build}` (MCP: sdlc action=build model={owner.Model} xref=true), then ask again. " +
                "Until then drop --xref for the text scan over indexed X++ source."), null);
        }

        return new Verdict(null,
            $"DYNAMICSXREFDB has 0 rows for module {owner.Module}, which declares '{symbol}': references from " +
            $"inside that module are missing from this answer. Build it with `{build}`.");
    }
}
