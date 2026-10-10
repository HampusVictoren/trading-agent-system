"""Reads the collector's debug output and finds one engine cycle with the agents' work in it.

Stage 7's check is that an analysis cycle is one coherent trace from the engine through the
agent chain. The collector in compose prints every span it receives (otel/collector.yaml);
this reads that output and looks for an agent-service server span whose ancestry, followed
parent by parent, runs through the engine's spans to the root of the engine's cycle,
`trading.cycle`, all in one trace id. It prints that chain and every span of the trace by
service, and exits 1 if there is no such chain yet - or 2, at once, if any span or metric
came from a service other than the engine and the agents.

    docker compose logs --no-log-prefix otel-collector | python3 otel/check_one_trace.py

Standard library only, so it runs on a CI runner or a host without the service's virtualenv.
"""

import re
import sys
from collections import Counter
from dataclasses import dataclass

CYCLE_SPAN = "trading.cycle"
ENGINE = "engine"
AGENTS = "agents"

_FIELD = re.compile(r"^\s*(Trace ID|Parent ID|ID|Name|Kind)\s*:\s?(.*)$")
_SERVICE = re.compile(r"^\s*-> service\.name: Str\((.*)\)\s*$")


@dataclass
class Span:
    service: str
    trace_id: str = ""
    parent_id: str = ""
    span_id: str = ""
    name: str = ""
    kind: str = ""


# The first resource of each batch shares its line with the collector's log prefix
# ("<time>\tinfo\tResourceSpans #0"), so the header is searched for, not matched at the start.
_RESOURCE = re.compile(r"\bResource(Spans|Metrics) #\d+")
_METRIC_NAME = re.compile(r"^\s*-> Name: (.*)$")


@dataclass
class Metric:
    service: str
    name: str = ""


def parse(text: str) -> list[Span]:
    return read(text)[0]


def read(text: str) -> tuple[list[Span], list[Metric]]:
    """Every span and every metric in the output, each with the service.name of its resource.

    A resource that names no service gives its spans and metrics "" - never the name of the
    resource before it, which would pass a nameless sender off as a known one.
    """
    spans: list[Span] = []
    metrics: list[Metric] = []
    service = ""
    in_resource = False
    current: Span | None = None
    metric: Metric | None = None
    for line in text.splitlines():
        stripped = line.strip()
        if _RESOURCE.search(line):
            service, in_resource, current, metric = "", False, None, None
            continue
        if stripped.startswith("Resource attributes:"):
            in_resource = True
            continue
        if stripped.startswith(("ScopeSpans #", "ScopeMetrics #")):
            in_resource = False
        if in_resource and (match := _SERVICE.match(line)):
            service = match.group(1)
            continue
        if re.match(r"^Span #\d+", stripped):
            current, metric = Span(service=service), None
            spans.append(current)
            continue
        if re.match(r"^Metric #\d+", stripped):
            current, metric = None, Metric(service=service)
            metrics.append(metric)
            continue
        if metric is not None and not metric.name and (match := _METRIC_NAME.match(line)):
            metric.name = match.group(1).strip()
            continue
        if current is not None and (match := _FIELD.match(line)):
            key, value = match.group(1), match.group(2).strip()
            if key == "Trace ID":
                current.trace_id = value
            elif key == "Parent ID":
                current.parent_id = value
            elif key == "ID":
                current.span_id = value
            elif key == "Name":
                current.name = value
            elif key == "Kind":
                current.kind = value
    return spans, metrics


def chain_to_cycle(spans: list[Span]) -> list[Span] | None:
    """An agents server span and its ancestors up to the engine's cycle, or None."""
    by_id = {(span.trace_id, span.span_id): span for span in spans}
    for span in spans:
        if span.service != AGENTS or span.kind != "Server" or not span.parent_id:
            continue
        chain = [span]
        while chain[-1].parent_id:
            parent = by_id.get((span.trace_id, chain[-1].parent_id))
            if parent is None:
                break
            chain.append(parent)
        root = chain[-1]
        if (
            root.service == ENGINE
            and root.name == CYCLE_SPAN
            and not root.parent_id
            and all(link.service == ENGINE for link in chain[1:])
        ):
            return chain
    return None


def main() -> int:
    spans, metrics = read(sys.stdin.read())
    services = Counter(span.service for span in spans)
    print(f"{len(spans)} spans read: " + ", ".join(f"{n} from {s}" for s, n in services.items()))
    metered = Counter(metric.service for metric in metrics)
    print(f"{len(metrics)} metrics read: " + ", ".join(f"{n} from {s}" for s, n in metered.items()))

    # Only the two services export, each through a setup of its own. A span or a metric from
    # anything else - FastAPI's built-in telemetry once shipped both as unknown_service:python -
    # is a provider nobody configured, and no amount of waiting makes that right: exit 2, not 1.
    # A resource without a service.name counts as a stranger too ("").
    strangers = sorted((set(services) | set(metered)) - {ENGINE, AGENTS})
    if strangers:
        named = ", ".join(s or "(no service.name)" for s in strangers)
        print(f"spans or metrics from a service that should not export: {named}")
        return 2

    chain = chain_to_cycle(spans)
    if chain is None:
        print("no agents server span whose ancestry reaches the engine's trading.cycle yet")
        return 1

    trace_id = chain[0].trace_id
    print(f"one trace, {trace_id}: the agents' request, up to the engine's cycle")
    for depth, span in enumerate(reversed(chain)):
        print(f"  {'  ' * depth}{span.service}: {span.name} ({span.kind})")
    in_trace = [span for span in spans if span.trace_id == trace_id]
    for service in (ENGINE, AGENTS):
        names = Counter(span.name for span in in_trace if span.service == service)
        listed = ", ".join(f"{name} x{count}" for name, count in sorted(names.items()))
        print(f"  {service} spans in this trace: {listed}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
