using System.Runtime.CompilerServices;

namespace D365FO.TestSupport;

/// <summary>
/// Points the test process at its own SQLite index, so the suite cannot write into the index —
/// or the journal — of the machine it runs on.
/// </summary>
/// <remarks>
/// <para>
/// <c>ModificationJournal.ForIndex</c> resolves the journal to
/// <c>&lt;dirname(D365FO_INDEX_DB)&gt;/journal</c>, and <c>ScaffoldFileWriter.Write</c> journals
/// every write. On a developer machine with <c>D365FO_INDEX_DB</c> configured — which is the
/// normal state, and how <c>d365fo init</c> leaves it — that meant every scaffold-writing test in
/// the suite appended into the developer's REAL undo journal. Measured on this repo's own host:
/// the journal directory held exactly 500 entries, which is <c>DefaultMaxEntries</c>, and the
/// newest was a <c>scaffold-write</c> naming a test temp directory. The cap prunes oldest-first,
/// so a full test run silently evicts the developer's real undo history and replaces it with
/// entries pointing at directories that no longer exist.
/// </para>
/// <para>
/// It was also a flakiness source. Both test assemblies run as concurrent processes under
/// <c>dotnet test</c> on the solution, so they appended to and pruned the SAME journal directory
/// at the same time — two processes each deleting "the oldest entries until under the cap" while
/// the other is writing them. CI never saw it, because CI has no <c>D365FO_INDEX_DB</c> set.
/// </para>
/// <para>
/// Only the index is redirected. <c>D365FO_PACKAGES_PATH</c> and <c>D365FO_WORKSPACE_PATH</c> are
/// deliberately left alone: <c>ObjectTypeRegistryAotTests</c> and <c>MetadataContractsAotTests</c>
/// are inert without them and are the tests that check this repo's claims against a real
/// installation. Clearing those to match CI would quietly delete that coverage on the one kind of
/// machine that can provide it.
/// </para>
/// </remarks>
internal static class TestIndexIsolation
{
    private static string? _root;

    [ModuleInitializer]
    internal static void RedirectIndexAndJournal()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "d365fo-testrun",
            $"{typeof(TestIndexIsolation).Assembly.GetName().Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (_root is not null && Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort: a temp directory left behind is not worth failing a green run over.
            }
        };
    }
}
