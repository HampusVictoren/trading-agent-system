import logging

from ag2 import Agent, tool

from app.domain.models import ActionEnum, InvestmentProposal
from app.infrastructure.ag2.config import get_llm_config
from app.infrastructure.mcp.market_data_server import get_stock_quote

logger = logging.getLogger(__name__)


@tool
def get_stock_quote_tool(ticker: str) -> dict:
    """Hämtar aktuellt pris, P/E-tal och nyckeltal för en aktieticker via FastMCP."""
    return get_stock_quote(ticker)


async def run_agent_analysis(ticker: str) -> InvestmentProposal:
    llm_config = get_llm_config()

    # 1. Analyst agent with its MCP tool
    analyst = Agent(
        "MarketAnalyst",
        prompt=(
            "Du är en junior aktieanalytiker. Din uppgift är att hämta marknadsdata "
            "för den givna tickern med verktyget get_stock_quote_tool och ge en kort bedömning."
        ),
        config=llm_config,
        tools=[get_stock_quote_tool],
    )

    # 2. Risk manager agent
    risk_manager = Agent(
        "RiskManager",
        prompt=(
            "Du är en strikt Risk Manager. Granska analytikerns data och tes. "
            "Identifiera eventuella nedsidor, hög värdering (P/E) eller osäkerhet."
        ),
        config=llm_config,
    )

    # 3. Portfolio manager agent (response shape is driven by response_schema below)
    portfolio_manager = Agent(
        "PortfolioManager",
        prompt=(
            "Du är ansvarig för portföljen. Lyssna på analytikerns och risk managerns slutsatser "
            "och fatta ett slutgiltigt beslut: action ska vara BUY eller HOLD, "
            "amount_usd är beloppet "
            "i USD att köpa för (0 vid HOLD), confidence är din säkerhet mellan 0 och 1 och "
            "reasoning är en kort motivering som sammanfattar valet."
        ),
        config=llm_config,
    )

    try:
        # Step A: the analyst runs its analysis through the tool
        analyst_reply = await analyst.ask(
            f"Analysera aktien {ticker}. Använd get_stock_quote_tool för att hämta data."
        )
        analyst_summary = analyst_reply.body

        # Step B: the risk manager reviews it
        risk_reply = await risk_manager.ask(
            f"Granska följande analys för {ticker}:\n{analyst_summary}"
        )
        risk_summary = risk_reply.body

        # Step C: the portfolio manager decides, validated against InvestmentProposal
        pm_reply = await portfolio_manager.ask(
            f"Fatta beslut för {ticker} baserat på:\n"
            f"Analys: {analyst_summary}\n"
            f"Risk: {risk_summary}",
            response_schema=InvestmentProposal,
        )
        proposal = await pm_reply.content(retries=2)
        if proposal is None:
            raise ValueError("Portfolio Manager returnerade inget svar.")

        return proposal.model_copy(update={"ticker": ticker.upper()})

    except Exception as e:
        # An error must never lead to a buy - the engine ignores anything that is not BUY
        logger.exception("Agentkedjan misslyckades för %s", ticker)
        return InvestmentProposal(
            ticker=ticker.upper(),
            action=ActionEnum.HOLD,
            amount_usd=0.0,
            confidence=0.0,
            reasoning=f"Fallback (HOLD) p.g.a. fel i agentkedjan: {e}",
        )
