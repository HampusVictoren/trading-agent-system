"""team_version is what stage 4 groups outcomes by, so the rule has to be exact.

Include what changes what the model says; exclude what changes how it is reached. Every
test below is one side of that line.
"""

import json
import subprocess
import sys

import pytest
from pydantic import BaseModel

from app.application.teams import DEFAULT_TEAM, MEMORY_TEAM, StepSpec, TeamSpec, load_prompts
from app.application.versioning import VERSION_LENGTH, compute_team_version, version_payload
from app.domain.signals import TradeView
from app.domain.steps import MarketRead, Trend
from app.settings import LlmSettings, ModelSpec, Provider

PROMPTS = {role: f"instructions for {role}" for role in DEFAULT_TEAM.roles}


def a_spec(**overrides) -> ModelSpec:
    defaults = {
        "provider": Provider.OPENAI_COMPATIBLE,
        "model": "llama3.2",
        "base_url": "http://127.0.0.1:11434/v1",
        "api_key": "placeholder",
        "temperature": 0.2,
        "timeout_s": 30.0,
    }
    return ModelSpec.model_validate(defaults | overrides)


def an_llm(**overrides) -> LlmSettings:
    return LlmSettings(default=a_spec(**overrides))


def version(spec=DEFAULT_TEAM, prompts=None, llm=None) -> str:
    return compute_team_version(spec, prompts or PROMPTS, llm or an_llm())


class TestTheVersionIsStable:
    def test_the_same_team_gives_the_same_version(self):
        assert version() == version()

    def test_it_survives_a_new_process(self):
        # The real point: Python's hash() is salted per process, so a version built on it
        # would change on every restart and split one team's statistics across versions.
        script = (
            "from app.application.teams import DEFAULT_TEAM;"
            "from app.application.versioning import compute_team_version;"
            "from app.settings import LlmSettings, ModelSpec, Provider;"
            "spec = ModelSpec(provider=Provider.OPENAI_COMPATIBLE, model='llama3.2',"
            "  base_url='http://127.0.0.1:11434/v1', api_key='placeholder',"
            "  temperature=0.2, timeout_s=30.0);"
            "prompts = {r: f'instructions for {r}' for r in DEFAULT_TEAM.roles};"
            "print(compute_team_version(DEFAULT_TEAM, prompts, LlmSettings(default=spec)))"
        )
        runs = [
            subprocess.run(  # noqa: S603 - the script is a literal three lines above
                [sys.executable, "-c", script],
                capture_output=True,
                text=True,
                check=True,
                env={"PYTHONHASHSEED": seed, "PATH": "/usr/bin:/bin"},
            ).stdout.strip()
            for seed in ("0", "12345")
        ]

        assert runs[0] == runs[1] == version()

    def test_it_is_twelve_hex_characters(self):
        result = version()

        assert len(result) == VERSION_LENGTH
        assert int(result, 16) >= 0


