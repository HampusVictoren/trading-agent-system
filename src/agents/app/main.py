from fastapi import FastAPI
from app.api.routes import router
app = FastAPI(
    title="Trading Agent Service",
    version="1.0.0",
    description="Python AI Agent Service for Financial Analysis"
)

@app.get("/health")
def health_check():
    return {"status": "online", "service": "agents"}

app.include_router(router)