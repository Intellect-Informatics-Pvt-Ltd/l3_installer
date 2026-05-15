#pragma warning disable CA1716 // Rename 'Error' — kept for parity with Intellect.Erp.Observability contract
#pragma warning disable CA1848 // Use LoggerMessage delegates — not needed for a thin adapter shim
#pragma warning disable CA2254 // Template should not vary — intentional pass-through
using Microsoft.Extensions.Logging;

namespace Harness.Common.Observability;

/// <summary>
/// Structured application logger used throughout the harness.
/// Mirrors the contract from <c>Intellect.Erp.Observability.Abstractions</c>.
/// <para>
/// Components MUST use this interface rather than the raw
/// <see cref="ILogger{TCategoryName}"/> so that the redaction engine and
/// structured checkpoints remain consistent.
/// </para>
/// </summary>
public interface IAppLogger<T>
{
    void Information(string messageTemplate, params object?[] args);
    void Warning(string messageTemplate, params object?[] args);

    // CA1716: name kept as 'Error' intentionally; matches platform contract.
    void Error(Exception? ex, string messageTemplate, params object?[] args);
    void Debug(string messageTemplate, params object?[] args);

    /// <summary>
    /// Creates a logical operation scope. Returns an <see cref="IDisposable"/>
    /// that closes the scope on disposal.
    /// </summary>
    IDisposable BeginOperation(string moduleName, string feature, string operation);

    /// <summary>
    /// Emits a named checkpoint with a dictionary of structured properties.
    /// </summary>
    void Checkpoint(string name, IReadOnlyDictionary<string, object?> data);
}

/// <summary>
/// Default implementation backed by the standard <see cref="ILogger{T}"/>.
/// Replace with the Intellect.Erp.Observability implementation for full
/// redaction and audit-hook support.
/// </summary>
public sealed class DefaultAppLogger<T>(ILogger<T> inner) : IAppLogger<T>
{
    public void Information(string messageTemplate, params object?[] args) => inner.Log(LogLevel.Information, messageTemplate, args);
    public void Warning(string messageTemplate, params object?[] args)     => inner.Log(LogLevel.Warning,     messageTemplate, args);
    public void Error(Exception? ex, string messageTemplate, params object?[] args) =>
        inner.Log(LogLevel.Error, ex, messageTemplate, args);
    public void Debug(string messageTemplate, params object?[] args) => inner.Log(LogLevel.Debug, messageTemplate, args);

    public IDisposable BeginOperation(string moduleName, string feature, string operation) =>
        inner.BeginScope("{Module}.{Feature}.{Operation}", moduleName, feature, operation)
        ?? NullScope.Instance;

    public void Checkpoint(string name, IReadOnlyDictionary<string, object?> data) =>
        inner.Log(LogLevel.Information, "[Checkpoint:{Name}] {@Data}", name, data);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
#pragma warning restore CA1716
#pragma warning restore CA1848
#pragma warning restore CA2254
