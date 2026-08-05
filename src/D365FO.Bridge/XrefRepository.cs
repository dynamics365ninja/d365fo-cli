// <copyright file="XrefRepository.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text.Json.Nodes;

namespace D365FO.Bridge
{
    /// <summary>
    /// Thin wrapper around the <c>DYNAMICSXREFDB</c> SQL Server database that
    /// the X++ compiler populates with reverse references. All queries are
    /// parameterised and read-only; the bridge never issues writes against
    /// this DB. Connection string defaults to local SQL with integrated
    /// auth and can be overridden via <c>D365FO_XREF_CONNECTIONSTRING</c>.
    /// </summary>
    internal static class XrefRepository
    {
        /// <summary>
        /// Map of XREFDB Kind id → human-readable label. Values are taken
        /// from the X++ compiler source (Microsoft.Dynamics.AX.Metadata.Xref).
        /// Unknown ids fall back to "Reference".
        /// </summary>
        private static readonly Dictionary<int, string> KindLabels = new Dictionary<int, string>
        {
            { 0, "Declaration" },
            { 1, "Set" },
            { 2, "Read" },
            { 3, "Call" },
            { 4, "Reference" },
            { 5, "Type" },
            { 6, "Extends" },
            { 7, "Implements" },
        };

        internal static string ConnectionString
        {
            get
            {
                var cs = Environment.GetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING");
                if (!string.IsNullOrWhiteSpace(cs)) return cs;
                return "Server=.;Database=DYNAMICSXREFDB;Integrated Security=true;Connection Timeout=5";
            }
        }

        internal static bool IsAvailable(out string error)
        {
            error = null;
            try
            {
                using (var c = new SqlConnection(ConnectionString))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "SELECT TOP 1 Id FROM Names";
                        cmd.CommandTimeout = 5;
                        cmd.ExecuteScalar();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static JsonObject Find(string symbol, string kindFilter, int limit)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "MISSING_ARG",
                    ["message"] = "symbol is required",
                };
            }
            if (limit <= 0 || limit > 1000) limit = 200;

            // Resolve the input into one or more candidate XREFDB target paths.
            var (exactPaths, likePaths, scopedTarget) = ResolveTargetPaths(symbol);

            var result = new JsonObject();
            var items = new JsonArray();

