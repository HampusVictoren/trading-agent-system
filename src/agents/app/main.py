from fastapi import FastAPI

app = FastAPI(title="Trading Agent Service")

@app.get("/health")
def health_check():
    return {"status": "online", "service": "agents"}
