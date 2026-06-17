# Batch Monitor — Batches Dashboard

> Part 2 of the design document series — June 2026

---

## 1. Batches Dashboard Tab

### Tab Identity

```
TabId = dashboard:batches:{env}
Label = "Batches"
Icon  = list/grid icon
```

### Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ [🔍 Filter bar — search RunId / RequestId / Name / Type / Status]│
├──────────────────────────────────────────────────────────────────┤
│ [●] BatchName  RunID  Type  Started  Duration  Status  │ Actions │
│ [✓] BatchName2 RunID  Type  Started  Duration  Status  │ Actions │
│ ...                                                              │
├──────────────────────────────────────────────────────────────────┤
│ Rows per page: [25 ▼]   < 1 2 3 ... >   Showing 1–25 of 120     │
└──────────────────────────────────────────────────────────────────┘
```

### Features

- **Default:** last 25 batches, ordered by start time descending
- **Pagination controls** with configurable rows per page (10 / 25 / 50 / 100)
- **Status icon per row:** Running (green pulse ●), Completed (✓), Failed (✗)
- **Per-row actions** (icon buttons):
  - 🔍 Open Batch Detail tab
  - ⏱ Open Timeline tab
  - ■ Cancel batch (enabled only when Running — confirm dialog before firing)
- **Home mini-summary** reuses the same endpoint with a small `count` parameter

### Filter Bar

Parameters:
- Free-text search across RunId, RequestId, Name
- Status multi-select: Running / Completed / Failed
- Type multi-select
- Filter state reflected in URL query params (deep-linkable)
- Filter resets pagination to page 1

Detailed filter UX (chips, clear all) deferred to a dedicated design session.

---

## 2. API — Batch Endpoints

### 2.1 Batch List

```
GET /api/{env}/batches?before={utcTimestamp}&count={n}&filter={text}&status[]={...}&type[]={...}
```

| Parameter | Type | Description |
|---|---|---|
| `env` | string | Environment identifier |
| `before` | UTC datetime | Return batches starting before this time |
| `count` | int | Results to return (default 25) |
| `filter` | string (optional) | Free-text: RunId / RequestId / Name |
| `status` | string[] (optional) | Filter by status |
| `type` | string[] (optional) | Filter by batch type |

**Response per item:**

| Field | Type |
|---|---|
| `batchName` | string |
| `type` | string |
| `runId` | string |
| `status` | string (Running / Completed / Failed) |
| `start` | UTC datetime |
| `end` | UTC datetime? |
| `duration` | timespan? |

Used by: Batches dashboard, Home mini-summary (count=5), Add Batch dialog in Timeline tab.

### 2.2 Cancel Batch

```
POST /api/{env}/batches/{runId}/cancel
```

Returns updated status. Requires confirmation dialog in the UI before firing.

---

## 3. Implementation Steps

### Step 1 — Shell, Theme, Sidebar, Tab Bar
*(See Shell design doc — must be completed first)*

### Step 2 — Batches Dashboard (read-only, no filter)

- Implement `GET /api/{env}/batches` endpoint
- Batches tab: table with status icon, name, runId, type, start, duration, status chip
- Running status has CSS pulse animation
- Pagination controls (rows-per-page selector, page navigation)
- Default: last 25 batches
- Per-row actions: Open Detail (stub), Open Timeline (stub), Cancel (POST + confirm)
- Home mini-summary using same endpoint (count=5)

**Acceptance:** List renders with real data, pagination works, cancel fires correctly.

### Step 3 — Batches Dashboard Filter

- Filter bar: free-text, status multi-select, type multi-select
- Extend endpoint with filter/status/type params
- Filter state in URL query params
- Filter resets to page 1

**Acceptance:** Filtering narrows results across all dimensions.

---

## 4. Open Questions

| Topic | Status |
|---|---|
| Filter bar detailed UX (chips, clear all, URL state) | Deferred to dedicated design session |
