# Activation via MCP server instructions — design

**Status:** spec, awaiting operator approval. No implementation plan yet.
**Origin:** operator observation, 2026-08-22 — *"installing the plugin is an approval action; it defines a
decision that the user wants the agent to have the tools and use them. What is telling the agent to
consider using the tools?"*

---

## 1. The defect

Installing this plugin is the user's **approval decision**: *I want the agent to have these desktop tools
and to use them.* The tools arrive over MCP. **The statement of when to reach for them does not.**

MEASURED:

| fact | evidence |
|---|---|
| the MCP server advertises no instructions | `src/FlaUI.Mcp.Server/Program.cs:214` — `.AddMcpServer()` with no `McpServerOptions.ServerInstructions` |
| the guidance ships only as a Claude Code hook | `plugin/hooks/hooks.json` — `SessionStart` matching `startup\|clear\|compact`, invoking the `activation-payload` verb |
| the protocol has a channel for this | `McpServerOptions.ServerInstructions` → `InitializeResult.Instructions`, present in ModelContextProtocol.Core 1.4.0 |
| other servers use it | this project's own agent sessions show an *"MCP Server Instructions"* block carrying `claude-in-chrome` guidance; **flaui-mcp is absent from it** |

**Three ways a user ends up with fifty tools and no reason to use them:**

1. **Between install and relaunch.** The installer says so itself — *"quit the client completely and
   relaunch — it registers plugin hooks only at startup"* (`CliRouter.cs:52`). MCP tools appear on an
   `/mcp` reconnect; the hook does not. In that window the agent holds the tools and has been told
   nothing, so it asks the human what is on screen — the exact behaviour the payload forbids.
2. **Any non-Claude-Code client, permanently.** `hooks.json` is a Claude Code plugin format. This project
   also installs for agy. Those clients connect to the same server, receive the same fifty tools, and
   never run the hook. For them the activation decision **never propagates at all**.
3. **Silent hook failure.** A moved exe, a broken path, a disabled hook: no guidance, no error, no
   fallback.

**This is not a documentation problem.** The user already did the thing that expresses intent — they
installed it. The defect is that the intent is encoded only in the fragile, client-specific layer.

---

## 2. What already exists, and is good

`src/FlaUI.Mcp.Server/Install/ActivationPayload.cs` is well designed and this spec keeps it:

- `ActivationPayload.Text` is a **single compiled-in constant**, not a script — the class comment
  explains why (a `bash "..."` hook command has no determinate interpreter on Windows, and an extracted
  script dies at its first `\r`).
- It is a **payload, not a signpost**: *"a skimmed reminder leaves nothing behind, whereas a skimmed
  payload still leaves an executable load line in context."*
