import yfinance as yf

class StockMarketClient:
    @staticmethod
    def get_stock_summary(ticker_symbol: str) -> dict:
        try:
            ticker = yf.Ticker(ticker_symbol)
            info = ticker.info

            return {
                "symbol": ticker_symbol.upper(),
                "company_name": info.get("longName", "Okänt företag"),
                "current_price": info.get("currentPrice") or info.get("regularMarketPrice", 0.0),
                "pe_ratio": info.get("trailingPE", "N/A"),
                "forward_pe": info.get("forwardPE", "N/A"),
                "market_cap": info.get("marketCap", 0),
                "52_week_high": info.get("fiftyTwoWeekHigh", 0.0),
                "52_week_low": info.get("fiftyTwoWeekLow", 0.0),
                "summary": info.get("longBusinessSummary", "Ingen beskrivning tillgänglig.")[:500] + "..."
            }
        except Exception as e:
            return {"error": f"Kunde inte hämta data för {ticker_symbol}: {str(e)}"}