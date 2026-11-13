# SolarApp Telemetry Platform — Design

Status: v1 (implemented in phases, see §11). Audience: developers and the
operator team. This document is also the contract between the parts of the
system built in parallel; names and signatures here are binding.

---

## 1. Goals

1. Accept data pushed from customer sites — **Home Assistant reading the
   inverter/battery/meter over Modbus TCP** — alongside the existing cloud
   (vendor API) polling. Per site the operator chooses: Cloud, Local
   Gateway, or Hybrid.
2. Store it so that **hundreds of sites × hundreds of channels × 1-minute
   samples** stays fast and affordable, with clear limits and an upgrade path.
3. Turn it into analytics that **match what each site actually has** — a site
   with no battery gets no battery report; a site with 6 strings gets a
   string comparison; a site without a grid meter gets no TOU analysis.
4. Let each customer choose **what** to be told about and **how** (LINE,
   e-mail), with LINE binding done by the customer tapping a link.

Non-goals (v1): replacing Grafana for in-house dashboards; controlling
inverters (write-back to Modbus); multi-tenant isolation beyond the existing
Admin/Partner/Customer roles.

---

## 2. Data flow

```
 Site (LAN)                                   SolarApp (IIS + SQL Server)
 ┌──────────────────────────┐   HTTPS POST    ┌────────────────────────────────────┐
 │ Inverter / BMS / Meter    │                │ /api/ingest/v1/telemetry           │
 │   │ Modbus TCP            │  Bearer token  │   validate → map → dedupe          │
 │ Home Assistant            ├───────────────►│   → ITelemetryStore.AppendRaw      │
 │   modbus: sensors         │  batch ≤5000   │   → channel LastValue / LastTs     │
 │   rest_command + automation│  samples      │   → dirty-range marks              │
 │   (every 60 s, buffered)  │                │   → live snapshot → EnergyData     │
 └──────────────────────────┘                 │                                    │
                                              │ Jobs:                              │
 Vendor clouds  ◄──── existing adapters ──────┤  TelemetryRollupJob   (5 min)      │
                                              │  TelemetryRetentionJob (daily)     │
                                              │  SiteAnalyticsJob      (hourly/day) │
                                              │  ExpectedPvModelJob    (weekly)     │
                                              │                                    │
                                              │ Pages: Analytics (per capability), │
                                              │        Notification Center         │
                                              └────────────────────────────────────┘
```

---

## 3. Channel key convention (canonical keys)

Every value is a **channel** identified by a lowercase dotted key. Keys are
what analytics read, so the convention is fixed; HA entity ids are mapped to
keys (§4.3).

| Key pattern | Unit | Notes |
|---|---|---|
| `pv.power_w` | W | total PV DC/AC production |
| `pv.energy_today_kwh` / `pv.energy_total_kwh` | kWh | counters (total is cumulative) |
| `pv.string{n}.voltage_v` / `.current_a` / `.power_w` | V / A / W | n = 1..32 |
| `inverter{n}.power_w` / `.temp_c` / `.status_code` / `.alarm_code` / `.energy_total_kwh` | | n = 1..16; status/alarm are integer codes |
| `grid.power_w` | W | **+ import / − export** |
| `grid.import_total_kwh` / `grid.export_total_kwh` | kWh | cumulative counters |
| `grid.voltage_v` (or `grid.l1.voltage_v`, `.l2.`, `.l3.`) | V | |
| `grid.frequency_hz` | Hz | |
| `grid.status` | 0/1 | 1 = grid present (used for outage detection) |
| `load.power_w` / `load.energy_total_kwh` | W / kWh | house consumption |
| `battery.soc_pct` / `battery.soh_pct` | % | system level |
| `battery.power_w` | W | **+ charge / − discharge** |
| `battery.charge_total_kwh` / `battery.discharge_total_kwh` | kWh | cumulative |
| `battery.voltage_v` / `battery.current_a` / `battery.temp_c` | | |
| `battery.pack{n}.soc_pct` / `.soh_pct` / `.power_w` / `.voltage_v` / `.current_a` | | n = 1..16 |
| `battery.pack{n}.cell_v_max` / `.cell_v_min` | V | per pack extremes |
| `battery.pack{n}.cell_temp_max_c` / `.cell_temp_min_c` | °C | |
| `battery.pack{n}.cell{m}.voltage_v` | V | optional, per cell |
| `env.lux` / `env.irradiance_wm2` / `env.temp_c` | lx / W·m⁻² / °C | e.g. Tuya lux sensor |

