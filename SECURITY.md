# Security Policy

## Supported versions

This repository is under active development. Security fixes are applied to the
default branch (`master`) only.

## Reporting a vulnerability

Please **do not** open a public issue for security vulnerabilities.

Report them privately via GitHub Security Advisories:

https://github.com/HampusVictoren/trading-agent-system/security/advisories/new

Include enough detail to reproduce the issue (affected component, version or
commit, steps, and impact). You should receive an acknowledgement when the
report is reviewed.

## Scope

In scope: the agent HTTP API (`src/agents`), the .NET engine (`src/engine`),
database init scripts, and CI/configuration that affects how secrets are handled.

Out of scope for this process: third-party services (LLM providers, Yahoo
Finance / yfinance), and social-engineering against personal accounts.

## Safe harbour

Security research against a private local deployment you control is welcome.
Do not attempt to access systems you do not own or have explicit permission to
test.
