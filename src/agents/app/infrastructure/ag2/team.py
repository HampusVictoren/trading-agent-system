import json
import re
from ag2 import Agent, tool
from app.infrastructure.ag2.config import get_llm_config
from app.infrastructure.mcp.market_data_server import get_stock_quote

@tool
def get_stock_quote_tool(ticker: str) -> dict:
    """Hämtar aktuellt pris, P/E-tal och nyckeltal för en aktieticker via FastMCP."""
    return get_stock_quote(ticker)

async def run_agent_analysis(ticker: str) -> dict:
    llm_config = get_llm_config()

    # 1. Analyst Agent med MCP-verktyg
    analyst = Agent(
        "MarketAnalyst",
        prompt=(
            "Du är en junior aktieanalytiker. Din uppgift är att hämta marknadsdata "
            "för den givna tickern med verktyget get_stock_quote_tool och ge en kort bedömning."
        ),
        config=llm_config,
        tools=[get_stock_quote_tool],
    )

    # 2. Risk Manager Agent
    risk_manager = Agent(
        "RiskManager",
        prompt=(
            "Du är en strikt Risk Manager. Granska analytikerns data och tes. "
            "Identifiera eventuella nedsidor, hög värdering (P/E) eller osäkerhet."
        ),
        config=llm_config,
    )

    # 3. Portfolio Manager Agent
    portfolio_manager = Agent(
        "PortfolioManager",
        prompt=(
            "Du är ansvarig för portföljen. Lyssna på analytikerns och risk managerns slutsatser. "
            "Fatta ett slutgiltigt beslut och svara EENBART med ett giltigt JSON-objekt i följande format:\n"
            "{\n"
            '  "ticker": "TICKER",\n'
            '  "action": "BUY" eller "HOLD",\n'
            '  "amount_usd": 250.0,\n'
            '  "confidence": 0.85,\n'
            '  "reasoning": "Kort motivering som sammanfattar valet."\n'
            "}\n"
            "Svara INTE med någon övrig text, markdown-kodblock eller förklaringar utöver JSON-objektet."
        ),
        config=llm_config,
    )

    try:
        # Steg A: Analytikern kör analys via sitt verktyg
        analyst_reply = await analyst.ask(f"Analysera aktien {ticker}. Använd get_stock_quote_tool för att hämta data.")
        analyst_summary = analyst_reply.body

        # Steg B: Risk Manager granskar
        risk_reply = await risk_manager.ask(f"Granska följande analys för {ticker}:\n{analyst_summary}")
        risk_summary = risk_reply.body

        # Steg C: Portfolio Manager fattar beslut
        pm_reply = await portfolio_manager.ask(
            f"Fatta beslut för {ticker} baserat på:\n"
            f"Analys: {analyst_summary}\n"
            f"Risk: {risk_summary}"
        )
        raw_response = pm_reply.body

        # Tvätta ur JSON om LLM inkluderade markdown-fencing
        json_match = re.search(r"\{.*\}", raw_response, re.DOTALL)
        if json_match:
            return json.loads(json_match.group(0))

        return json.loads(raw_response)

    except Exception as e:
        return {
            "ticker": ticker.upper(),
            "action": "BUY",
            "amount_usd": 250.0,
            "confidence": 0.5,
            "reasoning": f"Fallback p.g.a. fel under AG2 v1.0 agentdebatten: {str(e)}"
        }
