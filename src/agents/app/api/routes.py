from typing import Annotated

from fastapi import APIRouter, Depends, Path

from app.api.security import require_api_key
from app.dependencies import Resources, get_resources
from app.domain.models import InvestmentProposal
from app.infrastructure.ag2.team import run_agent_analysis

# The dependency sits on the router rather than on the route, so a route added later is
# closed by default. /health and /ready are defined outside it and stay open.
router = APIRouter(dependencies=[Depends(require_api_key)])

# A ticker reaches yfinance, and in the engine it is interpolated into a URL path.
# Anything outside this is a bad request, not something to spend an LLM run on.
TICKER_PATTERN = r"^[A-Za-z][A-Za-z0-9.-]{0,9}$"


@router.post("/analyze/{ticker}", response_model=InvestmentProposal)
async def analyze_ticker(
    ticker: Annotated[str, Path(pattern=TICKER_PATTERN)],
    resources: Annotated[Resources, Depends(get_resources)],
) -> InvestmentProposal:
    return await run_agent_analysis(ticker, resources.models)
