# Accepted boundaries (AGY-TEST-AUDIT do-not-re-raise ledger)

Behaviours deliberately not pinned by the test suites, each with its compensation and a code anchor. A future
audit re-validates the compensation before honouring an entry. Deferred gaps do NOT live here.

## B2 system tray (2026-11-06)

1. **Call of `RefuseIfDenied` in `RightClickAsync` is not pinned headless.** Anchor `src/FlaUI.Mcp.Core/Tray/TrayManager.cs:299`.
   Compensation: the helper is pinned (`TrayManagerGuardTests`), the call is one line reviewed in capstone rounds 3-4, and the
   poll-time `denied ? 0 : ...Read` guard (`ClassifyNewWindowsAsync`) is a second layer. No test can run a credential-store process.
2. **Classic Win32 menu text bypasses redaction.** Anchor `src/FlaUI.Mcp.Core/Tray/ClassicMenuReader.cs`. Compensation: documented in `docs/agent-contract.md`; the deny-list still applies.
3. **UIA names with a literal `&` lose it.** Anchor `MenuLabel.Clean`. Compensation: documented in `docs/agent-contract.md`.
4. **K3 Win32 path not uniquely pinned** (`hasHmenu=false` survives). Anchor `TrayManager.ClassifyNewWindowsAsync`. Compensation: UIA reads the same classic menu, output identical.
5. **Visible-icon enumeration pinned only by a lease-gated test.** Anchor `TrayIconReader.ReadTaskbar`. Compensation: `Right_click_on_a_VISIBLE_icon_lists_the_menu` measured red with a lease.
6. **Mixed-DPI multi-monitor and non-bottom taskbars unmeasured.** Compensation: spec section 5 out of scope.
7. **Desktop tests skip without an interactive desktop and input lease.** Compensation: headless suite covers the pure units, contract, guards and liveness; Desktop suite is run by the operator before release.
