from fastapi import APIRouter

from app.domain.models import InvestmentProposal
from app.infrastructure.ag2.team import run_agent_analysis

router = APIRouter()


@router.post("/analyze/{ticker}", response_model=InvestmentProposal)
async def analyze_ticker(ticker: str):
    return await run_agent_analysis(ticker)
