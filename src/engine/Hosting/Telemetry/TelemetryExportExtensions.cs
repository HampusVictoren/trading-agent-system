namespace Engine.Hosting.Telemetry;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

/// <summary>What startup decided about exporting, so it can be said once in the log.</summary>
public sealed record TelemetryExport(bool Enabled, string Description);

public static class TelemetryExportExtensions
{
    /// <summary>The standard OpenTelemetry variables, read through configuration like any other key.</summary>
    public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    public const string ServiceNameKey = "OTEL_SERVICE_NAME";
    public const string TimeoutKey = "OTEL_EXPORTER_OTLP_TIMEOUT";
    public const string DisabledKey = "OTEL_SDK_DISABLED";

    /// <summary>
    /// The per-signal endpoints the OpenTelemetry specification also defines. Not supported: see
    /// <see cref="AddTelemetryExport"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> PerSignalEndpointKeys =
    [
        "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
        "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
        "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT",
    ];

    /// <summary>
    /// Three seconds per export unless <c>OTEL_EXPORTER_OTLP_TIMEOUT</c> says otherwise, rather
    /// than the SDK's ten.
    /// </summary>
    /// <remarks>
    /// Measured: against a collector address that never answers, the SDK's default made stopping
    /// the engine take about fifteen seconds, because both providers flush on the way out and each
    /// waits out its timeout. Docker's default is SIGKILL ten seconds after SIGTERM, and compose
    /// gives the engine 45 s (docker-compose.yml) to cover the host's own 30 s shutdown as well. A
    /// collector on the same host answers in milliseconds, so three seconds costs nothing when it
    /// is up and keeps shutdown inside the window when it is not.
    /// </remarks>
    public const int DefaultTimeoutMilliseconds = 3000;

    public const string DefaultServiceName = "engine";

    /// <summary>The HTTP client's own ActivitySource, native since .NET 9.</summary>
    public const string HttpClientSource = "System.Net.Http";

    /// <summary>
    /// Exports the engine's metrics and traces over OTLP - but only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> names somewhere to send them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off unless asked for, and never fatal.</b> The OTLP exporter's own default is
    /// <c>localhost:4317</c>, so registering it unconditionally would have every engine without a
    /// collector - which is every engine today - retry a dead port in the background for its
    /// whole life. Without an endpoint, no OpenTelemetry provider is registered at all: nothing
    /// listens, no span is created, no header is sent, and the instruments cost what
    /// System.Diagnostics costs with nobody subscribed.
    /// </para>
    /// <para>
    /// An endpoint that is not an absolute http(s) URL, or a protocol the exporter does not know,
    /// is reported and ignored rather than refused. Telemetry is not a reason to stop an engine
    /// whose exits are what close losing positions; every other setting here that is wrong is a
    /// refusal at startup, and this one is deliberately not.
    /// </para>
    /// <para>
    /// <b>Two more standard variables.</b> <c>OTEL_SDK_DISABLED=true</c> is honoured: nothing is
    /// exported, whatever the endpoint says, so a collector can be switched off without unsetting
    /// it. The per-signal endpoints (<see cref="PerSignalEndpointKeys"/>) are not supported: one
    /// endpoint takes both signals, and the engine never ships logs. Rather than half-honour them -
    /// the exporter itself would read a per-signal endpoint and send a signal somewhere other than
    /// the place the startup line names - setting any of them turns export off and says to set
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> instead. The other <c>OTEL_*</c> selectors, such as
    /// <c>OTEL_TRACES_EXPORTER</c>, are not read.
    /// </para>
    /// <para>
    /// With an endpoint, the exporter runs on a background thread with its own timeout
    /// (<see cref="DefaultTimeoutMilliseconds"/>). A collector that is down costs a failed batch,
    /// reported on the SDK's own EventSource, and never a cycle.
    /// </para>
    /// <para>
    /// What is exported: the <c>Engine</c> meter, the <c>Engine</c> ActivitySource and the HTTP
    /// client's spans, whose URLs carry a ticker and nothing secret - the API key and the HMAC
    /// signature are headers, which are not recorded, and .NET redacts query strings by default.
    /// Not exported: EF Core and Npgsql, whose spans would carry SQL, and the logs.
    /// </para>
    /// </remarks>
    public static TelemetryExport AddTelemetryExport(this IServiceCollection services, IConfiguration configuration)
    {
        var export = Decide(configuration);
        services.AddSingleton(export);

        if (!export.Enabled)
            return export;

        var serviceName = configuration[ServiceNameKey] is { Length: > 0 } named ? named : DefaultServiceName;

        // The per-signal exporters rather than UseOtlpExporter, which turns on log export as well.
        // The engine's log lines carry the risk rules' reasons and what the agents answered, and
        // they already have a home - stdout, and compose's log driver - so shipping them is a
        // decision of its own rather than a side effect of this one. Each exporter reads the
        // endpoint, the protocol and any headers from configuration itself, which is the
        // environment in a container: the same variables every other OTel SDK reads.
        var timeoutConfigured = !string.IsNullOrWhiteSpace(configuration[TimeoutKey]);

        void Bounded(OtlpExporterOptions options)
        {
            if (!timeoutConfigured)
                options.TimeoutMilliseconds = DefaultTimeoutMilliseconds;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(EngineTelemetry.Name)
                .AddSource(HttpClientSource)
                .AddOtlpExporter(Bounded))
            .WithMetrics(metrics => metrics
                .AddMeter(EngineTelemetry.Name)
                .AddOtlpExporter(Bounded));

        return export;
    }

    private static TelemetryExport Decide(IConfiguration configuration)
    {
        if (bool.TryParse(configuration[DisabledKey]?.Trim(), out var disabled) && disabled)
            return new TelemetryExport(false, $"not exported: {DisabledKey} is true");

        if (PerSignalEndpointKeys.FirstOrDefault(key => !string.IsNullOrWhiteSpace(configuration[key])) is { } perSignal)
            return new TelemetryExport(
                false, $"not exported: {perSignal} is set, and per-signal endpoints are not supported - set {EndpointKey} instead");

        var endpoint = configuration[EndpointKey];

        if (string.IsNullOrWhiteSpace(endpoint))
            return new TelemetryExport(false, $"not exported: {EndpointKey} is not set");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new TelemetryExport(false, $"not exported: {EndpointKey} is not an absolute http(s) URL");

        var protocol = configuration[ProtocolKey];

        if (!string.IsNullOrWhiteSpace(protocol) && protocol is not ("grpc" or "http/protobuf"))
            return new TelemetryExport(false, $"not exported: {ProtocolKey} must be grpc or http/protobuf");

        // Scheme, host and port only. A URL can carry credentials in its user-info, and the
        // headers variable can carry a token; neither belongs in a log line.
        return new TelemetryExport(
            true,
            $"exported over OTLP ({(string.IsNullOrWhiteSpace(protocol) ? "grpc" : protocol)}) "
            + $"to {uri.Scheme}://{uri.Host}:{uri.Port}");
    }
}
