"""Fixtures for the tests that need a real database.

The engine has had these since stage 4's first pull request; this is the same idea on
Python's side. A throwaway pgvector container is built by the *checked-in*
db/init/01-schema.sh, and everything connects as `agent_svc` rather than as the superuser
- so a migration is proved under the grants it will actually run under. The engine found
a permission-denied bug that way (EF Core puts its history table in `public`, which
engine_svc may not write to); Alembic defaults to exactly the same place.

The container is session-scoped and only started by a test that asks for it, so a run that
touches only the unit suite starts nothing and still finishes in seconds.
"""

import asyncio
import threading
import time
from argparse import Namespace
from collections.abc import Callable, Iterator
from pathlib import Path

import asyncpg
import pytest
from alembic import command
from alembic.config import Config
from testcontainers.core.container import DockerContainer

# The same image the engine's fixture uses and the same one docker-compose.yml runs, so a
# migration is tested against the server version it will meet.
IMAGE = "pgvector/pgvector:0.8.6-pg16"

DATABASE = "tradingdb"

# Credentials for a container that exists for the length of one test run and is then
# thrown away. They are not secrets and are deliberately not read from the environment:
# a test that silently used the developer's real password would also silently connect to
# the developer's real database the day a variable was missing.
SUPERUSER = "postgres"
SUPERUSER_PASSWORD = "throwaway"  # noqa: S105
ENGINE_PASSWORD = "throwaway-engine"  # noqa: S105
AGENT_PASSWORD = "throwaway-agent"  # noqa: S105

AGENTS_ROOT = Path(__file__).resolve().parents[1]
INIT_SCRIPT = AGENTS_ROOT.parents[1] / "db" / "init" / "01-schema.sh"

# How long the init script may take before we call it a failure. It creates an extension,
# two roles and two schemas, which is fast; the time goes on Postgres starting twice, as
# the official entrypoint does when it runs initdb.
STARTUP_TIMEOUT_SECONDS = 60
POLL_INTERVAL_SECONDS = 0.25


def _dsn(user: str, password: str, port: int) -> str:
    return f"postgresql://{user}:{password}@127.0.0.1:{port}/{DATABASE}"


async def _wait_until_initialised(dsn: str) -> None:
    """Waits until `agent_svc` can connect, which is a stronger signal than a log line.

    The role is created by the init script, and the official entrypoint runs those scripts
    only after the server is up. So a successful connection as agent_svc means both the
    server and the script finished - whereas "ready to accept connections" is printed once
    before the scripts run and once after, and waiting for the wrong one races.
    """
    deadline = time.monotonic() + STARTUP_TIMEOUT_SECONDS
    last: Exception | None = None

    while time.monotonic() < deadline:
        try:
            connection = await asyncpg.connect(dsn)
        except (OSError, asyncpg.PostgresError) as e:
            last = e
            await asyncio.sleep(POLL_INTERVAL_SECONDS)
        else:
            await connection.close()
            return

    raise TimeoutError(
        f"The database was not initialised within {STARTUP_TIMEOUT_SECONDS} s."
    ) from last


@pytest.fixture(scope="session")
def agent_database() -> Iterator[str]:
    """A database with the roles and schemas but no tables, and the agent_svc DSN for it.

    No tables, because the init script no longer creates any: every table in `agent` is
    Alembic's from here on. A test that wants the schema applied asks for `migrated`.
    """
    container = (
        DockerContainer(IMAGE)
        .with_env("POSTGRES_USER", SUPERUSER)
        .with_env("POSTGRES_PASSWORD", SUPERUSER_PASSWORD)
        .with_env("POSTGRES_DB", DATABASE)
        .with_env("ENGINE_DB_PASSWORD", ENGINE_PASSWORD)
        .with_env("AGENT_DB_PASSWORD", AGENT_PASSWORD)
        .with_exposed_ports(5432)
        # The checked-in script itself, copied in rather than mounted, so the test cannot
        # pass against a file that only exists on this machine - and so that what is
        # proved here is the file that builds the real database.
        .with_copy_into_container(
            INIT_SCRIPT.read_bytes(), "/docker-entrypoint-initdb.d/01-schema.sh", mode=0o755
        )
    )

    with container:
        dsn = _dsn("agent_svc", AGENT_PASSWORD, container.get_exposed_port(5432))
        asyncio.run(_wait_until_initialised(dsn))
        yield dsn


