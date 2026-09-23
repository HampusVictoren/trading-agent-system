"""The other half of the seam: AG2 runs the turn, and its failures become ours.

ag2.testing.TestConfig scripts a model deterministically, so the whole chain runs without
a network. A scripted BaseException is raised untouched, which is what makes the exception
translation testable with the exceptions that actually occur - openai's, not builtins'.
"""

import json

import httpx2
import pytest
from ag2 import Agent
from ag2.testing import TestConfig
from openai import APIConnectionError, APIStatusError, APITimeoutError

from app.application.errors import (
    AgentResponseInvalid,
    LlmFailed,
    LlmTimeout,
    LlmUnreachable,
)
from app.application.teams import DEFAULT_TEAM, StepSpec, TeamSpec, load_prompts
from app.domain.signals import Stance, TradeView
from app.domain.steps import MarketRead, RiskAssessment, Severity, Trend, Valuation
from app.infrastructure.ag2.runner import Ag2StepRunner, build_agents

A_READ = {"trend": "UP", "valuation": "FAIR", "observations": ["Stabil vinstutveckling."]}
AN_ASSESSMENT = {"downside": "MEDIUM", "veto": False, "risks": ["Värderingen är utsträckt."]}
A_VIEW = {
    "stance": "BUY",
    "conviction": 0.7,
    "thesis": "Momentum med stöd i vinstrevideringar.",
    "key_risks": ["P/E över sektorsnittet."],
    "horizon_days": 5,
}

A_REQUEST = httpx2.Request("POST", "http://127.0.0.1:11434/v1/chat/completions")


def runner_answering(*answers: dict, role: str = "market_analyst") -> Ag2StepRunner:
    config = TestConfig(*[json.dumps(answer) for answer in answers])
    return Ag2StepRunner({role: Agent(role, prompt="Du är analytiker.", config=config)})


def runner_raising(error: BaseException, role: str = "market_analyst") -> Ag2StepRunner:
    return Ag2StepRunner({role: Agent(role, prompt="p", config=TestConfig(error))})


class TestAScriptedModel:
    async def test_a_valid_answer_becomes_the_schema(self):
        result = await runner_answering(A_READ).run_step("market_analyst", "msg", MarketRead)

        assert isinstance(result, MarketRead)
        assert result.trend is Trend.UP
        assert result.valuation is Valuation.FAIR

    async def test_the_whole_default_team_runs_without_a_network(self):
        # The point of TestConfig: the real TeamSpec, the real prompt files, the real
        # schemas, and a model that answers from a script.
        prompts = load_prompts(DEFAULT_TEAM)
        agents = {
            "market_analyst": Agent(
                "market_analyst",
                prompt=prompts["market_analyst"],
                config=TestConfig(json.dumps(A_READ)),
            ),
            "risk_manager": Agent(
                "risk_manager",
                prompt=prompts["risk_manager"],
                config=TestConfig(json.dumps(AN_ASSESSMENT)),
            ),
            "portfolio_manager": Agent(
                "portfolio_manager",
                prompt=prompts["portfolio_manager"],
                config=TestConfig(json.dumps(A_VIEW)),
            ),
        }
        runner = Ag2StepRunner(agents)

        read = await runner.run_step("market_analyst", "m", MarketRead)
        assessment = await runner.run_step("risk_manager", "m", RiskAssessment)
        view = await runner.run_step("portfolio_manager", "m", TradeView)

        assert read.trend is Trend.UP
        assert assessment.downside is Severity.MEDIUM
        assert view.stance is Stance.BUY