Sign conventions are part of the key's meaning. A sensor that reports the
opposite sign is fixed by the mapping (`Invert`), never by analytics.

Keys not matching the table are accepted (stored as `Kind = Other`) so no data
is lost; they are simply not used by analytics until mapped.

`TelemetryKey.Parse(string key)` returns `(DeviceKind, DeviceIndex, SubIndex,
Quantity, Unit, IsCumulative)` from the key; it is the single parser.

---

## 4. Ingestion API

### 4.1 Authentication

Per site one or more **gateways**. Each gateway has a token
`sag_<43 chars base64url>` shown **once** at creation; only its SHA-256 is
stored (`TelemetryGateways.TokenHash`) with a display prefix. Requests send
`Authorization: Bearer sag_…`. Revoking sets `RevokedAt`; the token stops at
once (no cache longer than 60 s).

### 4.2 Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET  | `/api/ingest/v1/ping` | token check; returns site name, server time (UTC), recommended interval, known keys |
| POST | `/api/ingest/v1/telemetry` | samples (two body shapes, below) |
| POST | `/api/ingest/v1/events` | status/alarm events |

**Telemetry body — list form** (any mix of channels and times; best for
buffered backfill):

```json
{ "samples": [
    { "key": "pv.power_w", "ts": "2026-11-13T05:10:00Z", "value": 8421.5 },
    { "entity_id": "sensor.battery_soc", "ts": 1763010600, "value": 87 }
] }
```

**Telemetry body — snapshot form** (one timestamp, many values; what an HA
automation naturally produces):

```json
{ "ts": "2026-11-13T05:10:00Z",
  "values": { "pv.power_w": 8421.5, "battery.soc_pct": 87, "sensor.grid_power": -1200 } }
```

A `values` name containing a `.` that is not a canonical key is treated as an
HA entity id. `ts` accepts ISO-8601 (offset required; `Z` for UTC) or Unix
seconds/milliseconds.

**Limits and validation** (each rejected sample is counted with a reason;
the request still succeeds for the rest):

| Rule | Value |
|---|---|
| Body size | ≤ 1 MB |
| Samples per request | ≤ 5000 |
| Rate | ≤ 120 requests / minute / gateway (HTTP 429 beyond) |
| Timestamp window | now − 35 days … now + 5 min |
| Value | finite double; NaN/∞ rejected |
| Key | `^[a-z0-9_]+(\.[a-z0-9_]+){0,5}$`, ≤ 100 chars |
| Channels per site | ≤ 2000 (new keys beyond are rejected) |

Response `202`:

```json
{ "accepted": 4980, "duplicates": 12, "rejected": 8,
  "rejectReasons": { "ts_out_of_window": 5, "bad_value": 3 },
  "unmappedEntities": ["sensor.foo"], "serverTimeUtc": "…" }
```

**Idempotency.** HA retries and buffered replays resend samples. Within a
request, duplicates `(channel, ts)` are dropped. Samples newer than the
channel's `LastTs` are appended directly (the common path, no lookup).
Samples at or before `LastTs` are checked against stored raw rows in one
batched query and only missing ones are appended.

**Events body**:

```json
{ "events": [ { "ts": "…", "source": "inverter1", "code": 4096,
                "kind": "alarm", "message": "optional text" } ] }
```

Events are decoded with `StatusCodeCatalog` (brand + source + code) and stored
in `DeviceEvents`. Status channels (`inverter{n}.status_code`) also produce an
event whenever their value changes.

### 4.3 Entity mapping

