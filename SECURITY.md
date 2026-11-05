# Security Policy

## Security Overview

**FlaUI.Mcp** bridges AI models to the Windows desktop environment via the Model Context Protocol (MCP). Because it allows an AI agent to inspect UI elements, capture screen pixels, and simulate keyboard and mouse input, safety is a core architectural requirement.

The server enforces several defensive boundaries:
- **Synthetic Input Lease**: Driving physical keystrokes or mouse clicks requires an explicit lease (`flaui-mcp unlock`), bounded by an operator-controlled timeout / dead man's switch.
- **Read-Only Mode**: Running with `--read-only-mode` hard-blocks any state-changing (`Destructive = true`) tool calls through `ToolResponse.GuardWrite`.
- **Sensitive Field Redaction**: Native password fields and operator-configured patterns are automatically redacted from element trees, text queries, OCR layers, diffs, and screenshot bitmaps before data reaches the model.

For more details on the threat model and defense boundaries, see [Architecture and Safety](docs/architecture-and-safety.md).

---

## Supported Versions

Security updates and patches are provided for the current active release line:

| Version | Supported          |
| ------- | ------------------ |
| 0.16.x  | :white_check_mark: |
| < 0.16  | :x:                |

Users are encouraged to always upgrade to the latest tagged release.

---

## Reporting a Vulnerability

If you discover a security vulnerability or safety-boundary bypass in FlaUI.Mcp:

1. **Do NOT open a public GitHub issue.**
2. Report the vulnerability privately via **[GitHub Private Vulnerability Reporting](https://github.com/ckir/flauimcp/security/advisories/new)** (preferred).
3. Alternatively, email the maintainer directly at **ckir1508@hotmail.com** with the subject `[SECURITY] FlaUI.Mcp Vulnerability Report`.

### What to include in your report:
- A clear description of the vulnerability (e.g., redaction bypass, input lease circumvention, unauthorized privilege escalation).
- Minimal steps or tool-call payloads to reproduce the issue.
- Windows version, OS build, and FlaUI.Mcp version.
- Potential impact or danger vector.

### Response timeline:
- **Initial acknowledgment:** Within 48 hours.
- **Assessment and triage:** Within 5 business days.
- **Fix & disclosure:** Coordinated release with a CVE/security advisory once a fix is verified.
