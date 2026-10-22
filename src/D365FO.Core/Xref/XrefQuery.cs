// <copyright file="XrefQuery.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

#nullable disable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json.Nodes;

namespace D365FO.Core.Xref
{
    /// <summary>
    /// The where-used query against <c>DYNAMICSXREFDB</c>, the reverse-reference database the
    /// X++ compiler fills on build.
    /// </summary>
    /// <remarks>
    /// Compiled into <c>D365FO.Bridge</c> as well (net48, <c>System.Data.SqlClient</c>), so it is
    /// written against <see cref="DbConnection"/> and keeps to what both compile: no records, no
    /// <c>init</c>, no nullable annotations. The CLI and MCP query the database directly through
    /// <c>Microsoft.Data.SqlClient</c>; the bridge's <c>findReferences</c> is the fallback. One
    /// file, so the two paths cannot drift into answering the same symbol differently. All
    /// queries are parameterised and read-only.
    /// </remarks>
    public static class XrefQuery
    {
        /// <summary>
        /// The connection the compiler's own tooling uses on a dev VM: local SQL, integrated auth.
        /// <c>D365FO_XREF_CONNECTIONSTRING</c> overrides it.
        /// </summary>
        public const string DefaultConnectionString =
            "Server=.;Database=DYNAMICSXREFDB;Integrated Security=true;Connection Timeout=5";

        /// <summary>
        /// Reference kinds by their <c>[References].Kind</c> id, from the X++ compiler source
        /// (Microsoft.Dynamics.AX.Metadata.Xref).
        /// </summary>
        public static readonly string[] ReferenceKinds =
            { "Declaration", "Set", "Read", "Call", "Reference", "Type", "Extends", "Implements" };

        /// <summary>The label for a kind id; unknown ids read as <c>Kind&lt;id&gt;</c>.</summary>
        public static string KindLabel(int id) =>
            id >= 0 && id < ReferenceKinds.Length ? ReferenceKinds[id] : "Kind" + id;

