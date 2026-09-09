# flaui-autotrain retired phrases

Distinctive fragments of GROWTH rules that `flaui-curate` **RETIRED because measurement showed them WRONG**.
`RetiredRuleAbsenceTests` asserts none of them reappears in either twin's `AUTOTRAIN:GROWTH` region.

**Why this file exists:** on 2026-09-09 a retired rule came back. A curate regeneration script skipped the
retired bullet but kept appending its CONTINUATION lines to the last *graduated* entry, smuggling the wrong text
into an unrelated bullet; that entry was exported to `graduation-candidates.md` and then reinstated wholesale
when the cap was raised. Three operations carried it and every gate stayed green, because the suite only checks
that the two twin copies MATCH — never what they SAY. Two identically-wrong copies pass.

**When curate retires a rule, append its fragment here.** Choose a fragment distinctive enough that the
REPLACEMENT rule cannot contain it (check by grepping the region after you promote the replacement). Remove a
line only if the rule is later RE-VALIDATED by measurement — and say so in the commit.

Format: `- "<fragment>" · retired <YYYY-MM-DD> · <why it was wrong>`

## Retired

- "no items in UIA" · retired 2026-09-09 · Win11 context-menu items ARE in UIA; `includeOffscreen:true` reveals them and `desktop_find` returns all 16 at defaults
- "returns off-screen rows WITHOUT scrolling" · retired 2026-09-09 · `get_grid_cell` indexes the REALIZED set, so an absolute row number silently returns the WRONG row; routed to fix-the-tool instead
- "Hydration is a **RAMP" · retired 2026-09-09 · `wake_accessibility` does not hydrate at all - the FIRST UIA walk does (two-arm cold-provider test, 39->63 identically with and without a wake)
- "500ms polling for 4s did NOT hydrate" · retired 2026-09-09 · same measurement: one walk triggers hydration, so polling demonstrably DOES hydrate an opaque tree