`TelemetryEntityMaps(InstallationId, EntityId, Key, Scale, Offset, Invert)`.
A sample with `entity_id` uses the map: `value' = (value × Scale + Offset) ×
(Invert ? −1 : 1)`. An unmapped entity is stored under
`ha.<sanitised entity id>` (Kind = Other) and listed in the response and in
the site's mapping page so the operator can map it later. Mapping is
retro-active only from the moment it is saved.

### 4.4 Side effects of a successful ingest

* `TelemetryChannels.LastValue/LastTs` updated.
* `Installation.LastOnlineAt = now` — the health monitor treats a pushing
  gateway like a reachable cloud.
* A realtime `EnergyData` row at most every 5 minutes per site, built from
  canonical keys, and today's Daily row upserted — so every existing page,
  report and alert keeps working for gateway sites without change.
* Dirty-range marks for late data (§5.3).

---

## 5. Storage

### 5.1 Tables

| Table | Shape | Index | Retention |
|---|---|---|---|
| `TelemetryChannels` | Id int, InstallationId, Key, Kind, DeviceIndex, SubIndex, Quantity, Unit, IsCumulative, FirstTs, LastTs, LastValue | unique (InstallationId, Key) | forever |
| `TelemetryRaw` | ChannelId int, Ts datetime2(0), Value real | **clustered columnstore** | 90 days (setting) |
| `TelemetryAggregates` | ChannelId, Resolution tinyint (1=5 min, 2=1 h, 3=1 day), BucketStart, Min, Max, Avg, First, Last, Count | clustered PK (ChannelId, Resolution, BucketStart), PAGE compressed | 5 min: 400 days · 1 h & 1 day: forever |
| `TelemetryDirtyRanges` | InstallationId, FromUtc, ToUtc | | until rolled up |
| `TelemetryGateways` | token hashes | | |
| `TelemetryEntityMaps` | mappings | | |
| `DeviceEvents` | Ts, Source, Code, Decoded, Severity | (InstallationId, Ts) | 2 years |
| `SiteAnalyticsDaily` | InstallationId, Date, Kind, Json | unique (InstallationId, Date, Kind) | forever |

The raw row is 3 columns; the channel id carries site, device and quantity.
Values are `real` (float4, ~7 significant digits) — enough for every
quantity here, half the size of `float`.

### 5.2 Size model

Assumptions: 150 channels per site (inverter + 6 strings + 3 packs with
extremes + grid + load + env), 1 sample/min.

| Tier | Rows / site / day | Bytes / row (stored) | MB / site / day | 100 sites, kept |
|---|---|---|---|---|
| Raw, columnstore | 216 000 | ~2–3 | ~0.5 | 90 d ≈ **4.5 GB** |
| 5-min aggregates | 43 200 | ~30 (page-compressed rowstore) | ~1.3 | 400 d ≈ **52 GB** |
| 1-hour aggregates | 3 600 | ~30 | ~0.1 | per year ≈ 3.9 GB |
| 1-day aggregates | 150 | ~30 | ~0.005 | negligible |

What this means:

* **SQL Server Express (10 GB per database) holds ≈ 10–15 sites** with
  these retentions. The 5-minute tier dominates, not raw.
* Levers, in order of cost: (1) 5-min tier only for analytics channels (power,
  energy, SOC, string power, cell extremes — ~40 of 150): ÷4; (2) shorten
  5-min retention to 13 months (still a full year for the PV model and
  year-on-year); (3) put telemetry in its **own database** so the 10 GB cap
  applies to it separately from the business data; (4) SQL Server Standard
  (no size cap; partitioning by month, `TRUNCATE … WITH (PARTITIONS)` for
  retention); (5) move raw + 5-min to TimescaleDB/InfluxDB behind
  `ITelemetryStore` (§5.4) — no change above that interface.
* Retention deletes are batched (`DELETE TOP (50 000)` loops) so the log and
  locks stay small; the columnstore is reorganised weekly.

### 5.3 Rollups and late data

`TelemetryRollupJob` runs every 5 minutes. Per site it keeps a watermark (the
last fully rolled-up 5-minute bucket). Buckets are rolled up once they are
older than a **10-minute grace** period: raw → 5 min, then 5 min → 1 h, then
1 h → 1 day (day boundaries in site-local time). Cumulative counters use
`Last − First` within the bucket, plus resets detected and bridged.

