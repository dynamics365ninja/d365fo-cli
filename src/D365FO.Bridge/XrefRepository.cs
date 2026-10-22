// <copyright file="XrefRepository.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
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
        internal static string ConnectionString
        {
            get
            {
                var cs = Environment.GetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING");
                if (!string.IsNullOrWhiteSpace(cs)) return cs;
                return D365FO.Core.Xref.XrefQuery.DefaultConnectionString;
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

        /// <summary>
        /// The where-used query, shared with the CLI's direct path (<see cref="D365FO.Core.Xref.XrefQuery"/>)
        /// so the two cannot answer the same symbol differently.
        /// </summary>
        internal static JsonObject Find(string symbol, string kindFilter, int limit)
        {
            using (var c = new SqlConnection(ConnectionString))
            {
                return D365FO.Core.Xref.XrefQuery.Find(c, symbol, kindFilter, limit);
            }
        }
    }
}
