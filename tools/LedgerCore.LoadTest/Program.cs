using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

// LedgerCore load test.
//
//   dotnet run --project tools/LedgerCore.LoadTest -c Release -- --base-urls http://localhost:8080,http://localhost:8081
//
// Phases: (1) create customers + wallets and fund them through the dev endpoints, (2) fire random
// transfers from many concurrent workers for a fixed duration (including deliberate duplicate retries
// and overdraft attempts), (3) verify that money was conserved and that reconciliation is consistent.
// Results are printed and written as JSON. Numbers depend entirely on the machine: report them only
// together with the environment line this tool prints.

var options = LoadTestOptions.Parse(args);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = options.Concurrency * 2, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
var clients = options.BaseUrls.Select(u => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(u), Timeout = TimeSpan.FromSeconds(30) }).ToArray();
var random = new Random(options.Seed);
const long OpeningBalance = 1_000_000; // 10 000.00 NOK per wallet

Console.WriteLine($"LedgerCore load test: {options.Customers} customers, concurrency {options.Concurrency}, {options.Duration.TotalSeconds:0}s, " +
                  $"duplicate ratio {options.DuplicateRatio:P0}, targets {string.Join(", ", options.BaseUrls)}");

// ---- Phase 1: setup -------------------------------------------------------------------------------
var operatorToken = await IssueTokenAsync(clients[0], "loadtest-operator", "ledger-operator");
var customers = new List<Customer>();
for (var i = 0; i < options.Customers; i++)
{
    var customerId = Guid.NewGuid();
    var token = await IssueTokenAsync(clients[0], customerId.ToString(), "customer");
    using var create = await SendAsync(clients[0], HttpMethod.Post, "/api/wallets", token, new { name = $"Load {i}", currency = "NOK" }, null);
    Ensure(create, HttpStatusCode.Created, "create wallet");
    var walletId = (await create.Content.ReadFromJsonAsync<JsonElement>(json)).GetProperty("id").GetGuid();
    using var fund = await SendAsync(clients[0], HttpMethod.Post, "/api/dev/funding", operatorToken,
        new { walletId, amountMinor = OpeningBalance, currency = "NOK" }, $"loadtest-fund-{walletId:N}");
    Ensure(fund, HttpStatusCode.Created, "fund wallet");
    customers.Add(new Customer(token, walletId));
}

// ---- Phase 2: load --------------------------------------------------------------------------------
var samples = new ConcurrentBag<Sample>();
var stopAt = DateTime.UtcNow + options.Duration;
var wall = Stopwatch.StartNew();
var workers = Enumerable.Range(0, options.Concurrency).Select(workerIndex => Task.Run(async () =>
{
    var workerRandom = new Random(options.Seed * 1000 + workerIndex);
    (Customer From, Guid To, long Amount, string Key)? previous = null;
    var counter = 0;
    while (DateTime.UtcNow < stopAt)
    {
        (Customer From, Guid To, long Amount, string Key) request;
        var duplicate = previous is not null && workerRandom.NextDouble() < options.DuplicateRatio;
        if (duplicate)
        {
            request = previous!.Value;
        }
        else
        {
            var from = customers[workerRandom.Next(customers.Count)];
            Guid to;
            do
            {
                to = customers[workerRandom.Next(customers.Count)].WalletId;
            }
            while (to == from.WalletId);
            request = (from, to, workerRandom.Next(1, 60_000), $"load-{options.Seed}-{workerIndex}-{counter++}");
        }

        var client = clients[workerRandom.Next(clients.Length)];
        var started = Stopwatch.GetTimestamp();
        HttpStatusCode status;
        try
        {
            using var response = await SendAsync(client, HttpMethod.Post, "/api/transfers", request.From.Token,
                new { fromWalletId = request.From.WalletId, toWalletId = request.To, amountMinor = request.Amount, currency = "NOK" }, request.Key);
            status = response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            status = 0;
        }

        samples.Add(new Sample(status, Stopwatch.GetElapsedTime(started).TotalMilliseconds, duplicate));
        previous = request;
    }
})).ToArray();
await Task.WhenAll(workers);
wall.Stop();

// ---- Phase 3: verify ------------------------------------------------------------------------------
long total = 0;
foreach (var customer in customers)
{
    using var balance = await SendAsync(clients[0], HttpMethod.Get, $"/api/wallets/{customer.WalletId}/balance", customer.Token, null, null);
    Ensure(balance, HttpStatusCode.OK, "read balance");
    total += (await balance.Content.ReadFromJsonAsync<JsonElement>(json)).GetProperty("availableBalanceMinor").GetInt64();
}