A sample that arrives behind the watermark (an HA box that was offline for a
day and replays its buffer) records a dirty range; the next rollup run
recomputes exactly those buckets (delete + insert) and everything above them.

**Reading a counter's energy** (`TelemetryRollup`): for `IsCumulative`
channels, the energy in any bucket at any resolution is `Last − First`
(`TelemetryRollup.Increase`). `First` is the *opening* reading — the last
good reading before the bucket (2 h look-back), so the step between two
buckets is not lost — and `Last = First + bridged increase`: a drop larger
than jitter (1 % / 0.05) is a reset and counts the new reading; a drop that
recovers within 10 minutes is a bad read and is ignored. Parents sum their
children's increases, so a day equals the sum of its hours. Min/Max/Avg stay
real readings. Work per site per run is capped at one day for each of the
dirty and the normal pass, so a backlog catches up gradually.

`EnergyDailyBridge` upserts today's and yesterday's EnergyData Daily row for
LocalGateway/Hybrid sites from 1-hour counter aggregates, else from 5-minute
power aggregates split by sign; figures never decrease (ingest's inverter
"today" counter may be fresher). `SolarDataSyncJob` skips LocalGateway sites
and, for Hybrid sites whose gateway pushed in the last 10 minutes, keeps
polling for alarms but writes no realtime/daily EnergyData
(`TelemetrySourcePolicy`).

### 5.4 `ITelemetryStore`

```csharp
public interface ITelemetryStore
{
    Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default);
    Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default);
    Task<List<RawSample>> ReadRawAsync(IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default);
    Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default);
    Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize, CancellationToken ct = default);
}
public readonly record struct RawSample(int ChannelId, DateTime TsUtc, double Value);
public readonly record struct AggregatePoint(int ChannelId, AggregateResolution Resolution, DateTime BucketStartUtc,
    double Min, double Max, double Avg, double First, double Last, int Count);
public enum AggregateResolution : byte { FiveMinutes = 1, Hour = 2, Day = 3 }
```

Implementations: `SqlServerTelemetryStore` (SqlBulkCopy for appends) and
`InMemoryTelemetryStore` (tests). A Timescale/Influx store is a third
implementation of the same interface.

---

## 6. Site capabilities (what each report shows)

`SiteCapabilities` is computed from the site's channel catalog plus the
installation's own fields (for cloud-only sites):

```csharp
public sealed record SiteCapabilities(
    bool HasPv, int StringCount, int InverterCount,
    bool HasBattery, int BatteryPackCount, bool HasCellData, bool HasBatteryTemps,
    bool HasGridMeter, bool HasGridVoltage, bool HasGridFrequency, bool HasGridStatus,
    bool HasLoad, bool HasInverterTemps, bool HasStatusCodes,
    bool HasLux, bool HasIrradiance, bool HasTelemetry);
```

Every report section declares the capability it needs; the page renders only
sections whose capability is present and says, in one line, why a section is
absent ("ไม่มีแบตเตอรี่ในระบบนี้"). Customer pages use the same flags.

---

## 7. Analytics (pure functions, `Services/Analytics`)

All analytics are deterministic functions over arrays — no database access —
so they are unit-tested with synthetic data. Inputs are `TimeSeries`:

```csharp
public readonly record struct Point(DateTime TsUtc, double Value);
public sealed class TimeSeries { IReadOnlyList<Point> Points; … }  // sorted ascending
```

### 7.1 Battery

`BatteryAnalyzer.Analyze(BatteryInput input, BatteryOptions opt) → BatteryReport`

* **Input:** SOC % series, battery power W series (+charge/−discharge), optional
  cell ΔV (mV) series per pack, cell max/min temperature per pack, pack SOC
  and power per pack, BMS SOH series, grid status series (for outages).
* **Sessions** — a charge (discharge) session starts when |P| > 300 W for > 3
  minutes in one direction; pauses shorter than 7 minutes are merged. Counts
  and kWh per session; noon oscillation from zero-export is not a session.
