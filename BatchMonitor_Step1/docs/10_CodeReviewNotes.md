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

### Repeated "refresh button + last-updated timestamp" markup

The same toolbar fragment — an icon button bound to a `RefreshAsync`
method, a `bm-infra-lastupdate`-styled timestamp, disabled state tied to a
loading flag — is hand-copied across ServicesPage, LogBrowser, Runs,
KafkaDashboard, KafkaGroupsDashboard, MongoDashboard, and
MongoCollectionInspector. A small shared component (`<ToolbarRefresh
Loading="@_loading" LastUpdated="@_lastUpdated" Label="Updated"
OnRefresh="RefreshAsync" />`) would remove ~7 copies of the same markup and
make future styling changes a one-file edit instead of a seven-file grep.

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

### `LogBrowserFileTree.razor` is a hand-unrolled 4-level tree

Root → child → grandchild → great-grandchild is written out as four nested
copies of essentially the same `<MudTreeViewItem>` block, which both caps
the tree at a hardcoded depth and quadruples the markup that has to change
if the row template ever needs a tweak. A genuinely recursive component
(rendering itself for each level of children) would remove both problems.
Not fixed this session since it's a structural change to a component that
already just got new props (`RootNodes`/`ChildCache` filtering) — better
done as its own isolated change with its own before/after screenshots.

### Server-side log line parsing duplicates the JS parser by hand

`LogBrowserService.CompileFormat` (added this session) is a line-by-line
port of `log-viewer-parser.js`'s `compileFormat()` token grammar, kept in
sync by hand. Any future change to the placeholder grammar (`{timestamp}`,
`{*}`, `{$}`, etc.) has to be made in both places or client/server parsing
will silently drift apart again — which is exactly the bug this session
just fixed. There's no automated test that would catch the two
implementations diverging on a new format string beyond the specific cases
already covered in `LogBrowserServiceFormatTests`. Worth considering
whether the format-string grammar could live in one place (e.g. a small
shared JSON schema both sides compile independently against, with a
contract test that runs the same sample format strings through both and
diffs the extracted groups) rather than two hand-maintained regex
compilers.

### `RunSummary` vs `RunDetails` — two overlapping "run" models

`RunSummary` (list/table row) had its `Name` property removed earlier this
session in favor of `Description`, but `RunDetails` (the single-run detail
view) still has its own independent `Name` property, populated separately
by each `IRunService` implementation. The two models aren't related by
inheritance or composition, so it's easy to update one and forget the
other exists — which is close to what almost happened here. Consider
whether `RunDetails` should carry a `Description` too for consistency, or
whether the two models should share a common base for the fields they do
overlap on (`RunId`, `Type`, `Status`, `Start`, `End`).

### Focus-gated polling is reimplemented per page

Home.razor and LogBrowser.razor (and to an extent KafkaTopicInspector's
stream lifecycle) each hand-roll the same shape: a nullable
`CancellationTokenSource? _pollCts`, a `StartPolling()`/`StopPolling()`
pair, and a `PeriodicTimer` loop that checks focus/visibility before doing
work. It's a small amount of code each time, but it's now been written at
least three times with slightly different edge-case handling (e.g.
LogBrowser's new `RestartPollingIfBrowsing()` helper doesn't have an
equivalent in Home.razor, which instead inlines the same decision at each
call site). A small reusable `FocusGatedPoller` helper class (start/stop
tied to an `IsVisible` predicate, wrapping the timer + cancellation
boilerplate) would let each page just supply "what to do on tick" and "am I
currently visible."

### Hand-rolled CSS spinners vs. MudBlazor's built-ins

`.bm-treemap-spinner`, `.bm-home-loading-spinner`, and `.bm-lv-loading-spinner`
are three near-identical CSS-only spinning rings (border + `@keyframes
bm-spin`), each defined separately for its own container. `MudProgressCircular`
already provides this out of the box with size/color props. Worth checking
whether the custom CSS versions exist because `MudProgressCircular` didn't
fit visually in those specific spots, or just because it was easier to
copy an existing CSS block than look up the MudBlazor component — if the
latter, consolidating would cut ~30 lines of CSS and match the rest of the
app's use of MudBlazor progress indicators (`MudProgressLinear` is already
used consistently for the linear case).

### Per-page boilerplate: `Env`/`Tab`/`TabService` wiring

Nearly every dashboard page (`Runs`, `ServicesPage`, `KafkaDashboard`,
`KafkaGroupsDashboard`, `KafkaTopicInspector`, `MongoDashboard`,
`MongoCollectionInspector`, `LogBrowser`) independently declares
`[Parameter] public string Environment/Env { get; set; }`,
`[Parameter] public TabModel? Tab { get; set; }`, injects `TabService`, and
repeats the same `Tab?.BeginLoading()/EndLoading() +
TabService.NotifyChanged()` pattern around each load method. A shared base
component (or a small injectable helper that wraps "run this async
operation with tab-loading feedback") would reduce the amount of
boilerplate that has to be kept consistent by hand across eight-plus files
— exactly the kind of drift this session's Step 1 had to go back and fix.