class TestWhatChangesTheVersion:
    def test_a_changed_prompt(self):
        edited = PROMPTS | {"market_analyst": "instructions for market_analyst, revised"}

        assert version(prompts=edited) != version()

    def test_a_changed_model(self):
        assert version(llm=an_llm(model="qwen3")) != version()

    def test_a_changed_provider(self):
        assert version(llm=an_llm(provider=Provider.OPENAI, base_url=None)) != version()

    def test_a_changed_temperature(self):
        assert version(llm=an_llm(temperature=0.9)) != version()

    def test_a_pinned_seed(self):
        assert version(llm=an_llm(seed=42)) != version()

    def test_a_per_role_override(self):
        # One step on a different model is a different team, even though the default is
        # untouched - which is why the version resolves the model per role.
        llm = LlmSettings(default=a_spec(), roles={"portfolio_manager": a_spec(model="qwen3")})

        assert version(llm=llm) != version()

    def test_a_changed_handover(self):
        # The portfolio manager seeing the fact sheet is a different experiment, and the
        # outcomes must not be pooled with the runs where it did not.
        steps = list(DEFAULT_TEAM.steps)
        last = steps[-1]
        steps[-1] = StepSpec(
            role=last.role,
            prompt_file=last.prompt_file,
            output_schema=last.output_schema,
            reads=(MarketRead,),
            sees_position=last.sees_position,
        )
        changed = TeamSpec(
            id=DEFAULT_TEAM.id,
            instrument_types=DEFAULT_TEAM.instrument_types,
            steps=tuple(steps),
        )

        assert version(spec=changed) != version()

    def test_memory_switched_on_for_a_step(self):
        # The flag alone, with the prompt untouched. A team given past outcomes is a
        # different experiment even when nobody edited a word of its instructions, and
        # pooling its measurements with the runs that had no memory is exactly the mixing
        # team_version exists to prevent.
        steps = list(DEFAULT_TEAM.steps)
        middle = steps[1]
        steps[1] = StepSpec(
            role=middle.role,
            prompt_file=middle.prompt_file,
            output_schema=middle.output_schema,
            reads=middle.reads,
            sees_memory=True,
            sees_position=middle.sees_position,
        )
        changed = TeamSpec(
            id=DEFAULT_TEAM.id,
            instrument_types=DEFAULT_TEAM.instrument_types,
            steps=tuple(steps),
        )

        assert version(spec=changed) != version()

    def test_a_changed_schema(self):
        # Adding a field changes what the model is asked for, so the schemas' contents are
        # hashed rather than their class names. Shown on a middle step, because the last
        # step's schema has to be TradeView exactly.
        class NarrowRead(BaseModel):
            trend: Trend

        class WideRead(BaseModel):
            trend: Trend
            note: str = ""

        def two_steps(first: type[BaseModel]) -> TeamSpec:
            path = DEFAULT_TEAM.steps[0].prompt_file
            return TeamSpec(
                id="t",
                instrument_types=frozenset({"equity"}),
                steps=(
                    StepSpec(role="a", prompt_file=path, output_schema=first),
                    StepSpec(role="b", prompt_file=path, output_schema=TradeView, reads=(first,)),
                ),
            )

        prompts = {"a": "x", "b": "y"}

        assert version(spec=two_steps(NarrowRead), prompts=prompts) != version(
            spec=two_steps(WideRead), prompts=prompts
        )


class TestWhatDoesNotChangeTheVersion:
    @pytest.mark.parametrize(
        "override",
        [
            {"base_url": "http://192.168.1.50:11434/v1"},
            {"timeout_s": 120.0},
            {"api_key": "a-completely-different-key"},
        ],
        ids=["base_url", "timeout", "api_key"],
    )
    def test_transport_and_credentials_are_not_part_of_it(self, override):
        # Moving Ollama to another port does not make it a different experiment, and a
        # rotated key certainly does not.
        assert version(llm=an_llm(**override)) == version()


class TestTheSecretNeverEnters:
    def test_the_api_key_is_nowhere_in_the_payload(self):
        # The version is logged and stored with every decision. A payload that carried the
        # key would put it wherever the version goes.
        serialised = json.dumps(version_payload(DEFAULT_TEAM, PROMPTS, an_llm()))

        assert "placeholder" not in serialised
        assert "api_key" not in serialised


def test_the_real_team_has_a_version():
    # Reads the checked-in prompt files, so an edit to one of them shows up as a new
    # version without anyone doing anything.
    assert len(compute_team_version(DEFAULT_TEAM, load_prompts(DEFAULT_TEAM), an_llm())) == 12


class TestTheVersionIgnoresWhatNobodySet:
    """A flag left at its default is not part of the team.

    Listing every flag with its value would tie the version to the *shape of the payload*
    rather than to the team: adding a flag nobody uses would re-hash every team there is,
    splitting each one's measurements in two for a change that altered nothing a model
    reads. That is not hypothetical - it happened when sees_memory was added, and moved the
    baseline team's version on the day the baseline started.
    """

    @staticmethod
    def handovers(payload: dict) -> list[dict]:
        return [
            {k: v for k, v in step.items() if k.startswith("sees")} for step in payload["steps"]
        ]

    def test_a_flag_left_alone_is_not_in_the_payload(self):
        payload = version_payload(DEFAULT_TEAM, PROMPTS, an_llm())

        # Only the portfolio manager sets one; the other two steps say nothing at all.
        assert self.handovers(payload) == [{}, {}, {"sees_position": True}]

    def test_a_flag_that_is_set_is_in_the_payload(self):
        prompts = {role: f"instructions for {role}" for role in MEMORY_TEAM.roles}

        payload = version_payload(MEMORY_TEAM, prompts, an_llm())

        assert self.handovers(payload) == [{}, {"sees_memory": True}, {"sees_position": True}]
