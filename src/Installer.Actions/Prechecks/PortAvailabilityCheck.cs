using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Configuration;

namespace Installer.Actions.Prechecks;

/// <summary>
/// Validates that required ports are not in use by non-ePACS processes.
/// Port list is configurable via PrecheckOptions.RequiredPorts.
/// </summary>
public sealed class PortAvailabilityCheck : IPrecheck
{
    private readonly IOptions<PrecheckOptions> _options;
    private readonly ILogger<PortAvailabilityCheck> _logger;

    public PortAvailabilityCheck(IOptions<PrecheckOptions> options, ILogger<PortAvailabilityCheck> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string CheckId => "PORTS";
    public string Name => "Port Availability";
    public int Order => 40;

    public Task<PrecheckResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var requiredPorts = _options.Value.RequiredPorts;
        var blockedPorts = new List<int>();

        foreach (var port in requiredPorts)
        {
            if (IsPortInUse(port))
            {
                blockedPorts.Add(port);
            }
        }

        if (blockedPorts.Count > 0)
        {
            var portList = string.Join(", ", blockedPorts);
            _logger.LogError("Port availability check failed. Blocked ports: {Ports}.", portList);
            return Task.FromResult(new PrecheckResult
            {
                CheckId = CheckId,
                Name = Name,
                Severity = PrecheckSeverity.Block,
                Message = $"Port(s) {portList} are in use. Please free them or configure alternate ports.",
                TechnicalDetail = $"Blocked ports: {portList}. Required ports: {string.Join(", ", requiredPorts)}.",
                ErrorCode = "ERP-INST-PRE-0007"
            });
        }

        _logger.LogInformation("Port availability check passed. All required ports are free.");
        return Task.FromResult(new PrecheckResult
        {
            CheckId = CheckId,
            Name = Name,
            Severity = PrecheckSeverity.Pass,
            Message = "All required ports are available.",
            TechnicalDetail = $"Checked ports: {string.Join(", ", requiredPorts)}."
        });
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false; // Port is free
        }
        catch (SocketException)
        {
            return true; // Port is in use
        }
    }
}
