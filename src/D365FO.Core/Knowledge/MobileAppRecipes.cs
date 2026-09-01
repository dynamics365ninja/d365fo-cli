// <copyright file="MobileAppRecipes.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core.Knowledge;

/// <summary>Which of the two warehouse-app frameworks a recipe belongs to.</summary>
public enum MobileFramework
{
    /// <summary>The current one: controller → step → page builder → data processor → navigation agent → action.</summary>
    ProcessGuide,

    /// <summary>The legacy one: a <c>WhsWorkExecuteDisplay</c> subclass with one <c>displayForm()</c> per mode.</summary>
    WorkExecuteDisplay,

    /// <summary>Neither — the answer is configuration rather than code.</summary>
    Configuration,
}

/// <summary>One class a warehouse-app recipe needs.</summary>
/// <param name="Role">Its job in the flow.</param>
/// <param name="Extends">Base class, spelled as the AOT spells it.</param>
/// <param name="Naming">The convention shipped classes follow.</param>
/// <param name="Required">False for the parts a flow only sometimes needs.</param>
public sealed record MobileRosterEntry(string Role, string? Extends, string Naming, bool Required = true);

/// <summary>An implementation recipe for one warehouse-app change.</summary>
public sealed record MobileAppRecipe(
    string Id,
    string Title,
    MobileFramework Framework,
    string WhenToUse,
    IReadOnlyList<MobileRosterEntry> Roster,
    IReadOnlyList<string> Guidance,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> ReferenceObjects);

