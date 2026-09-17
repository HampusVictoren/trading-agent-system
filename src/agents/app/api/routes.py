from fastapi import APIRouter
from app.domain.models import InvestmentProposal, ActionEnum

router = APIRouter()

@router.get("/health")
def health_check():
    return {"status": "online", "service": "agents"}

@router.post("/analyze/{ticker}", response_model=InvestmentProposal)
async def analyze_ticker(ticker: str):
    return InvestmentProposal(
        ticker=ticker.upper(),
        action=ActionEnum.BUY,
        amount_usd=250.0,
        confidence=0.85,
        reasoning=f"Preliminär analys av {ticker.upper()} visar goda nyckeltal."
    )
