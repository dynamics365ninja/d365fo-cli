using System.Collections;
using System.Reflection;
using Spectre.Console.Cli;

namespace D365FO.Cli.Tests;

/// <summary>
/// The command surface the app actually registers, read off Spectre's own configuration
/// tree rather than off a list someone maintains by hand.
/// </summary>
/// <remarks>
/// <para>
/// Every other inventory of this CLI — the JSON manifest <c>d365fo schema</c> publishes, the
/// docs, the MCP tool catalog — is written by hand, and each one drifted: the manifest listed
/// 130-odd commands while the app registered well over 200, so an agent reading it could not
/// discover half the tool. This walker is the ground truth those inventories are checked
/// against.
/// </para>
/// <para>
/// Reflection over <c>Spectre.Console.Cli</c> internals is deliberate and deliberately confined
/// to the test assembly: the shipped binary is trimmed, and reflecting into a trimmed
/// third-party assembly at run time is exactly the silent failure this repo has been bitten by
/// before (issue #182). If Spectre reshapes <c>ConfiguredCommand</c> on an upgrade, this fails
/// loudly at test time — which is the point.
/// </para>
/// </remarks>
internal static class CliSurface
{
    /// <param name="Path">Full command path as a user types it, e.g. <c>generate table</c>.</param>
    /// <param name="Description">The registered description, or empty when none was set.</param>
    /// <param name="CommandType">The <see cref="ICommand"/> implementation behind it.</param>
    internal sealed record Entry(string Path, string Description, Type? CommandType);

    /// <summary>Every runnable command, branches themselves excluded.</summary>
    internal static IReadOnlyList<Entry> Leaves()
    {
        var app = CliApp.Build();

        var configurator =
            typeof(CommandApp).GetField("_configurator", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(app)
            ?? throw new InvalidOperationException(
                "Spectre's CommandApp no longer exposes a '_configurator' field — the surface walker needs updating.");

        var roots = Read(configurator, "Commands")
            ?? throw new InvalidOperationException(
                "Spectre's Configurator no longer exposes 'Commands' — the surface walker needs updating.");

        var leaves = new List<Entry>();
        foreach (var root in roots) Walk(root, prefix: null, leaves);
        return leaves;
    }

    private static void Walk(object command, string? prefix, List<Entry> into)
    {
        var name = (string?)Property(command, "Name") ?? "";
        var path = prefix is null ? name : $"{prefix} {name}";

        var children = Read(command, "Children");
        var any = false;
        if (children is not null)
            foreach (var child in children)
            {
                any = true;
                Walk(child, path, into);
            }

        // A branch is a namespace, not something to run. Only leaves are commands.
        if (!any)
            into.Add(new Entry(
                path,
                (string?)Property(command, "Description") ?? "",
                (Type?)Property(command, "CommandType")));
    }

    private static IEnumerable<object>? Read(object target, string property)
        => Property(target, property) is IEnumerable items ? items.Cast<object>() : null;

    private static object? Property(object target, string name)
        => target.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(target);
}
