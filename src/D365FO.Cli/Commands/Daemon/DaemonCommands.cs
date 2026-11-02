using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using D365FO.Cli.Commands.Index;
using D365FO.Core;
using D365FO.Mcp;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Daemon;

/// <summary>
/// Long-running IPC server that keeps the SQLite index warm and answers
/// JSON-RPC requests over a local socket. The protocol is identical to the
/// stdio MCP server — we reuse <see cref="StdioDispatcher"/> per connection.
///
/// Transport:
/// <list type="bullet">
///   <item><b>Windows:</b> named pipe <c>\\.\pipe\d365fo-cli</c>.</item>
///   <item><b>Unix:</b> domain socket at <c>$XDG_RUNTIME_DIR/d365fo-cli.sock</c>
///   (or <c>$TMPDIR/d365fo-cli.sock</c> if XDG is unset).</item>
/// </list>
/// With a profile active (<c>--profile</c> / <c>D365FO_PROFILE</c>) the pipe, socket and pid
/// file carry its name — <c>d365fo-cli-&lt;profile&gt;</c> — so each profile runs its own daemon
/// over its own index.
/// Concurrency: each accepted connection gets its own <see cref="StdioDispatcher"/>,
/// but all dispatchers share a single <see cref="MetadataRepository"/> — the
/// repository is stateless across operations, so this is safe.
/// </summary>
internal static class DaemonEndpoint
{
    /// <summary>The endpoint name with no profile active — unchanged from before profiles.</summary>
    public const string BaseName = "d365fo-cli";

    /// <summary>
    /// The endpoint name for <paramref name="profile"/>. The pipe, socket and pid file were
    /// global, so a daemon started for one profile answered every other profile's clients from
    /// its own index, and a second profile's <c>daemon start</c> was refused as "already
    /// running". No profile keeps the bare name, so an existing setup finds its daemon where it
    /// always was. A name that is not a valid profile name is never put into a path; the
    /// profile check refuses such a name before the daemon starts.
    /// </summary>
    internal static string NameFor(string? profile) =>
        D365FoProfiles.IsValidName(profile) ? $"{BaseName}-{profile!.Trim()}" : BaseName;

    private static string ActiveName => NameFor(D365FoProfiles.GetActive()?.Name);

    public static string PipeName => ActiveName;
    public static string SocketLeafName => ActiveName + ".sock";

    public static string UnixSocketPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Path.GetTempPath();
            return Path.Combine(dir, SocketLeafName);
        }
    }

    public static string Describe() =>
        OperatingSystem.IsWindows() ? $@"\\.\pipe\{PipeName}" : UnixSocketPath;

    public static string PidFilePath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Path.GetTempPath();
            return Path.Combine(dir, ActiveName + ".pid");
        }
    }
}

