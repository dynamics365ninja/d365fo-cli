using D365FO.Core.Eval;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// Pins the eight defects the L3 build oracle found once it stopped reporting
/// false alarms — every one of them shipped metadata or X++ that named something
/// the compiler could not resolve.
/// </summary>
/// <remarks>
/// <para>
/// These are unit-level guards, not a substitute for <c>eval verify-build</c>:
/// the oracle needs a real installation, these do not. Each asserts the specific
/// text the compiler objected to, so a regression names itself.
/// </para>
/// </remarks>
public class ShippedReferenceIntegrityTests
{
    /// <summary>
    /// "AxService/…/ExternalName: Property cannot be empty" — the metadata
    /// provider rejects a service that is published under no name at all.
    /// </summary>
    [Fact]
    public void A_service_is_published_under_a_name()
    {
        var xml = CustomServiceScaffolder
            .ServiceXml("ConFmVehicleQueryService", "ConFmVehicleQueryServiceService",
                        [new OperationSpec("lookup", "str")])
            .ToString();

        Assert.Contains("<ExternalName>ConFmVehicleQueryService</ExternalName>", xml);

        var custom = CustomServiceScaffolder
            .ServiceXml("SvcA", "SvcAClass", [new OperationSpec("go", "void")], "PublishedAs")
            .ToString();
        Assert.Contains("<ExternalName>PublishedAs</ExternalName>", custom);
    }

    /// <summary>
    /// <c>BusinessEventsContract</c> is a class. Naming it in an implements list
    /// failed twice over — "must designate an interface", then a conversion error
    /// on <c>buildContract()</c>'s return.
    /// </summary>
    [Fact]
    public void A_business_event_contract_extends_its_base_class()
    {
        var xml = BusinessEventScaffolder
            .ContractClass("ConFmVehicleNoteAddedContract", [new PayloadSpec("noteId", "Name")])
            .ToString();

        Assert.Contains("extends BusinessEventsContract", xml);
        Assert.DoesNotContain("implements BusinessEventsContract", xml);
    }

    /// <summary>
    /// The module class convention is <c>NumberSeqModule&lt;Module&gt;</c>.
    /// <c>NumberSeqApplicationModule_&lt;Module&gt;</c> is the abstract base with a
    /// suffix bolted on, and exists in no module — "ExtendsOf attribute
    /// specification is invalid", followed by a <c>next</c> that chains to nothing.
    /// </summary>
    [Fact]
    public void A_number_sequence_extends_the_real_module_class()
    {
        Assert.Equal("NumberSeqModuleCustomer", NumberSequenceScaffolder.ModuleClassName("Customer"));
        Assert.Equal("NumberSeqModuleCustomer", NumberSequenceScaffolder.ModuleClassName("NumberSeqModuleCustomer"));

        var xml = NumberSequenceScaffolder.ModuleExtension("Customer", "ConDemoNum").ToString();

        Assert.Contains("[ExtensionOf(classStr(NumberSeqModuleCustomer))]", xml);
        Assert.DoesNotContain("NumberSeqApplicationModule_", xml);
        // A CoC method has to match the visibility of the one it wraps.
        Assert.Contains("protected void loadModule()", xml);
    }

    /// <summary>
    /// Two independent rules broke the same generated class: the name has to end
    /// with the literal <c>_Extension</c> (<c>_WorkflowExtension</c> does not), and
    /// a CoC method may not restate a default the wrapped method already declares.
    /// </summary>
    [Fact]
    public void A_can_submit_extension_satisfies_both_chain_of_command_rules()
    {
        var xml = WorkflowScaffolder.CanSubmitExtension("FmVehicle").ToString();

        Assert.Contains("FmVehicle_Workflow_Extension", xml);
        Assert.DoesNotContain("FmVehicle_WorkflowExtension", xml);
        Assert.Contains("public boolean canSubmitToWorkflow(str _workflowType)", xml);
        Assert.DoesNotContain("_workflowType = ''", xml);
    }

    /// <summary>
    /// A case scores one artifact, but a generate command usually ships several.
    /// Compiling the scored file alone made the siblings dangle and reported five
    /// cases red for objects the tool does in fact generate.
    /// </summary>
    [Fact]
    public void Companions_are_the_sibling_artifacts_a_case_does_not_score()
    {
        var dir = Path.Combine(Path.GetTempPath(), "d365fo-comp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Empty(L3ModelProvisioner.Companions(dir));

            var companions = Path.Combine(dir, L3ModelProvisioner.CompanionFolder);
            Directory.CreateDirectory(companions);
            File.WriteAllText(Path.Combine(dir, "Scored.xml"), "<AxService />");
            File.WriteAllText(Path.Combine(companions, "Sibling.xml"), "<AxClass />");

            var found = L3ModelProvisioner.Companions(dir);
            Assert.Single(found);
            Assert.EndsWith("Sibling.xml", found[0]);

            // The scorer enumerates the case directory itself, and must keep seeing
            // exactly one file — a companion that leaked up would make it ambiguous.
            Assert.Single(Directory.GetFiles(dir, "*.xml"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
