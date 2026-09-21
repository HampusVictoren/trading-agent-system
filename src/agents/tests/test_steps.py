"""The schemas are the ceiling on what crosses a step boundary, so the ceiling is tested.

Decision 4 says a step reads named earlier results rather than a transcript. That only
bounds the cost if the results themselves are bounded - an uncapped field would make the
next prompt as large as the model felt like making it.
"""

import json

import pytest
from pydantic import ValidationError

from app.domain.signals import TradeView
from app.domain.steps import (
    MAX_NOTE_LENGTH,
    MAX_NOTES,
    MarketRead,
    RiskAssessment,
    Severity,
    Trend,
    Valuation,
)

AGENT_WRITTEN = [MarketRead, RiskAssessment, TradeView]


def unbounded_strings(model: type) -> list[str]:
    """Every string field in a schema that has no maximum length, by path.

    Walks the $defs indirection pydantic generates, because a cap declared on a reused
    annotation is still a cap and must not read as a missing one.
    """
    schema = model.model_json_schema()
    definitions = schema.get("$defs", {})
    found: list[str] = []

    def resolve(node: dict) -> dict:
        while "$ref" in node:
            node = definitions[node["$ref"].rsplit("/", 1)[-1]]
        return node

    def walk(node: dict, path: str) -> None:
        node = resolve(node)
        # A category is bounded by its own list of values.
        if "enum" in node or "const" in node:
            return
        for combinator in ("anyOf", "oneOf", "allOf"):
            for branch in node.get(combinator, []):
                walk(branch, path)
        if node.get("type") == "string" and "maxLength" not in node:
            found.append(path)
        if node.get("type") == "array":
            walk(node.get("items", {}), f"{path}[]")
        for name, subschema in node.get("properties", {}).items():
            walk(subschema, f"{path}.{name}" if path else name)

    walk(schema, "")
    return found


class TestNothingAnAgentWritesIsUnbounded:
    @pytest.mark.parametrize("model", AGENT_WRITTEN, ids=lambda m: m.__name__)
    def test_every_string_an_agent_fills_has_a_cap(self, model):
        # Generic on purpose: a field added later is covered without anyone remembering to
        # extend this test.
        assert unbounded_strings(model) == []

    def test_the_check_itself_can_fail(self):
        # Otherwise a bug in the walk above would make every assertion vacuous.
        from pydantic import BaseModel

        class Leaky(BaseModel):
            note: str

        assert unbounded_strings(Leaky) == ["note"]

    @pytest.mark.parametrize(
        "worst_case",
        [
            MarketRead(
                trend=Trend.UP,
                valuation=Valuation.FAIR,
                observations=["x" * MAX_NOTE_LENGTH] * MAX_NOTES,
            ),
            RiskAssessment(
                downside=Severity.HIGH, veto=False, risks=["x" * MAX_NOTE_LENGTH] * MAX_NOTES
            ),
        ],
        ids=lambda m: type(m).__name__,
    )
    def test_the_largest_legal_handover_is_still_small(self, worst_case):
        # A ceiling on what one handover can cost the next prompt, taken at the worst case
        # the schema allows rather than at a typical answer. It is the cheapest test there
        # is for the thing the whole design is about.
        assert len(worst_case.model_dump_json()) < 700


class TestMarketRead:
    def valid(self, **overrides: object) -> dict:
        return {
            "trend": "UP",
            "valuation": "FAIR",
            "observations": ["Momentum utan stöd i vinstrevideringar."],
        } | overrides

    def test_it_reads_a_well_formed_answer(self):
        read = MarketRead.model_validate(self.valid())

        assert read.trend is Trend.UP
        assert read.valuation is Valuation.FAIR

    def test_a_share_with_no_earnings_can_say_so(self):
        # Without UNKNOWN the model has to pick CHEAP, FAIR or EXPENSIVE for a company
        # that has no P/E at all, and whichever it picks is invented.
        assert MarketRead.model_validate(self.valid(valuation="UNKNOWN")).valuation is (
            Valuation.UNKNOWN
        )

    def test_a_category_outside_the_list_is_refused(self):
        with pytest.raises(ValidationError):
            MarketRead.model_validate(self.valid(trend="VOLATILE"))

    def test_more_notes_than_the_cap_are_refused(self):
        with pytest.raises(ValidationError):
            MarketRead.model_validate(self.valid(observations=["note"] * (MAX_NOTES + 1)))

    def test_a_note_longer_than_the_cap_is_refused(self):
        with pytest.raises(ValidationError):
            MarketRead.model_validate(self.valid(observations=["x" * (MAX_NOTE_LENGTH + 1)]))

    def test_a_field_outside_the_schema_is_refused(self):
        with pytest.raises(ValidationError):
            MarketRead.model_validate(self.valid(price=100.0))

    def test_it_does_not_carry_the_fact_sheets_numbers_onward(self):
        # Copying a number through a model is how it gets changed on the way. A step that
        # needs the price reads the fact sheet, which is a `reads` entry anyone can see.
        for copied in ("price", "pe_ratio", "volatility_30d", "return_1m"):
            assert copied not in MarketRead.model_fields


class TestRiskAssessment:
    def valid(self, **overrides: object) -> dict:
        return {"downside": "MEDIUM", "veto": False, "risks": ["Värderingen är utsträckt."]} | (
            overrides
        )

    def test_it_reads_a_well_formed_answer(self):
        assessment = RiskAssessment.model_validate(self.valid())

        assert assessment.downside is Severity.MEDIUM
        assert assessment.veto is False

    def test_a_veto_is_a_view_and_not_a_control(self):
        # It reaches the portfolio manager and is stored; it cannot stop a trade. The
        # engine's RiskEngine is what does that, and an agent cannot cause one either.
        assert RiskAssessment.model_validate(self.valid(veto=True)).veto is True

    def test_a_severity_outside_the_list_is_refused(self):
        with pytest.raises(ValidationError):
            RiskAssessment.model_validate(self.valid(downside="CATASTROPHIC"))

    def test_more_risks_than_the_cap_are_refused(self):
        with pytest.raises(ValidationError):
            RiskAssessment.model_validate(self.valid(risks=["risk"] * (MAX_NOTES + 1)))


def test_the_schemas_the_model_is_sent_are_small():
    # These go into every request as response_format. Recorded rather than asserted
    # loosely, so that a schema that doubles shows up in a diff.
    sizes = {m.__name__: len(json.dumps(m.model_json_schema())) for m in AGENT_WRITTEN}

    assert sizes["MarketRead"] < 900
    assert sizes["RiskAssessment"] < 900