def alembic_config(dsn: str) -> Config:
    """Alembic pointed at a given database, the way an operator would point it at theirs.

    The URL travels as `-x url=`, which is the same path env.py offers a human who wants
    to migrate a database other than the one in their environment. Using it here means the
    test exercises that path rather than a second one written for tests.
    """
    config = Config(
        str(AGENTS_ROOT / "alembic.ini"),
        # What `alembic -x url=... upgrade head` would have parsed. env.py reads it
        # through get_x_argument, so nothing in it exists only for tests.
        cmd_opts=Namespace(x=[f"url={dsn}"]),
    )
    config.set_main_option("script_location", str(AGENTS_ROOT / "migrations"))
    return config


def _off_the_event_loop(work: Callable[[], None]) -> None:
    """Runs Alembic somewhere it can own an event loop.

    env.py drives an async engine through asyncio.run, which is exactly right for a
    command-line tool and impossible from inside a running loop - and pytest-asyncio's
    auto mode means one is always running by the time a fixture is set up. A thread of its
    own puts Alembic in the same situation the `alembic` command is in, rather than
    changing env.py to suit the tests.
    """
    failure: list[BaseException] = []

    def target() -> None:
        try:
            work()
        except BaseException as e:  # noqa: BLE001 - re-raised on the calling thread below
            failure.append(e)

    thread = threading.Thread(target=target)
    thread.start()
    thread.join()

    if failure:
        raise failure[0]


def upgrade(config: Config, revision: str = "head") -> None:
    _off_the_event_loop(lambda: command.upgrade(config, revision))


def downgrade(config: Config, revision: str = "base") -> None:
    _off_the_event_loop(lambda: command.downgrade(config, revision))


@pytest.fixture
def migrated(agent_database: str) -> Iterator[str]:
    """The schema at `head`, and torn down again by running the migrations backwards.

    Every test therefore runs `downgrade base` on its way out, which makes the roadmap's
    "migrations are tested both ways" a property of the whole suite rather than of one
    test that could be deleted. A downgrade that does not undo its upgrade leaves the next
    test looking at a schema that already exists, and the failure is immediate.

    Function-scoped on purpose: the container is what is slow, not the migrations.

    The rows a test wrote are cleared **before** the downgrade, and the reason is a property
    of the migrations rather than of any test. A migration that widens a column in place - as
    the one for Swedish symbols does - cannot be reversed while a stored value needs the extra
    width, and refusing is the right behaviour for an operator: narrowing would have to lose
    information, and losing it quietly is worse than stopping. The tables are append-only, so
    a downgrade cannot tidy up after itself either. `TRUNCATE` is the one statement the
    triggers deliberately allow, because it is the owner clearing a table on purpose.

    What that costs is real and worth naming: the downgrade is exercised against an empty
    schema rather than against data. The other migrations drop their tables, so they were
    never getting more than that anyway.
    """
    config = alembic_config(agent_database)
    upgrade(config)

    try:
        yield agent_database
    finally:
        _clear_append_only_tables(agent_database)
        downgrade(config)


def _clear_append_only_tables(dsn: str) -> None:
    """Empty every `agent` table, in one statement so foreign keys do not dictate an order."""

    async def truncate() -> None:
        connection = await asyncpg.connect(dsn)
        try:
            await connection.execute(
                "TRUNCATE agent.analysis_embeddings, agent.step_outputs, "
                "agent.analysis_runs, agent.signal_outcomes"
            )
        finally:
            await connection.close()

    _off_the_event_loop(lambda: asyncio.run(truncate()))
