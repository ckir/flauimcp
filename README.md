# FlaUI.Mcp

[![CI](https://github.com/ckir/flauimcp/actions/workflows/ci.yml/badge.svg)](https://github.com/ckir/flauimcp/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/ckir/flauimcp?sort=semver)](https://github.com/ckir/flauimcp/releases/latest)
[![License: PolyForm NC 1.0.0](https://img.shields.io/badge/license-PolyForm%20NC%201.0.0-blue)](LICENSE)
![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

A Model Context Protocol (MCP) server that lets an AI agent control the Windows desktop.

> **⚠️ WARNING:** This software allows an AI agent to drive real Windows input. It can cause data loss, launch arbitrary programs, or damage your machine. Synthetic input is locked by default. There is no sandbox. Read [Architecture and Safety](docs/architecture-and-safety.md) for the full danger model before installing.

FlaUI.Mcp translates MCP commands into native UI Automation reads, pattern-based interactions, and synthetic OS-level input. It gives non-deterministic agents supervised access to Windows applications behind strict safety guardrails.

## Quickstart

1. Download `flaui-mcp-setup.exe` from the [latest release](https://github.com/ckir/flauimcp/releases/latest).
2. Run it. SmartScreen will warn that it is unsigned; choose **More info → Run anyway**. It registers with Claude Code and Antigravity automatically via their own CLIs — no manual config editing.
3. **Restart your agent** to reload its tool registry.
4. Ask the agent to do desktop work (e.g., "List my open windows").

## Documentation

- **[Operator Manual](docs/operator-manual.md)** — read this to install via silent one-liner, view CLI commands, manage leases, run read-only mode, audit agents, or uninstall.
- **[Agent Contract](docs/agent-contract.md)** — read this to look up the `desktop_*` RPC tool catalog, element targeting schemas, and known limitations.
- **[Architecture and Safety](docs/architecture-and-safety.md)** — read this to understand the safety model, perception safeguards, and the full danger rationale.
- **[Building from Source](docs/building.md)** — read this to compile the SDK, run the local test loop, and package the executable.

## Requirements

- **Windows 10/11, x64.** (Build 19041 / version 2004 or later).
- **Interactive desktop session.** The agent drives real windows; it cannot run headless.
- **No .NET runtime required.**

For full requirement details, see the [Operator Manual](docs/operator-manual.md).

## Maintainers

When developing inside this repository, disable the globally installed skill for this repo so the local `driving-flaui-mcp` skill is the single authority. Add `{ "enabledPlugins": { "flaui-mcp@skills-dir": false } }` to `.claude/settings.local.json`, or run `claude plugin disable flaui-mcp@skills-dir --scope local`.

## Contributing

Contributions are welcome. See **[CONTRIBUTING.md](CONTRIBUTING.md)** for setup, testing, and adding MCP tools. Contributions require signing the **[CLA](CLA.md)**. Automated CLA enforcement isn't live yet; the maintainer verifies sign-offs manually on each PR.

## License

[PolyForm Noncommercial License 1.0.0](LICENSE) — © 2026 Costas Kirgoussios. Free for noncommercial use (personal, research, education, hobby, nonprofit, government). Commercial use requires a separate license.