* **Full cycles** — SOC reaches ≥ 98 %, then its minimum before SOC next
  reaches ≥ 98 % is the cycle's depth: deep (min ≤ 30 %), mid (30–60 %),
  shallow (> 60 %).
* **EFC** — Σ discharged kWh ÷ nominal capacity (system; per pack with the
  pack capacity).
* **Rainflow DoD histogram** — ASTM E1049 four-point rainflow on SOC turning
  points; full and half cycles into 10 % bins (0–10 … 90–100).
* **Measured capacity** — for each discharge that starts within 30 minutes
  after SOC ≥ 98 %, falls ≥ 50 percentage points, has < 0.2 kWh of charging
  inside it, and (if temperatures exist) stays 20–40 °C:
  `capacity = discharged kWh ÷ (ΔSOC ÷ 100)`. Report the 30-day median, the
  ± spread, change vs 90 days earlier (▲▼), and the gap to BMS SOH (flag if
  > 5 points).
* **Round-trip efficiency** — between consecutive "full" moments (SOC ≥ 98 %):
  discharged ÷ charged. Good ≥ 92 %, watch 88–92 %, poor < 88 %; > 100 % is
  reported as a data error, not as efficiency.
* **Daily metrics** — charged / discharged kWh, overnight discharge (max →
  min after 16:00), SOC max → min and DoD, EFC today, hours at 100 % within
  07:00–18:00 (a PV curtailment sign), hours below 20 %, C-rate average and
  maximum (|P| ÷ capacity), cell temperature min–max. **Heat-and-full flag:**
  > 5 h at 100 % on a day whose max cell temperature > 35 °C (accelerated
  calendar ageing in Chonburi heat) → orange.
* **Health KPIs** (per pack, grade good / watch / poor):

  | KPI | Condition | Good / Watch / Poor |
  |---|---|---|
  | Cell ΔV at rest | |P| < 100 W, SOC 30–80 % | ≤ 20 / 20–50 / > 50 mV |
  | Cell ΔV at full | SOC ≥ 99 % | < 50 / 50–150 / > 150 mV |
  | Max cell temp | daily | ≤ 35 / 35–45 / > 45 °C |
  | Temp spread in pack | max − min | ≤ 3 / 3–6 / > 6 °C |
  | SOH trend | vs 90 days, per year | ≤ 2 / 2–4 / > 4 points |
  | Pack vs packs | deviation from pack mean | < 3 / 3–7 / > 7 % |
  | Internal resistance | ΔV ÷ ΔI on load steps vs first month | ≤ +20 / 20–50 / > +50 % |

  Cell temperature alerts at 45 °C (warning) and 50 °C (critical).
* **Overall grade** — A: SOH ≥ 90, measured capacity ≥ 90 %, efficiency ≥ 90 %,
  cell ΔV < 30 mV; C: any "poor" KPI or cell temp > 50 °C; otherwise B.
* **Warranty throughput** — e.g. FoxESS EP11: 10 years or 33.4 MWh, whichever
  first, ≥ 70 % capacity. Report % of throughput used, years elapsed, and the
  expected remaining life = min(years left, remaining MWh ÷ trailing-year MWh).

### 7.2 PV and strings

* **Clear-sky** — solar position (NOAA) and Haurwitz GHI; plane-of-array is
  approximated by GHI (no tilt data needed for ratios).
* **Expected-PV model** — trained weekly on 5-minute data **from intervals
  that are not curtailed**: per (month, hour) bucket the median of
  `P_actual ÷ (GHI_clearsky × kWp)`; where a lux (or irradiance) channel
  exists, a per-hour least-squares fit `P = a·lux + b` replaces the clear-sky
  ratio. Model JSON is stored in `SiteAnalyticsDaily` (Kind = `pv-model`).
