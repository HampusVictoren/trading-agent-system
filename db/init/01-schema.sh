#!/usr/bin/env bash
# Runs once, when the container starts on an empty data volume
# (docker-entrypoint-initdb.d). It never runs against an existing database, so
# changing it has no effect until the volume is recreated.
set -euo pipefail

: "${ENGINE_DB_PASSWORD:?ENGINE_DB_PASSWORD must be set}"
: "${AGENT_DB_PASSWORD:?AGENT_DB_PASSWORD must be set}"

# Values reach SQL as psql variables, quoted by psql itself (:'name' for a
# literal, :"name" for an identifier) - never spliced into the SQL by the shell.
psql -v ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER" \
    --dbname "$POSTGRES_DB" \
    -v db="$POSTGRES_DB" \
    -v engine_pw="$ENGINE_DB_PASSWORD" \
    -v agent_pw="$AGENT_DB_PASSWORD" <<'SQL'

-- pgvector stays in public: pgvector.asyncpg.register_vector looks it up there.
CREATE EXTENSION IF NOT EXISTS vector;

-- One login role per service.
CREATE ROLE engine_svc LOGIN PASSWORD :'engine_pw';
CREATE ROLE agent_svc  LOGIN PASSWORD :'agent_pw';

-- Only the two services (and the superuser) may connect at all.
REVOKE CONNECT ON DATABASE :"db" FROM PUBLIC;
GRANT  CONNECT ON DATABASE :"db" TO engine_svc, agent_svc;

-- Each service owns exactly one schema and has no access to the other's.
-- Owning it is what lets the service's migration tool create tables there:
-- EF Core for trading (stage 4), Alembic for agent.
CREATE SCHEMA trading AUTHORIZATION engine_svc;
CREATE SCHEMA agent   AUTHORIZATION agent_svc;

-- Unqualified names resolve to the service's own schema. The agent keeps
-- public on its path only because the vector type lives there.
ALTER ROLE engine_svc SET search_path = trading;
ALTER ROLE agent_svc  SET search_path = agent, public;

-- No tables here. This script builds what a migration tool cannot build for
-- itself - the extension, the roles, the two schemas and who owns them - and
-- then stops. Each service's own tool takes it from there: EF Core for
-- trading, Alembic for agent. That is why this file may run exactly once
-- without that being a problem: nothing in it changes as the schemas grow.
SQL
