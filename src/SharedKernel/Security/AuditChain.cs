using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace SharedKernel.Security;

/// <summary>
/// File-based hash-chained audit log for critical installer operations.
/// Each entry includes the hash of the previous entry, creating a tamper-evident chain.
/// Stored as JSON lines in D:\ePACSData\installer\audit-chain.jsonl.
/// </summary>
public sealed class AuditChain : IAuditChain
{
    private readonly IOptions<InstallerOptions> _options;
    private readonly ILogger<AuditChain> _logger;
    private readonly string _chainFilePath;
    private string? _lastHash;
    private long _lastSequence;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AuditChain(IOptions<InstallerOptions> options, ILogger<AuditChain> logger)
    {
        _options = options;
        _logger = logger;
        _chainFilePath = Path.Combine(options.Value.DataRoot, "installer", "audit-chain.jsonl");
    }

    public async Task<AuditChainEntry> AppendAsync(AuditChainEntry entry, CancellationToken cancellationToken = default)
    {
        // Load last hash if not cached
        if (_lastHash is null)
        {
            await LoadLastEntryAsync(cancellationToken);
        }

        var sequence = ++_lastSequence;
        var chainedEntry = entry with
        {
            SequenceNumber = sequence,
            PreviousHash = _lastHash,
            EntryHash = ComputeEntryHash(sequence, entry, _lastHash)
        };

        // Append to file (append-only for tamper evidence)
        var directory = Path.GetDirectoryName(_chainFilePath);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(chainedEntry, JsonOptions);
        await File.AppendAllTextAsync(_chainFilePath, json + "\n", cancellationToken);

        _lastHash = chainedEntry.EntryHash;

        _logger.LogInformation("Audit chain entry #{Sequence}: {EventType} by {Actor}.",
            sequence, entry.EventType, entry.Actor);

        return chainedEntry;
    }

    public async Task<ChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_chainFilePath))
        {
            return new ChainVerificationResult { Valid = true, TotalEntries = 0, VerifiedEntries = 0 };
        }

        var lines = await File.ReadAllLinesAsync(_chainFilePath, cancellationToken);
        string? previousHash = null;
        var verified = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var entry = JsonSerializer.Deserialize<AuditChainEntry>(line, JsonOptions);
            if (entry is null)
            {
                return new ChainVerificationResult
                {
                    Valid = false,
                    TotalEntries = lines.Length,
                    VerifiedEntries = verified,
                    FirstBrokenSequence = verified + 1,
                    ErrorMessage = $"Failed to deserialize entry at line {verified + 1}."
                };
            }

            // Verify previous hash link
            if (!string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal))
            {
                return new ChainVerificationResult
                {
                    Valid = false,
                    TotalEntries = lines.Length,
                    VerifiedEntries = verified,
                    FirstBrokenSequence = entry.SequenceNumber,
                    ErrorMessage = $"Chain broken at sequence {entry.SequenceNumber}: previous hash mismatch."
                };
            }

            // Verify entry hash
            var expectedHash = ComputeEntryHash(entry.SequenceNumber, entry, previousHash);
            if (!string.Equals(entry.EntryHash, expectedHash, StringComparison.Ordinal))
            {
                return new ChainVerificationResult
                {
                    Valid = false,
                    TotalEntries = lines.Length,
                    VerifiedEntries = verified,
                    FirstBrokenSequence = entry.SequenceNumber,
                    ErrorMessage = $"Entry hash mismatch at sequence {entry.SequenceNumber}: content tampered."
                };
            }

            previousHash = entry.EntryHash;
            verified++;
        }

        _logger.LogInformation("Audit chain verified: {Verified} entries, integrity intact.", verified);
        return new ChainVerificationResult { Valid = true, TotalEntries = verified, VerifiedEntries = verified };
    }

    public async Task<IReadOnlyList<AuditChainEntry>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_chainFilePath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(_chainFilePath, cancellationToken);
        return lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(count)
            .Select(l => JsonSerializer.Deserialize<AuditChainEntry>(l, JsonOptions)!)
            .Where(e => e is not null)
            .ToList();
    }

    private async Task LoadLastEntryAsync(CancellationToken ct)
    {
        if (!File.Exists(_chainFilePath))
        {
            _lastHash = null;
            _lastSequence = 0;
            return;
        }

        var lines = await File.ReadAllLinesAsync(_chainFilePath, ct);
        var lastLine = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (lastLine is null)
        {
            _lastHash = null;
            _lastSequence = 0;
            return;
        }

        var lastEntry = JsonSerializer.Deserialize<AuditChainEntry>(lastLine, JsonOptions);
        _lastHash = lastEntry?.EntryHash;
        _lastSequence = lastEntry?.SequenceNumber ?? 0;
    }

    private static string ComputeEntryHash(long sequence, AuditChainEntry entry, string? previousHash)
    {
        var input = $"{sequence}|{entry.Timestamp:O}|{entry.EventType}|{entry.Actor}|{entry.Description}|{entry.DataJson ?? ""}|{previousHash ?? "GENESIS"}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
