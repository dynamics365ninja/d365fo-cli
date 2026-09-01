using D365FO.Core.Index;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Resolves an AOT object the caller named to the XML on disk, accepting either a path or an
/// indexed object name.
/// </summary>
/// <remarks>
/// Cloning and form-method injection both start by reading a form that already exists, and both
/// surfaces have to resolve "CustGroup" the same way — a file if one is there, otherwise the
/// <c>SourcePath</c> the index recorded. The index is a cache and is never invalidated on
/// delete, so a row whose file has gone says exactly that rather than failing as "not found".
/// </remarks>
public static class AotSourceReader
{
    /// <summary>Read a form's XML by path or by indexed name.</summary>
    /// <returns>The XML, or an error message explaining which of the two lookups failed and why.</returns>
    public static (string? Xml, string? Error) ReadForm(MetadataRepository? repo, string from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return (null, "No source form given.");

        if (File.Exists(from))
        {
            try { return (File.ReadAllText(from), null); }
            catch (Exception ex) { return (null, $"Could not read '{from}': {ex.Message}"); }
        }

        if (repo is null)
            return (null, $"No file exists at '{from}' and there is no index to resolve it as a form name.");

        try
        {
            var details = repo.GetForm(from);
            if (details is null)
                return (null, $"Form '{from}' is not in the index, and no file exists at that path. " +
                              "Run `d365fo index extract`, or pass the path to the AxForm XML.");

            var path = details.Form.SourcePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return (null, $"The index knows form '{from}' but its source file is not at '{path}'. " +
                              "The index is a cache and is never invalidated on delete — re-run `d365fo index refresh`.");

            return (File.ReadAllText(path), null);
        }
        catch (Exception ex)
        {
            return (null, $"Could not resolve form '{from}': {ex.Message}");
        }
    }
}
