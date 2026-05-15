using StackExchange.Redis;
using System.Text.Json;

namespace Harness.Common.TestHooks;

/// <summary>
/// Redis-backed <see cref="IFaultInjector"/> implementation.
/// Stores armed hooks in Redis under <c>pacs:fault:{hookName}</c> so all
/// processes in the same PACS profile share the same fault state (§10).
/// Only registered when <c>Harness:TestMode = true</c>.
/// </summary>
public sealed class RedisFaultInjector(IConnectionMultiplexer redis) : IFaultInjector
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private const string KeyPrefix = "pacs:fault:";

    private static string Key(FaultHook hook) => $"{KeyPrefix}{hook}";

    /// <inheritdoc />
    public async Task FireAsync(FaultHook hook, CancellationToken ct = default)
    {
        var db  = redis.GetDatabase();
        var raw = await db.StringGetAsync(Key(hook));
        if (!raw.HasValue) return;

        var config = JsonSerializer.Deserialize<FaultHookConfig>(raw.ToString(), _json);
        if (config is null) return;

        // Decrement counter; disarm when it reaches 0
        if (config.Count > 0)
        {
            var remaining = config.Count - 1;
            if (remaining <= 0)
                await db.KeyDeleteAsync(Key(hook));
            else
            {
                var updated = config with { Count = remaining };
                await db.StringSetAsync(Key(hook), JsonSerializer.Serialize(updated, _json));
            }
        }

        switch (config.Mode)
        {
            case FaultHookMode.Pause:
                var delay = config.DurationMs.HasValue
                    ? TimeSpan.FromMilliseconds(config.DurationMs.Value)
                    : TimeSpan.FromSeconds(30);
                await Task.Delay(delay, ct);
                break;

            case FaultHookMode.Crash:
                // Hard exit — simulates a power cut / process kill
                Environment.Exit(1);
                break;

            case FaultHookMode.Throw:
                var typeName = config.ExceptionTypeName ?? typeof(InvalidOperationException).FullName!;
                var exType   = Type.GetType(typeName) ?? typeof(InvalidOperationException);
                throw (Exception)Activator.CreateInstance(exType,
                    $"Fault hook {hook} fired with mode Throw")!;

            // FaultHookMode.Noop: record visit, do nothing
        }
    }

    /// <inheritdoc />
    public async Task ArmAsync(FaultHook hook, FaultHookConfig config, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.StringSetAsync(Key(hook), JsonSerializer.Serialize(config, _json));
    }

    /// <inheritdoc />
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        var db   = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var keys = server.Keys(pattern: $"{KeyPrefix}*").ToArray();
        if (keys.Length > 0)
            await db.KeyDeleteAsync(keys);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<FaultHook, FaultHookConfig>> GetStateAsync(CancellationToken ct = default)
    {
        var db     = redis.GetDatabase();
        var result = new Dictionary<FaultHook, FaultHookConfig>();

        foreach (FaultHook hook in Enum.GetValues<FaultHook>())
        {
            var raw = await db.StringGetAsync(Key(hook));
            if (!raw.HasValue) continue;

            var config = JsonSerializer.Deserialize<FaultHookConfig>(raw.ToString(), _json);
            if (config is not null)
                result[hook] = config;
        }

        return result;
    }
}
