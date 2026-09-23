"""The HTTP surface. One endpoint that costs money, and it is closed by default."""

from typing import Annotated

from fastapi import APIRouter, Depends

from app.api.security import require_api_key
from app.dependencies import Resources, get_resources
from app.domain.signals import SignalRequest, TradeSignal

# The dependency sits on the router rather than on the route, so a route added later is
# closed by default. /health and /ready are defined outside it and stay open.
router = APIRouter(dependencies=[Depends(require_api_key)])


@router.post("/v1/signals", response_model=TradeSignal)
async def create_signal(
    request: SignalRequest,
    resources: Annotated[Resources, Depends(get_resources)],
) -> TradeSignal:
    """The instrument travels in the body as a typed object, so there is nothing to
    interpolate into a path.

    Versioned in the path from the first day it exists: the engine and this service are
    deployed separately, so a breaking change has to be able to run beside the old shape
    rather than replace it under a running caller.

    A malformed request never reaches the pipeline - FastAPI validates it against
    SignalRequest first, which is where the symbol's format rule lives - and an unknown
    team_id or an instrument this team does not cover is refused before a model is paid for.
    """
    return await resources.pipeline.run(request)
