"""What a team is, reduced to twelve hex characters.

The engine stores `team_version` with every decision, and stage 4 groups outcomes by it.
That only works if the rule is exact: **include what changes what the model says, exclude
what changes how it is reached.** A prompt edit is a new version; moving Ollama to another
port is not.

sha256 over a canonically serialised payload, never Python's `hash()`, which is salted per
process and would give a different version on every restart.
"""

import hashlib
import json
from collections.abc import Mapping
from typing import Any

from app.application.teams import TeamSpec
from app.settings import LlmSettings, ModelSpec

# Long enough that the handful of versions this will ever have cannot collide, short
# enough to read in a log line or a database column.
VERSION_LENGTH = 12


def _model_identity(spec: ModelSpec) -> dict[str, Any]:
    """The part of a model choice that changes the answer.

    base_url, timeout and api_key are left out on purpose: the first two are transport,
    and the third must never reach a hash that gets stored and logged.
    """
    return {
        "provider": spec.provider.value,
        "model": spec.model,
        "temperature": spec.temperature,
        "seed": spec.seed,
    }


def version_payload(spec: TeamSpec, prompts: Mapping[str, str], llm: LlmSettings) -> dict[str, Any]:
    """Everything the version covers, as data. Separate from the hash so a test can read it."""
    return {
        "id": spec.id,
        "instrument_types": sorted(spec.instrument_types),
        "steps": [
            {
                "role": step.role,
                # The file's contents, not its path: moving a prompt is not a new team.
                "prompt": prompts[step.role],
                # The schemas' contents rather than their class names, because renaming a
                # class changes nothing the model sees while adding a field changes
                # everything. This also covers FactSheet, which no step produces.
                "output_schema": step.output_schema.model_json_schema(),
                "reads": [schema.model_json_schema() for schema in step.reads],
                "sees_position": step.sees_position,
                "model": _model_identity(llm.for_role(step.role)),
            }
            for step in spec.steps
        ],
    }


def compute_team_version(spec: TeamSpec, prompts: Mapping[str, str], llm: LlmSettings) -> str:
    canonical = json.dumps(version_payload(spec, prompts, llm), sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()[:VERSION_LENGTH]
