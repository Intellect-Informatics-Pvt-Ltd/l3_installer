using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SharedKernel.Hosting;

/// <summary>
/// The two routes the installer's service map is written against — <c>/health/live</c> and
/// <c>/health/ready</c> — on a plain <see cref="HttpListener"/>, so a worker service can answer
/// them without taking a dependency on ASP.NET Core.
///
/// WHY. The map probed <c>http://127.0.0.1:5080/health/live</c> (sync agent) and <c>:5090</c>
/// (installer agent) from the day it was written, and neither agent hosted a listener, so the
/// installer's own agents would have failed the installer's own health gate (G31). Live means the
/// process is up and this loop is running; ready means whoever owns the process has said so —
/// a worker that is still loading its state answers 503 until it flips the flag.
/// </summary>
public sealed partial class HealthEndpoint : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _ready;
    private Task? _loop;

    public HealthEndpoint(int port, ILogger logger, bool ready = false)
    {
        _logger = logger;
        _ready = ready;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/health/");
        Port = port;
    }

    public int Port { get; }

    public bool Ready
    {
        get => _ready;
        set => _ready = value;
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
        LogListening(_logger, Port);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var (status, body) = path switch
            {
                "/health/live" => (200, "{\"status\":\"live\"}"),
                "/health/ready" => _ready ? (200, "{\"status\":\"ready\"}") : (503, "{\"status\":\"not ready\"}"),
                _ => (404, "{\"status\":\"unknown route\"}")
            };

            try
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                ctx.Response.Close();
            }
            catch (HttpListenerException)
            {
                // The client went away mid-answer. Nothing to do; the next probe asks again.
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 2850, Level = LogLevel.Information,
        Message = "Health endpoint listening on http://127.0.0.1:{Port}/health/live and /health/ready.")]
    private static partial void LogListening(ILogger logger, int port);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}
