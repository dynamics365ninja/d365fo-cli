using System.Xml.Linq;

namespace D365FO.Core.Eval;

/// <summary>
/// Whether the SysTest runner is actually wired to a database — and can it tell a passing test
/// from a failing one.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, both of which have to be answered before a test result means anything.
/// </para>
/// <para>
/// The first is configuration. <c>SysTestConsole.exe</c> reads its own
/// <c>.exe.config</c> for the database the AOS uses, and a stock install can leave those keys
/// empty: the runner then dies with <c>Login failed</c>, which reads like a broken test model
/// rather than a runner that was never pointed anywhere. The keys are in the AOS
/// <c>web.config</c> a few directories away, so the answer is derivable rather than guessable.
/// </para>
/// <para>
/// The second is discrimination, and it is the one nobody checks. "All tests passed" is also
/// what a runner prints when it ran nothing at all. Until a test that is MEANT to fail actually
/// fails, and one that is meant to throw actually throws, a green run is not evidence — that is
/// what the negative control is for.
/// </para>
/// </remarks>
public static class RuntimeOracle
{
    /// <summary>The keys the runner needs to reach the database the AOS uses.</summary>
    public static readonly IReadOnlyList<string> RequiredKeys =
    [
        "DataAccess.DbServer",
        "DataAccess.Database",
        "DataAccess.SqlUser",
        "DataAccess.SqlPwd",
    ];

    /// <param name="Key">Setting name.</param>
    /// <param name="PresentInRunner">Whether the runner's config declares the key at all.</param>
    /// <param name="NonEmptyInRunner">Whether it declares it with a value — an empty value is what makes the runner fail as if the model were broken.</param>
    /// <param name="AvailableInWebConfig">Whether the AOS web.config carries a value the runner could be given.</param>
    public sealed record SettingState(string Key, bool PresentInRunner, bool NonEmptyInRunner, bool AvailableInWebConfig);

    public sealed record Diagnosis(
        string? RunnerPath,
        string? RunnerConfigPath,
        string? WebConfigPath,
        bool RunnerPresent,
        bool Configured,
        IReadOnlyList<SettingState> Settings,
        IReadOnlyList<string> Missing);

    /// <summary>Look at what is installed and say whether a test run would mean anything.</summary>
    /// <param name="packagesRoot">PackagesLocalDirectory; the runner lives under its <c>bin</c>.</param>
    /// <param name="webConfigPath">AOS web.config. Searched near the packages root when omitted.</param>
    public static Diagnosis Diagnose(string packagesRoot, string? webConfigPath = null)
    {
        var runner = Path.Combine(packagesRoot ?? "", "bin", "SysTestConsole.exe");
        var runnerConfig = runner + ".config";
        var web = webConfigPath ?? FindWebConfig(packagesRoot);

        var runnerSettings = ReadAppSettings(File.Exists(runnerConfig) ? runnerConfig : null);
        var webSettings = ReadAppSettings(File.Exists(web ?? "") ? web : null);

        var settings = RequiredKeys.Select(k => new SettingState(
            k,
            PresentInRunner: runnerSettings.ContainsKey(k),
            NonEmptyInRunner: runnerSettings.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v),
            AvailableInWebConfig: webSettings.TryGetValue(k, out var w) && !string.IsNullOrWhiteSpace(w))).ToList();

        var missing = settings.Where(s => !s.NonEmptyInRunner).Select(s => s.Key).ToList();

