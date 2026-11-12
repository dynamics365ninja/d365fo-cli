// <copyright file="XrefQuery.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

#nullable disable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
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
        /// Reference kinds by their <c>[References].Kind</c> id: the values of the compiler's
        /// <c>Microsoft.Dynamics.AX.Framework.Xlnt.XReference.CrossReferenceKind</c> enum
        /// (XReferenceProviders.dll; 0 Any, 1 MethodCall, 2 TypeReference, 3 InterfaceImplementation,
        /// 4 ClassExtended, 5 TestCall, 6 Property, 7 Attribute, 8 TestHelperCall, 9 Tag,
        /// 10 MethodOverride), under shorter labels.
        /// </summary>
        public static readonly string[] ReferenceKinds =
            { "Any", "Call", "Type", "Implements", "Extends", "TestCall", "Property", "Attribute",
              "TestHelperCall", "Tag", "Override" };

        /// <summary>The enum's own names, accepted as filters next to the labels.</summary>
        private static readonly string[] CrossReferenceKindNames =
            { "Any", "MethodCall", "TypeReference", "InterfaceImplementation", "ClassExtended", "TestCall",
              "Property", "Attribute", "TestHelperCall", "Tag", "MethodOverride" };

        /// <summary>The label for a kind id; unknown ids read as <c>Kind&lt;id&gt;</c>.</summary>
        public static string KindLabel(int id) =>
            id >= 0 && id < ReferenceKinds.Length ? ReferenceKinds[id] : "Kind" + id;

        /// <summary>The id for a kind label or enum name (case-insensitive), or -1.</summary>
        public static int KindId(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return -1;
            for (int i = 0; i < ReferenceKinds.Length; i++)
                if (string.Equals(ReferenceKinds[i], label.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(CrossReferenceKindNames[i], label.Trim(), StringComparison.OrdinalIgnoreCase))
                    return i;
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
        /// <para>
        /// With object kinds (what the local index says the name is: <c>table</c>, <c>class</c>,
        /// <c>form</c>, <c>edt</c>, <c>enum</c>), a bare name resolves to that object and its children in
        /// both path forms the compiler writes: <c>/Tables/X</c> for code, <c>Table/X</c> for metadata
        /// such as a form control bound to a field group. Every term is an exact path or a prefix,
        /// so the query seeks the <c>Names.Path</c> index instead of scanning it. The leading-wildcard
        /// terms cost about 60 s on a 6M-name database. A name that is a node deeper inside another
        /// object (a form data source named after the table) is a different symbol and is no longer
        /// pooled in. Without a known kind the bare name keeps the "anywhere" match; <see cref="Find(DbConnection, string, string, int, bool, IList{string})"/>
        /// first asks the database for the kinds (<see cref="ProbeKinds"/>) so that only a name the
        /// database has no object for scans.
        /// </para>
        /// </remarks>
        public static TargetPaths ResolveTargetPaths(string symbol) => ResolveTargetPaths(symbol, null);

        /// <inheritdoc cref="ResolveTargetPaths(string)"/>
        /// <param name="symbol">The caller's symbol.</param>
        /// <param name="objectKinds">Object kinds the bare name is known to be, or null.</param>
        public static TargetPaths ResolveTargetPaths(string symbol, IList<string> objectKinds)
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
                if (!scoped) like.Add("/" + EscapeLike(trimmed) + "/%");
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

            // Bare name of a known kind: the object and its children, in both path forms. Exact
            // paths and prefixes only, so every term seeks.
            var known = PathRootsFor(objectKinds);
            if (known.Count > 0)
            {
                foreach (var root in known)
                {
                    exact.Add(root + trimmed);
                    like.Add(EscapeLike(root + trimmed) + "/%");
                }
                return new TargetPaths(exact, like, false);
            }

            // Bare name — exact AOT root plus its children, and the name as a whole node anywhere.
            exact.Add("/" + trimmed);
            var pattern = EscapeLike(trimmed);
            like.Add("/" + pattern + "/%");
            like.Add("%/" + pattern);
            like.Add("%/" + pattern + "/%");
            return new TargetPaths(exact, like, false);
        }

        /// <summary>
        /// Fill <paramref name="cmd"/> with the where-used query for <paramref name="paths"/>.
        /// </summary>
        /// <param name="cmd">The command to fill; its connection is not touched.</param>
        /// <param name="paths">The target paths, from <see cref="ResolveTargetPaths(string, IList{string})"/>.</param>
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
        public static JsonObject Find(DbConnection connection, string symbol, string kindFilter, int limit, bool aggregate = false) =>
            Find(connection, symbol, kindFilter, limit, aggregate, null);

        /// <inheritdoc cref="Find(DbConnection, string, string, int, bool)"/>
        /// <param name="connection">An open-able connection to the cross-reference database.</param>
        /// <param name="symbol">A name, <c>Owner.member</c>, an AOT path, or a label reference.</param>
        /// <param name="kindFilter">A reference kind, or null for every kind.</param>
        /// <param name="limit">Most rows.</param>
        /// <param name="aggregate">One row per (source, kind) instead of one per reference.</param>
        /// <param name="objectKinds">What the local index says a bare name is, or null.</param>
        public static JsonObject Find(DbConnection connection, string symbol, string kindFilter, int limit, bool aggregate, IList<string> objectKinds)
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

            var items = new JsonArray();
            TargetPaths paths;
            try
            {
                if (connection.State != System.Data.ConnectionState.Open) connection.Open();

                // A bare name the local index does not know is asked of the database itself before
                // falling back to the scanning "anywhere" match. A known kind is not widened.
                if (PathRootsFor(objectKinds).Count == 0 && IsBareName(symbol))
                {
                    var probed = ProbeKinds(connection, symbol.Trim().Trim('/'));
                    if (probed.Count > 0) objectKinds = probed;
                }
                paths = ResolveTargetPaths(symbol, objectKinds);

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

            var result = new JsonObject
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
                // How the target was matched: "exact" (a member, label or AOT path), "object" (a bare
                // name of a known kind, from the local index or probed in the database; seeks only)
                // or "anywhere" (the name as a node in any path, a scan).
                ["match"] = MatchOf(symbol, paths, objectKinds),
                ["items"] = items,
            };
            // The kinds a bare name was matched as: from the local index, else from the database.
            if ((string)result["match"] == "object")
                result["objectKinds"] = new JsonArray(objectKinds
                    .Where(k => k != null && KindRoots.ContainsKey(k)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(k => (JsonNode)k).ToArray());
            return result;
        }

        /// <summary>
        /// Whether the database holds any name for each of <paramref name="modules"/>. A module
        /// that was never compiled with <c>xppc -xref</c> has no rows, and every reference
        /// question about a symbol it declares then comes back empty for that reason alone.
        /// One existence probe per module, so it stays cheap on a database that carries the
        /// whole standard application.
        /// </summary>
        /// <returns>
        /// <c>{ ok:true, modules:[{module, hasRows}] }</c> in the order asked, or
        /// <c>{ ok:false, error:"XREFDB_UNAVAILABLE", message }</c>.
        /// </returns>
        public static JsonObject ModuleCoverage(DbConnection connection, IList<string> modules)
        {
            var items = new JsonArray();
            if (modules == null || modules.Count == 0)
                return new JsonObject { ["ok"] = true, ["modules"] = items };

            var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (connection.State != System.Data.ConnectionState.Open) connection.Open();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandTimeout = 30;
                    var inParams = new List<string>();
                    for (int i = 0; i < modules.Count; i++)
                    {
                        var pn = "@M" + i;
                        inParams.Add(pn);
                        AddParameter(cmd, pn, modules[i]);
                    }
                    cmd.CommandText = @"
SELECT m.Module,
       CASE WHEN EXISTS (SELECT 1 FROM Names n WHERE n.ModuleId = m.Id) THEN 1 ELSE 0 END AS HasRows
FROM Modules m
WHERE m.Module IN (" + string.Join(",", inParams) + ")";

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var name = r["Module"] as string;
                            if (name != null) found[name] = Convert.ToInt32(r["HasRows"]) == 1;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Fail("XREFDB_UNAVAILABLE", ex.GetType().Name + ": " + ex.Message);
            }

            foreach (var module in modules)
            {
                items.Add(new JsonObject
                {
                    ["module"] = module,
                    // A module the Modules table does not list has no rows either.
                    ["hasRows"] = found.TryGetValue(module, out var has) && has,
                });
            }
            return new JsonObject { ["ok"] = true, ["modules"] = items };
        }

        /// <summary>
        /// The path roots the compiler writes for each object kind: the plural form under <c>/</c>
        /// for code references, and the root-element form without a leading slash for metadata
        /// references. Read off 10.0.2790 cross-reference databases. The local index names the
        /// first five; the rest are found only by <see cref="ProbeKinds"/>.
        /// </summary>
        private static readonly Dictionary<string, string[]> KindRoots = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "table", new[] { "/Tables/", "Table/" } },
            { "class", new[] { "/Classes/", "Class/" } },
            { "form", new[] { "/Forms/", "Form/" } },
            { "enum", new[] { "/Enums/", "Enum/" } },
            { "edt", new[] { "/Edts/", "EdtString/", "EdtInt/", "EdtInt64/", "EdtReal/", "EdtDate/", "EdtUtcDateTime/",
                             "EdtEnum/", "EdtGuid/", "EdtContainer/", "EdtTime/" } },
            { "view", new[] { "/Views/", "View/" } },
            { "map", new[] { "/Maps/", "Map/" } },
            { "dataentity", new[] { "/DataEntityViews/", "DataEntityView/" } },
            { "query", new[] { "/Queries/", "QuerySimple/", "QueryComposite/" } },
        };

        /// <summary>
        /// The kinds <paramref name="name"/> is in the cross-reference database itself: one exact
        /// <c>IN</c> over every root in <see cref="KindRoots"/>, so it seeks <c>Names.Path</c>.
        /// Used when the local index does not know the name (an unindexed model such as the
        /// platform), which would otherwise fall back to the scanning "anywhere" match.
        /// </summary>
        /// <param name="connection">An open connection to the cross-reference database.</param>
        /// <param name="name">A bare object name.</param>
        /// <returns>The kinds found, in <see cref="KindRoots"/> order; empty when none.</returns>
        internal static List<string> ProbeKinds(DbConnection connection, string name)
        {
            var kinds = new List<string>();
            if (string.IsNullOrEmpty(name)) return kinds;

            var kindOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandTimeout = 30;
                var inParams = new List<string>();
                foreach (var entry in KindRoots)
                    foreach (var root in entry.Value)
                    {
                        var pn = "@R" + inParams.Count;
                        inParams.Add(pn);
                        AddParameter(cmd, pn, root + name);
                        kindOf[root + name] = entry.Key;
                    }
                cmd.CommandText = "SELECT Path FROM Names WHERE Path IN (" + string.Join(",", inParams) + ")";

                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (r[0] is string path && kindOf.TryGetValue(path, out var kind)) found.Add(kind);
                foreach (var kind in KindRoots.Keys)
                    if (found.Contains(kind)) kinds.Add(kind);
            }
            return kinds;
        }

        /// <summary>A bare object name: not a label, not an AOT path, not <c>Owner.member</c>.</summary>
        private static bool IsBareName(string symbol)
        {
            var s = (symbol ?? string.Empty).Trim();
            return s.Length > 0
                && TryGetLabelRef(s.Trim('/')) == null
                && !s.StartsWith("/", StringComparison.Ordinal)
                && !s.Contains(".");
        }

        /// <summary>The path roots for <paramref name="objectKinds"/>; empty when none is known.</summary>
        internal static List<string> PathRootsFor(IList<string> objectKinds)
        {
            var roots = new List<string>();
            if (objectKinds == null) return roots;
            foreach (var kind in objectKinds)
            {
                string[] r;
                if (kind != null && KindRoots.TryGetValue(kind, out r))
                    foreach (var root in r)
                        if (!roots.Contains(root)) roots.Add(root);
            }
            return roots;
        }

        private static string MatchOf(string symbol, TargetPaths paths, IList<string> objectKinds)
        {
            if (paths.Scoped || (symbol ?? string.Empty).TrimStart().StartsWith("/", StringComparison.Ordinal)) return "exact";
            return PathRootsFor(objectKinds).Count > 0 ? "object" : "anywhere";
        }

        /// <summary>
        /// The object a source path belongs to:  <c>/Classes/SalesLineType/Methods/insert</c> →
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

        /// <summary>
        /// Escape the T-SQL <c>LIKE</c> wildcards in a name, so it matches literally. X++ names are
        /// full of <c>_</c> (<c>SalesLine_IN</c>), which <c>LIKE</c> reads as "any one character".
        /// </summary>
        internal static string EscapeLike(string s) =>
            s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

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
