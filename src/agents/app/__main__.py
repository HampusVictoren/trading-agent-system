"""The service's own entrypoint: `python -m app`.

uvicorn's command line is a fine way to start this during development, and it is the wrong
thing to put in a container - for one reason that only becomes visible there. uvicorn
installs a logging configuration of its own before the application starts, and
`configure_logging` runs inside the FastAPI lifespan, which is *after* it. The two lines
uvicorn prints first - "Started server process" and "Waiting for application startup" -
therefore came out in uvicorn's format while every later line was JSON. A collector
reading the stream would fail on exactly the lines that say whether startup happened.

Here logging is configured first and uvicorn is told not to configure it at all, so the
stream is JSON from its first line. `log_config=None` is what does that: uvicorn calls
`dictConfig` only when it has a configuration, and without one the root handler installed
below stays in place while uvicorn's own loggers propagate to it.

The socket comes from the same settings the startup warning reads, rather than from a
command line beside them. That is the other half of the module's point. `TAS_BIND_HOST`
used to be an operator's claim about a socket somebody else opened, and two places holding
one value is how the two come to disagree - the warning would say 127.0.0.1 while uvicorn
listened on every interface. Through this entrypoint the claim is the socket.
"""

import uvicorn

from app.observability.logging import configure_logging
from app.settings import get_settings


def main() -> None:
    """Configures logging, then hands the process to uvicorn. The order is the point."""
    configure_logging()

    settings = get_settings()

    # The import string rather than the app object, so this module does not import
    # app.main - and therefore does not build the application twice when uvicorn imports
    # it for itself.
    uvicorn.run(
        "app.main:app",
        host=settings.bind_host,
        port=settings.port,
        log_config=None,
    )


if __name__ == "__main__":
    main()
