"""A team is data, and the rules about it are enforced where it is constructed.

Importing app.application.teams runs every check below against the real team, so a team
that breaks one of them cannot even be imported - which is the startup validation the
roadmap asks for, without a separate startup step.
"""

from pathlib import Path

import pytest
from pydantic import BaseModel

from app.application.teams import DEFAULT_TEAM, TEAMS, StepSpec, TeamSpec, all_roles
from app.domain.facts import FactSheet
from app.domain.signals import TradeView
from app.domain.steps import MarketRead, RiskAssessment

SOMEWHERE = Path("prompts/whatever.md")


class Other(BaseModel):
    pass


def step(role: str, output_schema: type[BaseModel], **overrides) -> StepSpec:
    return StepSpec(role=role, prompt_file=SOMEWHERE, output_schema=output_schema, **overrides)


def team(*steps: StepSpec, team_id: str = "t") -> TeamSpec:
    return TeamSpec(id=team_id, instrument_types=frozenset({"equity"}), steps=steps)


class TestTheDefaultTeam:
    def test_it_is_three_steps_ending_in_a_view(self):
        assert DEFAULT_TEAM.roles == ("market_analyst", "risk_manager", "portfolio_manager")
        assert DEFAULT_TEAM.steps[-1].output_schema is TradeView

    def test_every_prompt_file_exists(self):
        # Checked here rather than in __post_init__: whether a file is on disk is a fact
        # about the filesystem, not about the specification. It is checked again where the
        # prompts are loaded, which is at startup.
        for spec in DEFAULT_TEAM.steps:
            assert spec.prompt_file.is_file(), spec.prompt_file

    def test_the_portfolio_manager_never_sees_the_raw_numbers(self):
        # The sharpest thing `reads` buys. The portfolio manager weighs two assessments and
        # is given no figure at all, which is also why it cannot invent one - and the
        # engine sizes the order from the fact sheet's price, not from anything it says.
        pm = DEFAULT_TEAM.steps[-1]

        assert FactSheet not in pm.reads
        assert set(pm.reads) == {MarketRead, RiskAssessment}

    def test_only_the_portfolio_manager_is_told_about_a_holding(self):
        assert [spec.role for spec in DEFAULT_TEAM.steps if spec.sees_position] == [
            "portfolio_manager"
        ]

    def test_it_is_registered_under_its_id(self):
        assert TEAMS["default"] is DEFAULT_TEAM

    def test_it_only_covers_equities(self):
        assert DEFAULT_TEAM.instrument_types == frozenset({"equity"})


class TestATeamThatCouldNotWork:
    def test_a_team_that_does_not_end_in_a_view_is_refused(self):
        # The pipeline returns the last step's result. A team ending in a RiskAssessment
        # would get all the way to the end and then have nothing to answer with.
        with pytest.raises(ValueError, match="TradeView"):
            team(step("a", MarketRead), step("b", RiskAssessment, reads=(MarketRead,)))

    def test_even_a_subclass_of_the_view_is_refused_as_the_last_step(self):
        # Identity, not isinstance. A subclass would serialise fields the engine's
        # TradeSignalDto forbids, and the answer would be refused on arrival instead of
        # here, where the reason is visible.
        class WiderView(TradeView):
            note: str = ""

        with pytest.raises(ValueError, match="TradeView"):
            team(step("a", WiderView))

    def test_two_steps_producing_the_same_schema_are_refused(self):
        # Results are indexed by schema type, so the second would overwrite the first and
        # win for no stated reason.
        with pytest.raises(ValueError, match="same schema"):
            team(
                step("a", MarketRead),
                step("b", MarketRead),
                step("c", TradeView, reads=(MarketRead,)),
            )

    def test_a_repeated_role_is_refused(self):
        with pytest.raises(ValueError, match="repeats a role"):
            team(step("a", MarketRead), step("a", TradeView, reads=(MarketRead,)))

    def test_reading_a_later_steps_result_is_refused(self):
        # It is a cycle: the analyst would need the risk manager's answer to produce the
        # input the risk manager needs.
        with pytest.raises(ValueError, match="RiskAssessment"):
            team(
                step("a", MarketRead, reads=(FactSheet, RiskAssessment)),
                step("b", RiskAssessment, reads=(MarketRead,)),
                step("c", TradeView, reads=(RiskAssessment,)),
            )

    def test_reading_its_own_result_is_refused(self):
        with pytest.raises(ValueError, match="MarketRead"):
            team(
                step("a", MarketRead, reads=(FactSheet, MarketRead)),
                step("b", TradeView, reads=(MarketRead,)),
            )

    def test_reading_something_nobody_produces_is_refused(self):
        with pytest.raises(ValueError, match="Other"):
            team(step("a", TradeView, reads=(Other,)))

    def test_a_team_with_no_steps_is_refused(self):
        with pytest.raises(ValueError, match="no steps"):
            TeamSpec(id="t", instrument_types=frozenset({"equity"}))

    def test_a_team_covering_no_instrument_is_refused(self):
        with pytest.raises(ValueError, match="no instrument types"):
            TeamSpec(id="t", instrument_types=frozenset(), steps=(step("a", TradeView),))

    def test_a_team_without_an_id_is_refused(self):
        with pytest.raises(ValueError, match="needs an id"):
            TeamSpec(id="", instrument_types=frozenset({"equity"}), steps=(step("a", TradeView),))

    @pytest.mark.parametrize("role", ["", " ", "trailing "])
    def test_a_role_that_is_not_a_name_is_refused(self, role):
        # The role is a key in TAS_LLM__ROLES__<ROLE>__*, so a stray space would make an
        # override impossible to spell.
        with pytest.raises(ValueError, match="role must be a name"):
            step(role, TradeView)


class TestTheFactSheetIsAlwaysAvailable:
    def test_a_first_step_may_read_it_without_anyone_producing_it(self):
        # It is computed before any agent runs, so it is the one input that is not a
        # step's output.
        assert team(step("a", TradeView, reads=(FactSheet,))).roles == ("a",)

    def test_a_step_may_read_nothing_at_all(self):
        assert team(step("a", TradeView, reads=())).steps[0].reads == ()


class TestAllRoles:
    def test_it_collects_every_role_across_teams(self):
        # This is what a per-role model override is checked against, so a role missing
        # here would make a valid override look like a typo.
        assert all_roles(TEAMS.values()) == frozenset(DEFAULT_TEAM.roles)

    def test_it_merges_teams_that_share_a_role(self):
        second = team(step("market_analyst", TradeView), team_id="other")

        assert all_roles([DEFAULT_TEAM, second]) == frozenset(DEFAULT_TEAM.roles)
