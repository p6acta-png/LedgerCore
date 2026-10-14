using LedgerCore.Api.Hosting;
using LedgerCore.Api.Http;
using LedgerCore.Api.Security;
using LedgerCore.Application.Funding;
using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Reconciliation;
using LedgerCore.Application.Reversals;
using LedgerCore.Application.Transfers;
using LedgerCore.Application.Wallets;
using LedgerCore.Domain.Common;
using LedgerCore.Domain.Monetary;
using Microsoft.AspNetCore.Mvc;

namespace LedgerCore.Api.Endpoints;

public sealed record WalletBalanceResponse(Guid WalletId, long AvailableBalanceMinor, string Currency, string AvailableBalanceFormatted, string Status);

public sealed record ReverseTransferRequest(string? Reason = null);

public sealed record DevTokenRequest(string? Subject = null, string? Name = null, string[]? Roles = null);

/// <summary>HTTP surface of LedgerCore. Handlers only translate HTTP to use-case calls and back.</summary>
public static class LedgerEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static void MapLedgerEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        var wallets = api.MapGroup("/wallets").WithTags("Wallets");
        wallets.MapPost("/", CreateWallet).RequireAuthorization(Policies.Customer)
            .WithSummary("Create a NOK wallet for the calling customer (demo wallets start at zero).");
        wallets.MapGet("/", ListMyWallets).RequireAuthorization(Policies.Customer)
            .WithSummary("List the calling customer's wallets.");
        wallets.MapGet("/{walletId:guid}", GetWallet)
            .WithSummary("Wallet details. Owners and operators only; other callers get 404.");
        wallets.MapGet("/{walletId:guid}/balance", GetBalance)
            .WithSummary("Available balance in minor units (øre).");
        wallets.MapGet("/{walletId:guid}/transactions", GetTransactions)
            .WithSummary("Posting history, newest first. Paginate with ?beforePostingId=.");

        var transfers = api.MapGroup("/transfers").WithTags("Transfers");
        transfers.MapPost("/", CreateTransfer).RequireAuthorization(Policies.Customer).RequireRateLimiting(RateLimitingSetup.TransfersPolicy)
            .WithSummary("Transfer simulated money between wallets. Requires an Idempotency-Key header.");
        transfers.MapGet("/{transferId:guid}", GetTransfer)
            .WithSummary("Transfer details for its sender, its receiver, or an operator.");
        transfers.MapPost("/{transferId:guid}/reversals", ReverseTransfer).RequireAuthorization(Policies.Operator)
            .WithSummary("Reverse a transfer with a compensating journal entry. Operators only; requires an Idempotency-Key.");

        var journal = api.MapGroup("/journal-entries").WithTags("Journal").RequireAuthorization(Policies.Operator);
        journal.MapGet("/", ListJournalEntries).WithSummary("Journal entries, newest first. Paginate with ?beforeEntryId=.");
        journal.MapGet("/{entryId:guid}", GetJournalEntry).WithSummary("One journal entry with all postings.");

        var admin = api.MapGroup("/admin").WithTags("Administration").RequireAuthorization(Policies.Operator);
        admin.MapPost("/reconciliation", RunReconciliation)
            .WithSummary("Recompute balances from the journal and compare with cached balances. Freezes affected wallets by default.");

        if (EnvironmentGuards.IsDevelopmentLike(app.Environment))
        {
            var dev = api.MapGroup("/dev").WithTags("Development only");
            if (app.Configuration.GetValue<bool>($"{DevAuthenticationOptions.SectionName}:Enabled"))
            {
                dev.MapPost("/tokens", IssueDevToken).AllowAnonymous().RequireRateLimiting(RateLimitingSetup.DevTokensPolicy)
                    .WithSummary("Mint a development access token. Does not exist outside Development/Testing.");
            }

            if (app.Configuration.GetValue<bool>("Features:SimulatedFunding"))
            {
                dev.MapPost("/funding", FundWallet).RequireAuthorization(Policies.Operator)
                    .WithSummary("Simulated deposit: balanced entry against the funding expense account. Requires an Idempotency-Key.");
            }
        }
    }

    private static async Task<IResult> CreateWallet(
        [FromBody] CreateWalletCommand request, HttpContext context, WalletService wallets, CancellationToken ct)
    {
        var result = await wallets.CreateAsync(request, context.GetCaller(), context.User.FindFirst(LedgerClaims.Name)?.Value, ct);
        return result.IsSuccess
            ? Results.Created($"/api/wallets/{result.Value.Id}", result.Value)
            : HttpResults.Problem(result.Error);
    }

    private static async Task<IResult> ListMyWallets(HttpContext context, WalletService wallets, CancellationToken ct) =>
        (await wallets.ListMineAsync(context.GetCaller(), ct)).ToHttp();

    private static async Task<IResult> GetWallet(Guid walletId, HttpContext context, WalletService wallets, CancellationToken ct) =>
        (await wallets.GetAsync(new WalletId(walletId), context.GetCaller(), ct)).ToHttp();

    private static async Task<IResult> GetBalance(Guid walletId, HttpContext context, WalletService wallets, CancellationToken ct)
    {
        var wallet = await wallets.GetAsync(new WalletId(walletId), context.GetCaller(), ct);
        if (wallet.IsFailure)
        {
            return HttpResults.Problem(wallet.Error);
        }

        var w = wallet.Value;
        return Results.Ok(new WalletBalanceResponse(
            w.Id, w.BalanceMinor, w.Currency, Money.Format(w.BalanceMinor, Currency.FromStorage(w.Currency)), w.Status));
    }

    private static async Task<IResult> GetTransactions(
        Guid walletId, HttpContext context, WalletService wallets, CancellationToken ct, int limit = 50, long? beforePostingId = null) =>
        (await wallets.GetTransactionsAsync(new WalletId(walletId), context.GetCaller(), limit, beforePostingId, ct)).ToHttp();

    private static async Task<IResult> CreateTransfer(
        [FromBody] CreateTransferCommand request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        HttpContext context,
        TransferService transfers,
        CancellationToken ct)
    {
        var key = IdempotencyKey.Parse(idempotencyKey);
        if (key.IsFailure)
        {
            return HttpResults.Problem(key.Error);
        }

        var outcome = await transfers.CreateAsync(request, context.GetCaller(), key.Value, ct);
        return outcome.ToHttp(context, r => $"/api/transfers/{r.TransferId}");
    }

    private static async Task<IResult> GetTransfer(Guid transferId, HttpContext context, WalletService wallets, CancellationToken ct) =>
        (await wallets.GetTransferAsync(new TransferId(transferId), context.GetCaller(), ct)).ToHttp();

    private static async Task<IResult> ReverseTransfer(
        Guid transferId,
        [FromBody] ReverseTransferRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        HttpContext context,
        ReversalService reversals,
        CancellationToken ct)
    {
        var key = IdempotencyKey.Parse(idempotencyKey);
        if (key.IsFailure)
        {
            return HttpResults.Problem(key.Error);
        }

        var outcome = await reversals.ReverseAsync(new ReverseTransferCommand(transferId, request.Reason), context.GetCaller(), key.Value, ct);
        return outcome.ToHttp(context, r => $"/api/transfers/{r.TransferId}");
    }

    private static async Task<IResult> ListJournalEntries(
        HttpContext context, WalletService wallets, CancellationToken ct, int limit = 50, Guid? beforeEntryId = null) =>
        (await wallets.ListJournalEntriesAsync(context.GetCaller(), limit, beforeEntryId, ct)).ToHttp();

    private static async Task<IResult> GetJournalEntry(Guid entryId, HttpContext context, WalletService wallets, CancellationToken ct) =>
        (await wallets.GetJournalEntryAsync(new JournalEntryId(entryId), context.GetCaller(), ct)).ToHttp();

    private static async Task<IResult> RunReconciliation(
        HttpContext context, ReconciliationService reconciliation, CancellationToken ct, bool freezeAffectedWallets = true) =>
        (await reconciliation.RunAsync(context.GetCaller(), freezeAffectedWallets, ct)).ToHttp();

    private static IResult IssueDevToken([FromBody] DevTokenRequest request, DevTokenIssuer issuer, TimeProvider clock)
    {
        var token = issuer.Issue(request.Subject, request.Name, request.Roles, clock.GetUtcNow());
        return token.ToHttp();
    }

    private static async Task<IResult> FundWallet(
        [FromBody] FundWalletCommand request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        HttpContext context,
        FundingService funding,
        CancellationToken ct)
    {
        var key = IdempotencyKey.Parse(idempotencyKey);
        if (key.IsFailure)
        {
            return HttpResults.Problem(key.Error);
        }

        var outcome = await funding.FundAsync(request, context.GetCaller(), key.Value, ct);
        return outcome.ToHttp(context, r => $"/api/journal-entries/{r.JournalEntryId}");
    }
}