class TestWhatAFailureBecomes:
    """A failure that looks like a decision is one the engine cannot tell apart from one."""

    async def test_a_timeout_is_a_timeout_and_not_an_unreachable_backend(self):
        # APITimeoutError is a subclass of APIConnectionError, so the order of the except
        # clauses is the whole test: caught the other way round this is a 503, not a 504.
        with pytest.raises(LlmTimeout):
            await runner_raising(APITimeoutError(request=A_REQUEST)).run_step(
                "market_analyst", "m", MarketRead
            )

    async def test_a_backend_that_cannot_be_reached_is_503_shaped(self):
        with pytest.raises(LlmUnreachable):
            await runner_raising(
                APIConnectionError(message="no route", request=A_REQUEST)
            ).run_step("market_analyst", "m", MarketRead)

    async def test_an_error_status_is_reported_with_its_code(self):
        error = APIStatusError(
            "bad gateway",
            response=httpx2.Response(502, request=A_REQUEST),
            body=None,
        )

        with pytest.raises(LlmFailed, match="502"):
            await runner_raising(error).run_step("market_analyst", "m", MarketRead)

    async def test_an_answer_that_never_matches_the_schema_is_refused(self):
        # AG2 has already asked again with the validation error attached; this is what is
        # left after those retries, and it must not become a decision.
        bad = {"trend": "SPACEWARD", "valuation": "FAIR", "observations": []}

        with pytest.raises(AgentResponseInvalid, match="MarketRead"):
            await runner_answering(bad, bad, bad).run_step("market_analyst", "m", MarketRead)

    async def test_the_failure_names_the_step_that_failed(self):
        # Three steps, and a message that does not say which one is a message that costs a
        # reader the time it takes to find out.
        with pytest.raises(LlmTimeout, match="risk_manager"):
            await runner_raising(APITimeoutError(request=A_REQUEST), role="risk_manager").run_step(
                "risk_manager", "m", RiskAssessment
            )


class TestLoadingPrompts:
    def test_the_real_teams_prompts_load(self):
        prompts = load_prompts(DEFAULT_TEAM)

        assert set(prompts) == set(DEFAULT_TEAM.roles)
        assert all(text.strip() for text in prompts.values())

    def test_every_prompt_says_the_data_block_is_data(self):
        # The pipeline wraps the run's context in <data>...</data> on the promise that the
        # prompt told the model what that means. A prompt without it makes the delimiter
        # decoration.
        for text in load_prompts(DEFAULT_TEAM).values():
            assert "<data>" in text and "</data>" in text

    def test_the_portfolio_manager_is_told_it_decides_no_amount(self):
        # The instruction half of decision 1. The schema is the real guarantee, but a
        # model that is not told will try, and the attempt shows up in the thesis.
        text = load_prompts(DEFAULT_TEAM)["portfolio_manager"]

        assert "aldrig belopp" in text

    def test_a_missing_prompt_file_is_caught(self, tmp_path):
        spec = TeamSpec(
            id="t",
            instrument_types=frozenset({"equity"}),
            steps=(
                StepSpec(
                    role="portfolio_manager",
                    prompt_file=tmp_path / "nope.md",
                    output_schema=TradeView,
                ),
            ),
        )

        with pytest.raises(ValueError, match="cannot read"):
            load_prompts(spec)

    def test_an_empty_prompt_file_is_caught(self, tmp_path):
        # An empty file leaves the role with no instructions, and the model answers from
        # the schema alone - which it will do, plausibly, and wrongly.
        empty = tmp_path / "empty.md"
        empty.write_text("   \n")
        spec = TeamSpec(
            id="t",
            instrument_types=frozenset({"equity"}),
            steps=(StepSpec(role="portfolio_manager", prompt_file=empty, output_schema=TradeView),),
        )

        with pytest.raises(ValueError, match="is empty"):
            load_prompts(spec)


def test_an_agent_is_built_per_step():
    from app.infrastructure.llm.provider import ModelConfigs

    configs = ModelConfigs(default=TestConfig("{}"), by_role={})

    agents = build_agents(DEFAULT_TEAM, load_prompts(DEFAULT_TEAM), configs)

    assert set(agents) == set(DEFAULT_TEAM.roles)


class TestAnAnswerThatIsNotThere:
    """`reply.content()` is typed `T | None`, so None has to go somewhere deliberate."""

    async def test_a_none_answer_does_not_reach_the_next_step(self):
        # Not reachable through TestConfig: with a response_schema, an empty answer raises
        # ValidationError instead. Stubbed at the AG2 boundary because the guard is about
        # the contract AG2's own type declares, and the alternative is the literal None
        # being serialised into the next step's data block as a result.
        class NoAnswer:
            async def content(self, retries: int) -> None:
                return None

        class SilentAgent:
            async def ask(self, message, response_schema):
                return NoAnswer()

        runner = Ag2StepRunner({"market_analyst": SilentAgent()})  # type: ignore[dict-item]

        with pytest.raises(AgentResponseInvalid, match="no answer at all"):
            await runner.run_step("market_analyst", "m", MarketRead)