* **Performance** — expected vs actual kWh per day, kWh/kWp, PR.
* **String comparison** — only 10:00–14:00 samples, total PV > 30 % of kWp,
  not curtailed, and all strings reporting. Each string's power ÷ the median
  of strings (per-kWp if string sizes known). Classification of a persistent
  deviation (≥ 3 days): uniform ratio decline over weeks that resets after
  rain/cleaning → **soiling**; a step down of ≈ 1/N of the string (one panel /
  bypass diode) → **panel fault**; intermittent drops to ~0 → **loose
  connector / fuse**.

### 7.3 Curtailment (zero export)

An interval is curtailed when export ≈ 0 (|grid| < 50 W on the export side
or grid importing ≈ 0), battery is full (≥ 98 %) or not accepting charge, and
PV is below 80 % of expected (or flat-topped well below the inverter's
rating). Lost energy = Σ (expected − actual) over curtailed intervals. Value
in baht at the import rate (energy that could have been used by moving loads).
The report lists the hours with the most curtailment as "best hours to run
appliances".

### 7.4 Economics and TOU

* Savings = self-consumed PV × import rate + export × feed-in rate; payback
  progress = cumulative savings ÷ `ContractValue`.
* **TOU what-if** — hourly import/export from telemetry (or Daily data for
  cloud sites, flat only) priced under both the site's current plan and the
  alternative (Flat / Progressive / TOU, rates editable per site, FT, service
  charge, VAT). Monthly table of both bills and the difference; recommendation
  when TOU saves more than 3 % over the last 3 months **and** the saving
  exceeds the meter change cost payback threshold (configurable).
* CO₂ = PV kWh × 0.5 kg (Thai grid factor, setting); trees = kg ÷ 9.
* Self-sufficiency = 1 − import ÷ consumption, monthly; year-on-year after 12
  months.

### 7.5 Grid, thermal, outages, data quality

* **Outages** — `grid.status = 0` or grid voltage < 100 V for ≥ 1 minute:
  start, end, duration, SOC at start/end, kWh supplied by battery, hours the
  battery covered.
* **Power quality** — voltage min/avg/max per hour, % time outside 230 V ±10 %,
  frequency outside 50 ± 0.5 Hz, sag/swell counts.
* **Thermal** — inverter and battery temperatures; derating detected when
  temperature is high and output flat below rating.
