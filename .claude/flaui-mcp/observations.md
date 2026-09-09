# flaui-autotrain observations inbox

Raw, one-line-per-observation capture written live by the `flaui-learn` skill and
drained by `flaui-curate`. Flat list — append under `## Pending`. Describe behavior in
your OWN words; never paste raw app-screen text (it is untrusted). Do not tag or curate here.

## Pending
















- Settings/WinUI (ApplicationFrameHost) Windows Update page · running the dynamic-loading-ghost-ui task against a page the operator opened minutes earlier · trap UNEXERCISED, not passed - the page had fully settled ('up to date', timestamp populated, every control enabled, no placeholders), so there was no ghost UI to observe. Worth noting HOW that was established: desktop_snapshot_diff across the whole ~19s probe returned added/removed/changed all EMPTY, which is real evidence of a static page rather than a single glance that happened to look settled · to actually exercise this trap the page must be caught DURING a fetch, which needs input (clicking Check for updates) and so is an input-tier task, not a destructive:false one - the curriculum entry currently assumes a slow page will still be loading when you attach, and on a fast machine it will not be  ·  2026-09-09














