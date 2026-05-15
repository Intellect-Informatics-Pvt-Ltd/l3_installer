namespace Harness.Common.Redaction;

/// <summary>
/// Marks a property as sensitive. The <c>Intellect.Erp.Observability.Core</c>
/// redaction engine masks the value in all log output.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SensitiveAttribute : Attribute { }

/// <summary>
/// Marks a property as excluded from logging entirely.
/// The value is never written to any log sink.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class DoNotLogAttribute : Attribute { }

/// <summary>
/// Marks a property for partial masking.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class MaskAttribute(string mask = "****") : Attribute
{
    /// <summary>Replacement string used in log output.</summary>
    public string Mask { get; } = mask;
}