- It already carries the right content shape: capability, an explicit prohibition (*"Never ask the user
  to look at, read, or click inside a desktop app on your behalf"*), **trigger conditions**, the load
  line, and the read-only-vs-lease distinction.
- It is **budgeted and tested** — 15 lines, 1100 chars of prose excluding the load line, asserted by
  `ActivationPayloadTests`, which also allow-lists the tool names and pins the anti-delegation sentence.

**The wiring is nearly free.** The text is already a reusable constant; nothing needs re-authoring.

---

## 3. Goal

**A new user, on a new box, on any MCP client, gets the activation decision delivered with the tools.**

Success criterion, checkable: with the SessionStart hook removed entirely, an agent connecting to the
server still receives the capability statement, the prohibition, and the trigger conditions.

---

## 4. Design decisions

### D1 — Split the text into a client-agnostic CORE and a Claude Code ADDENDUM

`ActivationPayload.Text` contains two lines that name mechanisms a generic MCP client does not have: the
`ToolSearch "select:..."` load line, and the pointer to the `driving-flaui-mcp` skill.

- **CORE** (goes to both channels): capability, the prohibition, trigger conditions, the
  read-only-vs-lease distinction, and **a client-agnostic loading line** (see D2).
- **ADDENDUM** (hook only): the exact `ToolSearch` load line and the skill pointer.

*Rejected — ship `Text` verbatim as instructions (the smallest change).* It sends Claude-Code-specific
instructions to every client. The argument for it is that a generic agent would waste one turn on a
failed `ToolSearch` and then fall back to calling the tools directly — but that is an **assumption about
recovery behaviour, not a measurement**, and the plausible alternative is that the agent concludes the
tools are unavailable and tells the user it cannot see the screen. That is precisely the failure this
work exists to remove, so the cheap option risks reproducing it.

### D2 — The CORE must carry a loading line, or the split creates a new trap

⚠ **The "addendum orphan" — raised by the AGY-FIRST consult and the sharpest point in it.** If the split
is naive and the Claude Code hook then fails to fire, the agent receives the core (*"you can operate this
desktop"*) but never the addendum (*"run ToolSearch to load the tools"*). MEASURED in this project's own
sessions: flaui-mcp tools are **deferred** in Claude Code and must be loaded via `ToolSearch` before they
can be called. So the agent would know it is allowed to act, try to call `desktop_snapshot`, fail, and
have no instruction telling it how to recover — a **worse** state than today's, where a failed hook at
least leaves it silent.

**Therefore the CORE carries a client-agnostic loading sentence.** ⚠ **The exact wording is deliberately
NOT fixed here, and that is a real gap rather than a stylistic one:** it must be written against the
observed behaviour of a client that is not Claude Code, and §8 asks whether to measure agy first. Fixing
prose now, before that measurement, would be inventing a contract for a client nobody has watched. What
IS fixed is the requirement: the core must tell an agent that these tools may need loading before they
can be called, without naming a mechanism only one client has. The exact Claude Code incantation stays in
the addendum.

### D3 — Keep the CORE in BOTH channels; do not shrink the hook to the addendum

The hook fires on `startup`, `clear` and `compact`. `InitializeResult.Instructions` is delivered once, at
connection. **A `/clear` or a compaction resets context and does not re-run initialize.** If the hook
carried only the addendum, the safety prohibition and the triggers would be lost at the first `/clear`.

The cost is that Claude Code users receive overlapping guidance twice per session. That is accepted:
redundancy in a safety rule is cheaper than losing it on a context reset.

### D3b — Instructions are delivered at INITIALIZE, and that has a staleness of its own

⚠ **Named because this spec's whole complaint is about staleness, and the remedy is not immune to it.**
`InitializeResult.Instructions` is sent once, when the client connects. So after UPGRADING the server,
an already-connected client keeps the old instructions until an `/mcp` reconnect.

This is strictly milder than the defect being fixed — a reconnect, not a full client restart, and MCP
clients reconnect on server upgrade anyway — and it does not affect the case that motivates the work (a
NEW user, whose first connection carries the current text). But the honest framing is *"a much shorter
staleness window"*, not *"no restart required"*, and the spec should not oversell it.

### D4 — Budget the instructions, and assert it

`ServerInstructions` is delivered to every connecting client, every session. It gets the same discipline
as the hook payload and the tool descriptions: **an asserted character budget with the rationale written
next to the number**, so it cannot silently metastasise. The existing 15-line / 1100-char budget governs
the hook payload (core + addendum); the core alone will be smaller and should have its own stated
ceiling rather than inheriting one by accident.

---

### D5 — Accept that a client ignoring the instructions is UNDETECTABLE from the server

If a client truncates, drops, or simply does not surface `InitializeResult.Instructions`, the server
cannot tell: `initialize` carries no acknowledgement of what the client did with the field. The failure
is silent and looks identical to success.

Accepted rather than mitigated — there is no protocol affordance to detect it — but it bounds the claim
this work may make. **It buys "the guidance is offered over the connection", not "the agent received
it".** The only positive evidence available is per-client and manual: connect that client and look. That
is why §8 asks whether to measure agy BEFORE building.

## 5. Contracts

The CORE must contain, and tests must pin:

1. **The capability**, stated as fact, not permission — the agent *can* see and operate this desktop.
2. **The prohibition** — never delegate observation to the human, never infer UI state from process
   lists. This is the sentence that changes behaviour; `ActivationPayloadTests` already pins its presence
   in the hook payload and must pin it in the instructions too.
3. **Trigger conditions** — the decision points, phrased as situations rather than tool names.
4. **The lease boundary** — read-only perception is free; input needs a lease.
5. **A client-agnostic loading sentence** (D2).

The ADDENDUM contains the `ToolSearch` load line (both tool-name prefixes, as today) and the
`driving-flaui-mcp` skill pointer.

**Single source of truth:** both channels derive from one constant. Two copies would drift, and this
project has already paid for that shape — item 8 shipped a `ScreenshotToolsFactory` for exactly this
reason, after five call sites each rewired the same dependencies by hand.

---

## 6. Testing

Extend `ActivationPayloadTests` rather than starting a new file:

- **The core is non-empty and within its budget** — with the number and its rationale in the message.
- **The core contains the prohibition** — the existing `Never ask the user` assertion, applied to the
  core.
- **The core names no client-specific mechanism** — assert it does NOT contain `ToolSearch` or
  `driving-flaui-mcp`. This is the test that would have caught D1 being skipped.
- **The hook payload still contains both** core and addendum, so `/clear` is not degraded (D3).
- **The server advertises the core** — ⚠ **and this assertion is gameable in a way that matters.**
  Asserting that `ActivationPayload.Core` is non-empty tests a constant and proves nothing about the
  server; asserting on a locally-constructed `McpServerOptions` tests the test's own setup. **It must
  read the value the SERVER is actually configured with**, so that deleting the wiring line in
  `Program.cs` turns it red. If that cannot be reached from a headless test, say so plainly in the test
  and pin the wiring line with a comment-stripped source sweep instead — labelled structural, as
  `DenylistShutterGuardTests` is. **This is the assertion that makes the feature real**; everything else
  tests strings.

**Non-vacuity:** each new assertion needs a LOGIC mutant, and — per this project's most recent lesson —
**a structural guard must be mutated by SHADOWING, not only by deletion**. Emptying `ServerInstructions`
must turn the wiring test red; so must assigning it something other than the core.

---

## 6b. The assumption this rests on

**Everything here assumes an agent given `InitializeResult.Instructions` actually CHANGES BEHAVIOUR
because of them.** That is unproven. What is measured is only that Claude Code SURFACES them (this
project's own sessions show a *"MCP Server Instructions"* block for `claude-in-chrome`). Surfacing is not
obeying.

The assumption is reasonable — it is the same mechanism the existing SessionStart payload relies on, and
that payload demonstrably works — but it is the load-bearing premise of the whole change and it is
recorded here rather than left implicit. The cheapest way to raise confidence before building is §8's
question 2: connect a non-Claude client and look.

## 7. Non-goals and known limits

- ⛔⛔ **SUBAGENTS HAVE THE TOOLS AND GET NEITHER CHANNEL — the worst pairing, and this work does NOT fix
  it.** BOTH halves MEASURED 2026-08-22 by dispatching subagents:
  - **Guidance: absent.** A subagent reports no *"MCP Server Instructions"* section and no
    `flaui-mcp is installed:` line in its context.
  - **Tools: present and callable.** Another subagent ran
    `ToolSearch "select:mcp__plugin_flaui-mcp_flaui-mcp__desktop_input_status,..."`, got usable schemas,
    **invoked the tool**, and received `{"leaseStatus":"locked","secondsRemaining":0,"shells":false}`.
    A keyword search returned five more flaui-mcp tools.

  So every subagent dispatched for desktop work holds fifty tools with **zero** framing: no capability
  statement, no trigger conditions, and — the one that actually costs — **no prohibition on asking the
  human to look at the screen**. It will do exactly what the payload forbids, and the orchestrator is the
  only thing that can prevent it, by carrying the guidance into the dispatch by hand.

  ⚠ **This is the same defect class as the one this spec fixes, one level down, and setting
  `ServerInstructions` does NOT reach it** — measured, subagents do not receive server instructions
  either. **It should be filed as its own ROADMAP item rather than quietly folded in here**, because the
  remedy is different in kind: it belongs in whatever composes a subagent's prompt, not in the MCP
  handshake.

  *(This measurement also refutes the AGY-FIRST consult's objection that instructions would tax every
  subagent's context — they never reach a subagent, so that cost does not exist. And it corrects an
  operator hypothesis that subagents have no MCP access at all: they do, demonstrably.)*
- **Whether agy surfaces `InitializeResult.Instructions` is UNVERIFIED.** It is protocol-standard and
  Claude Code plainly honours it, but this spec should not claim agy behaviour nobody has measured.
  **Worth measuring before implementation**, because agy is the concrete non-Claude client this plugin
  installs for, and it is the case that motivates the whole change.
- Not changing the hook's trigger matcher, the installer's restart advice, or the tool descriptions.
- Not attempting to make hooks register without a client restart; that is the client's behaviour.

---

## 8. Open for the operator

1. **Approve D1** (split) over the smaller verbatim option, accepting a little structure for
   client-correctness?
2. **Measure agy first?** Confirming agy actually surfaces server instructions would validate the
   premise before any code is written. Cheap, and it is the motivating client.
3. **Scope:** ship as its own small branch, or fold into the next release batch alongside item 4?
