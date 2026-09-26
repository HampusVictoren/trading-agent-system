"""The symbol rule, against the instruments it now has to admit.

The pattern is written in seven places - this module, the engine's `Ticker`, and five
contract files - so the tests that matter are the ones comparing them rather than any one of
them. The list of Swedish tickers is the specification: it was a ten-character cap until the
universe moved to Stockholm, and ten silently rejected a real index member.
"""

import json
import re
from pathlib import Path

import pytest

from app.domain.signals import MAX_SYMBOL_LENGTH, SYMBOL_PATTERN

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts"

# Every OMXS30 member as yfinance names it, plus the benchmark outcomes are measured against.
# ESSITY-B.ST is the one the old ten-character cap rejected, and XACT-OMXS30.ST at fourteen
# is the longest symbol the system uses at all.
OMXS30 = [
    "ABB.ST",
    "ALFA.ST",
    "ASSA-B.ST",
    "ATCO-A.ST",
    "ATCO-B.ST",
    "AZN.ST",
    "BOL.ST",
    "ELUX-B.ST",
    "EQT.ST",
    "ERIC-B.ST",
    "ESSITY-B.ST",
    "EVO.ST",
    "GETI-B.ST",
    "HEXA-B.ST",
    "HM-B.ST",
    "INVE-B.ST",
    "KINV-B.ST",
    "NDA-SE.ST",
    "NIBE-B.ST",
    "SAAB-B.ST",
    "SAND.ST",
    "SCA-B.ST",
    "SEB-A.ST",
    "SHB-A.ST",
    "SINCH.ST",
    "SKF-B.ST",
    "SWED-A.ST",
    "TEL2-B.ST",
    "TELIA.ST",
    "VOLV-B.ST",
]
BENCHMARK = "XACT-OMXS30.ST"


def contract(name: str) -> dict:
    return json.loads((CONTRACTS / f"{name}.schema.json").read_text(encoding="utf-8"))


def patterns_in(node) -> list[str]:
    """Every "pattern" value anywhere in a schema, however deeply nested."""
    if isinstance(node, dict):
        found = [node["pattern"]] if "pattern" in node else []
        return found + [p for value in node.values() for p in patterns_in(value)]
    if isinstance(node, list):
        return [p for item in node for p in patterns_in(item)]
    return []


class TestTheRuleAdmitsTheInstrumentsTheSystemTrades:
    @pytest.mark.parametrize("symbol", OMXS30)
    def test_every_omxs30_member_is_a_valid_symbol(self, symbol):
        assert re.match(SYMBOL_PATTERN, symbol), symbol
        assert len(symbol) <= MAX_SYMBOL_LENGTH

    def test_the_benchmark_is_a_valid_symbol(self):
        # It travels through GET /v1/quotes/{symbol}/history like any other, so a benchmark
        # the pattern rejects is an outcome that can never be measured.
        assert re.match(SYMBOL_PATTERN, BENCHMARK)
        assert len(BENCHMARK) <= MAX_SYMBOL_LENGTH

    def test_the_longest_symbol_in_use_leaves_room_but_not_much(self):
        # A cap that exactly fits today's longest symbol is one that breaks on the next
        # listing; a cap of fifty is not a cap. This states the margin on purpose.
        longest = max(OMXS30 + [BENCHMARK], key=len)

        assert len(longest) == 14
        assert MAX_SYMBOL_LENGTH == 16

    @pytest.mark.parametrize("symbol", ["AAPL", "MSFT", "SPY"])
    def test_the_american_symbols_still_pass(self, symbol):
        # The rule was widened, not replaced. The stored history is USD and the decisions
        # behind finding G are about these, so they have to stay readable.
        assert re.match(SYMBOL_PATTERN, symbol)


class TestTheRuleStillRefusesWhatItAlwaysDid:
    @pytest.mark.parametrize(
        "symbol",
        [
            "",
            "aapl",
            "1AAPL",
            ".AAPL",
            "-AAPL",
            "^OMX",
            "A" * (MAX_SYMBOL_LENGTH + 1),
            "AAPL ",
            "AAPL;DROP",
        ],
    )
    def test_a_symbol_that_is_not_a_symbol_is_refused(self, symbol):
        assert not re.match(SYMBOL_PATTERN, symbol)

    def test_an_index_symbol_is_refused_which_is_why_the_benchmark_is_a_fund(self):
        # ^OMX is the index itself and would be the purer benchmark, but a leading ^ is not
        # something any tradeable instrument has - admitting it here would widen the rule for
        # instruments the engine can own. The fund tracking the index passes as it stands.
        assert not re.match(SYMBOL_PATTERN, "^OMX")
        assert re.match(SYMBOL_PATTERN, BENCHMARK)


class TestEveryCopyOfTheRuleIsTheSameRule:
    CONTRACT_NAMES = ["trade-signal", "quote", "quote-history", "outcome", "screen"]

    @pytest.mark.parametrize("name", CONTRACT_NAMES)
    def test_the_contract_spells_the_pattern_exactly_as_this_module_does(self, name):
        # String equality rather than behavioural equivalence. Two spellings of one regex
        # behave the same until someone edits one of them, and this repo has five copies.
        found = {p for p in patterns_in(contract(name)) if p.startswith("^[A-Z]")}

        assert found == {SYMBOL_PATTERN}

    def test_no_contract_was_missed(self):
        # A sixth contract growing a symbol without joining the list above would otherwise
        # be the one place the rule quietly differs.
        with_symbols = {
            path.stem.removesuffix(".schema")
            for path in CONTRACTS.glob("*.schema.json")
            if any(p.startswith("^[A-Z]") for p in patterns_in(json.loads(path.read_text())))
        }

        assert with_symbols == set(self.CONTRACT_NAMES)
