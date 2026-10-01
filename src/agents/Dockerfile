# syntax=docker/dockerfile:1

# The agent service as two images from one file: the service itself, and the Alembic step
# that owns the `agent` schema. They are separate targets rather than one image with two
# commands, because the running service must not carry the ability to migrate the database
# it reads - the same separation the engine has, where applying a migration is a decision
# an operator takes rather than something a startup does.
#
# The build context is this directory, not the repository root: pyproject.toml and uv.lock
# are here, and a context one level up would hand the builder the engine's source and the
# root .env for no reason.

# Pinned to a patch version rather than 3.12-slim, so a rebuild is the same Python and an
# upgrade is a commit. Dependabot keeps both current; that is also why they are build
# arguments, since it only rewrites what it can find in a FROM line.
ARG PYTHON_IMAGE=python:3.12.14-slim-bookworm
ARG UV_IMAGE=ghcr.io/astral-sh/uv:0.12.20

# uv comes from its own published image rather than from pip or a curl: it is one static
# binary, and this way the version is pinned in a line Dependabot understands.
FROM ${UV_IMAGE} AS uv


FROM ${PYTHON_IMAGE} AS builder

COPY --from=uv /uv /usr/local/bin/uv

ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    # No Python downloads: the image's own interpreter is the one the service must run on,
    # and a silent download would make the base image a decoration.
    UV_PYTHON_DOWNLOADS=never \
    # An absolute path, identical in every stage below. A virtualenv records its own
    # location, so a venv copied to a different path is a venv that cannot find itself.
    UV_PROJECT_ENVIRONMENT=/srv/.venv

WORKDIR /src

# Dependencies before the source, so editing a module does not re-resolve a lock file of
# four hundred kilobytes. --locked fails if uv.lock has drifted from pyproject.toml, which
# is the same guarantee CI gets rather than a looser one.
COPY pyproject.toml uv.lock ./
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --locked --no-dev --no-install-project

# README.md is declared as the project's readme, so the build backend needs it.
COPY README.md ./
COPY app ./app

# --no-editable installs the project as a built wheel instead of pointing at /src, which
# is what makes the next stage able to copy a venv and nothing else. It also means the
# image cannot repeat the regression CI guards against: `app` was once missing from the
# wheel and the service only ran because the working directory happened to hide it. Here
# there is no working directory to hide behind.
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --locked --no-dev --no-editable


# The same venv plus Alembic. Its own stage rather than its own builder, so the service
# and the migration step are provably built from one resolution of one lock file.
FROM builder AS migrate-builder

RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --locked --no-dev --group migrate --no-editable


FROM ${PYTHON_IMAGE} AS runtime

# A user of its own, with a fixed uid so that ownership is predictable wherever this runs.
# System account, no home, no shell: nothing about this service needs to log in.
RUN groupadd --system --gid 10001 agents \
 && useradd --system --uid 10001 --gid agents --no-create-home --shell /usr/sbin/nologin agents

ENV PYTHONUNBUFFERED=1 \
    # The bytecode is compiled into the image by UV_COMPILE_BYTECODE above, and the
    # service's user cannot write to the venv anyway - which is deliberate: an image whose
    # code the running process may rewrite is an image whose code is not what was built.
    PYTHONDONTWRITEBYTECODE=1 \
    PATH="/srv/.venv/bin:${PATH}"


FROM runtime AS service

COPY --from=builder /srv/.venv /srv/.venv

# A container's loopback reaches only the container, so the service has to bind every
# interface for anything to reach it; compose publishes the port on 127.0.0.1 alone. It is
# declared here as well as bound because the entrypoint reads it - see app/__main__.py.
ENV TAS_BIND_HOST=0.0.0.0 \
    TAS_PORT=8000

EXPOSE 8000

USER agents

# Liveness, not readiness, and the difference matters to what compose does with it. /ready
# answers 503 until the database and the LLM backend both answer, so using it here would
# make a blinking Ollama an unhealthy container - and anything gated on this service being
# healthy would then refuse to start over an outage the engine already handles by taking no
# decision that cycle. /health says the process is up, which is what a restarter should act
# on. Python rather than curl, because the image has one and not the other.
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
    CMD ["python", "-c", "import os, sys, urllib.request as r; p = os.environ.get('TAS_PORT', '8000'); sys.exit(0 if r.urlopen(f'http://127.0.0.1:{p}/health', timeout=2).status == 200 else 1)"]

# No WORKDIR on purpose: `app` is installed, not copied, so there is no directory for an
# import to resolve against. uvicorn is reached through the module entrypoint rather than
# its command line, which is what keeps the first log line JSON.
CMD ["python", "-m", "app"]


FROM runtime AS migrate

COPY --from=migrate-builder /srv/.venv /srv/.venv

# alembic.ini's script_location is relative, so both halves have to sit in one directory.
WORKDIR /srv/schema
COPY alembic.ini ./
COPY migrations ./migrations

USER agents

# alembic as the entrypoint, so the command is the operator's: `upgrade head` by default,
# but `current` to ask where a database is and `upgrade head --sql` to read the statements
# before anything applies them.
#
# The URL travels as `-x url=...` rather than through TAS_DATABASE_URL, which is env.py's
# documented path for migrating a database other than the caller's own. That is not a
# workaround: get_settings() requires every setting the service needs, so without it this
# container would need an LLM API key to create a table.
ENTRYPOINT ["alembic"]
CMD ["upgrade", "head"]
