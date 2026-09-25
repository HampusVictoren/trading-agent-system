"""How Alembic reaches the database, and under whose grants.

Three things here are decisions rather than boilerplate.

**The URL is not in alembic.ini.** It carries the agent_svc password, so it comes from
app.settings like everything else secret in this service - or, for a test, from
`-x url=...`. A URL in a checked-in file is a password in a checked-in file.

**The version table is named, not inherited.** Alembic puts its bookkeeping wherever the
connection's search_path points, and agent_svc's search_path is set to `agent` - so the
default already lands in the right place. Naming it anyway is the point: the location of
this service's schema history should not depend on a role attribute set once, by a script
that runs once, in a file this service does not own. Saying it here also makes a wrong
value fail loudly, because `public` is a schema agent_svc has no rights in at all. The
engine has the same line for the same reason, for EF Core's history table - although
there it was not optional, because EF hardcodes `public` rather than following a path.

**There is no target_metadata.** This service has no SQLAlchemy models and is not getting
any - it talks to Postgres through asyncpg. Migrations are therefore written by hand as
SQL, and `alembic revision --autogenerate` would compare the database against nothing and
cheerfully propose dropping every table. Passing None is what makes that command useless
instead of dangerous.
"""

import asyncio
from logging.config import fileConfig

from alembic import context
from sqlalchemy import pool
from sqlalchemy.engine import Connection
from sqlalchemy.ext.asyncio import async_engine_from_config

from app.settings import get_settings

config = context.config

if config.config_file_name is not None:
    # disable_existing_loggers defaults to True, which would switch off every logger
    # already created in the process. That is harmless for the `alembic` command and
    # wrong inside the test suite, where this runs after the application's own logging
    # has been configured and other tests still expect their loggers to work.
    fileConfig(config.config_file_name, disable_existing_loggers=False)

# Handwritten migrations only. See the module docstring.
target_metadata = None

# Alembic's own bookkeeping, in the schema this service owns.
VERSION_TABLE = "alembic_version"
VERSION_TABLE_SCHEMA = "agent"

# SQLAlchemy needs a driver in the scheme; the service's DSN is a plain libpq one because
# asyncpg reads it directly. asyncpg is the driver this project already depends on, which
# is also why the async template is the one with *fewer* dependencies here: the sync
# alternative would mean adding psycopg for migrations alone.
ASYNC_DRIVER = "postgresql+asyncpg://"


def _database_url() -> str:
    """The URL to migrate, with the driver SQLAlchemy needs.

    `-x url=...` wins, so a test can point this at a throwaway container without an
    environment. Otherwise it is the same setting the service connects with, which means a
    migration cannot be applied to a database the service would not talk to.
    """
    supplied = context.get_x_argument(as_dictionary=True).get("url")
    url = supplied if supplied else get_settings().database_url.get_secret_value()

    for prefix in ("postgresql://", "postgres://"):
        if url.startswith(prefix):
            return ASYNC_DRIVER + url[len(prefix) :]

    return url


def run_migrations_offline() -> None:
    """Emit SQL instead of running it, for `alembic upgrade head --sql`.

    The engine has the same escape hatch in `dotnet ef migrations script`, and for the same
    reason: somewhere that is not this laptop, a human reads the statements before a
    database applies them.
    """
    context.configure(
        url=_database_url(),
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        version_table=VERSION_TABLE,
        version_table_schema=VERSION_TABLE_SCHEMA,
    )

    with context.begin_transaction():
        context.run_migrations()


def _run(connection: Connection) -> None:
    context.configure(
        connection=connection,
        target_metadata=target_metadata,
        version_table=VERSION_TABLE,
        version_table_schema=VERSION_TABLE_SCHEMA,
    )

    with context.begin_transaction():
        context.run_migrations()


async def _run_async() -> None:
    connectable = async_engine_from_config(
        {"sqlalchemy.url": _database_url()},
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
    )

    async with connectable.connect() as connection:
        await connection.run_sync(_run)

    await connectable.dispose()


if context.is_offline_mode():
    run_migrations_offline()
else:
    asyncio.run(_run_async())
