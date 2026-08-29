namespace SharedKernel.Configuration;

/// <summary>
/// Which infrastructure components this installation includes.
///
/// WHY THIS IS CONFIGURABLE RATHER THAN FIXED. The release manifest marks every payload
/// <c>required: true</c>, including Kafka (110 MB) and the JRE it needs (180 MB). Measured
/// against the L2-R2 estate, that is 290 MB carried to every site for something no deployment
/// turns on: there is no Kafka role in <c>ops/ansible</c>, no Kafka service in
/// <c>ops/compose</c>, and no mention of Kafka anywhere in <c>ops/</c>. The client library is
/// referenced by FAS and Loans, but every publisher sits behind the orchestration kill-switch
/// (<c>FAS/ServiceRegistration.cs:286</c>).
///
/// So eventing is off by default and the payload is conditional. Turning it on is a deliberate
/// act that also enlarges the medium.
/// </summary>
public sealed class ComponentsOptions
{
    public const string SectionName = "Components";

    public CacheComponentOptions Cache { get; set; } = new();
    public EventingComponentOptions Eventing { get; set; } = new();

    /// <summary>
    /// Service-map groups to install, derived from the switches above. The topology loader
    /// filters on these, so a component that is off is never registered as a Windows service —
    /// rather than being registered and left stopped, which looks like a failed install.
    /// </summary>
    public IReadOnlyCollection<string> EnabledGroups()
    {
        var groups = new List<string> { "core" };

        if (Cache.Enabled)
        {
            groups.Add("cache");
        }

        if (Eventing.Enabled)
        {
            groups.Add("eventing");
        }

        return groups;
    }
}

public sealed class CacheComponentOptions
{
    /// <summary>
    /// Redis is not optional in practice and the default reflects that. The estate's ansible
    /// role puts it plainly: the cache holds the idempotency keys (<c>fas:idem:</c>) that stop
    /// a retried request posting twice, which is a correctness concern and not a performance
    /// one. Turning this off is a lab-only choice.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>redis</c> or <c>garnet</c>.
    ///
    /// THE OPEN QUESTION, stated here because this is where it is chosen. ADR-0002 selected
    /// Garnet — MIT-licensed and Windows-native, which matters because there is no official
    /// Redis build for Windows. But L2-R2 runs <c>StackExchange.Redis</c> against real Redis,
    /// and <c>l3_ERPClient/Security/RateLimiting/RedisRateLimitStore.cs</c> uses
    /// <c>LoadedLuaScript</c> (SCRIPT LOAD / EVALSHA) for sliding-window and concurrency
    /// limiting — server-side Lua is Garnet's weakest compatibility area, and that particular
    /// consumer is a security control.
    ///
    /// The default is <c>redis</c>: match what the estate actually runs, and make the
    /// substitution an explicit decision backed by a test run of the estate's own caching and
    /// rate-limiting suites against Garnet. Do not flip this because the payload is easier.
    /// </summary>
    public string Provider { get; set; } = "redis";
}

public sealed class EventingComponentOptions
{
    /// <summary>
    /// Off by default — see the note on <see cref="ComponentsOptions"/>. When false, the Kafka
    /// and JRE payloads are not required on the medium and the eventing service is not
    /// registered.
    /// </summary>
    public bool Enabled { get; set; }
}