/// <summary>
/// Warehouse mobile-app (scanner) screen recipes.
/// </summary>
/// <remarks>
/// <para>
/// The list leads with the decision the platform forces on you, because it is the one that cannot
/// be undone cheaply: the SAME screens are built by TWO frameworks. <b>ProcessGuide</b> is the
/// current one — controller, step, page builder, data processor, navigation agent and action are
/// each their own class with an abstract factory in front of it, so each is an extension point.
/// The legacy <b>WhsWorkExecuteDisplay</b> hierarchy puts all of it in one class with a
/// <c>displayForm()</c> per mode. Picking the wrong one is a rewrite, not a refactor.
/// </para>
/// <para>
/// Counted in this installation rather than recalled. The <c>ProcessGuide</c> package holds 382
/// classes; within it <c>ProcessGuidePageBuilder</c> has 75 subclasses, <c>ProcessGuideStep</c>
/// 74, <c>ProcessGuideNavigationAgent</c> 40, <c>ProcessGuideController</c> 25,
/// <c>ProcessGuideAction</c> 23, and <c>ProcessGuideStepWithoutPrompt</c> 18. The legacy
/// <c>WhsWorkExecuteDisplay</c> is abstract with 64 subclasses in ApplicationSuite/Foundation
/// alone.
/// </para>
/// <para>
/// A casing note worth having, because it looks like a typo in review and is not: the AOT itself
/// is inconsistent here — both <c>WHSWorkExecuteDisplay</c> and <c>WhsWorkExecuteDisplay</c>
/// appear as the declared base among shipped subclasses. X++ resolves either.
/// </para>
/// </remarks>
public static class MobileAppRecipes
{
    private static readonly MobileAppRecipe[] All =
    [
        new(
            Id: "processguide-flow",
            Title: "New ProcessGuide flow",
            Framework: MobileFramework.ProcessGuide,
            WhenToUse: "A new scanner process of your own — the operator walks through a sequence of screens. "
                     + "This is the default answer for new work; reach for the legacy hierarchy only when "
                     + "extending something already built on it.",
            Roster:
            [
                new("Controller", "ProcessGuideController",
                    "<Prefix>ProcessGuide<Flow>Controller — owns the flow and the order of its steps."),
                new("Step", "ProcessGuideStep",
                    "<Prefix>ProcessGuide<Screen>Step — one screen. Use ProcessGuideStepWithoutPrompt for a step "
                    + "that does work and shows no prompt."),
                new("Page builder", "ProcessGuidePageBuilder",
                    "<Prefix>ProcessGuide<Screen>PageBuilder — builds the controls the step shows."),
                new("Navigation agent", "ProcessGuideNavigationAgent",
                    "<Prefix>ProcessGuide<Flow>NavigationAgent — decides which step comes next.", Required: false),
                new("Data processor", "ProcessGuideDataProcessor",
                    "<Prefix>ProcessGuide<Flow>DataProcessor — turns what the operator entered into a business "
                    + "action. ProcessGuideDataProcessorDefault covers the simple case.", Required: false),
            ],
            Guidance:
            [
                "Every part is reached through an abstract factory (ProcessGuide*AbstractFactory), which is what "
                + "makes each one an extension point. Register your class with the matching factory — a step that "
                + "no factory returns is a class the app never constructs.",
                "The step is the unit of navigation, the page builder the unit of layout. Putting layout in the "
                + "step is what makes a flow impossible to extend later.",
            ],
            Checks:
            [
                "d365fo build, then walk the flow on a device or the emulator — a missing factory registration "
                + "shows up as the flow ending early, not as an error.",
            ],
            ReferenceObjects: ["InventProcessGuideAdjustInController", "InventProcessGuideCaptureCatchWeightTagAdjustInStep"]),

        new(
            Id: "processguide-page-control",
            Title: "Add a control to a standard screen",
            Framework: MobileFramework.ProcessGuide,
            WhenToUse: "A shipped screen needs one more field shown or captured, and everything else about it "
                     + "stays as it is.",
            Roster:
            [
                new("Page builder extension", null,
                    "A Chain of Command extension of the standard <Screen>PageBuilder, wrapping the method that "
                    + "adds controls."),
            ],
            Guidance:
            [
                "Extend the page builder, NOT the step: the step decides navigation, and wrapping it to add a "
                + "control couples your change to a sequence that may be reordered.",
                "`d365fo find coc <PageBuilder>::<method>` first — a standard page builder often already has a "
                + "wrapper, and two wrappers adding the same control produce it twice.",
            ],
            Checks: ["d365fo validate xpp on the CoC class — the next-must-be-reached rules apply."],
            ReferenceObjects: ["InventProcessGuideDisplayInquiryItemDetailsPageBuilder"]),

        new(
            Id: "processguide-page-replace",
            Title: "Replace a standard page",
            Framework: MobileFramework.ProcessGuide,
            WhenToUse: "The shipped screen is wrong for your process rather than merely incomplete — different "
                     + "controls, different layout.",
            Roster:
            [
                new("Page builder", "ProcessGuidePageBuilder",
                    "Your own builder for the screen."),
                new("Page builder factory", "ProcessGuidePageBuilderAbstractFactory",
                    "Returns your builder instead of the standard one for this step."),
            ],
            Guidance:
            [
                "This is a substitution, not an extension: the factory is what decides, so the standard builder "
                + "stays untouched and yours is returned in its place.",
                "Prefer processguide-page-control when the difference is additive — a replaced page stops "
                + "receiving Microsoft's changes to that screen.",
                "Extend the nearest intermediate base, not always ProcessGuidePageBuilder itself. Modules ship "
                + "their own layer — WHSProcessGuideCycleCountPageBuilder extends ProcessGuidePageBuilder and has "
                + "five subclasses of its own — and starting from the framework type loses whatever that layer "
                + "already does for the flow.",
            ],
            Checks: ["Confirm the standard builder is no longer being constructed; two builders for one step is a "
                   + "factory that returns the wrong one under some condition."],
            ReferenceObjects: ["WHSProcessGuidePromptCycleCountItemBuilder"]),

        new(
            Id: "processguide-step-insert",
            Title: "Insert a step into a standard flow",
            Framework: MobileFramework.ProcessGuide,
            WhenToUse: "The operator must do or confirm something extra part-way through a shipped process.",
            Roster:
            [
                new("Step", "ProcessGuideStep",
                    "The new screen. ProcessGuideStepWithoutPrompt when it only does work."),
                new("Page builder", "ProcessGuidePageBuilder", "Its layout.", Required: false),
                new("Navigation agent", "ProcessGuideNavigationAgent",
                    "Your agent, returning the new step at the right point."),
                new("Navigation agent factory", "ProcessGuideNavigationAgentAbstractFactory",
                    "Returns your agent for the flow you are changing."),
            ],
            Guidance:
            [
                "The navigation agent is the ONLY thing that decides order. Inserting a step means replacing the "
                + "agent, not editing the controller.",
                "Handle the back path as well as the forward one — an inserted step that cannot be reversed strands "
                + "the operator mid-process.",
            ],
            Checks: ["Walk the flow forwards and backwards. Skipping the reverse direction is the usual defect."],
            ReferenceObjects: ["InventProcessGuideInquiryItemPromptNavigationAgent", "WHSProcessGuideBackEnabledNavigationAgentFactory"]),

        new(
            Id: "app-step-identity",
            Title: "The step's identity in the app",
            Framework: MobileFramework.Configuration,
            WhenToUse: "The screen works but shows the wrong title, the wrong icon, or does not appear on the "
                     + "menu the operator uses.",
            Roster:
            [
                new("Menu item (warehouse)", null,
                    "A warehouse mobile device menu item — configured in the app's own menu, not the AOT menu tree.",
                    Required: false),
            ],
            Guidance:
            [
                "This is configuration, not code: title, icon and menu placement come from the warehouse mobile "
                + "device menu setup, and a code change will not move them.",
                "Writing a class to change a caption is the mistake this recipe exists to prevent.",
            ],
            Checks: ["Change it in the setup form and re-open the app — no build is involved."],
            ReferenceObjects: ["WHSMobileAppActionIconSelector"]),

        new(
            Id: "legacy-workexecutedisplay",
            Title: "Extend a legacy WorkExecuteDisplay screen",
            Framework: MobileFramework.WorkExecuteDisplay,
            WhenToUse: "You are changing a process that is ALREADY built on the legacy hierarchy. Do not start "
                     + "new work here — but do not port an existing flow to ProcessGuide as a side effect of a "
                     + "small change either.",
            Roster:
            [
                new("Display class extension", null,
                    "A Chain of Command extension of the relevant WhsWorkExecuteDisplay subclass, wrapping its "
                    + "displayForm() (or the mode-specific method)."),
            ],
            Guidance:
            [
                "The base is abstract and has 64 subclasses in ApplicationSuite/Foundation alone — find the one "
                + "that owns the mode you are changing with `d365fo search class WHSWorkExecuteDisplay`.",
                "The AOT is inconsistent about the casing: both WHSWorkExecuteDisplay and WhsWorkExecuteDisplay "
                + "appear as the declared base among shipped subclasses. X++ resolves either; it is not a typo.",
                "One class does navigation, layout and business logic together, which is exactly why new work "
                + "belongs in ProcessGuide.",
            ],
            Checks: ["d365fo find coc <DisplayClass>::displayForm before writing — these classes are heavily wrapped already."],
            ReferenceObjects: ["WhsWorkExecuteDisplayAdjustIn", "WHSWorkExecuteDisplayCancelWork"]),

        new(
            Id: "gs1-scan-input",
            Title: "Accept a GS1 barcode",
            Framework: MobileFramework.Configuration,
            WhenToUse: "A scanned barcode carries several values — item, batch, quantity, expiry — and the screen "
                     + "must take them apart.",
            Roster: [],
            Guidance:
            [
                "Scanning is CONFIGURATION, not a hand-written parser. The GS1 application identifiers and the "
                + "fields they map to are set up in the warehouse barcode configuration; the app splits the scan "
                + "before any of your code sees it.",
                "Writing a parseBarcode() is the failure mode this recipe exists to prevent: it works for the "
                + "sample barcode and breaks on the first variable-length identifier.",
            ],
            Checks:
            [
                "Scan a barcode carrying two identifiers and confirm both fields populate. A parser that appears "
                + "to work on one identifier proves nothing.",
            ],
            ReferenceObjects: []),
    ];

    /// <summary>Every recipe.</summary>
    public static IReadOnlyList<MobileAppRecipe> List() => All;

    /// <summary>One recipe by id, case-insensitively; null when there is none.</summary>
    public static MobileAppRecipe? Find(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids, for an error message that lists what is available.</summary>
    public static IReadOnlyList<string> Ids() => All.Select(r => r.Id).ToList();

    /// <summary>
    /// The choice to make before any of the recipes apply.
    /// </summary>
    public static string FrameworkDecision =>
        "The SAME screens are built by TWO frameworks, and picking the wrong one is a rewrite. "
        + "ProcessGuide is the current one: controller, step, page builder, data processor, navigation agent and "
        + "action are separate classes, each behind an abstract factory, so each is an extension point. The legacy "
        + "WhsWorkExecuteDisplay hierarchy does all of it in one class with a displayForm() per mode. "
        + "New work goes in ProcessGuide; a change to a process already built on the legacy hierarchy stays there.";
}
