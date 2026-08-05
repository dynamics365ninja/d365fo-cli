using D365FO.Cli;

var app = CliApp.Build();

try
{
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine(D365FO.Core.D365Json.Serialize(
        D365FO.Core.ToolResult<object>.Fail("UNHANDLED", ex.Message, ex.GetType().FullName)));
    return 2;
}
