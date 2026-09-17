from enum import Enum
from pydantic import BaseModel, Field

class ActionEnum(str, Enum):
    BUY = "BUY"
    SELL = "SELL"
    HOLD = "HOLD"

class InvestmentProposal(BaseModel):
    ticker: str = Field(..., description="Aktiens ticker-symbol")
    action: ActionEnum = Field(..., description="BUY, SELL eller HOLD")
    amount_usd: float = Field(..., ge=0, description="Belopp i USD")
    confidence: float = Field(..., ge=0.0, le=1.0, description="Konfidensgrad")
    reasoning: str = Field(..., description="Motivering till beslutet")
