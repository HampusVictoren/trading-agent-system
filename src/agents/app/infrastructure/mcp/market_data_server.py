import yfinance as yf
from fastmcp import FastMCP

mcp = FastMCP("MarketDataService")


@mcp.tool()
def get_stock_quote(ticker: str) -> dict:
    """Hämtar aktuellt pris, P/E-tal och nyckeltal för en aktieticker."""
    try:
        stock = yf.Ticker(ticker)
        info = stock.info
        return {
            "symbol": ticker.upper(),
            "price": info.get("currentPrice") or info.get("regularMarketPrice"),
            "pe_ratio": info.get("forwardPE"),
            "market_cap": info.get("marketCap"),
            "currency": info.get("currency", "USD"),
            "summary": info.get("longBusinessSummary", "")[:300],
        }
    except Exception as e:
        return {"error": f"Misslyckades att hämta data för {ticker}: {str(e)}"}


if __name__ == "__main__":
    mcp.run()
