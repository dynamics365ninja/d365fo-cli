// <copyright file="BridgeClient.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace D365FO.Core.Bridge;

/// <summary>
/// Options that locate and configure the D365FO.Bridge child process.
/// </summary>
public sealed record BridgeOptions
{
    /// <summary>
    /// Absolute path to <c>D365FO.Bridge.exe</c>. Resolved from
    /// <c>D365FO_BRIDGE_PATH</c> env var when null.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Directory containing D365FO metadata assemblies
    /// (<c>Microsoft.Dynamics.AX.Metadata.*.dll</c>). Forwarded to the bridge
    /// via <c>D365FO_BIN_PATH</c>.
    /// </summary>
    public string? MetadataBinPath { get; init; }

    /// <summary>
    /// PackagesLocalDirectory root. Forwarded to the bridge via
    /// <c>D365FO_PACKAGES_PATH</c> so that a value configured only in
    /// settings.json still reaches the child process.
    /// </summary>
    public string? PackagesPath { get; init; }

    /// <summary>
    /// Additional custom-model roots (e.g. a git repo of custom models on a
    /// UDE setup). Forwarded to the bridge via <c>D365FO_CUSTOM_PACKAGES_PATH</c>
    /// so the metadata provider indexes them too — without this, models that
    /// live only under a custom root are invisible to <c>--install-to</c>.
    /// </summary>
    public IReadOnlyList<string> CustomPackagesPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// DYNAMICSXREFDB connection string. Forwarded to the bridge via
    /// <c>D365FO_XREF_CONNECTIONSTRING</c> when set.
    /// </summary>
    public string? XrefConnectionString { get; init; }

    /// <summary>
    /// Per-request timeout. Defaults to 10 s to match upstream.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When true, <see cref="BridgeClient"/> resolves the executable path but
    /// does not spawn — caller supplies the stdio streams. Used by tests.
    /// </summary>
    public bool UseInProcessStreams { get; init; }

    /// <summary>
    /// Resolve bridge executable path from env/default. Returns null if not
    /// found — caller should fall back to the SQLite index.
    /// </summary>
    public static string? ResolveExecutable(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var env = D365FoSettings.Resolve("D365FO_BRIDGE_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        // Common layouts: alongside D365FO.Cli.dll or one dir up.
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "D365FO.Bridge.exe"),
                     Path.Combine(baseDir, "..", "D365FO.Bridge", "D365FO.Bridge.exe"),
                 })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }
}

