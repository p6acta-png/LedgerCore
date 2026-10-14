using LedgerCore.Application.Idempotency;
using LedgerCore.Application.Security;
using LedgerCore.Application.Transfers;
using LedgerCore.Domain.Common;
using Xunit;

namespace LedgerCore.UnitTests.Application;

public sealed class WalletAccessPolicyTests
{
    private static readonly CustomerId Owner = new(Guid.NewGuid());

    private static CallerContext Customer(CustomerId id) => new(id.ToString(), id, new HashSet<string> { Roles.Customer }, null);

    private static CallerContext Operator() => new("ops-1", null, new HashSet<string> { Roles.LedgerOperator }, null);

    [Fact]
    public void Owner_can_view_and_debit() =>
        Assert.True(WalletAccessPolicy.CanView(Customer(Owner), Owner) && WalletAccessPolicy.CanDebit(Customer(Owner), Owner));

    [Fact]
    public void Another_customer_can_neither_view_nor_debit()
    {
        var stranger = Customer(new CustomerId(Guid.NewGuid()));

        Assert.False(WalletAccessPolicy.CanView(stranger, Owner));
        Assert.False(WalletAccessPolicy.CanDebit(stranger, Owner));
    }

    [Fact]
    public void Operators_can_view_but_never_move_customer_money()
    {
        Assert.True(WalletAccessPolicy.CanView(Operator(), Owner));
        Assert.False(WalletAccessPolicy.CanDebit(Operator(), Owner));
    }

    [Fact]
    public void A_token_without_the_customer_role_cannot_debit_even_with_a_matching_subject()
    {
        var noRole = new CallerContext(Owner.ToString(), Owner, new HashSet<string>(), null);

        Assert.False(WalletAccessPolicy.CanDebit(noRole, Owner));
        Assert.False(WalletAccessPolicy.CanView(noRole, Owner));
    }
}

public sealed class IdempotencyKeyTests
{
    [Theory]
    [InlineData("3f2b6c1e-8a4d-4b7e-9c0d-1e2f3a4b5c6d")]
    [InlineData("order:2026-10-14.001")]
    [InlineData("abcdefgh")]
    public void Reasonable_keys_are_accepted(string key) => Assert.True(IdempotencyKey.Parse(key).IsSuccess);

    [Theory]
    [InlineData(null, "IDEMPOTENCY_KEY_REQUIRED")]
    [InlineData("", "IDEMPOTENCY_KEY_REQUIRED")]
    [InlineData("short", "IDEMPOTENCY_KEY_INVALID")]
    [InlineData("has space inside", "IDEMPOTENCY_KEY_INVALID")]
    [InlineData("quote'injection--", "IDEMPOTENCY_KEY_INVALID")]
    [InlineData("æøå-not-ascii", "IDEMPOTENCY_KEY_INVALID")]
    public void Missing_or_malformed_keys_are_rejected(string? key, string expectedCode) =>
        Assert.Equal(expectedCode, IdempotencyKey.Parse(key).Error.Code);

    [Fact]
    public void Keys_longer_than_128_characters_are_rejected() =>
        Assert.Equal("IDEMPOTENCY_KEY_INVALID", IdempotencyKey.Parse(new string('a', 129)).Error.Code);
}

public sealed class RequestFingerprintTests
{
    private sealed record Canonical(Guid From, Guid To, long Amount, string Currency, string Description);

    [Fact]
    public void Identical_requests_produce_identical_fingerprints()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();

        Assert.Equal(
            RequestFingerprint.Compute(new Canonical(from, to, 100, "NOK", "x")),
            RequestFingerprint.Compute(new Canonical(from, to, 100, "NOK", "x")));
    }

    [Fact]
    public void Any_field_change_changes_the_fingerprint()
    {
        var baseline = new Canonical(Guid.NewGuid(), Guid.NewGuid(), 100, "NOK", "x");
        var hash = RequestFingerprint.Compute(baseline);

        Assert.NotEqual(hash, RequestFingerprint.Compute(baseline with { Amount = 101 }));
        Assert.NotEqual(hash, RequestFingerprint.Compute(baseline with { To = Guid.NewGuid() }));
        Assert.NotEqual(hash, RequestFingerprint.Compute(baseline with { Description = "y" }));
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Stored_outcomes_round_trip_both_successes_and_errors()
    {
        var success = StoredOutcome.From(LedgerCore.Domain.Common.Result<Canonical>.Success(new Canonical(Guid.NewGuid(), Guid.NewGuid(), 5, "NOK", "d")), _ => null);
        var failure = StoredOutcome.From(LedgerCore.Domain.Common.Result<Canonical>.Failure(LedgerErrors.SelfTransfer), _ => null);

        Assert.Equal(5, success.ToResult<Canonical>().Value.Amount);
        Assert.Equal(LedgerErrors.SelfTransfer, failure.ToResult<Canonical>().Error);
    }
}

public sealed class TransferValidationTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void A_valid_command_is_normalized()
    {
        var result = TransferService.Validate(new CreateTransferCommand(A, B, 80_000, "NOK", "  rent  "));

        Assert.True(result.IsSuccess);
        Assert.Equal("rent", result.Value.Description);
        Assert.Equal(80_000, result.Value.Amount.MinorUnits);
    }

    [Theory]
    [InlineData(0, "INVALID_AMOUNT")]
    [InlineData(-80_000, "INVALID_AMOUNT")]
    [InlineData(long.MaxValue, "INVALID_AMOUNT")]
    public void Invalid_amounts_are_rejected(long amount, string code) =>
        Assert.Equal(code, TransferService.Validate(new CreateTransferCommand(A, B, amount, "NOK", null)).Error.Code);

    [Fact]
    public void Unsupported_currency_is_rejected() =>
        Assert.Equal("UNSUPPORTED_CURRENCY", TransferService.Validate(new CreateTransferCommand(A, B, 1, "USD", null)).Error.Code);

    [Fact]
    public void Self_transfer_is_rejected_before_touching_the_database() =>
        Assert.Equal("SELF_TRANSFER", TransferService.Validate(new CreateTransferCommand(A, A, 1, "NOK", null)).Error.Code);

    [Fact]
    public void Empty_wallet_ids_are_rejected() =>
        Assert.Equal("VALIDATION_FAILED", TransferService.Validate(new CreateTransferCommand(Guid.Empty, B, 1, "NOK", null)).Error.Code);
}
