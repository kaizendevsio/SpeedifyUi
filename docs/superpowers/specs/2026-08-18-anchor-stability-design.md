# Anchor Stability: Sticky Selection with Prove-It Promotion

**Date:** 2026-08-18
**Status:** Approved design, pending implementation plan
**Problem owner:** uLink router (xeon-network), all redundancy policies

## Problem

In `balanced` and `fast` policies, bulk traffic rides only the anchor path. The anchor
selector flaps between a stable fiber line and an obstructed Starlink dish, and every flap
physically moves the user's traffic stream — a visible hiccup. Only `reliable` mode is
usable in production today, because full duplication masks the flapping rather than
preventing it.

### Root causes (verified in code)

1. **Measurement asymmetry.** The anchor is scored under load; backups are scored idle.
   `PathHealthSnapshot::score()` (`xbond-core/src/health.rs`) charges load-correlated
   penalties — late rate ×1000, queue pressure ×400, jitter ×2.5, RTT ×2 — so a loaded
   fiber anchor scores worse than an idle Starlink that is only passing 200 ms heartbeats
   between obstruction events.
2. **Weak hysteresis.** Switching requires a 150-point margin sustained for 5 ticks (5 s),
   hard-coded in `RoleSelectionConfig::default()`. Starlink obstruction cycles are tens of
   seconds apart; 5 clean seconds is trivial to pass.
3. **No flap memory.** Nothing records that a path recently failed as anchor. After a
   demotion, the same path is eligible again 5 seconds later.
4. **Fast loss decay.** Heartbeat loss is a rolling window; a burst of obstruction loss
   evaporates from the score once acks resume.

Role selection is shared by all policies (`select_path_roles_with_state`), so the fix
applies everywhere; `reliable` simply masks flips with duplication today.

## Goals

- The anchor never moves to a path that has not proven itself under real load.
- A path that repeatedly fails as anchor is distrusted for minutes, not seconds.
- Hard failover latency is unchanged: a dead anchor is replaced on the next tick.
- No manual path tiers; the algorithm stays fully automatic (user decision).
- Trial traffic on metered paths is acceptable (user decision).
- No wire-protocol break: old client/server pairs keep working during rollout.

## Non-goals

- Changing hard-demotion rules, recovery redundancy, FEC, or repair.
- Operator-facing per-path pin/exclude controls (rejected in favor of fully automatic).
- Persisting flap state across client restarts.

## Design

Four cooperating pieces, all in `xbond-core` except config plumbing and UI surfacing.

### 1. Score smoothing and variance penalty (`health.rs`)

Per path, `RoleSelectionState` gains EWMA state updated each scheduler tick (1 s):

- `smoothed = smoothed + alpha * (score - smoothed)`, `alpha = 0.2` (~5 s time constant).
- `deviation = deviation + alpha * (|score - smoothed| - deviation)` — mean absolute
  deviation, an economical variance proxy.
- **Effective score** for role comparison: `smoothed - variance_penalty_weight * deviation`
  (`weight = 2.0`). A path whose score swings (obstructed dish) ranks below a
  boringly-stable path even when its instantaneous score is higher.

Raw score is still computed and displayed; hard demotion continues to use raw signals so
smoothing never delays failover. Paths with no smoothing history initialize from their
first raw score.

### 2. Flap damping (`health.rs`)

BGP-style decaying penalty per path in `RoleSelectionState`:

| Event | Penalty added |
|---|---|
| Anchor loses role (hard demotion or replaced by better path) | +1000 |
| Trial aborted for cause (see §3) | +600 |

- Exponential decay, half-life 300 s (per-tick factor `0.5^(1/300)`), capped at 4000.
- While `penalty >= 500`, the path is **suppressed**: it cannot become an anchor
  candidate or start a trial. It remains fully eligible as a backup/duplicate and — 
  critically — for hard failover: if the anchor dies and only suppressed paths remain,
  the best of them is promoted immediately. Suppression gates upgrades, never failover.
- `role_reason` explains suppression, e.g. `"Suppressed after anchor failure
  (penalty 812, usable again in ~4m)."`

### 3. Prove-it promotion: the trial state machine (`health.rs` + `scheduler.rs`)

Anchor promotion becomes a three-stage ladder. Per tick:

```
Steady ──candidate beats anchor's effective score by margin
          for stable_ticks, and is not suppressed──▶ Trial ──clean under
          mirrored load for trial window──▶ Promote (new anchor)
                       │
                       └─degrades under load──▶ Abort + flap penalty ──▶ Steady
```

- **Entering trial.** The existing candidate hysteresis is kept (margin raised to 200,
  `stable_ticks_required` raised to 10) but now compares *effective* scores and skips
  suppressed paths. Passing it no longer switches the anchor — it starts a trial.
- **During trial.** The trial path is added to a new `SchedulePlan.trial_path_ids` list.
  The shared plan builders (`build_transmission_plan*`, `precompute_transmission_plans`)
  emit the trial path as a `Duplicate` transmission for **both small and bulk packets in
  every policy** — unlike normal backups, which balanced/fast only use for small packets
  or when the anchor is degraded. Because the server plans return traffic with the same
  shared builders from the client's `ScheduleControlMessage`, the mirror covers **both
  directions** with no server-specific code. The receiver already dedupes duplicates.
- **Promotion criteria** (evaluated over `trial_ticks = 20` ticks): promote iff the trial
  path's raw loaded score stayed at or above the anchor's for at least
  `trial_success_ticks = 15` ticks, with no hard demotion and no cooldown entry.
- **Abort for cause** (flap penalty applies): hard demotion, cooldown, or loaded score
  below the anchor's for more than `trial_ticks - trial_success_ticks` ticks.
