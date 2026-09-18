from fastapi import APIRouter
from pydantic import BaseModel
from app.infrastructure.ag2.team import run_agent_analysis

router = APIRouter()

class InvestmentProposal(BaseModel):
    ticker: str
    action: str
    amount_usd: float
    confidence: float
    reasoning: str

@router.post("/analyze/{ticker}", response_model=InvestmentProposal)
async def analyze_ticker(ticker: str):
    proposal_data = await run_agent_analysis(ticker)
    return InvestmentProposal(**proposal_data)
