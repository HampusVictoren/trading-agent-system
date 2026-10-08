"""The agent service's OpenAPI document, in the form contracts/openapi.json holds it.

Stage 6's drift check (decision D3 in the worklog). The engine does not generate its client
from this document - that would make Python the contract's owner and cost the engine its
Disallow attributes and the mappers' caps, which are its real defences against a wrong
answer. Instead the document is committed, and two suites hold it in place from either side:

- tests/test_openapi_snapshot.py fails when the service no longer generates what is
  committed, so a change to a route or a model cannot reach master without the file
  changing in the same diff;
- tests/Engine.Tests/OpenApiContractTests.cs reads the committed file and fails when an
  endpoint the engine calls, or a DTO it sends or reads, no longer agrees with it.

Regenerate it from src/agents with:

    uv run python -m app.openapi_snapshot > ../../contracts/openapi.json

It needs no server, no database, no LLM and no TAS_ENABLE_DOCS: that flag controls the
/openapi.json route, not the generator behind it.
"""

import json
import sys
from typing import Any

from app.main import create_app

# Keywords that describe rather than constrain. A docstring edit is not a contract change,
# and a snapshot that moved with every one of them would teach regenerating it without
# reading the diff - which is the habit that lets a real change through.
PROSE_KEYWORDS = frozenset({"description", "summary"})


def strip_prose(node: Any, *, naming: bool = False) -> Any:
    """Drops the prose keywords everywhere except where a key is a name rather than a keyword.

    Under `properties` the keys are field names, so a field that happened to be called
    "description" is part of the wire shape and has to survive.
    """
    if isinstance(node, dict):
        return {
            key: strip_prose(value, naming=key == "properties" and not naming)
            for key, value in node.items()
            if naming or key not in PROSE_KEYWORDS
        }
    if isinstance(node, list):
        return [strip_prose(item) for item in node]
    return node


def render() -> str:
    """The document exactly as the committed file holds it, byte for byte.

    Docs are passed as off explicitly, so the environment this runs in cannot change it.
    Keys are sorted so the file diffs by meaning rather than by declaration order.
    """
    document = strip_prose(create_app(enable_docs=False).openapi())
    return json.dumps(document, indent=2, sort_keys=True, ensure_ascii=False) + "\n"


def main() -> None:
    # Bytes, not text: the file is UTF-8 whatever the locale of the shell redirecting it.
    sys.stdout.buffer.write(render().encode("utf-8"))


if __name__ == "__main__":
    main()