using var reconciliation = await SendAsync(clients[0], HttpMethod.Post, "/api/admin/reconciliation?freezeAffectedWallets=false", operatorToken, null, null);
Ensure(reconciliation, HttpStatusCode.OK, "reconciliation");
var consistent = (await reconciliation.Content.ReadFromJsonAsync<JsonElement>(json)).GetProperty("isConsistent").GetBoolean();
var expectedTotal = OpeningBalance * customers.Count;

// ---- Report ---------------------------------------------------------------------------------------
var all = samples.ToArray();
var latencies = all.Where(s => s.Status != 0).Select(s => s.Milliseconds).Order().ToArray();
var byStatus = all.GroupBy(s => s.Status == 0 ? "network-error" : ((int)s.Status).ToString(CultureInfo.InvariantCulture))
    .ToDictionary(g => g.Key, g => g.Count());
var report = new
{
    environment = new
    {
        os = RuntimeInformation.OSDescription,
        runtime = RuntimeInformation.FrameworkDescription,
        logicalProcessors = Environment.ProcessorCount,
        targets = options.BaseUrls,
    },
    settings = new { options.Customers, options.Concurrency, durationSeconds = options.Duration.TotalSeconds, options.DuplicateRatio, options.Seed },
    requests = all.Length,
    elapsedSeconds = Math.Round(wall.Elapsed.TotalSeconds, 2),
    requestsPerSecond = Math.Round(all.Length / wall.Elapsed.TotalSeconds, 1),
    statusCounts = byStatus,
    duplicateRequests = all.Count(s => s.Duplicate),
    latencyMs = new
    {
        p50 = Percentile(latencies, 0.50),
        p95 = Percentile(latencies, 0.95),
        p99 = Percentile(latencies, 0.99),
        max = latencies.Length == 0 ? 0 : Math.Round(latencies[^1], 1),
    },
    correctness = new { expectedTotalMinor = expectedTotal, actualTotalMinor = total, moneyConserved = total == expectedTotal, reconciliationConsistent = consistent },
};

var output = JsonSerializer.Serialize(report, new JsonSerializerOptions(json) { WriteIndented = true });
Console.WriteLine(output);
Directory.CreateDirectory(options.OutputDirectory);
var file = Path.Combine(options.OutputDirectory, $"loadtest-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
await File.WriteAllTextAsync(file, output);
Console.WriteLine($"Written to {file}");

return total == expectedTotal && consistent ? 0 : 2;

// ---- Helpers --------------------------------------------------------------------------------------
async Task<string> IssueTokenAsync(HttpClient client, string subject, string role)
{
    using var response = await client.PostAsJsonAsync("/api/dev/tokens", new { subject, name = "load test", roles = new[] { role } });
    Ensure(response, HttpStatusCode.OK, "issue dev token (is the API running in Development?)");
    return (await response.Content.ReadFromJsonAsync<JsonElement>(json)).GetProperty("accessToken").GetString()!;
}

static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body, string? key)
{
    using var request = new HttpRequestMessage(method, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (key is not null)
    {
        request.Headers.Add("Idempotency-Key", key);
    }

    if (body is not null)
    {
        request.Content = JsonContent.Create(body);
    }

    return await client.SendAsync(request);
}

static void Ensure(HttpResponseMessage response, HttpStatusCode expected, string what)
{
    if (response.StatusCode != expected)
    {
        throw new InvalidOperationException($"{what} failed: {(int)response.StatusCode} {response.Content.ReadAsStringAsync().Result}");
    }
}

static double Percentile(double[] sorted, double p) =>
    sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)], 1);

internal sealed record Customer(string Token, Guid WalletId);

internal sealed record Sample(HttpStatusCode Status, double Milliseconds, bool Duplicate);

internal sealed record LoadTestOptions(string[] BaseUrls, TimeSpan Duration, int Concurrency, int Customers, double DuplicateRatio, int Seed, string OutputDirectory)
{
    public static LoadTestOptions Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        return new LoadTestOptions(
            Get("--base-urls", "http://localhost:8080").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TimeSpan.FromSeconds(int.Parse(Get("--duration", "30"), CultureInfo.InvariantCulture)),
            int.Parse(Get("--concurrency", "32"), CultureInfo.InvariantCulture),
            int.Parse(Get("--customers", "20"), CultureInfo.InvariantCulture),
            double.Parse(Get("--duplicate-ratio", "0.1"), CultureInfo.InvariantCulture),
            int.Parse(Get("--seed", "1"), CultureInfo.InvariantCulture),
            Get("--output", Path.Combine("tools", "LedgerCore.LoadTest", "results")));
    }
}
