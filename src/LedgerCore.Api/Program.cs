using LedgerCore.Api.Hosting;

// Usage:
//   dotnet LedgerCore.Api.dll            serve HTTP
//   dotnet LedgerCore.Api.dll migrate    apply database migrations and exit (run once per deployment)
//   dotnet LedgerCore.Api.dll seed       migrate + create demo wallets (Development only) and exit
var command = args.FirstOrDefault(a => a is "migrate" or "seed");
var builder = WebApplication.CreateBuilder(args.Where(a => a != command).ToArray());
builder.AddLedgerCoreApi();

var app = builder.Build();
app.UseLedgerCoreApi();

var exitCode = await app.RunCommandOrStartupTasksAsync(command);
if (exitCode is { } code)
{
    return code;
}

await app.RunAsync();
return 0;

/// <summary>Exposed so WebApplicationFactory&lt;Program&gt; can host the API in integration tests.</summary>
public partial class Program;
