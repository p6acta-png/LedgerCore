using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LedgerCore.TestSupport;

/// <summary>
/// Runs the real, separately built API or Worker as an OS process. Used by the crash-recovery tests:
/// unlike an in-process exception, killing this process skips every finally block, rollback call and
/// shutdown hook, so recovery depends only on PostgreSQL and the persisted idempotency records.
/// </summary>
public sealed class ProcessHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private ProcessHost(Process process, Uri? baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri? BaseAddress { get; }

    public bool HasExited => _process.HasExited;

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public static async Task<ProcessHost> StartApiAsync(string connectionString, IDictionary<string, string>? extraEnvironment = null)
    {
        var port = FreeTcpPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["ASPNETCORE_URLS"] = baseAddress.ToString(),
            ["ConnectionStrings__Ledger"] = connectionString,
            ["DevAuthentication__Enabled"] = "true",
            ["DevAuthentication__SigningKey"] = LedgerApiFactory.SigningKey,
            ["FailureInjection__Enabled"] = "true",
            ["Features__SimulatedFunding"] = "true",
            ["RateLimiting__TransferPermitsPerSecond"] = "100000",
            ["RateLimiting__TransferBurst"] = "100000",
            ["RateLimiting__GeneralPermitsPerMinute"] = "100000",
            ["RateLimiting__DevTokensPerMinute"] = "100000",
        };
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        var host = Start(LocateBuiltAssembly("LedgerCore.Api"), environment, baseAddress);
        await host.WaitUntilReadyAsync(TimeSpan.FromSeconds(60));
        return host;
    }

    public static ProcessHost StartWorker(string connectionString, IDictionary<string, string>? extraEnvironment = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing",
            ["ConnectionStrings__Ledger"] = connectionString,
        };
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return Start(LocateBuiltAssembly("LedgerCore.Worker"), environment, baseAddress: null);
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await WaitForExitAsync(TimeSpan.FromSeconds(10));
        }

        _process.Dispose();
    }

    private static ProcessHost Start(string assemblyPath, IDictionary<string, string> environment, Uri? baseAddress)
    {
        // DOTNET_HOST_PATH is set by `dotnet test` and points at the dotnet executable running the tests.
        var dotnet = System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath ? hostPath : "dotnet";
        var startInfo = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var host = new ProcessHost(process, baseAddress);
        process.OutputDataReceived += (_, e) => host.Append(e.Data);
        process.ErrorDataReceived += (_, e) => host.Append(e.Data);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {assemblyPath}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return host;
    }

    private async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Process exited during startup with code {_process.ExitCode}:{System.Environment.NewLine}{Output}");
            }

            try
            {
                using var response = await client.GetAsync("/health/ready");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Request timed out; try again.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Process did not become ready within {timeout}:{System.Environment.NewLine}{Output}");
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(line);
        }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Finds src/{project}/bin/{Configuration}/net10.0/{project}.dll relative to the repository root.</summary>
    private static string LocateBuiltAssembly(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LedgerCore.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not locate the repository root (LedgerCore.slnx).");
        }

        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var path = Path.Combine(directory.FullName, "src", project, "bin", configuration, "net10.0", $"{project}.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Build {project} ({configuration}) before running process tests.", path);
    }
}
