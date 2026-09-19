from enum import StrEnum

from pydantic import BaseModel, Field, model_validator


class ActionEnum(StrEnum):
    BUY = "BUY"
    SELL = "SELL"
    HOLD = "HOLD"


class InvestmentProposal(BaseModel):
    ticker: str = Field(..., description="Aktiens ticker-symbol")
    action: ActionEnum = Field(..., description="BUY, SELL eller HOLD")
    amount_usd: float = Field(..., ge=0, description="Belopp i USD")
    confidence: float = Field(..., ge=0.0, le=1.0, description="Konfidensgrad")
    reasoning: str = Field(..., description="Motivering till beslutet")

    @model_validator(mode="after")
    def buy_requires_amount(self) -> "InvestmentProposal":
        if self.action == ActionEnum.BUY and self.amount_usd <= 0:
            raise ValueError("amount_usd måste vara större än 0 vid BUY")
        return self
