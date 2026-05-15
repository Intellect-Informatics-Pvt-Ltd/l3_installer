namespace Harness.Common.Errors;

/// <summary>
/// Creates typed exceptions keyed to error catalog codes.
/// Mirrors <c>Intellect.Erp.ErrorHandling.IErrorFactory</c>.
/// All thrown exceptions go through this interface so error codes remain
/// traceable to <c>packaging/error-catalog/harness.yaml</c>.
/// </summary>
public interface IErrorFactory
{
    /// <summary>
    /// Creates a <see cref="HarnessException"/> for the given catalog code,
    /// augmented with <paramref name="contextMessage"/>.
    /// </summary>
    HarnessException FromCatalog(string errorCode, string contextMessage);

    /// <summary>Throws immediately after building the exception.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716",
        Justification = "Throw matches platform contract; CA1716 suppressed intentionally.")]
    void Throw(string errorCode, string contextMessage);
}

/// <summary>
/// Strongly typed exception carrying a structured error code that maps to an
/// entry in <c>packaging/error-catalog/harness.yaml</c>.
/// </summary>
public sealed class HarnessException : Exception
{
    public string ErrorCode { get; }

    public HarnessException(string errorCode, string message)
        : base($"[{errorCode}] {message}")
    {
        ErrorCode = errorCode;
    }

    public HarnessException(string errorCode, string message, Exception inner)
        : base($"[{errorCode}] {message}", inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Default implementation — reads no YAML file; just wraps the code and message.
/// Replace with the full Intellect.Erp.ErrorHandling implementation to get
/// HTTP-status mapping and severity routing from the YAML catalog.
/// </summary>
public sealed class DefaultErrorFactory : IErrorFactory
{
    public static readonly DefaultErrorFactory Instance = new();

    public HarnessException FromCatalog(string errorCode, string contextMessage) =>
        new(errorCode, contextMessage);

    public void Throw(string errorCode, string contextMessage) =>
        throw FromCatalog(errorCode, contextMessage);
}
