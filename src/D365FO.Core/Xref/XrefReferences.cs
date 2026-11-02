using System.Text.Json.Nodes;
using D365FO.Core.Bridge;
using Microsoft.Data.SqlClient;

namespace D365FO.Core.Xref;

/// <summary>
/// One source of xref rows: the raw <see cref="XrefQuery.Find"/> payload, or null with the
/// reason it could not be queried.
/// </summary>
internal delegate (JsonObject? Result, string? Error) XrefSource(string symbol, string? kind, int limit, bool aggregate);

/// <summary>
/// The compiler's where-used, answered from <c>DYNAMICSXREFDB</c> — shared by
/// <c>find refs --xref</c> and the MCP <c>find_references</c> tool with <c>xref: true</c>.
/// </summary>
/// <remarks>
/// <para>
/// The database is queried directly. It used to be reachable only through the metadata
/// bridge — a net48 child process behind <c>D365FO_BRIDGE_ENABLED=1</c> — which put the
/// cheapest, most complete answer to "who uses X" behind the most setup. The bridge is now the
/// fallback when the direct query fails; both run the same <see cref="XrefQuery"/>.
/// </para>
/// <para>
/// The default answer is one row per referencing object. The raw rows (one per reference, with
/// line and column) are what a caller needs to jump to code, not to answer "who uses X", and
/// cost several times the tokens; <c>lines &gt; 0</c> asks for them.
/// </para>
/// </remarks>
public static class XrefReferences
{
    /// <summary>
    /// (source path, kind) rows read per object asked for, in the names-only answer — an object
    /// contributes one per member and kind that references the symbol.
    /// </summary>
    private const int RowsPerObject = 20;

    /// <summary>Ceiling on raw rows read for a names-only answer.</summary>
    private const int MaxRows = 20000;

    /// <summary>
    /// The connection string: <c>D365FO_XREF_CONNECTIONSTRING</c>, else local SQL with integrated
    /// auth. <c>Encrypt</c> defaults to <c>Optional</c> when the string does not set it.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Data.SqlClient</c> encrypts and validates the server certificate by default,
    /// which fails against the self-signed certificate of a dev VM's local SQL Server. The
    /// bridge's <c>System.Data.SqlClient</c> never did, so the same connection string worked
    /// there and not here. <c>Optional</c> restores that default; a string that sets
    /// <c>Encrypt</c> keeps its own choice.
    /// </remarks>
    public static string ConnectionString() =>
        Normalize(D365FoSettings.Resolve("D365FO_XREF_CONNECTIONSTRING") ?? XrefQuery.DefaultConnectionString);

