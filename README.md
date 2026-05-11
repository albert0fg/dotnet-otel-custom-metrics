# dotnet-otel-custom-metrics

Minimal working example of **OpenTelemetry zero-code auto-instrumentation** for custom
metrics in .NET 8, deployed to Kubernetes without a private container registry.

The application exposes a custom `Counter` and a `Gauge` using only the .NET BCL
(`System.Diagnostics.Metrics`) — no OpenTelemetry NuGet packages in the app code.
The OTel auto-instrumentation startup hook, injected at runtime via `DOTNET_STARTUP_HOOKS`,
picks up the metrics and forwards them via OTLP to any compatible collector.

```
App (.NET Meter)
  └─ DOTNET_STARTUP_HOOKS → OTel startup hook
       └─ OTLP gRPC (port 4317) → Collector (Grafana Alloy / OTel Collector / …)
            └─ Prometheus remote_write → Grafana Cloud / Mimir / Prometheus
```

> **Tested with:** OTel .NET auto-instrumentation v1.9.0, .NET 8, Grafana Alloy,
> Grafana Cloud Prometheus.

---

## Repository layout

```
├── src/
│   ├── Program.cs        ← ASP.NET Core app with custom Meter and Counter
│   └── app.csproj        ← .NET 8 project, zero OTel NuGet dependencies
├── k8s/
│   └── deploy.yaml       ← Namespace + ConfigMap + Deployment + Service
└── docs/
    └── troubleshooting-dotnet-dockerfile.md   ← common mistakes and fixes
```

---

## Prerequisites

| Tool | Version |
|------|---------|
| `kubectl` | 1.24+ |
| Kubernetes cluster | 1.24+ |
| OTLP-capable collector | Grafana Alloy, OTel Collector, or similar |

No Docker daemon, no private container registry, no `helm` required.
The build happens inside the cluster via init containers using only public Microsoft images.

---

## Quick start

> **The YAML is self-contained.** The application source code lives inside the ConfigMap
> in `k8s/deploy.yaml`. A single `kubectl apply` builds and deploys everything.
> You only need to set **one mandatory value** before applying.

### 1 — Set the OTLP endpoint (mandatory)

Open `k8s/deploy.yaml` and change the `OTEL_EXPORTER_OTLP_ENDPOINT` value to point to
your collector. Everything else is optional — the demo works with the placeholder defaults.

```yaml
# k8s/deploy.yaml — the only line you must change:
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: "http://your-collector.your-namespace.svc.cluster.local:4317"  # ← change this
```

**Finding your Alloy receiver endpoint** (if using `grafana-k8s-monitoring` Helm chart):

```bash
kubectl get svc -A | grep alloy-receiver
# → grafana-k8s-monitoring-alloy-receiver  <monitoring-ns>  4317/TCP ...
# Endpoint: http://grafana-k8s-monitoring-alloy-receiver.<monitoring-ns>.svc.cluster.local:4317
```

**Other optional values** (change if you want custom names in Grafana):

| Variable | Default | Purpose |
|----------|---------|---------|
| `namespace` (3 places) | `otel-demo` | Kubernetes namespace |
| `OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES` | `MyCompany.MyService` | Meter name to capture |
| `OTEL_SERVICE_NAME` | `my-service-name` | Label in Grafana |

### 2 — Deploy

```bash
kubectl apply -f k8s/deploy.yaml
```

The two init containers run sequentially on first startup:
- `build-app` (~30–60 s): restores NuGet packages and compiles the app
- `setup-otel` (~30–60 s): downloads the OTel auto-instrumentation zip

Wait for the pod to become ready:

```bash
kubectl wait --for=condition=ready pod \
  -l app=otel-demo \
  -n otel-demo \
  --timeout=300s
```

### 3 — Generate traffic

```bash
kubectl port-forward -n otel-demo svc/otel-demo 8080:8080 &

# Increment the counter via HTTP
curl http://localhost:8080/
# → {"message":"counter incremented","meter":"MyCompany.MyService"}

# The background loop also increments the counter every 5 seconds automatically
```

### 4 — Verify in Grafana

