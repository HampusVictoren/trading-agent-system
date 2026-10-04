# syntax=docker/dockerfile:1

# The engine as two images from one file: the worker that trades, and the migration bundle
# that applies the `trading` schema. Two targets for the same reason the agent service has
# two - the engine already refuses to migrate itself, because applying a migration at
# startup would move the schema before anyone could decide to, and an image that carried
# the ability to do it would make that refusal a matter of discipline rather than of fact.
#
# The build context is the repository root. The project inherits Directory.Build.props and
# reaches dotnet-ef through dotnet-tools.json, and both live there - which also means the
# context contains the root .env, with three passwords in it. See .dockerignore.

# Pinned to the major, which is as far as these tags go; Dependabot updates them in place.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
# `runtime`, not `aspnet`: the engine is a Worker and never opens a socket, so an image
# carrying Kestrel would be carrying a web server nothing starts.
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0


FROM ${SDK_IMAGE} AS builder

WORKDIR /src

# The project files before the source, so editing a .cs file does not re-download the
# package graph. global.json comes along because it is what the SDK reads to decide how
# `dotnet test` runs - not used here, but leaving it out would make this build a different
# build from CI's for no reason anybody could see.
COPY Directory.Build.props global.json dotnet-tools.json ./
COPY src/engine/engine.csproj src/engine/
RUN dotnet restore src/engine/engine.csproj
RUN dotnet tool restore

COPY src/engine/ src/engine/

RUN dotnet publish src/engine/engine.csproj -c Release -o /app --no-restore

# The migration bundle: every migration compiled into one executable that takes a
# connection string. It is built here, beside the service, from the same restore - so the
# bundle and the engine can never disagree about which migrations exist, which is the
# failure the engine's startup check exists to catch and would rather not have to.
#
# No --self-contained: the runtime image below carries the runtime. The bundle is built for
# the builder's own architecture, which is the image's, because they are the same build.
RUN dotnet dotnet-ef migrations bundle --project src/engine --output /bundle/efbundle --force


# Both runtime images need one native library the base image does not carry. Npgsql
# probes for GSSAPI at startup, and without it every run begins with two unstructured lines
# on stderr - "Cannot load library libgssapi_krb5.so.2" - before anything structured is
# logged. The connection works anyway, because this system authenticates with a password,
# so it is noise rather than a fault. It is still worth two megabytes to remove: in a
# service whose logging discipline is that a line means something, the first two lines an
# operator reads should not be a library that was never needed.
FROM ${RUNTIME_IMAGE} AS runtime-base

RUN apt-get update \
 && apt-get install --no-install-recommends -y libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*


FROM runtime-base AS service

WORKDIR /app
COPY --from=builder /app .

# Production unless told otherwise. appsettings.Development.json is gitignored, so a
# container that defaulted to Development would be reading a file that does not exist and
# saying nothing about it.
ENV DOTNET_ENVIRONMENT=Production

# Deliberately NOT DOTNET_SYSTEM_GLOBALIZATION_INVARIANT. It would take about 30 MB off the
# image and change how a decimal formats and how a date parses, which is not a trade to
# make in a process that writes prices into a ledger.
#
# The three secrets - Database:ConnectionString, AgentService:ApiKey and
# AgentService:OutcomesHmacSecret - come from the environment as Database__ConnectionString
# and so on. AddUserSecrets is optional and simply finds nothing here, which is the point
# of it being optional.

# The base image's own account, uid 1654. $APP_UID is set by that image rather than by this
# file, so it stays right if Microsoft moves it.
USER $APP_UID

# There is no HEALTHCHECK, and that is a decision rather than an omission. Nothing is gated
# on this container - it is the last link in the chain, so there is nothing for compose to
# hold back. And what a useful check would ask is not "is the process alive", which Docker
# already knows from the process exiting, but "did a cycle finish in the last fifteen
# minutes" - which needs the engine to publish that somewhere. It will, in stage 7.

ENTRYPOINT ["dotnet", "engine.dll"]


FROM runtime-base AS migrate

WORKDIR /migrate
COPY --from=builder /bundle/efbundle ./efbundle

USER $APP_UID

# No CMD, because there is no sensible default. The bundle falls back to the connection
# string the design-time factory names, which is deliberately `Host=design.invalid` - so a
# run without --connection fails to resolve a hostname rather than migrating something
# nobody meant to. The operator passes it:
#
#   docker run --rm --network <net> <image> --connection "Host=db;...;Username=engine_svc;Password=..."
#
# `0` as the argument reverts every migration, and a migration name migrates to that point,
# which is how a rollback is tested rather than assumed.
ENTRYPOINT ["./efbundle"]
