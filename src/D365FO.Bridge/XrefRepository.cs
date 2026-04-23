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

            // Path looks like /Classes/<Name>[/<Element>/<Child>]. Match the
            // target symbol both as a standalone AOT root (e.g. /Tables/CustTable)
            // and as a node anywhere inside a path ( .../CustTable/... ).
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
WHERE tgtName.Path = @exact
   OR tgtName.Path LIKE @prefix
   OR tgtName.Path LIKE @contains
ORDER BY srcName.Path";
                        cmd.CommandText = sql;
                        cmd.Parameters.Add(new SqlParameter("@limit", limit));
                        // Exact AOT root (/Tables/CustTable) — cheap and
                        // typically returns the bulk of direct references.
                        cmd.Parameters.Add(new SqlParameter("@exact", "/" + TrimSlash(symbol)));
                        cmd.Parameters.Add(new SqlParameter("@prefix", "/" + TrimSlash(symbol) + "/%"));
                        cmd.Parameters.Add(new SqlParameter("@contains", "%/" + TrimSlash(symbol) + "%"));

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
            result["items"] = items;
            return result;
        }

        private static string TrimSlash(string s)
        {
            if (s == null) return string.Empty;
            return s.Trim('/');
        }
    }
}
