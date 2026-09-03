using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Shared by the navigation scaffolds: a menu item the index has never heard of is the failure
/// these objects exist to avoid — a tile or menu entry that opens nothing. Menu items are not in
/// <c>SymbolKinds</c>, so the gate's required-symbol check cannot see them; this asks the index
/// directly and reports what it could not find.
/// </summary>
internal static class NavigationGrounding
{
    internal static IReadOnlyList<string> UnknownMenuItems(IEnumerable<string> menuItems)
    {
        D365FO.Core.Index.MetadataRepository repo;
        try { repo = RepoFactory.Create(); }
        catch { return Array.Empty<string>(); }

        var unknown = new List<string>();
        foreach (var item in menuItems.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (!repo.MenuItemExists(item)) unknown.Add(item); }
            catch { /* an index without menu items cannot veto */ }
        }
        return unknown;
    }

    internal static string? Warning(IReadOnlyList<string> unknown, string what) =>
        unknown.Count == 0
            ? null
            : $"{what} not found in the index: {string.Join(", ", unknown)}. The index is a mirror of what was extracted — "
              + "if the menu item exists in a model that has not been extracted, run `d365fo index sync <model>`; otherwise create it "
              + "with `d365fo generate menu-item` first.";
}

/// <summary>Scaffolds an <c>AxMenu</c>: sub-menus, menu items, tiles and menu references.</summary>
public sealed class GenerateMenuCommand : Command<GenerateMenuCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Menu name.")]
        public string Name { get; init; } = "";

        [CommandOption("--label <KEY>")]
        [System.ComponentModel.Description("Label token or text shown in the navigation pane.")]
        public string? Label { get; init; }

        [CommandOption("--submenu <SPEC>")]
        [System.ComponentModel.Description("Repeatable sub-menu: <name>[[:<label>]]. Declared in order; an entry naming an undeclared sub-menu creates it unlabelled.")]
        public string[] Submenus { get; init; } = Array.Empty<string>();

        [CommandOption("--item <SPEC>")]
        [System.ComponentModel.Description("Repeatable menu item: [[<submenu>/]]<menuItem>[[:Display|Action|Output]]. Example: --item Setup/FMSetup --item Vehicles/FMVehicle")]
        public string[] Items { get; init; } = Array.Empty<string>();

        [CommandOption("--tile <SPEC>")]
        [System.ComponentModel.Description("Repeatable tile entry: [[<submenu>/]]<tile>. Example: --tile Workspaces/FMClerkWorkspace")]
        public string[] Tiles { get; init; } = Array.Empty<string>();

        [CommandOption("--menu-ref <SPEC>")]
        [System.ComponentModel.Description("Repeatable reference to another menu: [[<submenu>/]]<menu>.")]
        public string[] MenuRefs { get; init; } = Array.Empty<string>();

        [CommandOption("--in-content-area")]
        [System.ComponentModel.Description("Stamp DisplayInContentArea=Yes on every menu item (list pages that open in the content area).")]
        public bool InContentArea { get; init; }

        [CommandOption("--set-company")]
        [System.ComponentModel.Description("The menu switches legal entity (SetCompany=Yes).")]
        public bool SetCompany { get; init; }

        [CommandOption("--configuration-key <NAME>")]
        [System.ComponentModel.Description("AxConfigurationKey gating the whole menu.")]
        public string? ConfigurationKey { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Menu name required."));

        var submenus = new List<MenuSubmenuSpec>();
        foreach (var raw in settings.Submenus)
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrEmpty(parts[0]))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid --submenu '{raw}'. Expected <name>[:<label>]."));
            submenus.Add(new MenuSubmenuSpec(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null));
        }

        var entries = new List<MenuEntrySpec>();
        foreach (var raw in settings.Items)
        {
            var (sub, rest) = SplitSubmenu(raw);
            var parts = rest.Split(':', 2, StringSplitOptions.TrimEntries);
            if (string.IsNullOrEmpty(parts[0]))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid --item '{raw}'. Expected [<submenu>/]<menuItem>[:Display|Action|Output]."));
            entries.Add(new MenuEntrySpec(sub, MenuEntryKind.MenuItem, parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null));
        }
        foreach (var raw in settings.Tiles)
        {
            var (sub, target) = SplitSubmenu(raw);
            if (string.IsNullOrEmpty(target))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid --tile '{raw}'. Expected [<submenu>/]<tile>."));
            entries.Add(new MenuEntrySpec(sub, MenuEntryKind.Tile, target));
        }
        foreach (var raw in settings.MenuRefs)
        {
            var (sub, target) = SplitSubmenu(raw);
            if (string.IsNullOrEmpty(target))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, $"Invalid --menu-ref '{raw}'. Expected [<submenu>/]<menu>."));
            entries.Add(new MenuEntrySpec(sub, MenuEntryKind.MenuReference, target));
        }
        if (entries.Count == 0 && submenus.Count == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "A menu with nothing on it: pass at least one --item, --tile, --menu-ref or --submenu."));

        XDocument doc;
        try
        {
            doc = NavigationScaffolder.Menu(settings.Name, settings.Label, submenus, entries,
                settings.InContentArea, settings.SetCompany, settings.ConfigurationKey);
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.Menu, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        var warnings = new List<string>();
        var unknown = NavigationGrounding.UnknownMenuItems(entries.Where(e => e.Kind == MenuEntryKind.MenuItem).Select(e => e.Target));
        if (NavigationGrounding.Warning(unknown, "Menu item(s)") is { } w) warnings.Add(w);

        try
        {
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxMenu",
                name = settings.Name,
                label = settings.Label,
                submenus = submenus.Count,
                menuItems = entries.Count(e => e.Kind == MenuEntryKind.MenuItem),
                tiles = entries.Count(e => e.Kind == MenuEntryKind.Tile),
                menuReferences = entries.Count(e => e.Kind == MenuEntryKind.MenuReference),
                unknownMenuItems = unknown.Count > 0 ? unknown : null,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }, warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static (string? Submenu, string Remainder) SplitSubmenu(string raw)
    {
        var slash = raw.IndexOf('/');
        return slash < 0
            ? (null, raw.Trim())
            : (raw[..slash].Trim() is { Length: > 0 } s ? s : null, raw[(slash + 1)..].Trim());
    }
}