Wait one export interval (15 s in this demo config, adjust `OTEL_METRIC_EXPORT_INTERVAL`).

Open Grafana Explore and run:

```promql
# Discover all metrics from the demo service
{service_name="my-service-name"}

# Check the custom counter (OTel renames dots → underscores and appends _total)
my_custom_counter_requests_total
```

You should see two series, one per `endpoint` label value (`root` and `background`).

---

## Customising the metric

Edit `Program.cs` (or the inline copy in `k8s/deploy.yaml`) and change:

```csharp
// 1. Meter name — must match OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES
const string METER_NAME = "MyCompany.MyService";   // ← your value

// 2. Counter name
var requestCounter = meter.CreateCounter<long>(
    name: "my.custom.counter",   // ← your metric name
    unit: "{requests}",           // ← will appear in the Prometheus name as suffix
    description: "...");

// 3. Add a Gauge
var queueDepth = meter.CreateObservableGauge<int>(
    name: "my.queue.depth",
    observeValue: () => GetCurrentQueueDepth(),
    unit: "{messages}");

// 4. Add a Histogram
var processingTime = meter.CreateHistogram<double>(
    name: "my.processing.duration",
    unit: "ms");
// then: processingTime.Record(elapsed.TotalMilliseconds, tag);
```

After editing, reapply the ConfigMap and restart the pod:

```bash
kubectl apply -f k8s/deploy.yaml
kubectl rollout restart deployment/otel-demo -n otel-demo
```

---

## How the metric name changes between code and Prometheus

The OTel SDK and the Prometheus exporter apply a naming convention automatically:

| Step | Value |
|------|-------|
| Code (`CreateCounter`) | `my.custom.counter` with `unit="{requests}"` |
| OTLP wire format | `my.custom.counter` (unchanged) |
| Prometheus conversion | `my_custom_counter_requests_total` |

Rules: dots → underscores · unit appended as suffix · `_total` appended to counters.

---

## Using Alpine / musl images

If your application image is Alpine-based (`dotnet/aspnet:8.0-alpine`), change the
download URL in the `setup-otel` init container:

```yaml
# In k8s/deploy.yaml, setup-otel init container:
curl -fsSL -o "$ZIP" \
  "https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/v${OTEL_VERSION}/opentelemetry-dotnet-instrumentation-linux-musl-x64.zip"
```

Also add to the pod env (or the Dockerfile):

```yaml
# Required on Alpine to avoid ICU library errors:
- name: DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
  value: "1"
# Or install ICU in the Dockerfile:
# RUN apk add icu-libs icu-data-full
```

---

## Coexisting with Datadog APM

If your base image also installs the Datadog tracer (a common pattern), make sure:

1. Only one CLR profiler is active at a time — set `CORECLR_PROFILER` and
   `CORECLR_PROFILER_PATH` explicitly per deployment, not in the base image.
2. Set `OTEL_TRACES_EXPORTER=none` so the OTel startup hook does not create a
   second tracer alongside Datadog's.
3. Set `OTEL_METRICS_EXPORTER=otlp` explicitly — do not rely on the default.

```yaml
# Deployment env vars when Datadog handles traces + OTel handles metrics:
- name: OTEL_TRACES_EXPORTER
  value: "none"
- name: OTEL_METRICS_EXPORTER
  value: "otlp"
- name: OTEL_LOGS_EXPORTER
  value: "none"
```

See [`docs/troubleshooting-dotnet-dockerfile.md`](docs/troubleshooting-dotnet-dockerfile.md)
for a full list of common mistakes.

---

## Upgrading the OTel auto-instrumentation version

Change the `OTEL_VERSION` variable in the `setup-otel` init container:

```yaml
OTEL_VERSION=1.9.0   # ← bump this
```

Check available versions at:
https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases

---

## References

- [OTel .NET zero-code custom metrics](https://opentelemetry.io/docs/zero-code/dotnet/custom/)
- [OTel .NET auto-instrumentation configuration](https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/blob/main/docs/config.md)
- [System.Diagnostics.Metrics API](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation)
- [Prometheus naming conventions](https://prometheus.io/docs/practices/naming/)