* **Data quality watchdog** — per channel per day: coverage % (received ÷
  expected at the gateway's interval), gaps > 3 intervals, stuck values
  (unchanged ≥ 60 min while the quantity should move, e.g. PV power in
  daylight), out-of-range (SOC ∉ 0–100, cell temp ∉ −20–90 °C), counter
  resets/decreases, future timestamps. Daily score 0–100 and issue list.

### 7.6 Status and alarm decoding

`StatusCodeCatalog.Decode(brand, source, code) → (Text, Severity)` with
tables for FoxESS, Huawei SUN2000 and Deye running-status/alarm registers;
unknown codes are shown as the number with "ไม่ทราบความหมาย".

---

## 8. Dashboards and charts

Site page **วิเคราะห์ (Analytics)** with tabs shown per capability:

| Tab | Needs | Contents |
|---|---|---|
| ภาพรวม | any | power flow now, today PV / load / import / export, self-sufficiency, savings, CO₂ |
| แบตเตอรี่ | HasBattery | **history chart** (below), SOC heatmap, DoD histogram, cycles (4 kinds), capacity & efficiency trends, daily table, KPI grades, pack comparison, warranty |
| PV & สตริง | HasPv / StringCount ≥ 2 | expected vs actual, PR, string comparison and findings, curtailment |
| ค่าไฟ & TOU | HasGridMeter | monthly bills both ways, recommendation, rate settings |
| ไฟฟ้า & ความร้อน | HasGridVoltage / temps | power quality, outages, temperatures |
| คุณภาพข้อมูล | HasTelemetry | coverage, gaps, stuck/out-of-range channels |
| เหตุการณ์ | HasStatusCodes | decoded status/alarm event log |

**Battery history chart ("ทั้งหมดในภาพเดียว").** Daily bars: green above
zero = kWh charged, orange below zero = kWh discharged; behind them a band
from the day's SOC minimum to maximum on the right axis (0–100 %); a marker on
days with an outage. If the selected range exceeds 365 days the bars become
weekly automatically. Supporting charts for technicians: SOC heatmap (x = hour,
y = date) and DoD histogram (10 % bins).

The customer portal shows the owner-facing subset (savings, outages,
self-sufficiency, CO₂, year-on-year, battery summary) with the same
capability rules.

---

## 9. Notification center

* **Recipients** per customer (`CustomerNotificationRecipients`): channel
  (LINE / Email), address (LINE user id or e-mail), optional site scope,
  enabled flag, verified time.
* **Preferences** per customer and event type
  (`CustomerNotificationPreferences`): LINE on/off, Email on/off. Defaults are
  taken from the site's existing `NotifyOn*` flags so nothing changes until the
  operator edits.
* **LINE binding by link.** The operator presses *สร้างลิงก์ผูก LINE*; the
  system creates a one-time code (8 chars, 7-day expiry) and shows a link
  `https://<app>/l/<code>` (+ QR). The landing page has two buttons: add the
  LINE OA as friend (`https://line.me/R/ti/p/<basic id>`) and send the code
  (`https://line.me/R/oaMessage/<basic id>/?<text>`). When the webhook
  receives that text, it binds the sender's LINE user id as a recipient of
  that customer, replies with a confirmation, and marks the code used. The
  operator sees it appear on the page.
* **E-mail** via SMTP (settings page: host, port, TLS, user, password
  encrypted, from). Alerts go out as a simple HTML message with the same
  content as the LINE text.
* Delivery uses the existing alert pipeline: after the current customer LINE
  step, each enabled recipient whose preference allows the event receives the
  alert on its channel; quiet hours and severity rules are unchanged; every
  send is written to `NotificationLogs`.

---

## 10. Security

* Gateway tokens: 256-bit random, hashed at rest, constant-time compare,
  revocable, last-used time and IP recorded.
* Ingest endpoints are anonymous to cookie auth and accept only the bearer
  token; they never return other sites' data.
* Rate limiting per gateway; body size limit; strict key/value validation.
* The binding landing page reveals no customer data — only the site's
  display name after the code is used.

---

## 11. Phases

| Phase | Scope |
|---|---|
| 1 | Schema, store, ingestion API, gateway tokens, key parser, capabilities, HA setup page; analytics engine with unit tests; notification center with LINE link + e-mail; rollup/retention jobs; analytics page (battery, PV/strings, TOU, quality, events) |
| 2 | Expected-PV model weekly job tuned on real data; per-cell views; customer-portal analytics tab; PDF report |
| 3 | Optional Timescale/Influx store; partitioning on SQL Server Standard; Grafana data source endpoint |

---

## 12. Implementation notes (analytics wiring)

* **Loader** — `Services/Analytics/SiteAnalyticsLoader.cs`. Windows up to 2 days read raw
  rows; longer windows read 5-minute aggregates plus the raw tail after the last closed
  bucket. Counters and codes use the bucket's last value, `*_max` channels the maximum,
  `*_min` channels and `grid.status` the minimum, everything else the mean.
  `SiteAnalyticsInputs` maps channel keys to analyzer inputs (pure, unit-tested).
* **Service and job** — `SiteAnalyticsService.ComputeSiteAsync` computes each section the
  site's devices allow and stores JSON in `SiteAnalyticsDaily` under these kinds:
  `battery`, `battery-day` (one row per day, kept forever: the long history chart),
  `pv-model` (retrained when older than 7 days), `pv`, `curtailment`, `strings`, `grid`,
  `thermal`, `quality` (yesterday and today), `tou`. `SiteAnalyticsJob` runs it for every
  site with telemetry daily (`Schedule.SiteAnalytics`, default `15 21 * * *` UTC = 04:15
  Thailand). The page's "คำนวณใหม่" button recomputes one site on demand.
* **Page** — `/Installations/Analytics/{id}` (Admin, Partner). Tabs follow §8 and
  `SiteCapabilities`; hidden tabs are listed with their reason. Overview and savings come
  from Daily `EnergyData`, so cloud-only sites get them too. Tariff plans (`TariffPlans`)
  and the battery warranty fields are edited on the page.
