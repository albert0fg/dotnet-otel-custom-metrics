// ─────────────────────────────────────────────────────────────────────────────
// Custom metrics demo — OpenTelemetry zero-code auto-instrumentation for .NET 8
//
// HOW IT WORKS
//   This app never imports any OpenTelemetry NuGet package.
//   It uses only System.Diagnostics.Metrics, which ships with the .NET runtime.
//   The OTel auto-instrumentation startup hook (injected via DOTNET_STARTUP_HOOKS)
//   subscribes to your Meter at process start and forwards every measurement to
//   the configured OTLP exporter — no code change needed.
//
// CUSTOMISE
//   1. Change METER_NAME to match your OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES
//   2. Change COUNTER_NAME to whatever metric name you want to see in Grafana
//   3. Add more instruments (Gauge, Histogram, …) following the same pattern
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics.Metrics;

// ── 1. Declare the Meter ──────────────────────────────────────────────────────
// The name here MUST match (or be covered by) the value of
// OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES in your deployment.
// Example: "MyCompany.MyService" or a wildcard pattern "MyCompany.*"
const string METER_NAME = "MyCompany.MyService";

var meter = new Meter(METER_NAME, "1.0.0");

// ── 2. Create instruments ─────────────────────────────────────────────────────
// Counter: a monotonically increasing value (requests, errors, bytes sent, …)
// In Prometheus this becomes: my_custom_counter_total (dots → underscores, _total appended)
var requestCounter = meter.CreateCounter<long>(
    name: "my.custom.counter",
    unit: "{requests}",
    description: "Example custom counter — replace with your own metric");

// Gauge: a value that can go up or down (queue depth, temperature, active users, …)
var activeUsersGauge = meter.CreateObservableGauge<int>(
    name: "my.active.users",
    observeValue: () => Random.Shared.Next(1, 100),   // replace with real value
    unit: "{users}",
    description: "Example observable gauge — replace with your own metric");

// ── 3. Wire up the web app ────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Each request increments the counter with a label
app.MapGet("/", () =>
{
    requestCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "root"));
    return Results.Ok(new
    {
        message    = "Custom counter incremented",
        meterName  = METER_NAME,
        metricName = "my.custom.counter"
    });
});

app.MapGet("/healthz", () => Results.Ok("healthy"));

// Background loop so the counter increases even without HTTP traffic
_ = Task.Run(async () =>
{
    while (true)
    {
        requestCounter.Add(1, new KeyValuePair<string, object?>("endpoint", "background"));
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
});

app.Run();
