# Troubleshooting: OTel custom metrics not appearing in Grafana

This guide covers the most common mistakes when adding OpenTelemetry custom metrics
to a .NET 8 application using the zero-code auto-instrumentation approach.

---

## Checklist — run through this before debugging further

```
□ OTEL_EXPORTER_OTLP_ENDPOINT is set and reachable from the pod
□ OTEL_METRICS_EXPORTER=otlp is explicitly declared
□ DOTNET_STARTUP_HOOKS points to the correct .dll path
□ OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES matches your Meter name exactly
□ The Meter name in code matches (or is covered by) the env var above
□ No trailing/extra quote characters in env var values
□ You are querying Grafana with the correct Prometheus name (dots → underscores)
```

---

## Issue 1 — No data at all: `OTEL_EXPORTER_OTLP_ENDPOINT` is missing

**Symptom:** No metrics in Grafana, no errors in the application logs.

**Root cause:** Without an OTLP endpoint the exporter silently drops all data.

```dockerfile
# ❌ Common mistake — base image does not set the endpoint
ENV DOTNET_STARTUP_HOOKS=/otel-dotnet-auto/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll
# ... but OTEL_EXPORTER_OTLP_ENDPOINT is never set
```

**Fix:** add it in your Dockerfile or, preferably, as a Kubernetes env var so it can
differ per environment:

```yaml
# In your Kubernetes Deployment:
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: "http://your-collector.your-namespace.svc.cluster.local:4317"
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: "grpc"
```

---

## Issue 2 — No data at all: `OTEL_METRICS_EXPORTER` not declared

**Symptom:** Same as Issue 1.

**Root cause:** The default value for `OTEL_METRICS_EXPORTER` varies between versions
of the auto-instrumentation. Do not rely on the default.

```dockerfile
# ❌ Not declared → might default to "none" depending on version
```

**Fix:**

```dockerfile
ENV OTEL_METRICS_EXPORTER=otlp
```

If you only want metrics (and traces go via Datadog or another agent):

```dockerfile
ENV OTEL_METRICS_EXPORTER=otlp
ENV OTEL_TRACES_EXPORTER=none
ENV OTEL_LOGS_EXPORTER=none
```

---

## Issue 3 — Custom metric missing, built-in metrics present: `ADDITIONAL_SOURCES` misconfigured

**Symptom:** You can see ASP.NET Core / Kestrel metrics in Grafana but your custom
`Meter` is not there.

**Root cause A — Trailing quote in Dockerfile:**

```dockerfile
# ❌ Extra " at the end — the env var value may contain a literal " character
ENV OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES="MyCompany.MyService""
```

While Docker's parser *usually* treats `"value""` as `value` (two concatenated strings),
some tools and CI systems interpret the trailing `"` as part of the value, making the
env var `MyCompany.MyService"`. This never matches any Meter name.

```dockerfile
# ✅ Correct
ENV OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES="MyCompany.MyService"
```

**Root cause B — Meter name mismatch:**

The value must match *exactly* what you pass to `new Meter(...)` in your code,
or use a wildcard (`*`) to cover multiple meters.

```csharp
// In your code:
var meter = new Meter("MyCompany.Payments", "1.0.0");
```

```dockerfile
# ❌ Wrong: different casing or typo
ENV OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES="mycompany.payments"

# ✅ Exact match
ENV OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES="MyCompany.Payments"

# ✅ Wildcard (covers all MyCompany.* meters)
ENV OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES="MyCompany.*"
```

---

## Issue 4 — Metric name in Grafana looks different from the code

**Symptom:** You search for `my.custom.counter` in Grafana and find nothing.

**Root cause:** The OTLP → Prometheus conversion renames metrics:
- dots (`.`) become underscores (`_`)
- the unit is appended as a suffix
- `_total` is appended to counters

```
Code:     meter.CreateCounter<long>("my.custom.counter", unit: "{requests}")
Grafana:  my_custom_counter_requests_total
```

Use a regex query to discover the actual name:

```promql
{__name__=~"my_custom.*", service_name="my-service-name"}
```

---

## Issue 5 — Conflict between Datadog APM and OTel auto-instrumentation

**Symptom:** Metrics or traces are missing or duplicated; pod may crash with CLR errors.

**Root cause:** Only one CLR profiler can be active at a time. If both Datadog's profiler
and the OTel profiler are set via `CORECLR_PROFILER`, the second one is silently ignored
or causes a crash.

```dockerfile
# Common base-image pattern that installs both agents:
CORECLR_ENABLE_PROFILING=1
# CORECLR_PROFILER and CORECLR_PROFILER_PATH are intentionally left unset in the base
# image so that each deployment can choose which profiler to activate.
```

**Recommended split:**

| Signal   | Agent     | Env vars to set |
|----------|-----------|-----------------|
| Traces   | Datadog   | `CORECLR_PROFILER={846F5F1C-...}`, `CORECLR_PROFILER_PATH=/opt/datadog/...` |
| Metrics  | OTel hook | `DOTNET_STARTUP_HOOKS=...`, `OTEL_METRICS_EXPORTER=otlp` |
| Logs     | Either    | `DD_LOGS_INJECTION=true` or `OTEL_LOGS_EXPORTER=otlp` |

If Datadog handles traces, set `OTEL_TRACES_EXPORTER=none` so the OTel hook does not
try to create a second tracer.

---

## Issue 6 — `System.Diagnostics.DiagnosticSource` version conflict

**Symptom:** Runtime errors like `MissingMethodException` or `TypeLoadException` after
adding the NuGet package.

**Root cause:** Adding an explicit reference to `System.Diagnostics.DiagnosticSource`
at a version higher than the one bundled inside the OTel auto-instrumentation can cause
assembly binding conflicts at runtime.

```xml
<!-- ❌ Unnecessary and potentially harmful on .NET 8 -->
<PackageReference Include="System.Diagnostics.DiagnosticSource" Version="9.0.9" />
```

`System.Diagnostics.Metrics` (the API used for custom metrics) is part of the .NET 8
BCL. You do not need any NuGet package to create a `Meter` or a `Counter`.

```xml
<!-- ✅ Correct: no OTel or DiagnosticSource packages needed -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
```

---

## Issue 7 — Wrong zip variant for Alpine images

**Symptom:** Pod crashes at startup with `System.DllNotFoundException` or cannot load
the native instrumentation library.

**Root cause:** There are two builds of the auto-instrumentation:
- `linux-glibc-x64.zip` — for Debian/Ubuntu-based images (`dotnet/aspnet:8.0`)
- `linux-musl-x64.zip` — for Alpine-based images (`dotnet/aspnet:8.0-alpine`)

Using the wrong one causes the native `.so` file to fail to load.

```dockerfile
# ✅ Alpine / musl image:
# FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
# → use opentelemetry-dotnet-instrumentation-linux-musl-x64.zip

# ✅ Debian / glibc image:
# FROM mcr.microsoft.com/dotnet/aspnet:8.0
# → use opentelemetry-dotnet-instrumentation-linux-glibc-x64.zip
```

Also add `apk add icu-libs icu-data-full` when using Alpine if you need full
globalization support (or set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` to skip ICU).

---

## Verifying end-to-end with a PromQL query

Once the pod is running, wait one export interval (default 60 s, 15 s in the demo)
then run this in Grafana Explore:

```promql
# Discover all metrics from your service
{service_name="my-service-name"}

# Check your custom counter specifically
my_custom_counter_requests_total{service_name="my-service-name"}

# Rate over time (should be non-zero if the app is running)
rate(my_custom_counter_requests_total{service_name="my-service-name"}[5m])
```
