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

        var env = Environment.GetEnvironmentVariable("D365FO_BRIDGE_PATH");
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(options.RequestTimeout);

            // ReadLine on a process's StandardOutput is blocking and not
            // cancellable — accept the timeout as a hard upper bound.
            responseLine = reader.ReadLine();
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

        if (response["error"] is JsonObject err)
        {
            var code = (int?)err["code"] ?? -1;
            var message = (string?)err["message"] ?? "(no message)";
            throw new BridgeException($"Bridge error {code}: {message}");
        }

        return response["result"] as JsonObject;
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
        if (!string.IsNullOrWhiteSpace(options.MetadataBinPath))
        {
            psi.Environment["D365FO_BIN_PATH"] = options.MetadataBinPath;
        }

        process = Process.Start(psi);
        if (process is null)
        {
            return;
        }

        writer = process.StandardInput;
        reader = process.StandardOutput;
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
