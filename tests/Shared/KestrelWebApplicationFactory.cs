using Microsoft.AspNetCore.Mvc.Testing;
#if !NET10_0_OR_GREATER
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
#endif

namespace MessageBroker.Testing;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that can also serve on a real Kestrel port, for callers
/// outside the process (scripts, the load test). .NET 10 has <c>UseKestrel</c> and <c>StartServer</c> built in;
/// on .NET 9 this class adds both with the same signatures, so the call sites are the same on either framework.
/// </summary>
public abstract class KestrelWebApplicationFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
#if !NET10_0_OR_GREATER
    private int? _port;

    /// <summary>Serves on 127.0.0.1:<paramref name="port"/>. Call before the host is created.</summary>
    public void UseKestrel(int port) => _port = port;

    /// <summary>Creates and starts the host now, listening on the Kestrel port.</summary>
    public void StartServer() => _ = Services;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (_port is not { } port)
            return base.CreateHost(builder);

        // One host only: building the deferred host builder a second time would run Program twice (two sets
        // of startup tasks and background loops). Kestrel replaces the TestServer registered by the factory.
        builder.ConfigureWebHost(web => web.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port)));
        var host = builder.Build();
        host.Start();
        return new KestrelHost(host);
    }

    /// <summary>
    /// The factory reads IServer from the host and casts it to TestServer. Hand it an idle one; the app's
    /// own services, and the Kestrel server that is actually listening, are untouched.
    /// </summary>
    private sealed class KestrelHost(IHost inner) : IHost
    {
        public IServiceProvider Services { get; } = new ServicesWithIdleTestServer(inner.Services);
        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
        public void Dispose() => inner.Dispose();
    }

    private sealed class ServicesWithIdleTestServer(IServiceProvider inner) : IServiceProvider
    {
        private TestServer? _idle;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServer) ? _idle ??= new TestServer(inner) : inner.GetService(serviceType);
    }
#endif
}
