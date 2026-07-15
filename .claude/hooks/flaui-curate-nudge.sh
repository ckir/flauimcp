#!/usr/bin/env bash
# Stop hook: once per session, if the inbox has pending items, suggest running flaui-curate.
# Dumb: reads only session_id from stdin JSON (no date math). Non-hijacking, never auto-runs curate.
set -euo pipefail
# Read hook JSON from stdin, but NEVER block: if stdin is a terminal (no pipe attached, e.g. a manual
# run), skip the read entirely instead of hanging on cat.
if [ -t 0 ]; then input="{}"; else input="$(cat)"; fi
# Derive session_id; on empty/malformed stdin fall back to a stable literal so the sentinel is never
# ".nudged-" (which would globally throttle every session forever).
sid="$(printf '%s' "$input" | jq -r '.session_id // empty' 2>/dev/null || true)"
sid="${sid:-nosession}"
root="${CLAUDE_PROJECT_DIR:-.}"
inbox="$root/.claude/autotrain/observations.md"
sentinel="$root/.claude/autotrain/.nudged-$sid"
[ -f "$sentinel" ] && exit 0                        # already nudged this session
if [ -f "$inbox" ] && grep -qE '^- ' "$inbox"; then
  touch "$sentinel"
  echo "flaui-autotrain: inbox has pending observations — consider running flaui-curate when convenient (not now if you're mid-task)."
fi
exit 0
