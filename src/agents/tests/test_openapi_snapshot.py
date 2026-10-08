"""contracts/openapi.json is what this service generates, or this fails.

The committed document is the half of the drift check that belongs to this side. The engine
reads the same file in tests/Engine.Tests/OpenApiContractTests.cs and compares it with its
own DTOs, so a route or a model changed here without the file changing would pass the
engine's suite against a document that is no longer true. This is what closes that gap: the
change and the file move in the same diff, and the engine's suite then judges the file.
"""

import json
from pathlib import Path

from app.openapi_snapshot import render, strip_prose

SNAPSHOT = Path(__file__).resolve().parents[3] / "contracts" / "openapi.json"

REGENERATE = "uv run python -m app.openapi_snapshot > ../../contracts/openapi.json"


def test_the_committed_document_is_what_the_service_generates():
    committed = SNAPSHOT.read_text(encoding="utf-8")

    assert committed == render(), (
        "contracts/openapi.json is not what the agent service generates. If the change to "
        f"the API is intended, regenerate it from src/agents with `{REGENERATE}`, and read "
        "the diff: the engine's OpenApiContractTests judge the new file against its DTOs."
    )


def test_the_document_covers_every_endpoint_the_engine_calls():
    # A snapshot of an app that lost its router would still equal its own regeneration.
    # The engine's suite would catch that too, but this side should not need it to.
    paths = json.loads(SNAPSHOT.read_text(encoding="utf-8"))["paths"]

    assert {
        "/v1/signals",
        "/v1/screen",
        "/v1/quotes/{symbol}",
        "/v1/quotes/{symbol}/history",
        "/v1/outcomes",
    } <= set(paths)


class TestOnlyProseIsStripped:
    def test_descriptions_and_summaries_go(self):
        stripped = strip_prose({"summary": "s", "description": "d", "type": "object", "title": "T"})

        assert stripped == {"type": "object", "title": "T"}

    def test_a_field_called_description_is_a_field_and_stays(self):
        # Under `properties` the keys are names, not keywords. Stripping one would delete
        # a field from the contract while claiming to delete prose.
        schema = {
            "properties": {
                "description": {"type": "string", "description": "prose about it"},
            },
            "required": ["description"],
        }

        assert strip_prose(schema) == {
            "properties": {"description": {"type": "string"}},
            "required": ["description"],
        }

    def test_prose_is_stripped_inside_lists_too(self):
        stripped = strip_prose({"parameters": [{"name": "symbol", "description": "d"}]})

        assert stripped == {"parameters": [{"name": "symbol"}]}