/// <summary>
/// JSON-RPC 2.0 client for <c>D365FO.Bridge.exe</c>. Spawns the child process
/// lazily on first use and reuses it across calls. Thread-safe via an internal
/// lock — bridge protocol is strictly request/response, one at a time.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private readonly BridgeOptions options;
    private readonly object gate = new();
    private Process? process;
    private TextWriter? writer;
    private TextReader? reader;
    private int nextId;
    private bool disposed;

    /// <summary>
    /// Create a client that spawns <c>D365FO.Bridge.exe</c> on first call.
    /// </summary>
    public BridgeClient(BridgeOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Test-only constructor that wraps an existing stdio pair instead of
    /// spawning a process. <paramref name="writer"/> is the bridge's stdin
    /// (from the client's point of view: we write requests here) and
    /// <paramref name="reader"/> is the bridge's stdout.
    /// </summary>
    public BridgeClient(TextWriter writer, TextReader reader)
    {
        this.options = new BridgeOptions { UseInProcessStreams = true };
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>
    /// Returns true when the bridge is available on this machine. Does not
    /// spawn — just checks path resolution.
    /// </summary>
    public static bool IsAvailable(BridgeOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return BridgeOptions.ResolveExecutable(options?.ExecutablePath) is not null;
    }

    /// <summary>
    /// Send a <c>ping</c> request. Used to probe availability + warm the
    /// child process. Returns null when the bridge cannot be reached.
    /// </summary>
    public async Task<JsonObject?> PingAsync(CancellationToken cancel = default)
    {
        return await SendAsync("ping", new JsonObject(), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Invoke a bridge method. Throws <see cref="BridgeException"/> on RPC
    /// error or timeout. Returns null when bridge is unavailable.
    /// </summary>
    public async Task<JsonObject?> SendAsync(string method, JsonObject parameters, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("method is required", nameof(method));
        }

        EnsureStarted();
        if (writer is null || reader is null)
        {
            return null;
        }

        var id = Interlocked.Increment(ref nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };
        var line = request.ToJsonString();

        string? responseLine;
        lock (gate)
        {
            writer.WriteLine(line);
            writer.Flush();

            // ReadLine is blocking; run on a thread-pool thread and
            // enforce the configured timeout via Task.WhenAny.
            var readTask = Task.Run(() => reader.ReadLine(), CancellationToken.None);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(options.RequestTimeout);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            var completed = Task.WhenAny(readTask, timeoutTask).GetAwaiter().GetResult();
            if (completed != readTask)
            {
                // The abandoned ReadLine is still parked on the pipe and will consume
                // whatever the bridge eventually writes. Leaving the process alive would
                // hand that late reply to the NEXT request, silently shifting every
                // subsequent response by one. Tear the child down instead so the next
                // call starts a clean bridge.
                cancel.ThrowIfCancellationRequested();
                DiscardProcess();
                throw new BridgeException(
                    $"Bridge did not respond within {options.RequestTimeout.TotalSeconds:F0}s (method: {method}). " +
                    "The bridge process was restarted; retry the call.");
            }

            responseLine = readTask.GetAwaiter().GetResult();
        }

        if (responseLine is null)
        {
            throw new BridgeException("Bridge closed the stream before replying.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(responseLine);
        }
        catch (JsonException ex)
        {
            throw new BridgeException("Bridge returned non-JSON: " + ex.Message);
        }

        if (parsed is not JsonObject response)
        {
            throw new BridgeException("Bridge returned a non-object response.");
        }

        // Correlate the reply with the request. The protocol is strictly one
        // request/response at a time, so a mismatched id means the stream has
        // drifted out of step and every later reply would be misattributed —
        // surfacing stale data as if it answered the current call.
        var responseId = (int?)response["id"];
        if (responseId is not null && responseId != id)
        {
            DiscardProcess();
            throw new BridgeException(
                $"Bridge replied to request {responseId} while {id} was pending (method: {method}). " +
                "The stream was out of sync; the bridge process was restarted.");
        }

        if (response["error"] is JsonObject err)
        {
            var code = (int?)err["code"] ?? -1;
            var message = (string?)err["message"] ?? "(no message)";
            throw new BridgeException($"Bridge error {code}: {message}");
        }

        return response["result"] as JsonObject;
    }

    /// <summary>
    /// Build the set of environment variables forwarded to the bridge child
    /// process from <paramref name="options"/>. Only non-empty values are
    /// emitted; <c>CustomPackagesPaths</c> is joined with <c>;</c> so the bridge
    /// can re-split it. Exposed internally so tests can assert the contract
    /// without spawning a real process.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> BuildProcessEnvironment(BridgeOptions options)
    {
        var env = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(options.MetadataBinPath))
            env.Add(new("D365FO_BIN_PATH", options.MetadataBinPath!));
        if (!string.IsNullOrWhiteSpace(options.PackagesPath))
            env.Add(new("D365FO_PACKAGES_PATH", options.PackagesPath!));
        if (options.CustomPackagesPaths.Count > 0)
            env.Add(new("D365FO_CUSTOM_PACKAGES_PATH", string.Join(";", options.CustomPackagesPaths)));
        if (!string.IsNullOrWhiteSpace(options.XrefConnectionString))
            env.Add(new("D365FO_XREF_CONNECTIONSTRING", options.XrefConnectionString!));
        return env;
    }

    /// <summary>
    /// Kill and forget the current child process so the next <see cref="SendAsync"/> spawns a
    /// fresh one. Called when the stdio stream can no longer be trusted to be in step with the
    /// request sequence (timeout, mismatched response id). No-op in test-stream mode, where the
    /// caller owns the streams and there is no process to replace.
    /// </summary>
    private void DiscardProcess()
    {
        if (options.UseInProcessStreams) return;

        var dead = process;
        process = null;
        writer = null;
        reader = null;

        if (dead is null) return;
        try
        {
            if (!dead.HasExited) dead.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may have exited on its own between the check and the kill.
        }
        finally
        {
            dead.Dispose();
        }
    }

    private void EnsureStarted()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(BridgeClient));
        }

        if (writer is not null && reader is not null)
        {
            return;
        }

        // Synchronize startup so concurrent callers don't spawn duplicates.
        lock (gate)
        {
            // Double-check after acquiring the lock.
            if (writer is not null && reader is not null)
            {
                return;
            }

            if (options.UseInProcessStreams)
            {
                // Test mode — streams must have been supplied via ctor.
                return;
            }

            var exe = BridgeOptions.ResolveExecutable(options.ExecutablePath);
            if (exe is null)
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var (key, value) in BuildProcessEnvironment(options))
            {
                psi.Environment[key] = value;
            }

            process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            // Drain stderr asynchronously to prevent child process deadlock
            // when the OS pipe buffer fills up.
            process.ErrorDataReceived += (_, e) => { /* discard stderr output */ };
            process.BeginErrorReadLine();

            writer = process.StandardInput;
            reader = process.StandardOutput;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (process is { HasExited: false })
            {
                try
                {
                    writer?.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"shutdown\"}");
                    writer?.Flush();
                }
                catch
                {
                    // best-effort
                }

                if (!process.WaitForExit(2000))
                {
                    process.Kill();
                }
            }
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            if (!options.UseInProcessStreams)
            {
                writer?.Dispose();
                reader?.Dispose();
            }

            process?.Dispose();
        }
    }
}

/// <summary>
/// Raised when the bridge returns a JSON-RPC error or closes unexpectedly.
/// </summary>
public sealed class BridgeException : Exception
{
    /// <summary>Create a bridge exception with a message.</summary>
    public BridgeException(string message)
        : base(message)
    {
    }
}
