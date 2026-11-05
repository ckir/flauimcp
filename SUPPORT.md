# Support Guide

Thanks for using **FlaUI.Mcp**! Here is how to find help, report issues, and connect with the maintainer.

---

## 📖 1. Documentation First

Before filing an issue or asking for support, check the official documentation:

- **[Operator Manual](docs/operator-manual.md)**: Setup, CLI flags, configuration, transport options (stdio vs HTTP), and sensitive field redaction rules.
- **[Agent Contract & Tool Catalog](docs/agent-contract.md)**: Full list of exposed MCP tools, parameter schemas, and error envelopes.
- **[Architecture & Safety](docs/architecture-and-safety.md)**: Threat model, synthetic input lease mechanism, and boundary protections.
- **[Building & Testing Guide](docs/building.md)**: Prerequisites (.NET 10 SDK, Windows 10/11), test suites, and RDP vs physical console requirements.
- **[Contributing Guide](CONTRIBUTING.md)**: Adding tools, code conventions, CLA requirements, and PR workflows.

---

## 💬 2. Asking Questions & Getting Help

If you have questions about how to drive a specific application, prompt engineering for desktop agents, or configuring client MCP integrations:

- Open a **[GitHub Discussion](https://github.com/ckir/flauimcp/discussions)** or issue labeled `question`.
- Include the name and UI framework of the target application (e.g., WPF, WinForms, Qt, Electron, WinUI 3) and what tool calls you've attempted.

---

## 🐛 3. Reporting Bugs & Deficiencies

If you encounter unexpected behavior, crashes, or unhandled exceptions:

- Check existing [GitHub Issues](https://github.com/ckir/flauimcp/issues) to see if the issue is already reported.
- Verify whether the defect is already tracked in [docs/fix-the-tool-backlog/](docs/fix-the-tool-backlog/).
- Open a new issue using the **Bug Report** template, providing:
  - Windows OS build.
  - Session type (physical console vs RDP).
  - Target application details.
  - MCP tool call payload and the error envelope returned.

---

## 🔒 4. Security & Safety Vulnerabilities

If you believe you have discovered a vulnerability (such as a redaction bypass or input lease defect), please do **NOT** post publicly. Follow the process in [SECURITY.md](SECURITY.md) to disclose privately.

---

## 💼 5. Commercial Licensing

FlaUI.Mcp is distributed under the PolyForm Noncommercial License 1.0.0. For commercial licensing, enterprise deployment inquiries, or dual-license arrangements, contact the maintainer directly at **ckir1508@hotmail.com**.
