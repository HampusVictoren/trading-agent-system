"""docker-compose.yml and .env.example have to configure the same team.

Four values feed `team_version` - provider, model, temperature and seed; see
`app.application.versioning._model_identity` - and this repository writes them down twice.
The compose file spells them out because `docker compose up` has to work from a clean
checkout, which has no `src/agents/.env`; `.env.example` spells them out because that is the
file a developer copies. Two places holding the same four values is how the same team comes
to answer under two versions, with two populations that cannot be pooled accumulating under
one name - and nothing in either service would look wrong.

The comparison is between the **hashes**, not the four values, so it keeps asking the right
question if the hash ever starts covering a fifth thing.
"""

import re
from pathlib import Path
from typing import Any

import pytest
import yaml

from app.application.teams import TEAMS, load_prompts
from app.application.versioning import compute_team_version
from app.settings import LlmSettings, ModelSpec

AGENTS_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = AGENTS_ROOT.parents[1]

COMPOSE_FILE = REPO_ROOT / "docker-compose.yml"
ENV_EXAMPLE = AGENTS_ROOT / ".env.example"

# Only what the hash covers. The rest of a ModelSpec - base_url, api_key, timeout - is
# transport, deliberately excluded from team_version, and so not this test's business.
HASHED = ("PROVIDER", "MODEL", "TEMPERATURE", "SEED")

# `${NAME:-default}`. The default is what a clean checkout gets, so it is the value under
# test. Non-greedy on the name so a default containing a colon - qwen2.5:14b does - stays
# whole.
INTERPOLATION = re.compile(r"^\$\{([A-Za-z_][A-Za-z0-9_]*):-(.*)\}$")


def _compose_agents_environment() -> dict[str, str]:
    compose = yaml.safe_load(COMPOSE_FILE.read_text(encoding="utf-8"))
    return {key: str(value) for key, value in compose["services"]["agents"]["environment"].items()}


def _resolve(key: str, raw: str) -> str:
    """The value a clean checkout would see, or a failure naming why it cannot be known."""
    if not raw.startswith("${"):
        return raw

    match = INTERPOLATION.match(raw)
    if match is None:
        pytest.fail(
            f"docker-compose.yml sets {key} to {raw}, which has no default - so what a "
            "clean checkout runs cannot be read from the file, and this check cannot "
            "compare it with .env.example. Give it a default, or stop hashing it."
        )

    return match.group(2)


def _env_file_values(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if stripped.startswith("#") or "=" not in stripped:
            continue
        key, _, value = stripped.partition("=")
        values[key.strip()] = value.strip()
    return values


def _spec(values: dict[str, str], where: str) -> ModelSpec:
    missing = [name for name in HASHED if f"TAS_LLM__DEFAULT__{name}" not in values]
    assert not missing, f"{where} does not set {', '.join(missing)}"

    hashed = {name.lower(): values[f"TAS_LLM__DEFAULT__{name}"] for name in HASHED}

    # base_url and api_key are required to construct a spec and excluded from the hash, so
    # the same placeholders go on both sides: whatever they are, they cannot be the reason
    # two versions differ.
    return ModelSpec(
        base_url="http://127.0.0.1:11434/v1",
        api_key="excluded-from-the-hash",
        timeout_s=60,
        **hashed,  # type: ignore[arg-type]
    )


def _versions(spec: ModelSpec) -> dict[str, str]:
    llm = LlmSettings(default=spec)
    return {
        team_id: compute_team_version(team, load_prompts(team), llm)
        for team_id, team in TEAMS.items()
    }


def _hashed_values(values: dict[str, Any]) -> dict[str, str]:
    return {name: str(values[f"TAS_LLM__DEFAULT__{name}"]) for name in HASHED}


def test_compose_and_the_env_example_produce_the_same_team_versions():
    # Only the four. The rest of that environment is secrets written `${VAR:?...}` with no
    # default, which is right for a secret and would have nothing to compare against here.
    declared = _compose_agents_environment()
    compose = {
        key: _resolve(key, declared[key])
        for key in (f"TAS_LLM__DEFAULT__{name}" for name in HASHED)
        if key in declared
    }
    example = _env_file_values(ENV_EXAMPLE)

    from_compose = _versions(_spec(compose, "docker-compose.yml"))
    from_example = _versions(_spec(example, "src/agents/.env.example"))

    # The differing values are named, because a hash that differs says nothing about why.
    differing = {
        name: (value, _hashed_values(example).get(name))
        for name, value in _hashed_values(compose).items()
        if value != _hashed_values(example).get(name)
    }

    assert from_compose == from_example, (
        "docker-compose.yml and src/agents/.env.example configure different teams, so the "
        "same team would answer under two team_versions depending on how the service was "
        f"started. Differing: {differing or 'nothing - the hash covers something new'}. "
        f"compose {from_compose}, example {from_example}."
    )


def test_every_hashed_value_is_in_the_compose_file():
    # A value dropped from the compose file would fall back to .env, which a clean checkout
    # does not have - so the service would refuse to start rather than drift. That is the
    # safe failure, but this says it at the right moment instead.
    environment = _compose_agents_environment()

    for name in HASHED:
        assert f"TAS_LLM__DEFAULT__{name}" in environment
