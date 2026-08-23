using System.Text.Json.Nodes;
using D365FO.Core;
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
    internal static bool ShouldTry() => D365FoSettings.ResolveFlag("D365FO_BRIDGE_ENABLED");

    /// <summary>
    /// Build bridge options from the unified config resolver so values set in
    /// settings.json (not just real env vars) reach the bridge child process.
    /// </summary>
    private static BridgeOptions DefaultOptions() => new()
    {
        MetadataBinPath = D365FoSettings.Resolve("D365FO_BIN_PATH"),
        PackagesPath = D365FoSettings.Resolve("D365FO_PACKAGES_PATH"),
        CustomPackagesPaths = D365FoSettings.FromEnvironment().CustomPackagesPaths,
        XrefConnectionString = D365FoSettings.Resolve("D365FO_XREF_CONNECTIONSTRING"),
    };

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
            var options = DefaultOptions();
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
    /// Update an existing Ax* object in <paramref name="model"/> via the live
    /// metadata provider (bridge <c>updateObject</c>). Unlike
    /// <see cref="TrySaveObject"/> (which creates), this overwrites an object
    /// that already exists — used by the form-method commands, which read the
    /// current form, inject a method, and push the whole modified XML back.
    /// Returns (true, null) on success, (false, message) on any failure.
    /// </summary>
    internal static (bool ok, string? error) TryUpdateObject(string kind, string name, string model, string xml)
    {
        if (!BridgeClient.IsAvailable())
        {
            return (false, "bridge is not available (set D365FO_BRIDGE_ENABLED=1 and D365FO_BRIDGE_PATH).");
        }

        try
        {
            var options = DefaultOptions();
            using var client = new BridgeClient(options);
            var args = new JsonObject
            {
                ["kind"] = kind,
                ["name"] = name,
                ["model"] = model,
                ["xml"] = xml,
            };

            var result = client.SendAsync("updateObject", args).GetAwaiter().GetResult();
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
    /// Delete an existing Ax* object in <paramref name="model"/> via the live metadata
    /// provider (bridge <c>deleteObject</c>). Used by <c>d365fo delete</c> and, in reverse,
    /// by <c>d365fo undo</c> to revert a bridge-mediated create (issue #113).
    /// Returns (true, null) on success, (false, message) on any failure.
    /// </summary>
    internal static (bool ok, string? error) TryDeleteObject(string kind, string name, string model)
    {
        if (!BridgeClient.IsAvailable())
        {
            return (false, "bridge is not available (set D365FO_BRIDGE_ENABLED=1 and D365FO_BRIDGE_PATH).");
        }

        try
        {
            var options = DefaultOptions();
            using var client = new BridgeClient(options);
            var args = new JsonObject
            {
                ["kind"] = kind,
                ["name"] = name,
                ["model"] = model,
            };

            var result = client.SendAsync("deleteObject", args).GetAwaiter().GetResult();
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
    /// Read a live Ax* object back as raw XML via the bridge's <c>readObjectXml</c> — used to
    /// capture an exact pre-image before a bridge delete so <c>d365fo undo</c> can recreate it
    /// (issue #113). Returns null on any failure, including bridge unavailability.
    /// </summary>
    internal static string? TryReadObjectXml(string kind, string name)
    {
        if (!BridgeClient.IsAvailable()) return null;
        try
        {
            var options = DefaultOptions();
            using var client = new BridgeClient(options);
            var result = client.SendAsync("readObjectXml", new JsonObject { ["kind"] = kind, ["name"] = name })
                .GetAwaiter().GetResult();
            if (result is null) return null;
            var ok = (bool?)result["ok"] ?? false;
            if (!ok) return null;
            return (string?)result["xml"];
        }
        catch (BridgeException)
        {
            return null;
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
            var options = DefaultOptions();
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
            var options = DefaultOptions();
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

    /// <summary>
    /// Verdict from the bridge's <c>validateArtifact</c>: did the XML deserialize into its
    /// MetaModel type, and did anything vanish on the way back out.
    /// </summary>
    /// <param name="Deserialized">The provider's serializer accepted the document.</param>
    /// <param name="Valid">It deserialized <em>and</em> the round-trip lost nothing.</param>
    /// <param name="Dropped">Leaf elements (path = value) present in the input and gone after the round-trip.</param>
    internal sealed record MetadataVerdict(
        bool Deserialized,
        bool Valid,
        string? RootElement,
        string? ClrType,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<string> Dropped);

    /// <summary>
    /// Ask the live metadata provider whether it can read this XML, without writing
    /// anything. Returns null when the bridge is unavailable — the caller decides whether
    /// that is a skip or a failure.
    /// </summary>
    internal static MetadataVerdict? TryValidateArtifact(string? kind, string xml)
    {
        if (!BridgeClient.IsAvailable()) return null;
        try
        {
            var options = DefaultOptions();
            using var client = new BridgeClient(options);
            var args = new JsonObject { ["xml"] = xml };
            if (!string.IsNullOrWhiteSpace(kind)) args["kind"] = kind;

            var result = client.SendAsync("validateArtifact", args).GetAwaiter().GetResult();
            if (result is null) return null;
            if (((bool?)result["ok"] ?? false) == false) return null;

            var dropped = new List<string>();
            if (result["dropped"] is JsonArray arr)
                foreach (var item in arr)
                    if ((string?)item is { } s) dropped.Add(s);

            return new MetadataVerdict(
                Deserialized: (bool?)result["deserialized"] ?? false,
                Valid: (bool?)result["valid"] ?? false,
                RootElement: (string?)result["rootElement"],
                ClrType: (string?)result["clrType"],
                ErrorCode: (string?)result["errorCode"],
                ErrorMessage: (string?)result["errorMessage"],
                Dropped: dropped);
        }
        catch (BridgeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Outcome of an opt-in post-write verification. <c>Skipped</c> means the
    /// Metadata API runtime is not available at all — generation must keep working
    /// offline, so this is never an error.
    /// </summary>
    internal enum VerifyOutcome { Skipped, Readable, Unreadable }

    /// <summary>
    /// Read a just-written artefact back through the live metadata provider — the
    /// same path Visual Studio takes when it opens the file. Purely a check: nothing
    /// is written and the artefact is left alone either way.
    /// </summary>
    /// <param name="axKind">Bridge collection kind (class | table | edt | enum | form | view | map | query | dataEntityView | *Extension).</param>
    /// <returns>
    /// <see cref="VerifyOutcome.Skipped"/> when nothing could be asked — the bridge is
    /// unavailable, did not answer, or has no read channel for the kind;
    /// <see cref="VerifyOutcome.Readable"/> when the provider returned the object;
    /// <see cref="VerifyOutcome.Unreadable"/> when the provider was reachable and still
    /// could not load it. <c>detail</c> carries the reason for the first and last, and a
    /// caveat for the one <see cref="VerifyOutcome.Readable"/> case that is not clean.
    /// </returns>
    /// <remarks>
    /// The bridge's own error code decides, rather than "did a read come back": every
    /// failure used to collapse to <see cref="VerifyOutcome.Unreadable"/>, which reads as
    /// "the metadata reader refuses your file" and fails the command. Most of the codes
    /// mean nothing of the sort — a bridge timeout, a kind with no read channel, or a
    /// MetaModel type the bridge's <c>XmlSerializer</c> cannot reflect are all limits of
    /// the tooling, and blaming a perfectly good artefact for them is worse than not
    /// checking at all.
    /// </remarks>
    internal static (VerifyOutcome outcome, string? detail) TryVerifyObject(string axKind, string name)
    {
        // The typed read verbs exist only for the five original kinds. Everything the
        // bridge can now write (views, maps, queries, and the *Extension kinds) is
        // verified through the generic readObjectXml instead, so `--verify` covers the
        // whole write surface rather than the subset that predates it.
        var method = axKind?.ToLowerInvariant() switch
        {
            "class" => "readClass",
            "table" => "readTable",
            "edt"   => "readEdt",
            "enum"  => "readEnum",
            "form"  => "readForm",
            _       => null,
        };

        // Same availability check the read/write helpers use — no second notion of
        // "is the runtime here".
        if (!BridgeClient.IsAvailable())
            return (VerifyOutcome.Skipped, "the D365FO Metadata API runtime is not available.");

        var response = method is not null
            ? SendRaw(method, new JsonObject { ["name"] = name })
            : SendRaw("readObjectXml", new JsonObject { ["kind"] = axKind ?? "", ["name"] = name });

        return VerdictFrom(axKind, response);
    }

    /// <summary>
    /// Turn one bridge read response into a verification verdict. Split out from
    /// <see cref="TryVerifyObject"/> so the mapping — the part that decides whether a
    /// command fails — is testable without a live AOS.
    /// </summary>
    /// <param name="response">The bridge envelope, or null when the bridge did not answer.</param>
    internal static (VerifyOutcome outcome, string? detail) VerdictFrom(string? axKind, JsonObject? response)
    {
        // No answer at all — a timeout or a child process that died says nothing about
        // the artefact, so it is a skip and never a verdict against the file.
        if (response is null)
            return (VerifyOutcome.Skipped, "the metadata bridge did not answer, so nothing was read back.");

        if ((bool?)response["ok"] == true)
            return (VerifyOutcome.Readable, null);

        var code = (string?)response["error"];
        var message = (string?)response["message"];

        return code switch
        {
            // IMetadataProvider handed the artefact back; only the bridge's own
            // XmlSerializer could not reflect that MetaModel type (AxMenuItemAction and
            // AxSecurityPrivilege both fail this way on a live AOS). The load — which is
            // what --verify asks about — succeeded.
            "SERIALIZE_FAILED" => (VerifyOutcome.Readable,
                "the provider loaded it, though the bridge could not render it back as XML"),

            "INVALID_KIND" => (VerifyOutcome.Skipped,
                $"the metadata bridge has no read channel for '{axKind}'."),

            "METADATA_UNAVAILABLE" => (VerifyOutcome.Skipped,
                message ?? "the D365FO Metadata API runtime is not available."),

            _ => (VerifyOutcome.Unreadable,
                "the metadata provider is reachable but could not load the object back" +
                (string.IsNullOrWhiteSpace(message) ? "." : $" ({message}).")),
        };
    }

    /// <summary>
    /// One bridge round-trip, envelope and all. Null only when the bridge could not be
    /// reached — a <c>{ok:false}</c> answer is returned as-is so the caller can tell the
    /// bridge's error codes apart.
    /// </summary>
    private static JsonObject? SendRaw(string method, JsonObject args)
    {
        if (!BridgeClient.IsAvailable()) return null;
        try
        {
            var options = DefaultOptions();
            using var client = new BridgeClient(options);
            return client.SendAsync(method, args).GetAwaiter().GetResult();
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
            var options = DefaultOptions();
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
