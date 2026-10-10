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


def test_spans_from_any_other_service_fail_at_once(monkeypatch, capsys):
    import io

    stranger = resource("unknown_service:python", span(0, "GET", "f000000000000001", kind="Server"))
    monkeypatch.setattr("sys.stdin", io.StringIO(AGENTS + ENGINE + stranger))

    assert check.main() == 2
    assert "unknown_service:python" in capsys.readouterr().out


def nameless(spans: str) -> str:
    """A resource with no service.name, as an SDK configured without one would send."""
    return f"""ResourceSpans #0
Resource attributes:
     -> telemetry.sdk.language: Str(python)
ScopeSpans #0
{spans}"""


def test_a_resource_without_a_service_name_does_not_inherit_the_one_before():
    spans = check.parse(AGENTS + nameless(span(0, "GET", "f000000000000001", kind="Server")))

    assert [s.service for s in spans] == ["agents", "agents", ""]


def test_the_first_resource_of_a_batch_is_found_behind_the_log_prefix():
    # The collector prints "<time>\tinfo\tResourceSpans #0" on one line.
    prefixed = "2026-10-10T18:38:56.399Z\tinfo\t" + nameless(span(0, "GET", "f000000000000001"))

    assert [s.service for s in check.parse(ENGINE + prefixed)][-1] == ""


def test_a_nameless_sender_fails_at_once(monkeypatch, capsys):
    import io

    stranger = nameless(span(0, "GET", "f000000000000001", kind="Server"))
    monkeypatch.setattr("sys.stdin", io.StringIO(AGENTS + ENGINE + stranger))

    assert check.main() == 2
    assert "(no service.name)" in capsys.readouterr().out


def metrics(service: str, *names: str) -> str:
    listed = "".join(
        f"""Metric #{index}
Descriptor:
     -> Name: {name}
     -> DataType: Sum
NumberDataPoints #0
Value: 1
"""
        for index, name in enumerate(names)
    )
    return f"""2026-10-10T18:38:56.399Z\tinfo\tResourceMetrics #0
Resource attributes:
     -> service.name: Str({service})
ScopeMetrics #0
InstrumentationScope m
{listed}"""


def test_metrics_are_read_with_their_service():
    spans, read = check.read(ENGINE + metrics("engine", "trading.cycles", "trading.orders"))

    assert len(spans) == 3
    assert [(m.service, m.name) for m in read] == [
        ("engine", "trading.cycles"),
        ("engine", "trading.orders"),
    ]


def test_metrics_from_the_two_services_pass(monkeypatch):
    import io

    text = AGENTS + ENGINE + metrics("engine", "trading.cycles") + metrics("agents", "x")
    monkeypatch.setattr("sys.stdin", io.StringIO(text))

    assert check.main() == 0


def test_metrics_from_any_other_service_fail_at_once(monkeypatch, capsys):
    import io

    stranger = metrics("unknown_service:python", "http.server.request.duration")
    monkeypatch.setattr("sys.stdin", io.StringIO(AGENTS + ENGINE + stranger))

    assert check.main() == 2
    assert "unknown_service:python" in capsys.readouterr().out