            try
            {
                using (var c = new SqlConnection(ConnectionString))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandTimeout = 30;

                        // Build a parameterised WHERE: exact target paths via IN (...),
                        // plus LIKE conditions for child/contains matches. A scoped
                        // target (member-qualified, or a label) points at an exact
                        // leaf, so it carries no LIKE terms — adding "/%" children or
                        // a "%/name%" contains would pool callers of every same-named
                        // member across types.
                        var inParams = new List<string>();
                        for (int i = 0; i < exactPaths.Count; i++)
                        {
                            var pn = "@P" + i;
                            inParams.Add(pn);
                            cmd.Parameters.Add(new SqlParameter(pn, exactPaths[i]));
                        }
                        var likeConds = new List<string>();
                        for (int i = 0; i < likePaths.Count; i++)
                        {
                            var pn = "@L" + i;
                            likeConds.Add("tgtName.Path LIKE " + pn);
                            cmd.Parameters.Add(new SqlParameter(pn, likePaths[i]));
                        }

                        var where = "tgtName.Path IN (" + string.Join(",", inParams) + ")";
                        if (likeConds.Count > 0)
                            where += " OR " + string.Join(" OR ", likeConds);

                        var sql = @"
SELECT TOP (@limit)
    srcName.Path  AS SourcePath,
    tgtName.Path  AS TargetPath,
    r.Kind        AS Kind,
    r.Line        AS Line,
    r.[Column]    AS Col,
    m.Module      AS Module
FROM [References] r
INNER JOIN Names srcName ON srcName.Id = r.SourceId
INNER JOIN Names tgtName ON tgtName.Id = r.TargetId
LEFT  JOIN Modules m     ON m.Id = srcName.ModuleId
WHERE " + where + @"
ORDER BY srcName.Path";
                        cmd.CommandText = sql;
                        cmd.Parameters.Add(new SqlParameter("@limit", limit));

                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                var srcPath = r["SourcePath"] as string;
                                var tgtPath = r["TargetPath"] as string;
                                var kind = r["Kind"] is byte b ? (int)b : Convert.ToInt32(r["Kind"]);
                                var line = r["Line"] is short s ? (int)s : Convert.ToInt32(r["Line"]);
                                var col = r["Col"] is short sc ? (int)sc : Convert.ToInt32(r["Col"]);
                                var module = r["Module"] as string;

                                if (!string.IsNullOrEmpty(kindFilter) &&
                                    !string.Equals(KindLabels.TryGetValue(kind, out var kl) ? kl : null, kindFilter, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                items.Add(new JsonObject
                                {
                                    ["source"] = srcPath,
                                    ["target"] = tgtPath,
                                    ["kind"]   = KindLabels.TryGetValue(kind, out var lbl) ? lbl : ("Kind" + kind),
                                    ["kindId"] = kind,
                                    ["line"]   = line,
                                    ["column"] = col,
                                    ["module"] = module ?? string.Empty,
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "XREFDB_UNAVAILABLE",
                    ["message"] = ex.GetType().Name + ": " + ex.Message,
                };
            }

            result["ok"] = true;
            result["symbol"] = symbol;
            result["kindFilter"] = kindFilter ?? string.Empty;
            result["count"] = items.Count;
            result["source"] = "xrefdb";
            // When the target is scoped (member-qualified, or a label) the query
            // hit one exact leaf, so an empty list is an authoritative "no callers"
            // — not a hint to fall back to a looser name-only scan.
            result["scoped"] = scopedTarget;
            result["items"] = items;
            return result;
        }

        /// <summary>
        /// Resolve a caller-supplied symbol into candidate XREFDB target paths.
        /// Three input shapes are recognised:
        /// <list type="bullet">
        /// <item>Explicit AOT path ("/Tables/SalesTable/Methods/initFromX") — used
        /// verbatim. Method/field paths are treated as exact leaves.</item>
        /// <item>Member-qualified ("SalesTable.initFromX") — scoped to a single
        /// method/field on its declaring type. The owner's object kind is unknown,
        /// so method+field variants are expanded across every container type; only
        /// the path that actually exists matches. This is the precise where-used
        /// Visual Studio shows, instead of pooling every same-named member.</item>
        /// <item>Label ("@WAX2194" old format, "@LabelFile:LabelId" new format, or
        /// the explicit "/Labels/@..." xref path) — normalised to the single exact
        /// path the X++ compiler records for a label, "/Labels/@&lt;ref&gt;". Without
        /// this case a bare "@WAX2194" fell into the bare-name branch and produced
        /// "/@WAX2194", which matches nothing, so label where-used silently returned
        /// zero results.</item>
        /// <item>Bare name ("CustTable") — matched loosely as a standalone AOT root,
        /// its children, and as a node anywhere inside a path.</item>
        /// </list>
        /// Returns exact paths (matched via IN), LIKE patterns, and whether the
        /// target is scoped to an exact leaf (carrying no LIKE terms).
        /// </summary>
        internal static (List<string> exactPaths, List<string> likePaths, bool scopedTarget) ResolveTargetPaths(string symbol)
        {
            // Container segments that can own methods/fields.
            string[] memberContainers = { "Tables", "Classes", "Forms", "Views", "DataEntityViews", "Queries", "Maps" };

            var exact = new List<string>();
            var like = new List<string>();
            bool scoped = false;
            var trimmed = TrimSlash(symbol);

            var labelRef = TryGetLabelRef(trimmed);
            if (labelRef != null)
            {
                // A label is a leaf with a globally unique id — no children to widen
                // to, and a "%/@Id%" contains scan would only pool unrelated paths.
                exact.Add("/Labels/@" + labelRef);
                return (exact, like, true);
            }

            if (symbol.StartsWith("/"))
            {
                exact.Add("/" + trimmed);
                scoped = symbol.Contains("/Methods/") || symbol.Contains("/Fields/");
                if (!scoped) like.Add("/" + trimmed + "/%");
            }
            else if (symbol.Contains("."))
            {
                scoped = true;
                var dot = symbol.IndexOf('.');
                var owner = symbol.Substring(0, dot);
                var member = symbol.Substring(dot + 1);
                foreach (var ct in memberContainers)
                {
                    exact.Add("/" + ct + "/" + owner + "/Methods/" + member);
                    exact.Add("/" + ct + "/" + owner + "/Fields/" + member);
                }
            }
            else
            {
                // Bare name — exact AOT root plus loose child/contains matching.
                exact.Add("/" + trimmed);
                like.Add("/" + trimmed + "/%");
                like.Add("%/" + trimmed + "%");
            }

            return (exact, like, scoped);
        }

        /// <summary>
        /// Extracts the label id from a caller-supplied symbol, or returns null when
        /// the symbol is not a label. Accepts the bare label reference in either
        /// format ("@WAX2194", "@SysCommon:Delete") and the explicit xref path
        /// ("/Labels/@WAX2194"). The id itself is passed through verbatim — the
        /// compiler records it exactly as written in the source.
        /// </summary>
        private static string TryGetLabelRef(string trimmed)
        {
            if (string.IsNullOrEmpty(trimmed)) return null;

            const string labelsPrefix = "Labels/@";
            if (trimmed.StartsWith(labelsPrefix, StringComparison.OrdinalIgnoreCase))
                return NullIfEmpty(trimmed.Substring(labelsPrefix.Length));

            if (trimmed[0] == '@')
                return NullIfEmpty(trimmed.Substring(1));

            return null;
        }

        private static string NullIfEmpty(string s) =>
            string.IsNullOrWhiteSpace(s) ? null : s;

        private static string TrimSlash(string s)
        {
            if (s == null) return string.Empty;
            return s.Trim('/');
        }
    }
}
