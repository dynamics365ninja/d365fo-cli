using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using D365FO.Core;
using D365FO.Core.Index;
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
/// Concurrency: each accepted connection gets its own <see cref="StdioDispatcher"/>,
/// but all dispatchers share a single <see cref="MetadataRepository"/> — the
/// repository is stateless across operations, so this is safe.
/// </summary>
internal static class DaemonEndpoint
{
    public const string PipeName = "d365fo-cli";
    public const string SocketLeafName = "d365fo-cli.sock";

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
            return Path.Combine(dir, "d365fo-cli.pid");
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
        [System.ComponentModel.Description("PackagesLocalDirectory to watch for metadata changes. Defaults to D365FO_PACKAGES_PATH. The D365FO_CUSTOM_PACKAGES_PATH roots are watched as well.")]
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

        // Every root the index reads from: --packages (or D365FO_PACKAGES_PATH) and the custom
        // roots, the same set `index refresh` extracts.
        var watchRoots = IndexAutoRefresh.ConfiguredRoots(settings.DatabasePath, settings.PackagesPath);
        var packagesPath = watchRoots.FirstOrDefault();

        var summary = ToolResult<object>.Success(new
        {
            endpoint = DaemonEndpoint.Describe(),
            pid = Environment.ProcessId,
            pidFile = DaemonEndpoint.PidFilePath,
            platform = OperatingSystem.IsWindows() ? "windows-named-pipe" : "unix-socket",
            watching = !settings.NoWatch && watchRoots.Count > 0,
            watchPath = packagesPath,
            watchPaths = watchRoots.Count > 0 ? watchRoots : null,
            watchDebounceMs = settings.WatchDebounceMs,
        });

        // Emit the start envelope so callers know the daemon is listening.
        RenderHelpers.Render(kind, summary);

        // Start file watcher if requested.
        using var watcher = settings.NoWatch
            ? null
            : StartWatchers(watchRoots, settings.DatabasePath, settings.WatchDebounceMs, cts.Token);

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
    /// One <see cref="FileSystemWatcher"/> per packages root — <c>D365FO_PACKAGES_PATH</c> and every
    /// <c>D365FO_CUSTOM_PACKAGES_PATH</c> root — that debounces metadata changes and re-syncs the
    /// affected model. Dispose the returned handle to stop all of them.
    /// </summary>
    /// <remarks>
    /// It used to watch the packages path only, so on a UDE, where custom models live in their own
    /// root, an edit to a custom model was never seen. And it re-read the model through a bare
    /// extract: no extra roots (a custom model was not found), and no <c>--index-source</c> (a model
    /// that had method bodies in the full-text index lost them on the first save). The refresh now
    /// goes through <see cref="IndexAutoRefresh.RefreshModel"/>, the same per-model sync a write
    /// uses, which searches every configured root and keeps the model's method-source state.
    /// </remarks>
    internal static IDisposable? StartWatchers(
        IReadOnlyList<string> roots, string? dbPath, int debounceMs, CancellationToken ct)
    {
        var existing = roots.Where(Directory.Exists).ToList();
        if (existing.Count == 0) return null;

        // model name → debounce timer
        var pending = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        var @lock = new object();

        void ScheduleRefresh(string modelName, string root)
        {
            lock (@lock)
            {
                if (pending.TryGetValue(modelName, out var existingTimer))
                {
                    existingTimer.Change(debounceMs, Timeout.Infinite);
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
                    var r = IndexAutoRefresh.RefreshModel(modelName, root, dbPath);
                    Console.Error.WriteLine(D365Json.Serialize(r.Ok
                        ? new { @event = "index_refreshed", model = modelName, elapsedMs = r.ElapsedMs, methodSourceIndexed = r.MethodSourceIndexed } as object
                        : new { @event = "index_refresh_failed", model = modelName, error = r.Error }));
                }, null, debounceMs, Timeout.Infinite);
            }
        }

        var watchers = new List<FileSystemWatcher>();
        foreach (var root in existing)
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            void OnChanged(object _, FileSystemEventArgs e)
            {
                // <root>/<package>/<model>/Ax*/*.xml — the model is the second segment, which is not
                // the package for ISV models where the two differ.
                if (IndexAutoRefresh.LocateUnderRoots(e.FullPath, new[] { fullRoot }) is { } hit)
                    ScheduleRefresh(hit.Model, hit.Root);
            }

            // One root that cannot be watched (a network share without change notifications, a
            // root removed since it was resolved) must not take the daemon or the other roots down.
            FileSystemWatcher? fsw = null;
            try
            {
                fsw = new FileSystemWatcher(fullRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                };
                // Label files are metadata too: a label added in Visual Studio changes the model.
                fsw.Filters.Add("*.xml");
                fsw.Filters.Add("*.label.txt");
                fsw.Changed += OnChanged;
                fsw.Created += OnChanged;
                fsw.Deleted += OnChanged;
                fsw.Renamed += (s, e) => OnChanged(s, e);
                // A burst larger than the buffer (git checkout, a full build) drops events silently
                // unless this is reported; `index sync` / `index refresh` is then the repair.
                fsw.Error += (_, e) => Console.Error.WriteLine(D365Json.Serialize(
                    new { @event = "watch_error", root = fullRoot, error = e.GetException().Message }));
                fsw.EnableRaisingEvents = true;
                watchers.Add(fsw);
            }
            catch (Exception ex)
            {
                fsw?.Dispose();
                Console.Error.WriteLine(D365Json.Serialize(
                    new { @event = "watch_failed", root = fullRoot, error = ex.Message }));
            }
        }
        return watchers.Count == 0 ? null : new WatcherSet(watchers);
    }

    private sealed class WatcherSet(IReadOnlyList<FileSystemWatcher> watchers) : IDisposable
    {
        public IReadOnlyList<FileSystemWatcher> Watchers { get; } = watchers;

        public void Dispose()
        {
            foreach (var w in Watchers) w.Dispose();
        }
    }

    /// <summary>The paths a watcher handle covers, for tests.</summary>
    internal static IReadOnlyList<string> WatchedPaths(IDisposable? handle)
        => handle is WatcherSet set ? set.Watchers.Select(w => w.Path).ToList() : Array.Empty<string>();

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