        return new Diagnosis(
            File.Exists(runner) ? runner : null,
            File.Exists(runnerConfig) ? runnerConfig : null,
            File.Exists(web ?? "") ? web : null,
            RunnerPresent: File.Exists(runner),
            Configured: File.Exists(runner) && missing.Count == 0,
            settings,
            missing);
    }

    /// <param name="RunnerConfigPath">The runner config that was written.</param>
    /// <param name="BackupPath">Where the previous file was kept — this edits a Microsoft-installed configuration.</param>
    /// <param name="Written">Keys copied into the runner's config.</param>
    /// <param name="Unavailable">Keys still missing because the web.config has no value either.</param>
    public sealed record ConfigureResult(
        string RunnerConfigPath, string BackupPath, IReadOnlyList<string> Written, IReadOnlyList<string> Unavailable);

    /// <summary>
    /// Copy the database settings the runner is missing out of the AOS web.config.
    /// </summary>
    /// <remarks>
    /// Only the missing ones, and only from the installation's own web.config — the point is to
    /// make the runner agree with the AOS it is testing, not to invent credentials. The previous
    /// file is kept beside the new one: this edits a Microsoft-installed configuration.
    /// </remarks>
    public static ConfigureResult Configure(string runnerConfigPath, string webConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(webConfigPath);

        var web = ReadAppSettings(webConfigPath);
        var doc = XDocument.Load(runnerConfigPath, LoadOptions.PreserveWhitespace);
        var appSettings = doc.Root?.Element("appSettings");
        if (appSettings is null)
        {
            appSettings = new XElement("appSettings");
            doc.Root?.Add(appSettings);
        }

        var written = new List<string>();
        var unavailable = new List<string>();

        foreach (var key in RequiredKeys)
        {
            var existing = appSettings.Elements("add")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), key, StringComparison.Ordinal));
            if (existing is not null && !string.IsNullOrWhiteSpace((string?)existing.Attribute("value"))) continue;

            if (!web.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                unavailable.Add(key);
                continue;
            }

            if (existing is null) appSettings.Add(new XElement("add", new XAttribute("key", key), new XAttribute("value", value)));
            else existing.SetAttributeValue("value", value);
            written.Add(key);
        }

        var backup = runnerConfigPath + ".d365fo-cli.bak";
        if (written.Count > 0)
        {
            if (!File.Exists(backup)) File.Copy(runnerConfigPath, backup);
            doc.Save(runnerConfigPath);
        }

        return new ConfigureResult(runnerConfigPath, backup, written, unavailable);
    }

    /// <summary>
    /// The negative control: a test class whose three methods pass, fail and throw on purpose.
    /// </summary>
    /// <remarks>
    /// Run it before trusting a green suite. A runner that reports all three as passing is not
    /// running them, and a runner that reports the failure and the throw distinctly is one whose
    /// verdicts mean something. The passing method is there so that "everything failed" is
    /// distinguishable too.
    /// </remarks>
    public static string NegativeControlSource(string className = "D365FoCliNegativeControlTest") =>
        $$"""
        [SysTestTargetAttribute(classStr({{className}}), 'class')]
        class {{className}} extends SysTestCase
        {
            /// <summary>
            /// Passes. Its purpose is to prove the run happened at all: if this one does not
            /// report as passed, the runner never reached the class.
            /// </summary>
            [SysTestMethodAttribute]
            public void passesOnPurpose()
            {
                this.assertEquals(1, 1);
            }

            /// <summary>
            /// Fails on purpose. A suite in which this reports as PASSED is not discriminating,
            /// and every other green result in it is worthless.
            /// </summary>
            [SysTestMethodAttribute]
            public void failsOnPurpose()
            {
                this.assertEquals(1, 2, 'Negative control: this assert is meant to fail.');
            }

            /// <summary>
            /// Throws on purpose. A thrown error and a failed assert are different verdicts, and
            /// a runner that collapses them tells you less than it appears to.
            /// </summary>
            [SysTestMethodAttribute]
            public void throwsOnPurpose()
            {
                throw error('Negative control: this throw is meant to be reported as an error.');
            }
        }
        """;

    // ── reading configuration ──────────────────────────────────────────────

    private static Dictionary<string, string> ReadAppSettings(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;

        try
        {
            var doc = XDocument.Load(path);
            foreach (var add in doc.Descendants("appSettings").Elements("add"))
            {
                var key = (string?)add.Attribute("key");
                if (string.IsNullOrEmpty(key)) continue;
                result[key] = (string?)add.Attribute("value") ?? "";
            }
        }
        catch { /* an unreadable config is reported as "absent", which is what it is to the runner */ }

        return result;
    }

    /// <summary>The AOS web.config, looked for beside the packages root.</summary>
    private static string? FindWebConfig(string? packagesRoot)
    {
        if (string.IsNullOrWhiteSpace(packagesRoot)) return null;
        var service = Path.GetDirectoryName(packagesRoot);
        if (service is null) return null;

        foreach (var folder in new[] { "WebRoot", "webroot" })
        {
            var candidate = Path.Combine(service, folder, "web.config");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
