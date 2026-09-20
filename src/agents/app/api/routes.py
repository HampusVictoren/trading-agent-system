from typing import Annotated

from fastapi import APIRouter, Depends

from app.dependencies import Resources, get_resources
from app.domain.models import InvestmentProposal
from app.infrastructure.ag2.team import run_agent_analysis

router = APIRouter()


@router.post("/analyze/{ticker}", response_model=InvestmentProposal)
async def analyze_ticker(ticker: str, resources: Annotated[Resources, Depends(get_resources)]):
    return await run_agent_analysis(ticker, resources.llm_config)