        /// <summary>The id for a kind label (case-insensitive), or -1.</summary>
        public static int KindId(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return -1;
            for (int i = 0; i < ReferenceKinds.Length; i++)
                if (string.Equals(ReferenceKinds[i], label.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Candidate target paths for one symbol.</summary>
        public sealed class TargetPaths
        {
            public TargetPaths(List<string> exact, List<string> like, bool scoped)
            {
                Exact = exact;
                Like = like;
                Scoped = scoped;
            }

            /// <summary>Paths matched with <c>IN (...)</c>.</summary>
            public List<string> Exact { get; }

            /// <summary>Patterns matched with <c>LIKE</c>.</summary>
            public List<string> Like { get; }

            /// <summary>
            /// The target is one exact leaf (a member or a label), so an empty answer is an
            /// authoritative "no callers".
            /// </summary>
            public bool Scoped { get; }
        }

        /// <summary>
        /// Resolve a caller-supplied symbol into candidate XREFDB target paths.
        /// Four input shapes are recognised:
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
        /// path the X++ compiler records for a label, "/Labels/@&lt;ref&gt;".</item>
        /// <item>Bare name ("CustTable") — the AOT root, its children, and the same
        /// name as a whole node anywhere inside a path.</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// The bare-name "anywhere" match is a whole path node (<c>%/CustTable</c>,
        /// <c>%/CustTable/%</c>). It used to be <c>%/CustTable%</c>, a prefix match that
        /// pooled every object whose name merely starts with the symbol — <c>SalesLine</c>
        /// collected <c>SalesLineType</c>, <c>SalesLine_IN</c> and the rest into its callers.
        /// </remarks>
        public static TargetPaths ResolveTargetPaths(string symbol)
        {
            // Container segments that can own methods/fields.
            string[] memberContainers = { "Tables", "Classes", "Forms", "Views", "DataEntityViews", "Queries", "Maps" };

            var exact = new List<string>();
            var like = new List<string>();
            symbol = (symbol ?? string.Empty).Trim();
            var trimmed = symbol.Trim('/');

            var labelRef = TryGetLabelRef(trimmed);
            if (labelRef != null)
            {
                // A label is a leaf with a globally unique id — no children to widen to.
                exact.Add("/Labels/@" + labelRef);
                return new TargetPaths(exact, like, true);
            }

            if (symbol.StartsWith("/", StringComparison.Ordinal))
            {
                exact.Add("/" + trimmed);
                var scoped = symbol.Contains("/Methods/") || symbol.Contains("/Fields/");
                if (!scoped) like.Add("/" + trimmed + "/%");
                return new TargetPaths(exact, like, scoped);
            }

            if (symbol.Contains("."))
            {
                var dot = symbol.IndexOf('.');
                var owner = symbol.Substring(0, dot);
                var member = symbol.Substring(dot + 1);
                foreach (var ct in memberContainers)
                {
                    exact.Add("/" + ct + "/" + owner + "/Methods/" + member);
                    exact.Add("/" + ct + "/" + owner + "/Fields/" + member);
                }
                return new TargetPaths(exact, like, true);
            }

            // Bare name — exact AOT root plus its children, and the name as a whole node anywhere.
            exact.Add("/" + trimmed);
            like.Add("/" + trimmed + "/%");
            like.Add("%/" + trimmed);
            like.Add("%/" + trimmed + "/%");
            return new TargetPaths(exact, like, false);
        }

        /// <summary>
        /// Fill <paramref name="cmd"/> with the where-used query for <paramref name="paths"/>.
        /// </summary>
        /// <param name="cmd">The command to fill; its connection is not touched.</param>
        /// <param name="paths">The target paths, from <see cref="ResolveTargetPaths"/>.</param>
        /// <param name="kindId">A reference kind id to filter on, or -1 for every kind. Filtered in
        /// SQL, before <c>TOP</c> — filtering the returned rows instead answered "the Calls among
        /// the first N references of any kind", which undercounts without saying so.</param>
        /// <param name="limit">Most rows to return.</param>
        /// <param name="aggregate">One row per (source path, kind) with a <c>Refs</c> count, instead of
        /// one per reference. For "who uses X" the per-reference rows are mostly repeats — one method
        /// reading a field 300 times is 300 rows — and they used up the row limit before the next
        /// object was reached. Plain GROUP BY/COUNT, so SQL Server 2016 (older dev VMs) runs it.</param>
        public static void Prepare(DbCommand cmd, TargetPaths paths, int kindId, int limit, bool aggregate = false)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (paths == null) throw new ArgumentNullException(nameof(paths));

            // A scoped target (member-qualified, or a label) points at an exact leaf, so it
            // carries no LIKE terms — adding "/%" children or a "%/name" match would pool
            // callers of every same-named member across types.
            var inParams = new List<string>();
            for (int i = 0; i < paths.Exact.Count; i++)
            {
                var pn = "@P" + i;
                inParams.Add(pn);
                AddParameter(cmd, pn, paths.Exact[i]);
            }
            var likeConds = new List<string>();
            for (int i = 0; i < paths.Like.Count; i++)
            {
                var pn = "@L" + i;
                likeConds.Add("tgtName.Path LIKE " + pn);
                AddParameter(cmd, pn, paths.Like[i]);
            }

            var where = "tgtName.Path IN (" + string.Join(",", inParams) + ")";
            if (likeConds.Count > 0)
                where += " OR " + string.Join(" OR ", likeConds);
            where = "(" + where + ")";
            if (kindId >= 0)
            {
                where += " AND r.Kind = @kind";
                AddParameter(cmd, "@kind", kindId);
            }

            const string from = @"
FROM [References] r
INNER JOIN Names srcName ON srcName.Id = r.SourceId
INNER JOIN Names tgtName ON tgtName.Id = r.TargetId
LEFT  JOIN Modules m     ON m.Id = srcName.ModuleId
WHERE ";
            cmd.CommandText = aggregate
                ? @"
SELECT TOP (@limit)
    srcName.Path  AS SourcePath,
    r.Kind        AS Kind,
    MIN(m.Module) AS Module,
    COUNT(*)      AS Refs" + from + where + @"
GROUP BY srcName.Path, r.Kind
ORDER BY srcName.Path, r.Kind"
                : @"
SELECT TOP (@limit)
    srcName.Path  AS SourcePath,
    tgtName.Path  AS TargetPath,
    r.Kind        AS Kind,
    r.Line        AS Line,
    r.[Column]    AS Col,
    m.Module      AS Module" + from + where + @"
ORDER BY srcName.Path";
            AddParameter(cmd, "@limit", limit);
        }

        /// <summary>
        /// Run the where-used query on an open-able connection.
        /// </summary>
        /// <returns>
        /// <c>{ ok:true, symbol, kindFilter, count, truncated, scoped, items:[{source, target, kind,
        /// kindId, line, column, module}] }</c> — with <paramref name="aggregate"/>, items are
        /// <c>{source, kind, kindId, module, refs}</c> — or <c>{ ok:false, error:"XREFDB_UNAVAILABLE",
        /// message }</c> when the database could not be queried.
        /// </returns>
        public static JsonObject Find(DbConnection connection, string symbol, string kindFilter, int limit, bool aggregate = false)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return Fail("MISSING_ARG", "symbol is required");
            if (limit <= 0) limit = 200;

            int kindId = -1;
            if (!string.IsNullOrWhiteSpace(kindFilter))
            {
                kindId = KindId(kindFilter);
                if (kindId < 0)
                    return Fail("BAD_INPUT", "Unknown reference kind '" + kindFilter + "'. Use one of: "
                        + string.Join(" | ", ReferenceKinds) + ".");
            }

            var paths = ResolveTargetPaths(symbol);
            var items = new JsonArray();
            try
            {
                if (connection.State != System.Data.ConnectionState.Open) connection.Open();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandTimeout = 30;
                    Prepare(cmd, paths, kindId, limit, aggregate);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var kind = Convert.ToInt32(r["Kind"]);
                            if (aggregate)
                            {
                                items.Add(new JsonObject
                                {
                                    ["source"] = r["SourcePath"] as string,
                                    ["kind"] = KindLabel(kind),
                                    ["kindId"] = kind,
                                    ["module"] = r["Module"] as string ?? string.Empty,
                                    ["refs"] = Convert.ToInt32(r["Refs"]),
                                });
                                continue;
                            }
                            items.Add(new JsonObject
                            {
                                ["source"] = r["SourcePath"] as string,
                                ["target"] = r["TargetPath"] as string,
                                ["kind"] = KindLabel(kind),
                                ["kindId"] = kind,
                                ["line"] = Convert.ToInt32(r["Line"]),
                                ["column"] = Convert.ToInt32(r["Col"]),
                                ["module"] = r["Module"] as string ?? string.Empty,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Fail("XREFDB_UNAVAILABLE", ex.GetType().Name + ": " + ex.Message);
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["symbol"] = symbol,
                ["kindFilter"] = kindId >= 0 ? ReferenceKinds[kindId] : string.Empty,
                ["count"] = items.Count,
                // TOP cut the answer off: there may be more references than returned.
                ["truncated"] = items.Count >= limit,
                ["source"] = "xrefdb",
                // When the target is scoped (member-qualified, or a label) the query hit one
                // exact leaf, so an empty list is an authoritative "no callers" — not a hint to
                // fall back to a looser name-only scan.
                ["scoped"] = paths.Scoped,
                ["items"] = items,
            };
        }

        /// <summary>
        /// The object a source path belongs to: <c>/Classes/SalesLineType/Methods/insert</c> →
        /// <c>/Classes/SalesLineType</c>, with the rest (<c>insert</c>) as the member.
        /// </summary>
        public static string ObjectOf(string sourcePath, out string member)
        {
            member = null;
            if (string.IsNullOrEmpty(sourcePath)) return sourcePath ?? string.Empty;
            var parts = sourcePath.Trim('/').Split('/');
            if (parts.Length < 2) return sourcePath;
            if (parts.Length > 2) member = parts[parts.Length - 1];
            return "/" + parts[0] + "/" + parts[1];
        }

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

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static void AddParameter(DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        private static JsonObject Fail(string code, string message) => new JsonObject
        {
            ["ok"] = false,
            ["error"] = code,
            ["message"] = message,
        };
    }
}
