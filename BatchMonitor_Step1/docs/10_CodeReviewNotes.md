# 10 — Code Review Notes

A running list of smells, inconsistencies, and improvement opportunities
spotted while working across the codebase. These are *notes*, not a
redesign plan — nothing here has been acted on unless explicitly marked
"fixed".

---

## Fixed this session

- **Inconsistent loading feedback** — before this session, "does clicking
  Refresh show the tab-bar spinner / disable the button?" had three
  different answers depending on the page: some wired `Tab.BeginLoading()`
  correctly (MongoDashboard, MongoCollectionInspector), some wired the tab
  spinner but forgot to disable the button (Runs, Services), some had a
  local `_loading` bool disabling the button but never touched `Tab` at all
  (KafkaDashboard's filter/detail loads), and `KafkaTopicInspector` didn't
  even have a `Tab` parameter. Unified across all of them — see git history
  "Wire consistent tab-loading spinner/progress/disabled-refresh across pages".
- **Duplicate `@keyframes bm-spin`** in `app.css` (defined twice, ~70 lines
  apart) — removed the second definition.
- **`FilterEvaluator.EvalField`'s `(MatchType.Exact, NumberValue)` case was
  missing** — a bare-number filter term like `thread:5` silently evaluated
  to `false` everywhere in the app (Runs, Services, LogBrowser search, any
  future filter box), not just the case that surfaced it. Fixed; worth
  double-checking the rest of that switch for other silently-unhandled
  `(MatchType, FilterValue)` combinations that fall through to the `_ =>
  false` default — I only audited the numeric-exact case, not the whole
  matrix.

## Open observations


### Two different table-sort paradigms coexist

`Runs.razor` now does fully server-side sort (click a header → re-query),
while `KafkaDashboard`, `KafkaGroupsDashboard`, and `MongoDashboard` still
use MudBlazor's built-in `<MudTableSortLabel>`, which sorts whatever's
already been fetched, client-side. Both are reasonable choices depending on
whether the full dataset is already in memory, but having both patterns
active in visually-similar tables in the same app is inconsistent. Worth a
deliberate per-page decision (documented, not just incidental) rather than
each page picking whichever pattern was closest to hand when it was
written.


