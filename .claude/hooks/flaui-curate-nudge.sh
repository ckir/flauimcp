#!/usr/bin/env bash
# Stop hook: once per session, if the inbox has pending items, surface a nudge to run flaui-curate.
# Emits hookSpecificOutput.additionalContext JSON — the only Stop-hook output that reaches the model
# (plain stdout on Stop is transcript-only). Dumb: reads only session_id from stdin JSON (no date math).
# Non-hijacking (advisory additionalContext, no decision:block), never auto-runs curate.
set -euo pipefail
# Stay silent in automation. A nudge is advice for a human at a prompt; in a headless run there is nobody to
# act on it and the injected text lands in whatever is scraping the model's output — that is exactly how a
# release changelog got replaced by a reply to this hook (see scripts/release.ps1's Invoke-ChangelogLlm).
# Claude Code exposes no flag that says "this session is non-interactive", so do NOT guess one: honour an
# explicit opt-out that callers we control can set, plus the CI convention. Callers we do not control are
# covered from the other side, by running `claude -p --safe-mode` (which disables hooks outright).
case "${FLAUI_MCP_NO_NUDGE:-}" in ''|0|false|FALSE) ;; *) exit 0 ;; esac
[ -n "${CI:-}" ] && exit 0
# Read hook JSON from stdin, but NEVER block: if stdin is a terminal (no pipe attached, e.g. a manual
# run), skip the read entirely instead of hanging on cat.
if [ -t 0 ]; then input="{}"; else input="$(cat)"; fi
# Derive session_id; on empty/malformed stdin fall back to a stable literal so the sentinel is never
# ".nudged-" (which would globally throttle every session forever).
sid="$(printf '%s' "$input" | jq -r '.session_id // empty' 2>/dev/null || true)"
sid="${sid:-nosession}"
root="${CLAUDE_PROJECT_DIR:-.}"
inbox="$root/.claude/flaui-mcp/observations.md"
sentinel="$root/.claude/flaui-mcp/.nudged-$sid"
[ -f "$sentinel" ] && exit 0                        # already nudged this session
if [ -f "$inbox" ] && grep -qE '^- ' "$inbox"; then
  touch "$sentinel"
  jq -n --arg msg "flaui-autotrain: inbox has pending observations — consider running flaui-curate when convenient (not now if you're mid-task)." \
    '{hookSpecificOutput: {hookEventName: "Stop", additionalContext: $msg}}'
fi
exit 0
