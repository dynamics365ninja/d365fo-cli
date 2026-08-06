using D365FO.Core.Eval;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// Issue #160 — reading the document <c>SysTestConsole.exe /xml:</c> writes.
/// </summary>
/// <remarks>
/// The fixtures below are built to the schema <c>SysTestListenerXML</c> actually emits, taken
/// from its X++ source on a live installation: element names are <c>#define</c>s at the top of
/// that class, attributes are literal <c>setAttribute</c> calls. The polarity of
/// <c>success</c> is the trap and is pinned first.
/// </remarks>
public class SysTestResultsTests
{
    private const string Run = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <!-- Created by SysTestListenerXML -->
        <test-results date="2026-08-06" time="14:22:01">
          <environment user="admin" machine="AOS1" company="DAT" layer="usr" />
          <test-suite name="ConVehicleTest" time="412" success="true">
            <results>
              <test-case name="ConVehicleTest.testPlateIsMandatory" time="120"
                         starttime="2026-08-06T14:22:01" endtime="2026-08-06T14:22:01"
                         success="true" skipped="false" />
              <test-case name="ConVehicleTest.testMileageRejectsNegative" time="292"
                         success="false" skipped="false">
                <failure>
                  <message>Assert.isTrue failed: mileage -1 was accepted</message>
                </failure>
              </test-case>
              <test-case name="ConVehicleTest.testNeedsDemoData" success="true" skipped="true" />
              <test-case name="ConVehicleTest.testNeverGotThere" success="true" execution="pending" />
            </results>
          </test-suite>
        </test-results>
        """;

    [Fact]
    public void Success_is_a_pass_flag_not_a_failure_flag()
    {
        // SysTestListenerXML.isFailure() returns 'false' when the status is Failed and 'true'
        // otherwise. Reading it the way its name suggests inverts every verdict in the file.
        var results = SysTestResults.Parse(Run)!;

        Assert.True(results.Cases.Single(c => c.Name.EndsWith("testPlateIsMandatory")).Passed);
        Assert.False(results.Cases.Single(c => c.Name.EndsWith("testMileageRejectsNegative")).Passed);
    }

    [Fact]
    public void Every_case_is_counted_into_exactly_one_bucket()
    {
        var results = SysTestResults.Parse(Run)!;

        Assert.Equal(4, results.Cases.Count);
        Assert.Equal(1, results.Passed);
        Assert.Equal(1, results.Failed);
        Assert.Equal(1, results.Skipped);
        Assert.Equal(1, results.Pending);
    }

    [Fact]
    public void A_skipped_case_is_neither_passed_nor_failed()
    {
        var skipped = SysTestResults.Parse(Run)!.Cases.Single(c => c.Name.EndsWith("testNeedsDemoData"));

        Assert.True(skipped.Skipped);
        Assert.False(skipped.Passed);
    }

    [Fact]
    public void A_pending_case_is_not_a_pass_however_it_is_marked()
    {
        // execution="pending" means the runner registered the case and never executed it. The
        // document still carries success="true", which is exactly the trap: a run that died
        // half way would otherwise read as a clean run.
        var pending = SysTestResults.Parse(Run)!.Cases.Single(c => c.Name.EndsWith("testNeverGotThere"));

        Assert.True(pending.Pending);
        Assert.False(pending.Passed);
    }

    [Fact]
    public void The_failure_message_survives()
    {
        var failed = SysTestResults.Parse(Run)!.Failures.Single();

        Assert.Equal("ConVehicleTest.testMileageRejectsNegative", failed.Name);
        Assert.Equal("Assert.isTrue failed: mileage -1 was accepted", failed.FailureMessage);
        Assert.Equal(292, failed.TimeMs);
    }

    [Fact]
    public void A_run_is_clean_only_when_everything_that_should_have_run_did_and_passed()
    {
        Assert.False(SysTestResults.Parse(Run)!.Clean);

        const string allGood = """
            <test-results date="2026-08-06" time="14:22:01">
              <test-suite name="ConVehicleTest" success="true">
                <results>
                  <test-case name="ConVehicleTest.testOne" success="true" skipped="false" />
                  <test-case name="ConVehicleTest.testTwo" success="true" skipped="true" />
                </results>
              </test-suite>
            </test-results>
            """;
        Assert.True(SysTestResults.Parse(allGood)!.Clean);
    }

    [Fact]
    public void A_run_that_tested_nothing_is_not_a_clean_run()
    {
        // Reporting an empty run as a pass is how a broken harness looks healthy.
        const string empty = """
            <test-results date="2026-08-06" time="14:22:01">
              <test-suite name="ConVehicleTest" success="true"><results /></test-suite>
            </test-results>
            """;

        var results = SysTestResults.Parse(empty)!;
        Assert.Empty(results.Cases);
        Assert.False(results.Clean);
    }

    [Fact]
    public void Nested_suites_are_walked()
    {
        const string nested = """
            <test-results date="2026-08-06" time="14:22:01">
              <test-suite name="Outer" success="true">
                <results>
                  <test-suite name="Inner" success="true">
                    <results>
                      <test-case name="Inner.testOne" success="true" skipped="false" />
                    </results>
                  </test-suite>
                </results>
              </test-suite>
            </test-results>
            """;

        Assert.Equal(1, SysTestResults.Parse(nested)!.Passed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<TestRun><Results /></TestRun>")]     // a .trx, not a SysTest document
    public void Anything_that_is_not_a_result_document_is_refused(string? xml)
        => Assert.Null(SysTestResults.Parse(xml));

    [Fact]
    public void A_missing_file_is_refused_rather_than_read_as_an_empty_run()
    {
        Assert.Null(SysTestResults.TryParseFile(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.xml")));
        Assert.Null(SysTestResults.TryParseFile(null));
    }

    [Fact]
    public void A_document_on_disk_reads_the_same_as_one_in_memory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"systest-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, Run);
            Assert.Equal(SysTestResults.Parse(Run)!.Failed, SysTestResults.TryParseFile(path)!.Failed);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
