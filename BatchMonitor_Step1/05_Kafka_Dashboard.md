# Batch Monitor — Kafka Dashboard

> Part 5 of the design document series — June 2026

---

## 1. Overview

The Kafka section covers four areas:

1. **Home widget** — broker health and lag summary
2. **Topics panel** — list, filter, and inspect topics
3. **Topic Inspector tab** — consume and inspect messages for a specific topic
4. **Consumer Groups panel** — list, filter, and inspect consumer group details

Connection: Confluent.Kafka .NET client via AdminClient, consumer, and producer. Scoped to topics matching a configured prefix. All topics and consumer groups outside this prefix are excluded.

---

## 2. Home Widget

```
┌─────────────────────────────────────────────────────┐
│ ⬡ Kafka                               [UAT1]        │
│ Brokers  3/3 ✓            Cluster  healthy          │
│ Topics   247  (prefix scope)                        │
│ Groups   89   (on your topics)                      │
│ Total lag  1,842    ⚠ 3 groups lagging              │
└─────────────────────────────────────────────────────┘
```

- Broker count from `AdminClient.DescribeClusterAsync()` — controller ID, cluster ID, broker list
- "Lagging" = consumer lag above a configurable threshold (default: any lag > 0)
- `⚠ 3 groups lagging` is a link opening the Consumer Groups panel pre-filtered to lagging groups
- Data refreshed on a slow poll (e.g. 30s); no SignalR needed for the home widget

---

## 3. Topics Panel

### Tab Identity

```
TabId = dashboard:kafka:{env}
Label = "Kafka"
Icon  = Kafka / message icon
```

### Layout

```
┌─────────────────────────────────────────────────────────────────┐
│ TOPICS                                                          │
│ [🔍 your-prefix·················]  [↺ Refresh]                  │
├─────────────────────────────────────────────────────────────────┤
│ Topic name              Partitions  Repl  Messages    Retention │
│ your-prefix.orders      12          3     1,842,301   7d        │
│ your-prefix.invoices    6           3     422,018     7d        │
│ ...  (MudDataGrid, Virtualize=true)                             │
└─────────────────────────────────────────────────────────────────┘
```

- Filter pre-populated with the configured topic prefix; user can refine within it but not outside it
- Filter debounced (~300ms); client-side on already-fetched list (topics list is loaded once and filtered in memory)
- `[↺ Refresh]` re-fetches topic metadata from AdminClient
- `MudDataGrid` with `Virtualize=true` — no pagination; handles thousands of topics without DOM overhead

### Columns

| Column | Detail |
|---|---|
| Topic name | Full name |
| Partitions | Partition count |
| Repl | Replication factor |
| Messages | Exact for `delete` cleanup policy (`endOffset - beginOffset` per partition, summed). Prefixed with `~` for `compact` or `compact,delete` policies |
| Retention | From topic config (retention.ms converted to human-readable) |

### Per-Row Actions

```
│ your-prefix.orders  12  3  1,842,301  7d  [ℹ]  [▶ Inspect] │
```

- `[ℹ]` opens Topic Info dialog (config + subscribed consumer groups)
- `[▶ Inspect]` opens Topic Inspector tab for this topic

---

## 4. Topic Info Dialog

Opened from `[ℹ]` on the topic list row or `[ℹ Info]` button in the Topic Inspector tab top bar.

```
┌─────────────────────────────────────────────────────────────┐
│ your-prefix.orders                                      [×] │
├────────────────────────┬────────────────────────────────────┤
│ CONFIGURATION          │ CONSUMER GROUPS                    │
│ Retention      7d      │ Group ID          State   Lag      │
│ Cleanup        delete  │ your-svc-orders   Stable  0        │
│ MaxMsgBytes    1MB     │ your-svc-retry    Stable  12 ⚠     │
│ MinInSync      2       │ your-svc-archive  Dead    —        │
│ Compression    lz4     │                                    │
│ Partitions     12      │ [↗ Open Consumer Groups]           │
│ Repl factor    3       │                                    │
└────────────────────────┴────────────────────────────────────┘
```

- Two-column layout: config overrides left (from `AdminClient.DescribeConfigsAsync`), subscribed consumer groups right
- `[↗ Open Consumer Groups]` opens Consumer Groups panel pre-filtered to this topic
- Consumer group rows clickable — opens Consumer Group side panel (behind dialog)
- `⚠` on groups with lag > threshold

---

## 5. Topic Inspector Tab

### Tab Identity

```
TabId = inspector:topic:{topicName}:{env}
Label = topic name (truncated)
Icon  = message/envelope icon
```

### Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ TOP BAR: your-prefix.orders   12 partitions  3×  [ℹ Info]       │
├──────────────────────────────────────────────────────────────────┤
│ INSPECTOR CONTROLS                                               │
│ Partition [All ▼]  From [Earliest ▼]  Limit [100 ▼]             │
│ Key filter [··········]  [▶ Fetch]  [■ Stop]                     │
│ Fetching… 1,240 messages  ████████░░░  (during fetch only)       │
├──────────────────────────────────────────────────────────────────┤
│ Partition  Offset   Key            Size      Timestamp           │
│ 0          10042    order-abc-123  1.2 KB    2026-06-15 14:22:01 │
│ 2          8831     order-def-456  980 B     2026-06-15 14:22:00 │
│ ...  (MudDataGrid, Virtualize=true, no pagination)               │
└──────────────────────────────────────────────────────────────────┘
```

### Inspector Controls

| Control | Options |
|---|---|
| Partition | All / 0 / 1 / ... / N-1 |
| From | Earliest / Latest-N / Specific offset / Timestamp |
| Limit | 50 / 100 / 500 / 1000 / All |
| Key filter | Client-side substring match on fetched keys — live, no re-fetch |
| Fetch `[▶]` | Triggers consume run; replaces previous results |
| Stop `[■]` | Visible during active fetch; cancels streaming consume via CancellationToken |

Fetch is always manual — no auto-refresh. Results replace previous fetch (not append).

### Streaming Fetch Strategy

Uses `IAsyncEnumerable<KafkaMessage>` from a controller action (chunked HTTP response, not SignalR). Rationale:

- One-shot operation, not a long-lived subscription — HTTP is the right primitive
- Cancellation via `CancellationToken` maps cleanly to the Stop button
- Server consumes Kafka partition(s) and yields messages as they are consumed
- Blazor component reads via `await foreach`, calls `StateHasChanged()` every 100 messages to update grid without flooding the render loop
- Progress label ("Fetching… N messages") updated on each batch
- "All" limit: no cap — server consumes to end offset then completes the stream

### Message Table

| Column | Detail |
|---|---|
| Partition | Integer |
| Offset | Integer |
| Key | Truncated at ~40 chars; full value in hover tooltip |
| Size | Value byte size, human-readable (B / KB / MB) |
| Timestamp | Absolute UTC. Default sort: descending. All columns sortable client-side after fetch. |
| Actions | `[{ }]` inspect / `[⬇]` download raw |

### Message Inspect Dialog

Opened on row click or `[{ }]` button. Value decode requested on demand (not pre-fetched for the table).

**When decoded successfully:**

```
┌─────────────────────────────────────────────────────────────┐
│ P:0  Offset:10042   2026-06-15 14:22:01 UTC         [×][⬇] │
│ Key: order-abc-123                                           │
├─────────────────────────────────────────────────────────────┤
│ {                                                            │
│   "orderId": "abc-123",                                      │
│   "amount": 142.50,                                          │
│   "status": "pending"                                        │
│ }                                                       [{}] │
│                                            [Copy]  [⬇ Raw]  │
└─────────────────────────────────────────────────────────────┘
```

**When decode fails (binary content):**

```
├─────────────────────────────────────────────────────────────┤
│ Binary content — value could not be decoded as JSON          │
│                                         [⬇ Download raw]    │
└─────────────────────────────────────────────────────────────┘
```

- `[{}]` toggles between pretty-printed and compact JSON
- `[Copy]` copies formatted JSON to clipboard
- `[⬇ Raw]` / `[⬇]` (header) downloads raw binary payload as `.bin` or `.json` depending on decode result
- Messages have no headers — header section omitted

---

## 6. Consumer Groups Panel

Part of the Kafka tab, accessible via sidebar Kafka entry or `[↗ Open Consumer Groups]` links.

### Layout — List + Side Panel

```
┌──────────────────────────────────────┬──────────────────────────┐
│ LIST                                 │ SIDE PANEL               │
│ [🔍 filter] [State ▼] [⚠ Lagging ▼] │ your-svc-retry      [×]  │
├──────────────────────────────────────┤ State: Stable            │
│ Group ID          State   Lag        │ Members: 2  Lag: 12      │
│ your-svc-consumer Stable  0          │ [↺ Refresh]              │
│ ▶ your-svc-retry  Stable  12 ⚠       ├──────────────────────────┤
│ your-svc-archive  Dead    —          │ MEMBERS                  │
│ ...                                  │ server01 PID 4821  (2p)  │
│ (MudDataGrid, Virtualize=true)       │ server02 PID 5103  (4p)  │
│                                      ├──────────────────────────┤
│                                      │ PARTITION ASSIGNMENTS    │
│                                      │ Topic  Part Cmtd  Lag    │
│                                      │ .ord   0    10042  0     │
│                                      │ .ord   1    8830   12 ⚠  │
│                                      │ .ord   2    9201   0     │
│                                      │ ...                      │
│                                      │ [↗ Open Topic Inspector] │
└──────────────────────────────────────┴──────────────────────────┘
```

- All groups matching the filter loaded; `Virtualize=true` handles thousands
- Selected row marked with `▶`; clicking another row updates side panel in place
- `[×]` closes side panel; list returns to full width
- Side panel fixed width ~340px

### List Controls

| Control | Options |
|---|---|
| Filter `[🔍]` | Substring match on group ID. Debounced ~300ms. |
| State `[▼]` | All / Stable / Rebalancing / Dead / Empty |
| Lagging `[▼]` | All / Lagging only (lag > threshold) |

Shows all cluster consumer groups (not filtered to your topic prefix). User applies filter to narrow.

### Side Panel

| Section | Content |
|---|---|
| Header | Group ID, State chip, Member count, Total lag |
| `[↺ Refresh]` | Re-fetches lag for this group from AdminClient |
| Members | server / PID / consumer ID / assigned partition count |
| Partition assignments | Topic name (truncated), partition, committed offset, end offset, lag. `⚠` on lag > 0. Sortable by lag descending. |
| `[↗ Open Topic Inspector]` | Opens Topic Inspector for the assigned topic. If multiple topics, shows inline picker. |

Lag data sources:
- Committed offsets: `AdminClient.ListConsumerGroupOffsetsAsync`
- End offsets: watermark queries per partition
- Lag = end offset − committed offset

---

## 7. API Endpoints

All Kafka API endpoints are server-side wrappers around Confluent.Kafka AdminClient / Consumer calls. The server handles authentication (SASL/SSL/mTLS) — the dashboard calls only your own API.

### 7.1 Cluster Info

```
GET /api/{env}/kafka/cluster
```

Returns: broker list (id, host, port), controller id, cluster id, broker online status.

### 7.2 Topics List

```
GET /api/{env}/kafka/topics
```

Returns all topics matching configured prefix. Per topic: name, partitionCount, replicationFactor, messageCount (offset delta), cleanupPolicy, retentionMs.

### 7.3 Topic Config

```
GET /api/{env}/kafka/topics/{topicName}/config
```

Returns config overrides from `DescribeConfigsAsync`: retention.ms, cleanup.policy, max.message.bytes, min.insync.replicas, compression.type, and others.

### 7.4 Topic Consumer Groups

```
GET /api/{env}/kafka/topics/{topicName}/consumergroups
```

Returns consumer groups subscribed to this topic with state and total lag.

### 7.5 Topic Messages (streaming)

```
GET /api/{env}/kafka/topics/{topicName}/messages?partition={p}&from={earliest|latest|offset|timestamp}&fromValue={v}&limit={n}
```

Returns `IAsyncEnumerable<KafkaMessage>` as chunked HTTP response. Per message: partition, offset, key, valueSize, timestamp, decoded JSON (or null + cannotDecode flag).

`limit` = 0 or omitted means no limit (consume to end offset).

CancellationToken on the server side maps to the client Stop button.

### 7.6 Message Value (on demand)

```
GET /api/{env}/kafka/topics/{topicName}/messages/{partition}/{offset}/value
```

Returns decoded JSON or `{ decoded: false }`. Called when message inspect dialog opens — not pre-fetched for the table.

### 7.7 Message Raw Download

```
GET /api/{env}/kafka/topics/{topicName}/messages/{partition}/{offset}/raw
```

Returns raw binary payload with `Content-Type: application/octet-stream`. Browser download.

### 7.8 Consumer Groups List

```
GET /api/{env}/kafka/consumergroups?filter={text}&state={...}
```

Returns all cluster consumer groups (not scoped to prefix). Per group: groupId, state, memberCount, totalLag.

### 7.9 Consumer Group Detail

```
GET /api/{env}/kafka/consumergroups/{groupId}
```

Returns: state, members (server/PID/consumerId/assignedPartitions), per-topic-partition assignments with committedOffset, endOffset, lag.

---

## 8. Implementation Steps

### Step K1 — Kafka Tab Shell + Topics List

- Implement GET `/kafka/cluster` and GET `/kafka/topics`
- Kafka tab: topics list with filter, columns, Virtualize=true
- Home widget: broker count, topic count, group count, lag summary
- `[ℹ]` stub and `[▶ Inspect]` stub per row

### Step K2 — Topic Info Dialog

- Implement GET `/kafka/topics/{topicName}/config` and `/consumergroups` (topic-scoped)
- Topic Info dialog: two-column config + consumer groups
- `[↗ Open Consumer Groups]` pre-filter link

### Step K3 — Topic Inspector Tab

- Implement GET `/kafka/topics/{topicName}/messages` (streaming IAsyncEnumerable)
- Implement GET `/kafka/topics/{topicName}/messages/{p}/{o}/value`
- Implement GET `/kafka/topics/{topicName}/messages/{p}/{o}/raw`
- Topic Inspector tab: controls, streaming fetch with progress, MudDataGrid Virtualize=true
- Message inspect dialog: JSON pretty-print / compact toggle, copy, download
- Stop button + CancellationToken

### Step K4 — Consumer Groups Panel + Side Panel

- Implement GET `/kafka/consumergroups` and `/kafka/consumergroups/{groupId}`
- Consumer Groups list with filter, state filter, lagging filter
- Side panel: members, partition assignments, lag per partition, refresh button
- `[↗ Open Topic Inspector]` from side panel

---

## 9. Open Questions

| Topic | Status |
|---|---|
| Configurable lag threshold (home widget + lagging filter) | TBD — suggest making it a Settings value |
| Topic prefix configuration (where it lives — appsettings?) | TBD |
| Message count display for compacted topics — hide or show with `~` prefix | Decided: show with `~` |
| Consumer group scope (all cluster vs prefix-scoped) | All cluster — user applies filter |
