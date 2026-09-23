from ag2 import Agent, tool
from ag2.config import ModelConfig
from ag2.exceptions import AG2Error
from openai import APIConnectionError, APIStatusError, APITimeoutError
from pydantic import ValidationError

from app.application.errors import (
    AgentChainFailed,
    AgentResponseInvalid,
    LlmFailed,
    LlmTimeout,
    LlmUnreachable,
)
from app.domain.models import InvestmentProposal
from app.infrastructure.mcp.market_data_server import get_stock_quote


@tool
def get_stock_quote_tool(ticker: str) -> dict:
    """Hämtar aktuellt pris, P/E-tal och nyckeltal för en aktieticker via FastMCP."""
    return get_stock_quote(ticker)


async def run_agent_analysis(ticker: str, llm_config: ModelConfig) -> InvestmentProposal:
    """Runs the three agents in sequence. The model configuration is built once by the
    lifespan and passed in, so a request never constructs it."""
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

    # APITimeoutError is a subclass of APIConnectionError, so it has to be caught first.
    except APITimeoutError as e:
        raise LlmTimeout(f"The LLM did not answer in time for {ticker}.") from e
    except APIConnectionError as e:
        raise LlmUnreachable(f"The LLM backend could not be reached for {ticker}.") from e
    except APIStatusError as e:
        raise LlmFailed(f"The LLM backend answered {e.status_code} for {ticker}.") from e
    except ValidationError as e:
        # ask(response_schema=...) already asked the model again with the validation error;
        # this is what is left after those retries.
        raise AgentResponseInvalid(
            f"The model never answered in the agreed shape for {ticker}: {e}"
        ) from e
    except AG2Error as e:
        raise AgentChainFailed(f"The agent chain failed for {ticker}: {e}") from e

    if proposal is None:
        raise AgentResponseInvalid(f"The portfolio manager returned no answer for {ticker}.")

    return proposal.model_copy(update={"ticker": ticker.upper()})
