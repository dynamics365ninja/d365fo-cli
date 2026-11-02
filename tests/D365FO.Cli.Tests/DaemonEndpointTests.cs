using D365FO.Cli.Commands.Daemon;
using D365FO.Core;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// The daemon's pipe, socket and pid file were global. A daemon started for one profile then
/// answered every other profile's clients from its own index, and the second profile's
/// <c>daemon start</c> was refused as "already running".
/// </summary>
// Sets the process-wide --profile selection, so it must not run beside tests that read it.
[Collection("EnvIndexDb")]
public sealed class DaemonEndpointTests
{
    [Fact]
    public void Pipe_name_differs_per_profile()
    {
        Assert.Equal("d365fo-cli-contoso", DaemonEndpoint.NameFor("contoso"));
        Assert.NotEqual(DaemonEndpoint.NameFor("contoso"), DaemonEndpoint.NameFor("fabrikam"));
    }

    [Fact]
    public void No_profile_keeps_the_name_existing_daemons_listen_on()
    {
        Assert.Equal("d365fo-cli", DaemonEndpoint.NameFor(null));
        Assert.Equal("d365fo-cli", DaemonEndpoint.NameFor(""));
    }

    [Fact]
    public void Invalid_profile_name_never_reaches_a_pipe_or_file_name()
    {
        Assert.Equal("d365fo-cli", DaemonEndpoint.NameFor(@"..\..\x"));
    }

    [Fact]
    public void Active_profile_names_the_pipe_socket_and_pid_file()
    {
        var previousFlag = D365FoProfiles.FlagProfile;
        try
        {
            D365FoProfiles.FlagProfile = "uat";

            Assert.Equal("d365fo-cli-uat", DaemonEndpoint.PipeName);
            Assert.Equal("d365fo-cli-uat.sock", DaemonEndpoint.SocketLeafName);
            Assert.Equal("d365fo-cli-uat.pid", Path.GetFileName(DaemonEndpoint.PidFilePath));
            Assert.Equal("d365fo-cli-uat.sock", Path.GetFileName(DaemonEndpoint.UnixSocketPath));
        }
        finally
        {
            D365FoProfiles.FlagProfile = previousFlag;
        }
    }
}