public sealed class DaemonStartCommand : AsyncCommand<DaemonStartCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("PackagesLocalDirectory to watch for XML changes. Defaults to D365FO_PACKAGES_PATH.")]
        public string? PackagesPath { get; init; }

        [CommandOption("--foreground")]
        public bool Foreground { get; init; }

        [CommandOption("--no-watch")]
        [System.ComponentModel.Description("Disable the automatic file watcher (index refresh on XML change).")]
        public bool NoWatch { get; init; }

        [CommandOption("--watch-debounce <MS>")]
        [System.ComponentModel.Description("Debounce delay in milliseconds before triggering index refresh after a file change. Default: 3000.")]
        public int WatchDebounceMs { get; init; } = 3000;
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        // Atomic PID file creation — CreateNew fails if the file already exists,
        // preventing race between two concurrent daemon starts.
        FileStream? pidLock = null;
        try
        {
            pidLock = new FileStream(
                DaemonEndpoint.PidFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }
        catch (IOException)
        {
            pidLock?.Dispose();
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "DAEMON_ALREADY_RUNNING",
                $"Pid file exists at {DaemonEndpoint.PidFilePath}.",
                "Run 'd365fo daemon stop' first, or delete the stale pid file."));
        }

        using (pidLock)
        {
            using var sw = new StreamWriter(pidLock, leaveOpen: false);
            sw.Write(Environment.ProcessId.ToString());
        }

        // Warm the repository once so all connections share the same FS layout.
        var dispatcher = StdioDispatcher.CreateDefault(settings.DatabasePath);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Resolve packages path for the file watcher.
        var packagesPath = settings.PackagesPath
            ?? D365FoSettings.FromEnvironment(settings.DatabasePath).PackagesPath;

        var summary = ToolResult<object>.Success(new
        {
            endpoint = DaemonEndpoint.Describe(),
            pid = Environment.ProcessId,
            pidFile = DaemonEndpoint.PidFilePath,
            platform = OperatingSystem.IsWindows() ? "windows-named-pipe" : "unix-socket",
            watching = !settings.NoWatch && !string.IsNullOrWhiteSpace(packagesPath),
            watchPath = string.IsNullOrWhiteSpace(packagesPath) ? null : packagesPath,
            watchDebounceMs = settings.WatchDebounceMs,
        });

        // Emit the start envelope so callers know the daemon is listening.
        RenderHelpers.Render(kind, summary);

        // Start file watcher if requested.
        using var watcher = settings.NoWatch || string.IsNullOrWhiteSpace(packagesPath)
            ? null
            : StartWatcher(packagesPath, settings.DatabasePath, settings.WatchDebounceMs, cts.Token);

        try
        {
            await AcceptLoop(dispatcher, cts.Token);
        }
        finally
        {
            TryDeletePidFile();
            TryDeleteSocket();
        }
        return 0;
    }

    /// <summary>
    /// Creates a <see cref="FileSystemWatcher"/> over <paramref name="packagesPath"/>
    /// that debounces XML changes and triggers an incremental <c>index refresh</c>
    /// for the affected model. Returns the watcher so the caller can dispose it.
    /// </summary>
    private static FileSystemWatcher? StartWatcher(
        string packagesPath, string? dbPath, int debounceMs, CancellationToken ct)
    {
        if (!Directory.Exists(packagesPath)) return null;

        // model name → debounce timer
        var pending = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        var @lock = new object();

        void ScheduleRefresh(string modelName)
        {
            lock (@lock)
            {
                if (pending.TryGetValue(modelName, out var existing))
                {
                    existing.Change(debounceMs, Timeout.Infinite);
                    return;
                }
                pending[modelName] = new Timer(state =>
                {
                    Timer? self;
                    lock (@lock)
                    {
                        pending.Remove(modelName, out self);
                    }
                    self?.Dispose();
                    if (ct.IsCancellationRequested) return;
                    // Fire-and-forget incremental extract for this model only.
                    try
                    {
                        IndexExtractCommand.ExtractCore(
                            OutputMode.Kind.Json,
                            packagesPath,
                            dbPath,
                            modelName,
                            sinceIso: null);
                        Console.Error.WriteLine(
                            D365Json.Serialize(new { @event = "index_refreshed", model = modelName }));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            D365Json.Serialize(new { @event = "index_refresh_failed", model = modelName, error = ex.Message }));
                    }
                }, null, debounceMs, Timeout.Infinite);
            }
        }

        void OnChanged(object _, FileSystemEventArgs e)
        {
            // D365FO layout: packagesPath/<package>/<model>/Ax*/*.xml
            // parts[0] = package name, parts[1] = model name.
            // In the common case where package == model, parts[0] works too,
            // but we must use parts[1] for ISV models where they differ.
            var rel = Path.GetRelativePath(packagesPath, e.FullPath);
            var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;
            ScheduleRefresh(parts[1]);
        }

        var fsw = new FileSystemWatcher(packagesPath)
        {
            IncludeSubdirectories = true,
            Filter = "*.xml",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        fsw.Changed += OnChanged;
        fsw.Created += OnChanged;
        fsw.Deleted += OnChanged;
        fsw.Renamed += (s, e) => OnChanged(s, e);
        return fsw;
    }

    private static async Task AcceptLoop(StdioDispatcher dispatcher, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            await AcceptPipeLoop(dispatcher, ct);
        else
            await AcceptSocketLoop(dispatcher, ct);
    }

    private static async Task AcceptPipeLoop(StdioDispatcher dispatcher, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                DaemonEndpoint.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { server.Dispose(); break; }

            _ = ServeAsync(dispatcher, server, server, ct);
        }
    }

    private static async Task AcceptSocketLoop(StdioDispatcher dispatcher, CancellationToken ct)
    {
        var path = DaemonEndpoint.UnixSocketPath;
        if (File.Exists(path)) File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(16);

        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            var ns = new NetworkStream(accepted, ownsSocket: true);
            _ = ServeAsync(dispatcher, ns, ns, ct);
        }
    }

    private static async Task ServeAsync(StdioDispatcher dispatcher, Stream input, Stream output, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(input, leaveOpen: false);
            using var writer = new StreamWriter(output, leaveOpen: false) { AutoFlush = false };
            await dispatcher.RunAsync(reader, writer, ct);
        }
        catch (IOException) { /* client hung up */ }
        catch (OperationCanceledException) { }
    }

    private static void TryDeletePidFile()
    {
        try { if (File.Exists(DaemonEndpoint.PidFilePath)) File.Delete(DaemonEndpoint.PidFilePath); } catch { }
    }

    private static void TryDeleteSocket()
    {
        if (OperatingSystem.IsWindows()) return;
        try { if (File.Exists(DaemonEndpoint.UnixSocketPath)) File.Delete(DaemonEndpoint.UnixSocketPath); } catch { }
    }
}

