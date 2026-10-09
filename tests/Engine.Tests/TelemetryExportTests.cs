using System.Diagnostics;
using Engine.Hosting.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;

namespace Engine.Tests.Hosting;

/// <summary>
/// Exporting is off unless an endpoint is configured, and a bad or dead endpoint costs telemetry
/// and never the engine.
/// </summary>
public class TelemetryExportTests
{
    private static (TelemetryExport Export, ServiceProvider Provider) Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEngineTelemetry();
        var export = services.AddTelemetryExport(configuration);

        return (export, services.BuildServiceProvider());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_endpoint_nothing_is_registered_that_could_export(string? endpoint)
    {
        // Not an exporter pointed at the default localhost:4317, which would retry a dead port for
        // the engine's whole life: no provider at all, so nothing even listens.
        var (export, provider) = Build((TelemetryExportExtensions.EndpointKey, endpoint));
        using var _ = provider;

        export.Enabled.ShouldBeFalse();
        export.Description.ShouldContain("not set");
        provider.GetService<TracerProvider>().ShouldBeNull();
        provider.GetService<MeterProvider>().ShouldBeNull();
        provider.GetRequiredService<TelemetryExport>().ShouldBe(export);
    }

    [Theory]
    [InlineData("collector:4317", null)]
    [InlineData("ftp://collector:4317", null)]
    [InlineData("/v1/traces", null)]
    [InlineData("http://collector:4317", "thrift")]
    public void A_setting_the_exporter_cannot_use_is_reported_and_ignored_not_refused(string endpoint, string? protocol)
    {
        // Telemetry is not a reason to stop an engine whose exits close losing positions.
        var (export, provider) = Build(
            (TelemetryExportExtensions.EndpointKey, endpoint),
            (TelemetryExportExtensions.ProtocolKey, protocol));
        using var _ = provider;

        export.Enabled.ShouldBeFalse();
        export.Description.ShouldStartWith("not exported");
        provider.GetService<TracerProvider>().ShouldBeNull();
        provider.GetService<MeterProvider>().ShouldBeNull();
    }

    [Theory]
    [InlineData("http://collector:4317", null, "grpc")]
    [InlineData("http://collector:4318", "http/protobuf", "http/protobuf")]
    public void With_an_endpoint_both_metrics_and_traces_are_exported(string endpoint, string? protocol, string described)
    {
        var (export, provider) = Build(
            (TelemetryExportExtensions.EndpointKey, endpoint),
            (TelemetryExportExtensions.ProtocolKey, protocol));
        using var _ = provider;

        export.Enabled.ShouldBeTrue();
        export.Description.ShouldContain(described);
        provider.GetService<TracerProvider>().ShouldNotBeNull();
        provider.GetService<MeterProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void The_startup_line_never_repeats_credentials_from_the_url()
    {
        var (export, provider) = Build((TelemetryExportExtensions.EndpointKey, "https://someone:hunter2@collector.example:4317/"));
        using var _ = provider;

        export.Description.ShouldBe("exported over OTLP (grpc) to https://collector.example:4317");
        export.Description.ShouldNotContain("hunter2");
    }

    [Theory]
    // Nothing listening: refused at once.
    [InlineData("http://127.0.0.1:9")]
    // An address that does not answer at all, which is what a collector behind a dead route looks
    // like: the connect hangs until the exporter's own timeout gives up.
    [InlineData("http://10.255.255.1:4317")]
    public async Task A_dead_collector_never_stops_the_engine_or_holds_up_its_shutdown(string endpoint)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExportExtensions.EndpointKey] = endpoint,
        });
        builder.Services.AddEngineTelemetry();
        builder.Services.AddTelemetryExport(builder.Configuration).Enabled.ShouldBeTrue();

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        // A span and a decision, so both exporters have a batch to fail to send.
        using (EngineTelemetry.ActivitySource.StartActivity("a cycle"))
        {
            host.Services.GetRequiredService<EngineTelemetry>()
                .DecisionStored(Engine.Application.UseCases.DecisionOutcome.NoAction, Engine.Domain.Trading.TradingMode.Paper);
        }

        var stopping = Stopwatch.StartNew();
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
        stopping.Stop();

        // Compose gives a container ten seconds between SIGTERM and SIGKILL.
        stopping.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}