    internal static string Normalize(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.ShouldSerialize("Encrypt")) builder.Encrypt = SqlConnectionEncryptOption.Optional;
        return builder.ConnectionString;
    }

    /// <summary>Where-used for <paramref name="symbol"/> from the compiler's cross-reference.</summary>
    /// <param name="symbol">A name, <c>Owner.member</c>, an AOT path, or a label reference.</param>
    /// <param name="kind">A reference kind (<c>Call</c>, <c>Read</c>, …), or null.</param>
    /// <param name="model">Must be null: the database is keyed by module, not model.</param>
    /// <param name="limit">Most objects (names-only) or references (<paramref name="lines"/> &gt; 0).</param>
    /// <param name="lines">0 for one row per referencing object; above 0 for one row per reference.</param>
    /// <param name="ownerOf">
    /// The model and module that declare a symbol (<see cref="XrefCoverage.OwnerOf"/> over the
    /// local index), or null to skip the coverage check.
    /// </param>
    public static ToolResult<object> Find(
        string symbol, string? kind, string? model, int limit, int lines,
        Func<string, XrefCoverage.Owner?>? ownerOf = null) =>
        Find(symbol, kind, model, limit, lines, QueryDirect,
            // The bridge speaks the per-reference shape only; Group folds either.
            BridgeGate.ShouldTry() ? (s, k, l, _) => BridgeGate.FindReferencesViaXref(s, k, l) : null,
            ownerOf, CoverageDirectThenBridge);

    internal static ToolResult<object> Find(
        string symbol, string? kind, string? model, int limit, int lines,
        XrefSource direct, XrefSource? bridge,
        Func<string, XrefCoverage.Owner?>? ownerOf = null,
        Func<string, (bool? HasRows, string? Error)>? moduleHasRows = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "A symbol is required.");

        // The bridge query takes no model either — silently dropping it would widen the answer
        // to every module.
        if (!string.IsNullOrWhiteSpace(model))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "A model filter cannot be combined with the xref query: DYNAMICSXREFDB is keyed by module, not model.",
                "Drop the model filter — every xref row carries its `module` — or use the text scan, which honours it.");

        // To the text scan a kind is an artifact kind; here it is a reference kind, and
        // `class` would filter on one that does not exist and return an empty list.
        string? refKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var id = XrefQuery.KindId(kind);
            if (id < 0)
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"'{kind}' is not a reference kind. With the xref query, the kind filters on how the symbol is used.",
                    "Use one of: " + string.Join(" | ", XrefQuery.ReferenceKinds)
                    + ". Every xref row's `source` path names the referencing artifact.");
            refKind = XrefQuery.ReferenceKinds[id];
        }

        if (limit <= 0) limit = 200;
        var rowLimit = lines > 0 ? limit : (int)Math.Min((long)limit * RowsPerObject, MaxRows);

        var aggregate = lines <= 0;
        var (raw, directError) = direct(symbol, refKind, rowLimit, aggregate);
        var source = "xrefdb";
        string? bridgeError = null;

        // The bridge reads the same D365FO_XREF_CONNECTIONSTRING. When the server itself could not
        // be reached it can only wait out the same connect timeout again, and so can the coverage
        // probe below: a stopped LocalDB cost three timeouts, about 74 s, before the error.
        var unreachable = directError?.StartsWith(ServerUnreachable, StringComparison.Ordinal) == true;
        if (raw is null && bridge is not null && !unreachable)
        {
            (raw, bridgeError) = bridge(symbol, refKind, rowLimit, aggregate);
            source = "xrefdb-bridge";
        }

        // Whether the database covers the module that declares the symbol, asked on both
        // outcomes: an empty answer from a module never compiled with cross-references is not
        // "no references", and a failed query over such a module has a precise cause worth naming.
        var owner = SafeOwnerOf(ownerOf, symbol);
        bool? hasRows = null;
        if (!unreachable && owner?.Module is { } module && moduleHasRows is not null) (hasRows, _) = moduleHasRows(module);

        if (raw is null)
        {
            var queryError = Display(directError) + (bridgeError is null ? "" : " Bridge fallback: " + bridgeError)
                + (unreachable && bridge is not null ? " (Bridge fallback skipped: it reads the same server.)" : "");
            if (XrefCoverage.Assess(0, symbol, owner, hasRows).Failure is { Error: { } pe })
                return ToolResult<object>.Fail(pe.Code, pe.Message + " (The reference query itself failed: " + queryError + ")", pe.Hint);

            return ToolResult<object>.Fail(D365FoErrorCodes.XrefUnavailable,
                "DYNAMICSXREFDB could not be queried: " + queryError,
                (unreachable ? LocalDbHint(ConnectionStringOrNull()) : null)
                ?? "Check D365FO_XREF_CONNECTIONSTRING (default: local SQL, integrated auth) and that the "
                + "database exists — it is filled by a build with cross-references enabled "
                + "(`d365fo build --model <Model> --xref`). Or use the text "
                + "scan over indexed X++ source: it is not compiler-resolved and can miss references made "
                + "through variables and aliases.");
        }

        var rawCount = (raw["items"] as JsonArray)?.Count ?? (int?)raw["count"] ?? 0;
        var verdict = XrefCoverage.Assess(rawCount, symbol, owner, hasRows);
        if (verdict.Failure is not null) return verdict.Failure;
        IReadOnlyList<string>? warnings = verdict.Warning is null ? null : [verdict.Warning];

        raw.Remove("ok");
        raw.Remove("source");
        raw["_source"] = source;
        if (lines <= 0) return ToolResult<object>.Success(Group(raw, limit), warnings);

        // Provenance and shape ahead of the rows: a caller that reads the head of a long answer
        // must not miss which store answered.
        var rows = new JsonObject { ["_source"] = source, ["mode"] = "references" };
        foreach (var key in new[] { "symbol", "kindFilter", "scoped", "count", "truncated", "items" })
        {
            if (raw[key] is not { } value) continue;
            raw.Remove(key);
            rows[key] = value;
        }
        return ToolResult<object>.Success(rows, warnings);
    }

    private static XrefCoverage.Owner? SafeOwnerOf(Func<string, XrefCoverage.Owner?>? ownerOf, string symbol)
    {
        if (ownerOf is null) return null;
        try { return ownerOf(symbol); }
        catch { return null; } // no index: the coverage check is skipped, never the query
    }

    /// <summary>
    /// Whether DYNAMICSXREFDB holds any row for <paramref name="module"/>: asked over the same
    /// direct connection as the query, with the bridge's <c>xrefCoverage</c> as the fallback.
    /// </summary>
    private static (bool? HasRows, string? Error) CoverageDirectThenBridge(string module)
    {
        string? error;
        try
        {
            using var connection = new SqlConnection(ConnectionString());
            var answer = BridgeGate.ReadCoverage(XrefQuery.ModuleCoverage(connection, [module]), module);
            if (answer.HasRows is not null) return answer;
            error = answer.Error;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        return BridgeGate.ShouldTry() ? BridgeGate.XrefModuleHasRows(module) : (null, error);
    }

    /// <summary>
    /// Fold the raw rows into one row per referencing object: its module, how it uses the
    /// symbol, and from which members.
    /// </summary>
    internal static JsonObject Group(JsonObject raw, int limit)
    {
        var groups = new List<(string Source, string Module, List<string> Kinds, List<string> Members, int Refs)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = raw["items"] as JsonArray ?? new JsonArray();

        foreach (var row in rows.OfType<JsonObject>())
        {
            var obj = XrefQuery.ObjectOf((string?)row["source"] ?? "", out var member);
            if (!index.TryGetValue(obj, out var i))
            {
                i = groups.Count;
                index[obj] = i;
                groups.Add((obj, (string?)row["module"] ?? "", new List<string>(), new List<string>(), 0));
            }
            var g = groups[i];
            var kind = (string?)row["kind"];
            if (kind is not null && !g.Kinds.Contains(kind)) g.Kinds.Add(kind);
            if (member is not null && !g.Members.Contains(member)) g.Members.Add(member);
            groups[i] = g with { Refs = g.Refs + ((int?)row["refs"] ?? 1) };
        }

        var items = new JsonArray();
        foreach (var g in groups.Take(limit))
        {
            var item = new JsonObject
            {
                ["source"] = g.Source,
                ["module"] = g.Module,
                ["kinds"] = new JsonArray(g.Kinds.Select(k => (JsonNode)k).ToArray()),
                ["refs"] = g.Refs,
            };
            if (g.Members.Count > 0)
                item["members"] = new JsonArray(g.Members.Select(m => (JsonNode)m).ToArray());
            items.Add(item);
        }

        var result = new JsonObject
        {
            ["_source"] = (string?)raw["_source"],
            ["symbol"] = (string?)raw["symbol"],
            ["scoped"] = (bool?)raw["scoped"] ?? false,
            ["mode"] = "names",
            ["count"] = items.Count,
            ["references"] = groups.Sum(g => g.Refs),
            // Cut off by the object cap, or the row read stopped before the last object.
            ["truncated"] = groups.Count > limit || ((bool?)raw["truncated"] ?? false),
            ["items"] = items,
        };
        var kindFilter = (string?)raw["kindFilter"];
        if (!string.IsNullOrEmpty(kindFilter)) result["kindFilter"] = kindFilter;
        return result;
    }

    /// <summary>
    /// Prefix of a direct-query error that means the server itself was not reached (not found,
    /// not started, connect timeout), as opposed to a login, certificate or query failure, which
    /// the bridge's other SQL client can still get past.
    /// </summary>
    internal const string ServerUnreachable = "SERVER_UNREACHABLE: ";

    /// <summary>
    /// Whether a failed <c>Open()</c> means the server was never reached. By number: -2 connect
    /// timeout, -1 and 2 network or instance error, 26 instance not located, 40 and 53 no
    /// connection or network path, 258 wait timeout. By severity: the client raises the rest of
    /// its transport failures at class 20, LocalDB's among them, with HRESULT-like numbers
    /// (<c>-1983577849</c>, "the specified LocalDB instance does not exist"). A class-20 failure
    /// after the connection was established (the TLS pre-login handshake, a certificate) is
    /// answered by a server, and the bridge's other SQL client can get past it.
    /// </summary>
    internal static bool IsUnreachable(int number, byte severity, string? message) =>
        number is -2 or -1 or 2 or 26 or 40 or 53 or 258
        || (severity >= 20 && message?.Contains("successfully established", StringComparison.OrdinalIgnoreCase) != true);

    /// <summary>The direct-query error without the <see cref="ServerUnreachable"/> marker, for display.</summary>
    private static string? Display(string? error) =>
        error is not null && error.StartsWith(ServerUnreachable, StringComparison.Ordinal) ? error[ServerUnreachable.Length..] : error;

    /// <summary>
    /// A hint for a LocalDB instance that did not answer, or null when the connection string
    /// names another server. LocalDB starts on the first connection, but an instance whose
    /// process is left behind reports "Stopped" and refuses <c>SqlLocalDB start</c> until it is
    /// stopped explicitly.
    /// </summary>
    internal static string? LocalDbHint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        string dataSource;
        try { dataSource = new SqlConnectionStringBuilder(connectionString).DataSource ?? ""; }
        catch { return null; }

        const string prefix = "(localdb)\\";
        if (!dataSource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var instance = dataSource[prefix.Length..];
        return $"The LocalDB instance '{instance}' did not answer. `SqlLocalDB info {instance}` shows its state; a "
            + "stopped instance normally starts on the first connection. If it stays 'Stopped' and "
            + $"`SqlLocalDB start {instance}` fails with \"SQL Server process failed to start\", a process of the "
            + $"previous run is still holding it: `SqlLocalDB stop {instance}`, then ask again.";
    }

    private static string? ConnectionStringOrNull()
    {
        try { return ConnectionString(); }
        catch { return null; }
    }

    private static (JsonObject? Result, string? Error) QueryDirect(string symbol, string? kind, int limit, bool aggregate)
    {
        try
        {
            using var connection = new SqlConnection(ConnectionString());
            try
            {
                connection.Open();
            }
            catch (SqlException ex) when (IsUnreachable(ex.Number, ex.Class, ex.Message))
            {
                return (null, ServerUnreachable + ex.GetType().Name + ": " + ex.Message);
            }
            var result = XrefQuery.Find(connection, symbol, kind, limit, aggregate);
            if ((bool?)result["ok"] == true) return (result, null);
            return (null, (string?)result["error"] + ": " + (string?)result["message"]);
        }
        catch (Exception ex)
        {
            // A malformed connection string throws from the constructor, before the query runs.
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