- **Insufficient load.** If total mirrored payload over the window is below
  `trial_min_bytes = 5 MB`, the load test was inconclusive. The path is promoted anyway
  if it stayed clean — with near-zero traffic a switch is near-zero risk — otherwise the
  trial aborts without penalty.
- **Concurrency and pacing.** At most one trial at a time, tunnel-wide. After any trial
  ends, no new trial starts for `trial_min_interval_ticks = 60`. Repeat trials of the
  same path are additionally gated by its decaying flap penalty.
- **Interactions.** No trials while recovery redundancy is active (everything is already
  duplicated; the existing hysteresis switch applies unchanged). A trial path keeps its
  backup duties; the trial adds a mirror lane, it does not consume the
  `max_active_backups` budget. Anchor hard demotion during a trial cancels the trial and
  fails over instantly to the best eligible path (which may be the trial path itself).

### 4. New role and status surfacing

- `PathRole` gains a `Trial` variant (serialized `"trial"`). `PathRole` never crosses the
  client-server wire (only `PathHealthSnapshot`s do), so this is compat-safe; it appears
  only in client status JSON.
- Client status events gain per-path `smoothed_score`, `effective_score`, `flap_penalty`,
  and a `trial` block (`active`, `ticks`, `success_ticks`, `mirrored_bytes`) for
  debugging from journald.
- **XNetwork dashboard:** paths with role `trial` render in the Active group with an
  amber "Trial" pill; suppressed paths keep their normal group but surface the
  suppression `role_reason` in the details sheet. `XBondStatsSnapshot`/`XBondPathStatus`
  parse the new fields tolerantly (missing = defaults) so the UI works against old
  clients.

### 5. Configuration

`RoleSelectionConfig` stops being hard-coded. New optional `[role_selection]` table in
the client TOML (all fields defaulted; absent table = new defaults):

```toml
[role_selection]
anchor_switch_score_margin = 200.0   # was 150, hard-coded
stable_ticks_required = 10           # was 5, hard-coded
smoothing_alpha = 0.2
variance_penalty_weight = 2.0
flap_penalty_demoted = 1000.0
flap_penalty_trial_failed = 600.0
flap_suppress_threshold = 500.0
flap_penalty_cap = 4000.0
flap_half_life_secs = 300
trial_enabled = true                 # false = legacy direct promotion
trial_ticks = 20
trial_success_ticks = 15
trial_min_bytes = 5000000
trial_min_interval_ticks = 60
```

`backup_switch_score_margin` stays as-is (backup churn is already handled by
`stabilize_recovery_schedule` and is invisible to traffic in balanced/fast).

### Wire compatibility

`SchedulePlan` gains `#[serde(default)] trial_path_ids: Vec<u16>`. An old server ignores
the field (upload direction still mirrored by the new client; return direction untested
during the trial — acceptable degraded behavior). The paired deploy script ships both
sides together, so this window exists only mid-deploy.

## Expected behavior in the reported scenario

Globe fiber anchor, obstructed Starlink, Smart SIM, policy `balanced`:

1. Starlink idles clean for a stretch; its effective score clears the margin for 10 s.
2. Trial starts: bulk traffic mirrors onto Starlink both ways; user traffic still rides
   fiber untouched.
3. Under mirrored load the obstruction shows up (loss burst / RTT spike / late packets)
   → trial aborts, +600 penalty. **The anchor never moved.**
4. Retries are throttled: penalty must decay below 500 (~5–10 min after repeat failures,
   growing with each failure), plus the 60 s global trial spacing.
5. If the roof obstruction is actually fixed, Starlink passes a 20 s loaded trial and
   earns the anchor role legitimately.

## Testing

All deterministic, no timing dependence (tick-driven pure functions):

- **Obstruction-cycle simulation** (`xbond-core`): scripted score sequences for a
  clean-idle/bad-under-load path vs a stable anchor. Assert: anchor never changes,
  trials abort, flap penalty suppresses retries with growing intervals, mirroring occurs
  only during trial windows.
- **Legitimate upgrade:** candidate clean under load → promoted after exactly
  `stable_ticks + trial_ticks`; exactly one schedule generation bump.
- **Failover safety:** anchor hard-demoted mid-trial and with all alternatives
  suppressed → replacement on the same tick.
- **Planner tests** (`scheduler.rs`): trial path emitted as `Duplicate` for small and
  bulk in `fast`, `balanced`, `reliable`; never in `data_path_ids`; removed when trial
  ends.
- **Serde compat:** `SchedulePlan` JSON without `trial_path_ids` parses; old
  `ScheduleControlMessage` fixtures still deserialize.
- **Decay math:** penalty halves in one configured half-life; suppression lifts at the
  threshold boundary.
- **XNetwork:** role `"trial"` maps to the Trial pill; snapshots without the new fields
  parse with defaults (old client compat).

Existing `cargo test` and `dotnet test` suites must stay green. Manual validation on the
live router: journald `role_reason`/trial events while forcing `balanced`, confirming
zero anchor changes across Starlink obstruction cycles (`schedule_change_count`).

## Rollout

1. Implement + unit tests (`xbond-core`, client plumbing, XNetwork UI).
2. Deploy with `deploy-xbond-paired.ps1` (both hosts, per repo convention), version-pin
   changelog and AGENTS journal entry per release process.
3. Observe on the live router in `balanced` for a day; compare
   `schedule_change_count` growth against the current build before declaring fixed.

## Alternatives considered

- **Tuning-only** (bigger margin/longer ticks): rejected as primary fix — does not
  address measurement asymmetry or memory; kept as part of the defaults change.
- **Operator priority tiers / per-path pins:** rejected by user — wants fully automatic.
- **Skipping trials on metered paths:** rejected by user — trial cost accepted; flap
  damping keeps trials rare in steady state.
