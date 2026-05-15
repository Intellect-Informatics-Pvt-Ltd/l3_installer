using FluentAssertions;
using Harness.Common.Canonicalization;
using Harness.Common.Envelope;
using Harness.Common.Identifiers;
using Xunit;

namespace Harness.ContractTests.Canonicalization;

public sealed class PayloadHasherTests
{
    [Fact]
    public void Compute_SameInputsCalledTwice_ReturnsSameHash()
    {
        var payload     = new { amount = 100m, voucherNo = "VCH-001" };
        var beforeState = new { amount = 99m,  voucherNo = "VCH-001" };
        var meta        = new AmendmentMeta { Reason = "Correction", Approver = "user@pacs" };

        var hash1 = PayloadHasher.Compute(payload, beforeState, meta);
        var hash2 = PayloadHasher.Compute(payload, beforeState, meta);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Compute_NullBeforeState_ProducesDifferentHashThanNonNull()
    {
        var payload = new { amount = 100m };

        var hashWithNull = PayloadHasher.Compute(payload, null, null);
        var hashWithVal  = PayloadHasher.Compute(payload, new { amount = 50m }, null);

        hashWithNull.Should().NotBe(hashWithVal);
    }

    [Fact]
    public void Compute_MutatedPayload_ProducesDifferentHash()
    {
        var original = new { amount = 100.00m };
        var mutated  = new { amount = 100.01m };

        PayloadHasher.Compute(original, null, null)
            .Should().NotBe(PayloadHasher.Compute(mutated, null, null));
    }

    [Fact]
    public void Compute_HashIsLowercaseHex()
    {
        var hash = PayloadHasher.Compute(new { x = 1 }, null, null);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Verify_CorrectEnvelope_ReturnsTrue()
    {
        var envelope = new EventEnvelopeBuilder()
            .WithPacsId("PACS-AP-0001")
            .WithEntityType("voucher")
            .WithEntityId("42")
            .WithChangeType(ChangeType.INSERT)
            .WithSequenceNo(1)
            .WithIdempotencyKey(IdempotencyKey.Format(
                "PACS-AP-0001", "voucher", "42", "INSERT", DateTimeOffset.UtcNow))
            .WithPayload(new { amount = 1000m })
            .Build();

        PayloadHasher.Verify(envelope).Should().BeTrue();
    }

    [Fact]
    public void Verify_TamperedEnvelope_ReturnsFalse()
    {
        var envelope = new EventEnvelopeBuilder()
            .WithPacsId("PACS-AP-0001")
            .WithEntityType("voucher")
            .WithEntityId("42")
            .WithChangeType(ChangeType.INSERT)
            .WithSequenceNo(1)
            .WithPayload(new { amount = 1000m })
            .Build();

        // Create a version with the same payloadHash but mutated payload
        var tampered = envelope with { PayloadHash = "0000000000000000000000000000000000000000000000000000000000000000" };

        PayloadHasher.Verify(tampered).Should().BeFalse();
    }
}

public sealed class IdempotencyKeyTests
{
    [Fact]
    public void Format_ProducesExpectedPattern()
    {
        var ts  = new DateTimeOffset(2026, 5, 14, 10, 0, 0, TimeSpan.Zero);
        var key = IdempotencyKey.Format("PACS-AP-0001", "voucher", "42", "INSERT", ts);

        key.Should().Be("PACS-AP-0001:voucher:42:INSERT:2026-05-14T10:00:00Z");
        IdempotencyKey.ValidPattern().IsMatch(key).Should().BeTrue();
    }

    [Fact]
    public void TryParse_ValidKey_RoundTrips()
    {
        var ts  = new DateTimeOffset(2026, 5, 14, 10, 0, 0, TimeSpan.Zero);
        var key = IdempotencyKey.Format("PACS-AP-0001", "loan_application", "LA-001", "AMENDMENT", ts);

        var ok = IdempotencyKey.TryParse(key,
            out var pacsId, out var entityType, out var entityId,
            out var changeType, out var parsed);

        ok.Should().BeTrue();
        pacsId.Should().Be("PACS-AP-0001");
        entityType.Should().Be("loan_application");
        entityId.Should().Be("LA-001");
        changeType.Should().Be("AMENDMENT");
        parsed.Should().BeCloseTo(ts, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ValidPattern_AllChangeTypes_Match()
    {
        foreach (var ct in new[] { "INSERT", "UPDATE", "DELETE", "AMENDMENT" })
        {
            var key = IdempotencyKey.Format("PACS-AP-0001", "voucher", "1", ct,
                DateTimeOffset.UtcNow);
            IdempotencyKey.ValidPattern().IsMatch(key).Should().BeTrue(
                because: $"change type {ct} should be valid");
        }
    }
}
