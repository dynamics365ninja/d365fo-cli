using System.Text.RegularExpressions;
using D365FO.Core.Ops;
using D365FO.Core.Validation;

namespace D365FO.Core.Eval;

/// <summary>
/// Invokes the real X++ compiler over a provisioned model and reads its verdict.
/// </summary>
/// <remarks>
/// <para>
/// One invocation, shared by the golden verification and by the single-artefact probe. The
/// argument list is the part worth having in one place: it was read off <c>xppc.exe -?</c> on a
/// real installation, <c>{metadata}</c> is the metadata STORE rather than the model root, and
/// getting either wrong produces a usage dump that parses as "no errors" — a green run that
/// compiled nothing.
/// </para>
/// <para>
/// That last failure mode is why <see cref="Result.InvocationRejected"/> exists: a compiler that
/// printed its own usage did not judge the code, and reporting that as clean is the worst
/// possible answer from an oracle.
/// </para>
/// </remarks>
public static class XppcRunner
{
    /// <summary>
    /// The argument list, read off <c>xppc.exe -?</c> on a real installation.
    /// </summary>
    public const string DefaultArgs =
        "-metadata={metadata} -modelmodule={model} -output={output} -referencefolder={packages} -refPath={output} -log={log}";

    /// <summary>xppc printing its own usage means the arguments were rejected, not that the code is broken.</summary>
    private static readonly Regex UsageTextPattern = new(
        @"^usage:|Microsoft \(R\) X\+\+ Compiler|unrecognized option|Invalid option",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <param name="ExitCode">What xppc returned. Non-zero without diagnostics usually means the invocation itself failed.</param>
    /// <param name="ElapsedMs">Wall-clock time of the compiler run.</param>
    /// <param name="Compiler">The xppc.exe that ran.</param>
    /// <param name="Args">The argument list it was given, so a rejected invocation can be read back.</param>
    /// <param name="Diagnostics">Everything the compiler said, parsed.</param>
    /// <param name="InvocationRejected">
    /// The compiler printed usage instead of compiling — the run says nothing about the code.
    /// </param>
    /// <param name="LogTail">The end of the compiler log, which carries the message when parsing found nothing.</param>
    public sealed record Result(
        int ExitCode,
        long ElapsedMs,
        string Compiler,
        IReadOnlyList<string> Args,
        IReadOnlyList<XppcDiagnostic> Diagnostics,
        bool InvocationRejected,
        string LogTail);

    /// <summary>Compile <paramref name="modelName"/> out of the metadata store at <paramref name="workDir"/>.</summary>
    public static Result Compile(
        string workDir, string packagesRoot, string compiler, string modelName, string? argsTemplate = null)
    {
        var outputDir = Path.Combine(workDir, "bin");
        var logPath = Path.Combine(workDir, $"Dynamics.AX.{modelName}.xppc.log");
        Directory.CreateDirectory(outputDir);

        var args = Expand(
            string.IsNullOrWhiteSpace(argsTemplate) ? DefaultArgs : argsTemplate!,
            workDir, packagesRoot, modelName, outputDir, logPath);

        var (exit, stdout, stderr, elapsed) = SdlcRunner.Run(compiler, args);
        var log = stdout + "\n" + stderr;
        if (File.Exists(logPath))
        {
            try { log += "\n" + File.ReadAllText(logPath); }
            catch (IOException) { /* the compiler may still hold the handle; stdout is enough */ }
        }

        var diagnostics = XppcDiagnostics.Parse(log);
        var rejected = diagnostics.Count == 0 && UsageTextPattern.IsMatch(log);

        return new Result(exit, (long)elapsed.TotalMilliseconds, compiler, args, diagnostics, rejected, Tail(log, 20));
    }

    /// <summary>
    /// Expand the argument template into a real argument list.
    /// </summary>
    /// <remarks>
    /// Split before substitution, so a path containing a space stays one argument — the
    /// process runner passes each element through without re-parsing it.
    /// </remarks>
    public static IReadOnlyList<string> Expand(
        string template, string metadata, string packages, string model, string output, string log) =>
        template.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a
                .Replace("{metadata}", metadata, StringComparison.Ordinal)
                .Replace("{packages}", packages, StringComparison.Ordinal)
                .Replace("{model}", model, StringComparison.Ordinal)
                .Replace("{output}", output, StringComparison.Ordinal)
                .Replace("{log}", log, StringComparison.Ordinal))
            .ToList();

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Split('\n').TakeLast(lines));
}
