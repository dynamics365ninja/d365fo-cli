using System.Text.Json.Nodes;
using D365FO.Core.Bridge;

namespace D365FO.Cli.Commands.Get;

/// <summary>
/// Opt-in entry point for bridge-primary reads. When
/// <c>D365FO_BRIDGE_ENABLED=1</c> and the bridge spawns successfully, read
/// helpers here call the live <c>IMetadataProvider</c>-backed handlers and
/// return the deserialised payload. Any bridge failure / unavailability
/// returns null and the CLI falls back to the SQLite index.
/// </summary>
internal static class BridgeGate
{
    internal static bool ShouldTry()
    {
        var flag = System.Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");
        if (string.IsNullOrWhiteSpace(flag))
        {
            return false;
        }

        return flag == "1"
            || string.Equals(flag, "true", System.StringComparison.OrdinalIgnoreCase);
    }

    internal static object? TryReadClass(string name) => TryRead("readClass", name);
    internal static object? TryReadTable(string name) => TryRead("readTable", name);
    internal static object? TryReadEdt(string name) => TryRead("readEdt", name);
    internal static object? TryReadEnum(string name) => TryRead("readEnum", name);
    internal static object? TryReadForm(string name) => TryRead("readForm", name);

    /// <summary>
    /// Persist a raw Ax* XML blob into <paramref name="model"/> via the
    /// live metadata provider. Returns (true, null) on success, (false,
    /// message) on any failure — including bridge unavailability. Callers
    /// should surface the error back to the user because the generate
    /// command has no on-disk fallback for this operation.
    /// </summary>
    internal static (bool ok, string? error) TrySaveObject(string kind, string name, string model, string? xml)
    {
        if (!BridgeClient.IsAvailable())
        {
            return (false, "bridge is not available (set D365FO_BRIDGE_ENABLED=1 and D365FO_BRIDGE_PATH).");
        }

        try
        {
            var options = new BridgeOptions
            {
                MetadataBinPath = System.Environment.GetEnvironmentVariable("D365FO_BIN_PATH"),
            };
            using var client = new BridgeClient(options);
            var args = new JsonObject
            {
                ["kind"] = kind,
                ["name"] = name,
                ["model"] = model,
            };
            if (!string.IsNullOrEmpty(xml)) args["xml"] = xml;

            var result = client.SendAsync("createObject", args).GetAwaiter().GetResult();
            if (result is null) return (false, "bridge returned no result");

            var ok = (bool?)result["ok"] ?? false;
            if (!ok)
            {
                var err = (string?)result["error"] ?? "UNKNOWN";
                var msg = (string?)result["message"] ?? string.Empty;
                return (false, err + ": " + msg);
            }
            return (true, null);
        }
        catch (BridgeException ex)
        {
            return (false, "bridge error: " + ex.Message);
        }
    }

    /// <summary>
    /// Query the DYNAMICSXREFDB for reverse references via the bridge.
    /// Returns the raw bridge JSON (tag _source already included by the
    /// bridge) or null on any failure — callers fall back to the regex
    /// scanner.
    /// </summary>
    internal static JsonObject? TryFindReferences(string symbol, string? kind, int limit)
    {
        if (!BridgeClient.IsAvailable()) return null;
        try
        {
            var options = new BridgeOptions
            {
                MetadataBinPath = System.Environment.GetEnvironmentVariable("D365FO_BIN_PATH"),
            };
            using var client = new BridgeClient(options);
            var args = new JsonObject { ["symbol"] = symbol, ["limit"] = limit };
            if (!string.IsNullOrEmpty(kind)) args["kind"] = kind;
            var result = client.SendAsync("findReferences", args).GetAwaiter().GetResult();
            if (result is null) return null;
            var ok = (bool?)result["ok"] ?? false;
            if (!ok) return null;
            return result;
        }
        catch (BridgeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ask the bridge for the on-disk folder that owns <paramref name="model"/>
    /// (via <c>ModelManifest.GetFolderForModel</c>). Returns null on any
    /// failure — callers should surface a clear error to the user.
    /// </summary>
    internal static string? TryGetModelFolder(string model)
    {
        if (!BridgeClient.IsAvailable()) return null;
        try
        {
            var options = new BridgeOptions
            {
                MetadataBinPath = System.Environment.GetEnvironmentVariable("D365FO_BIN_PATH"),
            };
            using var client = new BridgeClient(options);
            var result = client.SendAsync("getModelFolder", new JsonObject { ["name"] = model })
                .GetAwaiter().GetResult();
            if (result is null) return null;
            var ok = (bool?)result["ok"] ?? false;
            if (!ok) return null;
            return (string?)result["folder"];
        }
        catch (BridgeException)
        {
            return null;
        }
    }

    private static object? TryRead(string method, string name)
    {
        if (!BridgeClient.IsAvailable())
        {
            return null;
        }

        try
        {
            var options = new BridgeOptions
            {
                MetadataBinPath = System.Environment.GetEnvironmentVariable("D365FO_BIN_PATH"),
            };
            using var client = new BridgeClient(options);
            var result = client.SendAsync(method, new JsonObject { ["name"] = name })
                .GetAwaiter()
                .GetResult();

            if (result is null)
            {
                return null;
            }

            // Bridge signals unavailability / not-found / serialisation errors
            // by returning { ok:false, error:..., message:... }. Treat any
            // ok==false as "bridge declined" → caller falls back to index.
            var ok = (bool?)result["ok"];
            if (ok == false)
            {
                return null;
            }

            // Unwrap the bridge envelope: the handler returns
            // { ok:true, kind, name, source, data: <payload> } — hand the
            // payload to the CLI and surface the provenance separately so
            // the final envelope is { ok:true, data: { _source:"bridge", ... } }.
            if (result["data"] is JsonNode payload)
            {
                if (payload is JsonObject payloadObj)
                {
                    // Honour a non-default source (e.g. "bridge-kernel" for
                    // fallbacks) if the handler set one, otherwise default.
                    payloadObj["_source"] = (string?)result["source"] ?? "bridge";
                    return payloadObj;
                }
                return payload;
            }

            return result;
        }
        catch (BridgeException)
        {
            return null;
        }
    }
}
