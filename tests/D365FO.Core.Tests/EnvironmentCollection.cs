namespace D365FO.Core.Tests;

/// <summary>
/// The one collection every test that mutates process-wide environment state belongs to.
/// </summary>
/// <remarks>
/// Issue #158. Environment variables are process-wide, and <c>D365FoSettings.FromEnvironment()</c>
/// is read on every scaffold write, every journal append and every index open — so a class that
/// sets <c>D365FO_INDEX_DB</c> / <c>D365FO_HOME</c> / <c>D365FO_CUSTOM_MODELS</c> in its
/// constructor and restores it in <c>Dispose</c> is, for the duration of that test class,
/// redefining where every *other* concurrently running test writes. xUnit runs separate
/// collections in parallel by default, so grouping the mutators without
/// <c>DisableParallelization</c> would only stop them racing each other, not the readers.
///
/// <c>DisableParallelization = true</c> is what actually closes it: this collection never runs
/// alongside another collection, so no test outside it can observe a half-applied environment.
/// The sibling <c>D365FO.Cli.Tests</c> assembly does the same thing under the name "EnvIndexDb".
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollectionDefinition
{
    public const string Name = "Environment";
}
