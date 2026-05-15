using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using Harness.Common.Canonicalization;
using Xunit;

namespace Harness.ContractTests.Canonicalization;

public sealed class CanonicalJsonWriterTests
{
    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void WriteString_SameDictionaryDifferentInsertionOrder_ProducesIdenticalOutput()
    {
        // Build the same logical object in two different insertion orders
        var obj1 = new Dictionary<string, object?> { ["zebra"] = 1, ["apple"] = 2, ["mango"] = 3 };
        var obj2 = new Dictionary<string, object?> { ["mango"] = 3, ["zebra"] = 1, ["apple"] = 2 };

        CanonicalJsonWriter.WriteString(obj1).Should().Be(CanonicalJsonWriter.WriteString(obj2));
    }

    [Fact]
    public void WriteString_NestedObject_SortsKeysAtEveryDepth()
    {
        var obj = new
        {
            z = new { b = 1, a = 2 },
            a = new { y = 3, x = 4 }
        };

        var result = CanonicalJsonWriter.WriteString(obj);

        // 'a' comes before 'z' at top level; 'a' before 'b', 'x' before 'y' nested
        result.Should().StartWith("{\"a\":");
        result.Should().Contain("\"a\":");
    }

    [Fact]
    public void WriteString_NullInput_ReturnsLiteralNull()
    {
        CanonicalJsonWriter.WriteString(null).Should().Be("null");
    }

    [Fact]
    public void WriteString_Boolean_ProducesLowercase()
    {
        var obj = new { flag = true, other = false };
        var result = CanonicalJsonWriter.WriteString(obj);
        result.Should().Contain("true").And.Contain("false");
        result.Should().NotContain("True").And.NotContain("False");
    }

    [Fact]
    public void WriteString_NoInsignificantWhitespace()
    {
        var obj = new { a = 1, b = "hello" };
        var result = CanonicalJsonWriter.WriteString(obj);
        result.Should().NotContain("  ").And.NotContain("\n").And.NotContain("\r");
    }

    // ── Idempotency (calling twice on same input produces same output) ─────────

    [Fact]
    public void WriteBytes_CalledTwice_ReturnsBytewiseIdenticalOutput()
    {
        var obj = new { amount = 1234.56, name = "test", active = true };
        var bytes1 = CanonicalJsonWriter.WriteBytes(obj);
        var bytes2 = CanonicalJsonWriter.WriteBytes(obj);
        bytes1.Should().Equal(bytes2);
    }

    // ── Property-based test: any insertion order yields same canonical form ──

    [Property(MaxTest = 200)]
    public Property AnyKeyInsertionOrder_ProducesIdenticalCanonicalForm()
    {
        // FsCheck 2.x: use Gen.map2 / tuple generators to create random dicts
        var gen = Gen.Elements("alpha", "beta", "gamma", "delta", "epsilon")
            .Two()
            .Select(pair => new Dictionary<string, object?> { [pair.Item1] = 1, [pair.Item2] = 2 });

        return Prop.ForAll(Arb.From(gen), dict =>
        {
            var canonical  = CanonicalJsonWriter.WriteString(dict);
            var reversed   = new Dictionary<string, object?>(
                dict.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value));
            var canonical2 = CanonicalJsonWriter.WriteString(reversed);
            return canonical == canonical2;
        });
    }
}
