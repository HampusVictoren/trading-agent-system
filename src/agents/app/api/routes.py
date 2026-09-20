from typing import Annotated

from fastapi import APIRouter, Depends, Path

from app.dependencies import Resources, get_resources
from app.domain.models import InvestmentProposal
from app.infrastructure.ag2.team import run_agent_analysis

router = APIRouter()

# A ticker reaches yfinance, and in the engine it is interpolated into a URL path.
# Anything outside this is a bad request, not something to spend an LLM run on.
TICKER_PATTERN = r"^[A-Za-z][A-Za-z0-9.-]{0,9}$"


@router.post("/analyze/{ticker}", response_model=InvestmentProposal)
async def analyze_ticker(
    ticker: Annotated[str, Path(pattern=TICKER_PATTERN)],
    resources: Annotated[Resources, Depends(get_resources)],
) -> InvestmentProposal:
    return await run_agent_analysis(ticker, resources.llm_config)
