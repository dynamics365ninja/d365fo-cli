using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
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

        [CommandOption("--foreground")]
        public bool Foreground { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (File.Exists(DaemonEndpoint.PidFilePath))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "DAEMON_ALREADY_RUNNING",
                $"Pid file exists at {DaemonEndpoint.PidFilePath}.",
                "Run 'd365fo daemon stop' first, or delete the stale pid file."));
        }

        // Warm the repository once so all connections share the same FS layout.
        var dispatcher = StdioDispatcher.CreateDefault(settings.DatabasePath);

        File.WriteAllText(DaemonEndpoint.PidFilePath, Environment.ProcessId.ToString());
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var summary = ToolResult<object>.Success(new
        {
            endpoint = DaemonEndpoint.Describe(),
            pid = Environment.ProcessId,
            pidFile = DaemonEndpoint.PidFilePath,
            platform = OperatingSystem.IsWindows() ? "windows-named-pipe" : "unix-socket",
        });

        // Emit the start envelope so callers know the daemon is listening.
        RenderHelpers.Render(kind, summary);

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
