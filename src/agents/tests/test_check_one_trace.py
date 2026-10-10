"""The CI step's reader of the collector's output (otel/check_one_trace.py).

It lives outside this package because it runs on a bare runner, but it decides whether stage
7's check passes, so it is tested here against the debug exporter's format.
"""

import importlib.util
from pathlib import Path

import pytest

_PATH = Path(__file__).resolve().parents[3] / "otel" / "check_one_trace.py"
_spec = importlib.util.spec_from_file_location("check_one_trace", _PATH)
assert _spec is not None and _spec.loader is not None
check = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(check)

TRACE = "db80357deb448cef358198333a3b08ec"


def resource(service: str, spans: str) -> str:
    return f"""2026-10-10T17:41:09.4Z	info	Traces	{{"resource spans": 1}}
ResourceSpans #0
Resource SchemaURL:
Resource attributes:
     -> telemetry.sdk.language: Str(python)
     -> service.name: Str({service})
ScopeSpans #0
InstrumentationScope app
{spans}"""


def span(index: int, name: str, span_id: str, parent: str = "", kind: str = "Internal") -> str:
    return f"""Span #{index}
    Trace ID       : {TRACE}
    Parent ID      : {parent}
    ID             : {span_id}
    Name           : {name}
    Kind           : {kind}
    Status code    : Unset
Attributes:
     -> service.name: Str(not-a-resource)
"""


ENGINE = resource(
    "engine",
    span(0, "trading.cycle", "c000000000000001")
    + span(1, "trading.analysis", "a000000000000001", "c000000000000001")
    + span(2, "POST", "b000000000000001", "a000000000000001", kind="Client"),
)
AGENTS = resource(
    "agents",
    span(0, "POST /v1/signals", "d000000000000001", "b000000000000001", kind="Server")
    + span(1, "invoke_agent market_analyst", "e000000000000001", "d000000000000001"),
)


def test_a_chain_from_the_agents_up_to_the_engines_cycle_is_found():
    chain = check.chain_to_cycle(check.parse(AGENTS + ENGINE))

    assert chain is not None
    assert [(s.service, s.name) for s in chain] == [
        ("agents", "POST /v1/signals"),
        ("engine", "POST"),
        ("engine", "trading.analysis"),
        ("engine", "trading.cycle"),
    ]


def test_a_span_attribute_named_service_name_is_not_the_resource():
    spans = check.parse(AGENTS)

    assert {s.service for s in spans} == {"agents"}


@pytest.mark.parametrize(
    "text",
    [
        AGENTS,  # the engine's half never arrived
        AGENTS + ENGINE.replace("trading.cycle", "trading.select"),  # not a cycle at its root
        AGENTS.replace("b000000000000001", "f000000000000001") + ENGINE,  # a different parent
        AGENTS + ENGINE.replace(TRACE, "0" * 32),  # the same ids in another trace
    ],
    ids=["half-a-chain", "root-not-a-cycle", "broken-parent", "another-trace"],
)
def test_anything_short_of_one_unbroken_trace_is_not_found(text):
    assert check.chain_to_cycle(check.parse(text)) is None


def test_the_script_exits_non_zero_until_the_chain_exists(monkeypatch, capsys):
    import io

    monkeypatch.setattr("sys.stdin", io.StringIO(AGENTS))
    assert check.main() == 1

    monkeypatch.setattr("sys.stdin", io.StringIO(AGENTS + ENGINE))
    assert check.main() == 0
    assert TRACE in capsys.readouterr().out
