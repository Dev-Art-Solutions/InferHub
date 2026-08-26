# InferHub v3.35.1 — a second `# HELP` is not a duplicate line, it is a scrape nobody can read

Configure **two** cloud providers on a v3.34.0 or v3.35.0 hub and `/metrics` stops being valid
Prometheus exposition. Not the provider series — **the whole endpoint**. Every counter, every gauge,
every histogram this project emits disappears from the dashboard, and the only thing the operator
changed was adding the second vendor the v3.29–v3.35 track exists to make possible.

## What was wrong

`PrometheusFormatter.Info` writes its own `# HELP` / `# TYPE` header and then one sample. It was
being called **inside a loop over the providers**, so a hub with two of them emitted the header
twice:

```
# HELP inferhub_provider_info A cloud provider this hub is configured to use.
# TYPE inferhub_provider_info gauge
inferhub_provider_info{provider="claude",type="anthropic",policy="no-node",credential="configured"} 1
# HELP inferhub_provider_info A cloud provider this hub is configured to use.
# TYPE inferhub_provider_info gauge
inferhub_provider_info{provider="gem",type="gemini",policy="no-node",credential="absent"} 1
```

A repeated `HELP` for a metric name is not a cosmetic repeat: Prometheus's text parser **rejects the
scrape**, so the failure is total rather than local.

Two families, and the older one is worse:

| Family | Broken since | Reachable when |
|---|---|---|
| `inferhub_provider_last_model` | **v3.29.0** (phase 61) | two providers have each dispatched at least once |
| `inferhub_provider_info` | v3.34.0 (phase 66) | two providers are configured at all |

## The fix

- **One `Header` per family, `Sample` per row** — the shape the `inferhub_node_vram_*` families have
  used since phase 48. No series names, labels or values changed: a hub with **one** provider emits
  byte-identical output to v3.35.0.
- **`Exposition.Parse` in `PrometheusMetricsTests` now fails on a second `# HELP` or `# TYPE` line
  for a name**, exactly as Prometheus does. This is the durable half. It guards every family in the
  formatter, including the ones nobody has written yet, and running it against the current formatter
  found no other offenders.
- `TwoProvidersShareOneHeaderPerFamilyRatherThanRepeatingIt` — two providers configured, two
  dispatched, one header each.

## Why seven releases of tests did not see it

Every provider test in the suite declares **one** provider — enough to assert the labels, the
credential word, the absence rules and the cardinality decisions, and structurally unable to reach a
duplicate header. And the in-test exposition reader did `help[name] = …`, silently overwriting on a
repeat: the one thing a real Prometheus will not do. *The bug needed the feature to actually be used
before it existed at all.*

This is `tests/CLAUDE.md`'s own rule — *parse it back, do not string-match it* — meeting its limit: a
reader more forgiving than the consumer is a reader that certifies output the consumer rejects.

## How it was found

Phase 68 (the provider verification day) pulled the published `3.35.0` coordinator image, configured
two providers on it and scraped `/metrics`. That is the whole of it: no unit test could have, and no
green suite did.

## What was not established

- **No live provider was called in this release either.** The two providers in the reproduction hold
  a fake key and no key; nothing left the hub. The vendor sections of the verification day are still
  outstanding and are v3.36.0's.
- **The node's scrape is untouched** — it has no provider families — and the node image is unchanged
  by this release.
- Whether any deployment in the wild actually hit this. It is silent from the hub's side: the hub
  serves a 200 with a body Prometheus discards, so the symptom is a dashboard that went blank, not an
  error in a log.

## Upgrading

Nothing to change. Same config, same series names, same labels. A hub with one provider or none is
byte-identical to v3.35.0.