public sealed class DaemonStopCommand : Command<DaemonStopCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (!File.Exists(DaemonEndpoint.PidFilePath))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("DAEMON_NOT_RUNNING", "No pid file found."));
        if (!int.TryParse(File.ReadAllText(DaemonEndpoint.PidFilePath), out var pid))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("DAEMON_PID_CORRUPT", "Pid file is not a number."));
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: false);
            proc.WaitForExit(5000);
        }
        catch (ArgumentException) { /* already gone */ }

        try { File.Delete(DaemonEndpoint.PidFilePath); } catch { }
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new { stopped = pid }));
    }
}

public sealed class DaemonStatusCommand : Command<DaemonStatusCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var running = File.Exists(DaemonEndpoint.PidFilePath);
        int? pid = null;
        if (running && int.TryParse(File.ReadAllText(DaemonEndpoint.PidFilePath), out var p))
            pid = p;
        bool alive = false;
        if (pid is not null)
        {
            try { System.Diagnostics.Process.GetProcessById(pid.Value); alive = true; }
            catch (ArgumentException) { alive = false; }
        }
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            running = alive,
            pid,
            endpoint = DaemonEndpoint.Describe(),
            pidFile = DaemonEndpoint.PidFilePath,
        }));
    }
}

/// <summary>
/// Pre-warms the SQLite page cache by issuing lightweight count queries on
/// the major index tables. Speeds up the first real query after a cold start
/// (e.g., after booting the daemon) by loading frequently-accessed B-tree
/// pages into the OS page cache before any user request arrives.
/// </summary>
public sealed class DaemonWarmupCommand : Command<DaemonWarmupCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create(settings.DatabasePath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var counts = repo.Warmup();
        sw.Stop();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            elapsedMs = sw.ElapsedMilliseconds,
            counts = new
            {
                tables       = counts.Tables,
                classes      = counts.Classes,
                methods      = counts.Methods,
                edts         = counts.Edts,
                enums        = counts.Enums,
                labels       = counts.Labels,
                forms        = counts.Forms,
                cocExtensions = counts.CocExtensions,
                dataEntities = counts.DataEntities,
            },
        }), _ => Spectre.Console.AnsiConsole.MarkupLine(
            $"[green]OK[/] warm-up complete in {sw.ElapsedMilliseconds}ms " +
            $"(tables={counts.Tables} classes={counts.Classes} labels={counts.Labels})"));
    }
}
