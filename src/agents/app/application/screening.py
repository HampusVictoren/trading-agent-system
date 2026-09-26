"""Fetch the universe once, score every instrument, return the best of them.

The orchestration only. Every decision about *which* instruments are good is a pure function
in `app.domain.screening`, and everything about *reaching* a source is behind the
`UniverseData` port - so this file is the part that can be read in one sitting to know what
a screen does.

It costs no LLM call, which is the point: the agents see the shortlist instead of the
universe, and that is the largest token saving in the system. It is also the control the
project is judged against - if the decisions made from a shortlist do no better than the
shortlist itself, the ranking is worth more than the team.
"""

import logging
from datetime import UTC, datetime

from app.application.ports import UniverseData
from app.domain.screening import (
    Candidate,
    Rejection,
    ScreenRequest,
    ScreenResult,
    rank,
    score_candidate,
)

logger = logging.getLogger(__name__)

# What a symbol missing from the source's answer is rejected with. Phrased as a fact about
# the data rather than as an error, because that is what it is at this level: the request
# reached the source and the source had nothing for this one.
NO_DATA = "no price history came back for this symbol"


class ScreeningService:
    """One instance, built at startup beside the pipeline."""

    def __init__(self, data: UniverseData) -> None:
        self._data = data

    async def screen(self, request: ScreenRequest) -> ScreenResult:
        """Rank the universe. Raises `MarketDataUnavailable` if the source cannot be reached.

        Nothing here catches that: a screen against a fraction of the universe would be a
        shortlist that reads as a judgement and is an outage. A *single* symbol the source
        had nothing for is the other case entirely, and becomes a rejection.
        """
        symbols = tuple(instrument.symbol for instrument in request.universe)
        histories = await self._data.histories(symbols)

        candidates: list[Candidate] = []
        rejected: list[Rejection] = []

        for instrument in request.universe:
            bars = histories.get(instrument.symbol)
            if not bars:
                rejected.append(Rejection(instrument=instrument, reason=NO_DATA))
                continue

            scored = score_candidate(instrument, bars, min_dollar_volume=request.min_dollar_volume)
            if isinstance(scored, Candidate):
                candidates.append(scored)
            else:
                rejected.append(scored)

        shortlist = rank(candidates, limit=request.limit)

        # One line per screen, at info: how much of the universe could be ranked is the first
        # thing to look at when a shortlist gets strange, and it is cheap enough to keep.
        logger.info(
            "Screened %d instruments: %d ranked, %d rejected, %d shortlisted.",
            len(request.universe),
            len(candidates),
            len(rejected),
            len(shortlist),
        )

        return ScreenResult(
            candidates=shortlist,
            rejected=tuple(rejected),
            # Wall clock rather than the newest bar's date. It records when the ranking was
            # made, which is what a stored shortlist needs to be read against - the bars it
            # used are already implied by the figures travelling with each candidate.
            as_of=datetime.now(UTC),
        )
