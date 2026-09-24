"""The HTTP surface. Closed by default, and only one of its endpoints costs money."""

from typing import Annotated

from fastapi import APIRouter, Depends, Path

from app.api.security import require_api_key
from app.dependencies import Resources, get_resources
from app.domain.quotes import InstrumentQuote
from app.domain.signals import (
    MAX_SYMBOL_LENGTH,
    SYMBOL_PATTERN,
    EquityInstrument,
    SignalRequest,
    TradeSignal,
)

# The dependency sits on the router rather than on the route, so a route added later is
# closed by default. /health and /ready are defined outside it and stay open.
router = APIRouter(dependencies=[Depends(require_api_key)])


@router.post("/v1/signals", response_model=TradeSignal)
async def create_signal(
    request: SignalRequest,
    resources: Annotated[Resources, Depends(get_resources)],
) -> TradeSignal:
    """The instrument travels in the body as a typed object, so there is nothing to
    interpolate into a path.

    Versioned in the path from the first day it exists: the engine and this service are
    deployed separately, so a breaking change has to be able to run beside the old shape
    rather than replace it under a running caller.

    A malformed request never reaches the pipeline - FastAPI validates it against
    SignalRequest first, which is where the symbol's format rule lives - and an unknown
    team_id or an instrument this team does not cover is refused before a model is paid for.
    """
    return await resources.pipeline.run(request)


@router.get("/v1/quotes/{symbol}", response_model=InstrumentQuote)
async def get_quote(
    symbol: Annotated[str, Path(pattern=SYMBOL_PATTERN, max_length=MAX_SYMBOL_LENGTH)],
    resources: Annotated[Resources, Depends(get_resources)],
) -> InstrumentQuote:
    """One instrument's price, deterministic and with no model involved.

    The engine has no market data of its own - one integration, in one service - so it
    cannot value a holding it is not currently analysing. Since the position limit is a
    share of the portfolio's value, a portfolio with two holdings could not be sized at all
    without this.

    The symbol is in the path, which is what finding B was about. What made that finding a
    hole was that nothing validated the value; here FastAPI checks it against the same
    pattern the contract and the engine's Ticker enforce, *before* the route body runs, so a
    traversal attempt is a 422 rather than a lookup. The instrument in the answer is then
    built from that validated value rather than echoed from the provider.

    It shares the pipeline's cache, so a price fetched for an analysis is the price a
    valuation gets. The provider returns a quote and its history together and this reads
    only the quote; the history is the same fetch the fact sheet needs anyway, and splitting
    the port in two to save it would mean two shapes of the same call.

    A GET is not retried by the engine's client, which is set up for the expensive endpoint
    beside it. A quote that fails is simply a holding without a price for one cycle, and the
    engine reports that as a sizing outcome rather than trading on a guess.
    """
    snapshot = await resources.market.snapshot(symbol)

    return InstrumentQuote.from_quote(
        snapshot.quote, instrument=EquityInstrument(type="equity", symbol=symbol)
    )
