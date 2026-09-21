# Social copy — v3.46.0 (phase 81: OTLP push exporter)

Blog: https://blog.devart.solutions/blog/inferhub-3-46-otlp-push-exporter
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.46.0

## X / Twitter

InferHub's /metrics has always been pull-only — great for Prometheus, no help if your stack is
Grafana Cloud, Honeycomb or Datadog. v3.46 adds an opt-in OTLP push exporter. Zero new
dependencies: hand-rolled JSON over HTTP, not the OpenTelemetry SDK.

https://blog.devart.solutions/blog/inferhub-3-46-otlp-push-exporter

## Facebook / LinkedIn

**InferHub can now push its metrics to any OTLP collector — and it didn't cost a single new
dependency.**

Since v2.10, `/metrics` has served the Prometheus text format. Fine if you run Prometheus; no help
at all if your observability stack speaks OTLP instead, which most modern ones do. v3.46 adds the
other direction: an opt-in background exporter that pushes the same numbers to any OTLP-compatible
collector on an interval, off by default.

The obvious way to speak OTLP from .NET is the OpenTelemetry SDK. We said no — its exporter
package and the protobuf/gRPC machinery behind it exist to re-serialize numbers we already compute
in one place. OTLP has a documented JSON encoding every collector accepts on the same port as the
binary one, so the exporter is a hand-rolled `HttpClient` posting JSON — the same call this project
already made for the Qdrant connector's REST API over its official gRPC client.

Rather than a second formatter walking every registry a second time (the exact bug shape that once
took a whole scrape offline over a duplicate header), a small reader parses `PrometheusFormatter`'s
own text output back into rows, and the OTLP payload is built from those. One string serialized
twice, one set of facts computed once.

Verified twice against a real OpenTelemetry Collector — once locally, once against the published
image itself — every push accepted with no error, counters decoded as cumulative sums, gauges as
gauges, the resource carrying the exact release version.

https://blog.devart.solutions/blog/inferhub-3-46-otlp-push-exporter
