using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace D365FO.Core.Guardrails;

/// <summary>Condensed facts gathered by <c>prepare change</c> / <c>prepare create</c>.</summary>
public sealed record ProvenanceContext(
    string Goal,
    string ObjectName,
    string? MethodName = null,
    string? ObjectType = null,
    string? ProposedName = null);

public sealed record ProvenanceBundle(
    string Token,
    ProvenanceContext Context,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// File-backed provenance store for grounding tokens — adaptation of the
/// upstream MCP server's in-memory <c>provenanceStore</c> to the CLI's
/// multi-process reality (every CLI invocation is a fresh process).
///
/// A grounding token proves that the model queried the real D365FO codebase
/// (via <c>prepare change</c>/<c>prepare create</c>) before generating code.
/// Tokens are object-bound and expire after 30 minutes. When the environment
/// variable <c>D365FO_GROUNDING_ENFORCE=true</c>, generate commands for
/// extension-shaped objects require a valid token.
/// </summary>
public static class ProvenanceStore
{
    public const string EnforceEnvVar = "D365FO_GROUNDING_ENFORCE";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public static bool EnforcementEnabled =>
        D365FoSettings.ResolveFlag(EnforceEnvVar);

    /// <summary>Storage directory; override via D365FO_HOME for tests.</summary>
    internal static string StoreDirectory
    {
        get
        {
            var home = D365FoSettings.Resolve("D365FO_HOME");
            var root = string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".d365fo")
                : home;
            return Path.Combine(root, "provenance");
        }
    }

    public static string CreateToken(ProvenanceContext context)
    {
        var payload = $"{context.Goal}:{context.ObjectName}:{context.MethodName}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}";
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..32].ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var bundle = new ProvenanceBundle(token, context, now, now + Ttl);

        Directory.CreateDirectory(StoreDirectory);
        Prune();
        File.WriteAllText(TokenPath(token), JsonSerializer.Serialize(bundle, JsonOptions));
        return token;
    }

    public static ProvenanceBundle? TryGet(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var path = TokenPath(token.Trim());
        if (!File.Exists(path)) return null;
        try
        {
            var bundle = JsonSerializer.Deserialize<ProvenanceBundle>(File.ReadAllText(path), JsonOptions);
            if (bundle is null) return null;
            if (bundle.ExpiresUtc < DateTimeOffset.UtcNow)
            {
                TryDelete(path);
                return null;
            }
            return bundle;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validate a token for a write against <paramref name="objectName"/>.
    /// Tokens are object-bound: a token issued for CustTable does not authorize
    /// writes touching SalesTable.
    /// </summary>
    /// <remarks>
    /// The target must either BE the bound object, be an artefact DERIVED from it (D365FO names
    /// extensions and handlers by prefixing the base object — <c>CustTable_MyExt_Extension</c>,
    /// <c>CustTableEventHandler</c>), or match the name <c>prepare create</c> proposed.
    /// Deliberately a prefix test rather than a substring one in either direction: a plain
    /// "contains" match let a token issued for <c>Cust</c> authorize writes to every object with
    /// "Cust" anywhere in its name, and a token for <c>CustTable</c> authorize a write to the
    /// unrelated shorter <c>Cust</c> — which defeats the point of binding the token to an object.
    /// </remarks>
    public static (bool Ok, string Reason) Validate(string? token, string objectName)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, "No grounding token supplied. Run `d365fo prepare change`/`prepare create` first and pass --grounding-token.");
        var bundle = TryGet(token);
        if (bundle is null)
            return (false, "Grounding token not found or expired (30-min TTL). Re-run `d365fo prepare change`/`prepare create`.");
        var bound = bundle.Context.ObjectName;
        var proposed = bundle.Context.ProposedName;

        var authorized =
            string.Equals(objectName, bound, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(bound) && objectName.StartsWith(bound, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(proposed) && string.Equals(proposed, objectName, StringComparison.OrdinalIgnoreCase));

        if (!authorized)
        {
            return (false, $"Grounding token is bound to \"{bound}\" — it does not authorize writes to \"{objectName}\". " +
                           "Run `d365fo prepare change`/`prepare create` for this object.");
        }
        return (true, "");
    }

    private static string TokenPath(string token)
    {
        // Tokens are lowercase hex; reject anything else to keep the path safe.
        if (token.Any(c => !char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)))
            token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32].ToLowerInvariant();
        return Path.Combine(StoreDirectory, token + ".json");
    }

    private static void Prune()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - Ttl;
            foreach (var f in Directory.EnumerateFiles(StoreDirectory, "*.json"))
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff.UtcDateTime) TryDelete(f);
            }
        }
        catch { /* best-effort */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