/// <summary>Scaffolds an <c>AxTile</c> bound to a menu item.</summary>
public sealed class GenerateTileCommand : Command<GenerateTileCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Tile name.")]
        public string Name { get; init; } = "";

        [CommandOption("--menu-item <NAME>")]
        [System.ComponentModel.Description("Menu item the tile opens. Required for a Standard or Count tile; a KPI tile binds --kpi and a Link tile --url instead.")]
        public string? MenuItem { get; init; }

        [CommandOption("--menu-item-type <TYPE>")]
        [System.ComponentModel.Description("Display (default, omitted from the XML) | Action | Output.")]
        public string? MenuItemType { get; init; }

        [CommandOption("--type <TYPE>")]
        [System.ComponentModel.Description("Standard (default, omitted) | Count (shows a record count from --query) | KPI (needs --kpi) | Link (needs --url).")]
        public string? Type { get; init; }

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }

        [CommandOption("--help-text <KEY>")]
        public string? HelpText { get; init; }

        [CommandOption("--size <SIZE>")]
        [System.ComponentModel.Description("Medium | Wide | ShortWide | Large.")]
        public string? Size { get; init; }

        [CommandOption("--image <NAME>")]
        [System.ComponentModel.Description("Symbol or AOT resource name for NormalImage (e.g. Workspace_DataManagement).")]
        public string? Image { get; init; }

        [CommandOption("--display <MODE>")]
        [System.ComponentModel.Description("TileDisplay: Auto | TextAndImage | TextOnly | ImageOnly | BackgroundImage.")]
        public string? Display { get; init; }

        [CommandOption("--query <NAME>")]
        [System.ComponentModel.Description("AxQuery a Count tile counts.")]
        public string? Query { get; init; }

        [CommandOption("--kpi <NAME>")]
        [System.ComponentModel.Description("AxKPI a KPI tile shows.")]
        public string? Kpi { get; init; }

        [CommandOption("--url <ADDRESS>")]
        [System.ComponentModel.Description("Address a Link tile opens, e.g. https://learn.microsoft.com/dynamics365/. Link tiles only.")]
        public string? Url { get; init; }

        [CommandOption("--configuration-key <NAME>")]
        public string? ConfigurationKey { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Tile name required."));

        XDocument doc;
        try
        {
            // What a tile has to bind to depends on its type, so the scaffolder decides — it is
            // the one place that knows Standard and Count open a menu item while KPI and Link
            // do not.
            doc = NavigationScaffolder.Tile(settings.Name, settings.MenuItem, settings.Type, settings.Label, settings.Size,
                settings.Image, settings.Display, settings.Query, settings.Kpi, settings.ConfigurationKey,
                settings.MenuItemType, settings.HelpText, settings.Url);
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message,
                hint: ex.ParamName == "menuItemName"
                    ? "Create the menu item first with `d365fo generate menu-item`, or pass --type KPI/--type Link for a tile that opens something else."
                    : null));
        }

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.Tile, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        var warnings = new List<string>();
        var unknown = NavigationGrounding.UnknownMenuItems(new[] { settings.MenuItem ?? "" });
        if (NavigationGrounding.Warning(unknown, "Menu item") is { } w) warnings.Add(w);

        try
        {
            // A Count tile's query is an AOT object the index knows; a hallucinated one is
            // exactly what the gate exists for. The menu item is checked above.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc,
                requiredSymbols: new[] { settings.Query }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!));
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxTile",
                name = settings.Name,
                menuItem = settings.MenuItem,
                type = settings.Type ?? "Standard",
                query = settings.Query,
                kpi = settings.Kpi,
                url = settings.Url,
                unknownMenuItems = unknown.Count > 0 ? unknown : null,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }, warnings);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}

/// <summary>Scaffolds an <c>AxFormPart</c> registering a form as a hostable part.</summary>
public sealed class GenerateFormPartCommand : Command<GenerateFormPartCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Form part name. Convention: the same name as the form it hosts.")]
        public string Name { get; init; } = "";

        [CommandOption("--form <NAME>")]
        [System.ComponentModel.Description("The AxForm the part hosts. Required.")]
        public string? Form { get; init; }

        [CommandOption("--caption <KEY>")]
        [System.ComponentModel.Description("Caption label token or text. Required — every shipped part carries one.")]
        public string? Caption { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Form part name required."));
        if (string.IsNullOrWhiteSpace(settings.Form))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--form <NAME> required."));
        if (string.IsNullOrWhiteSpace(settings.Caption))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--caption <KEY> required.", hint: "Reuse a label (`d365fo labels search`) or create one (`d365fo labels create`)."));

        XDocument doc;
        try { doc = NavigationScaffolder.FormPart(settings.Name, settings.Form!, settings.Caption!); }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.FormPart, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        try
        {
            // The hosted form must exist: a part over a form that is not there is a fact box
            // that opens to an error.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc, requiredSymbols: new[] { settings.Form! });
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind = "AxFormPart",
                name = settings.Name,
                form = settings.Form,
                caption = settings.Caption,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
