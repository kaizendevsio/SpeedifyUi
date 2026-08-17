# Anchor Stability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the uLink anchor from flapping between a stable fiber line and an obstructed Starlink dish in `balanced`/`fast` policies, by making anchor promotion sticky, memory-driven, and load-tested.

**Architecture:** Three new mechanisms in a new `xbond-core/src/anchor.rs` module — EWMA score smoothing with a variance penalty, BGP-style decaying flap damping, and a "prove-it" trial state machine. `health.rs` keeps role assignment and orchestrates them. `scheduler.rs` mirrors real traffic onto the trial path using the existing `Duplicate` packet kind, so the server needs no protocol change. The XNetwork dashboard surfaces the new `trial` role and suppression reasons.

**Tech Stack:** Rust (xbond workspace, `cargo test`), .NET 9 Blazor Server (XNetwork, `dotnet test`), TOML client config, deploy via `deploy-xbond-paired.ps1`.

**Spec:** [docs/superpowers/specs/2026-08-18-anchor-stability-design.md](../specs/2026-08-18-anchor-stability-design.md)

---

## File Structure

**Created:**
- `xbond/crates/xbond-core/src/anchor.rs` — score smoothing, flap damping, trial state machine. Pure logic, no I/O. Owns `ScoreSmoothingConfig`, `FlapDampingConfig`, `AnchorTrialConfig`, `PathScoreSmoothing`, `FlapDamping`, `AnchorTrial`, `AnchorTrialStatus`, `TrialObservation`, `TrialOutcome`.
- `XNetwork.Tests/AnchorTrialSurfacingTests.cs` — C# tests for trial/suppression surfacing.

**Modified:**
- `xbond/crates/xbond-core/src/lib.rs` — declare and re-export `anchor`.
- `xbond/crates/xbond-core/src/health.rs` — `PathRole::Trial`, extended `ScoredPath`, extended `RoleSelectionConfig`/`RoleSelectionState`, trial-aware `select_path_roles_with_state`.
- `xbond/crates/xbond-core/src/scheduler.rs` — `SchedulePlan.trial_path_ids`, mirroring in the plan builders.
- `xbond/crates/xbond-core/src/config.rs` — `[role_selection]` TOML table, `ClientConfig::role_selection_config()`.
- `xbond/crates/xbond-core/src/status.rs` — new per-path status fields.
- `xbond/crates/xbond-client/src/main.rs` — pass `recovery_active` into role selection; JSON status fields.
- `xbond/examples/client.example.toml` — document the new table.
- `XNetwork/Models/XBondStatus.cs` — `TrialPathIds`, per-path smoothing/flap/trial fields.
- `XNetwork/Models/XBondStatsSnapshot.cs` — snapshot fields, `IsTrial`, `IsSuppressed`, `StateText`.
- `XNetwork/Services/XBondStatsService.cs` — map new fields; count trial paths as active.
- `XNetwork/Components/Pages/Home.razor` — amber Trial pill.
- `XNetwork/Components/Pages/XBond.razor` — `trial` pill colour.
- `XNetwork/Models/AppChangelog.cs`, `XNetwork.Tests/AppChangelogTests.cs`, `XNetwork.Tests/BuildInfoTests.cs` — release bump.
- `AGENTS.md` — release journal entry.

---

### Task 1: Anchor module scaffolding with score smoothing

**Files:**
- Create: `xbond/crates/xbond-core/src/anchor.rs`
- Modify: `xbond/crates/xbond-core/src/lib.rs`

- [ ] **Step 1: Write the failing test**

Create `xbond/crates/xbond-core/src/anchor.rs` containing ONLY this test module for now:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn first_observation_seeds_smoothing_without_deviation() {
        let mut smoothing = PathScoreSmoothing::default();
        smoothing.observe(500.0, 0.2);

        assert_eq!(smoothing.smoothed, 500.0);
        assert_eq!(smoothing.deviation, 0.0);
        assert_eq!(smoothing.effective_score(2.0), 500.0);
    }

    #[test]
    fn swinging_scores_are_penalised_below_a_steady_path() {
        // A path that alternates 900/100 averages 500 but is useless as an anchor.
        let mut swinging = PathScoreSmoothing::default();
        let mut steady = PathScoreSmoothing::default();
        for tick in 0..40 {
            swinging.observe(if tick % 2 == 0 { 900.0 } else { 100.0 }, 0.2);
            steady.observe(480.0, 0.2);
        }

        assert!(swinging.deviation > 100.0);
        assert!(steady.deviation < 5.0);
        assert!(
            steady.effective_score(2.0) > swinging.effective_score(2.0),
            "steady {} should beat swinging {}",
            steady.effective_score(2.0),
            swinging.effective_score(2.0)
        );
    }

    #[test]
    fn smoothing_alpha_is_clamped_to_a_usable_range() {
        let mut smoothing = PathScoreSmoothing::default();
        smoothing.observe(0.0, 0.2);
        smoothing.observe(100.0, 0.0);

        // alpha 0.0 would freeze the filter forever; it is clamped to 0.01.
        assert!(smoothing.smoothed > 0.0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib anchor::`
Expected: FAIL — `cannot find type PathScoreSmoothing in this scope` (the module is not declared yet, so it may also report `file not found for module` from lib.rs; both are the expected pre-implementation failure).

- [ ] **Step 3: Write minimal implementation**

Prepend to `xbond/crates/xbond-core/src/anchor.rs` (above the test module):

```rust
//! Anchor stability mechanisms: score smoothing, flap damping, and prove-it trials.
//!
//! These keep the anchor role from oscillating between a stable link and one that only
//! looks good while idle. `health.rs` owns role assignment and drives everything here
//! once per scheduler tick.

use serde::{Deserialize, Serialize};

use std::collections::BTreeMap;

/// How raw per-tick scores are turned into a stable comparison value.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct ScoreSmoothingConfig {
    /// EWMA weight for the newest sample. 0.2 is roughly a five-tick time constant.
    pub alpha: f64,
    /// How harshly score instability is punished, in score points per point of deviation.
    pub variance_penalty_weight: f64,
}

impl Default for ScoreSmoothingConfig {
    fn default() -> Self {
        Self {
            alpha: 0.2,
            variance_penalty_weight: 2.0,
        }
    }
}

/// EWMA-smoothed score plus mean absolute deviation for one path.
#[derive(Debug, Clone, Copy, PartialEq, Default, Serialize, Deserialize)]
pub struct PathScoreSmoothing {
    pub smoothed: f64,
    pub deviation: f64,
    pub initialized: bool,
}

impl PathScoreSmoothing {
    pub fn observe(&mut self, raw_score: f64, alpha: f64) {
        let alpha = alpha.clamp(0.01, 1.0);
        if !self.initialized {
            self.smoothed = raw_score;
            self.deviation = 0.0;
            self.initialized = true;
            return;
        }

        let error = raw_score - self.smoothed;
        self.smoothed += alpha * error;
        self.deviation += alpha * (error.abs() - self.deviation);
    }

    /// Comparison value for role selection: a jittery path ranks below a steady one.
    pub fn effective_score(&self, variance_penalty_weight: f64) -> f64 {
        self.smoothed - self.deviation * variance_penalty_weight.max(0.0)
    }
}
```

Add to `xbond/crates/xbond-core/src/lib.rs`, next to the other `mod` declarations:

```rust
pub mod anchor;
```

And extend the existing `pub use` block in `lib.rs` with:

```rust
pub use anchor::{PathScoreSmoothing, ScoreSmoothingConfig};
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core --lib anchor::`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/anchor.rs xbond/crates/xbond-core/src/lib.rs
git commit -m "feat(xbond): add ewma score smoothing with variance penalty"
```

---

### Task 2: Flap damping

**Files:**
- Modify: `xbond/crates/xbond-core/src/anchor.rs`
- Modify: `xbond/crates/xbond-core/src/lib.rs`

- [ ] **Step 1: Write the failing test**

Add to the `tests` module in `anchor.rs`:

```rust
    #[test]
    fn penalty_suppresses_then_decays_below_threshold() {
        let config = FlapDampingConfig::default();
        let mut damping = FlapDamping::default();
        damping.add(7, config.penalty_demoted, config.penalty_cap);

        assert!(damping.is_suppressed(7, config.suppress_threshold));

        // One half-life takes 1000 -> 500, which is still at the threshold.
        for _ in 0..config.half_life_secs {
            damping.decay(config.half_life_secs);
        }
        assert!((damping.penalty(7) - 500.0).abs() < 1.0);

        // A little more decay drops it under the bar.
        for _ in 0..10 {
            damping.decay(config.half_life_secs);
        }
        assert!(!damping.is_suppressed(7, config.suppress_threshold));
    }

    #[test]
    fn repeat_offences_accumulate_up_to_the_cap() {
        let config = FlapDampingConfig::default();
        let mut damping = FlapDamping::default();
        for _ in 0..20 {
            damping.add(3, config.penalty_demoted, config.penalty_cap);
        }

        assert_eq!(damping.penalty(3), config.penalty_cap);
    }

    #[test]
    fn decay_drops_negligible_entries() {
        let mut damping = FlapDamping::default();
        damping.add(1, 1.5, 4_000.0);
        for _ in 0..600 {
            damping.decay(60);
        }

        assert_eq!(damping.penalty(1), 0.0);
        assert!(damping.is_empty());
    }

    #[test]
    fn seconds_until_clear_reports_one_half_life_for_double_the_threshold() {
        let config = FlapDampingConfig::default();
        let mut damping = FlapDamping::default();
        damping.add(9, 1_000.0, config.penalty_cap);

        let eta = damping.seconds_until_clear(9, config.suppress_threshold, config.half_life_secs);

        assert_eq!(eta, config.half_life_secs);
        assert_eq!(
            damping.seconds_until_clear(4, config.suppress_threshold, config.half_life_secs),
            0
        );
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib anchor::`
Expected: FAIL — `cannot find type FlapDampingConfig in this scope`.

- [ ] **Step 3: Write minimal implementation**

Append to `anchor.rs`, before the `#[cfg(test)]` module:

```rust
/// Distrust budget for paths that have failed as anchor recently.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct FlapDampingConfig {
    /// Charged when a path loses the anchor role, for any reason.
    pub penalty_demoted: f64,
    /// Charged when a path degrades under mirrored load during its trial.
    pub penalty_trial_failed: f64,
    /// At or above this penalty a path cannot be promoted or start a trial.
    pub suppress_threshold: f64,
    /// Ceiling so a chronically bad path cannot accrue an effectively permanent ban.
    pub penalty_cap: f64,
    /// Exponential decay half-life, in scheduler ticks (one tick per second).
    pub half_life_secs: u64,
}

impl Default for FlapDampingConfig {
    fn default() -> Self {
        Self {
            penalty_demoted: 1_000.0,
            penalty_trial_failed: 600.0,
            suppress_threshold: 500.0,
            penalty_cap: 4_000.0,
            half_life_secs: 300,
        }
    }
}

/// Per-path decaying penalties. Suppression gates *upgrades* only: `health.rs` still
/// promotes a suppressed path when nothing else is available, so failover never stalls.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct FlapDamping {
    penalties: BTreeMap<u16, f64>,
}

impl FlapDamping {
    pub fn penalty(&self, path_id: u16) -> f64 {
        self.penalties.get(&path_id).copied().unwrap_or_default()
    }

    pub fn add(&mut self, path_id: u16, amount: f64, cap: f64) {
        let cap = cap.max(0.0);
        let entry = self.penalties.entry(path_id).or_default();
        *entry = (*entry + amount.max(0.0)).min(cap);
    }

    /// One scheduler tick of exponential decay. Negligible entries are dropped so the
    /// map cannot grow without bound across path reconfigurations.
    pub fn decay(&mut self, half_life_secs: u64) {
        let factor = 0.5f64.powf(1.0 / half_life_secs.max(1) as f64);
        self.penalties.retain(|_, penalty| {
            *penalty *= factor;
            *penalty >= 1.0
        });
    }

    pub fn is_suppressed(&self, path_id: u16, suppress_threshold: f64) -> bool {
        self.penalty(path_id) >= suppress_threshold
    }

    pub fn is_empty(&self) -> bool {
        self.penalties.is_empty()
    }

    pub fn retain_paths(&mut self, live: &[u16]) {
        self.penalties.retain(|path_id, _| live.contains(path_id));
    }

    /// Seconds until this path stops being suppressed, for operator-facing text.
    pub fn seconds_until_clear(
        &self,
        path_id: u16,
        suppress_threshold: f64,
        half_life_secs: u64,
    ) -> u64 {
        let penalty = self.penalty(path_id);
        if suppress_threshold <= 0.0 || penalty < suppress_threshold {
            return 0;
        }

        let half_lives = (penalty / suppress_threshold).log2();
        (half_lives * half_life_secs.max(1) as f64).round().max(0.0) as u64
    }
}
```

Extend the `pub use` in `lib.rs`:

```rust
pub use anchor::{FlapDamping, FlapDampingConfig, PathScoreSmoothing, ScoreSmoothingConfig};
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core --lib anchor::`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/anchor.rs xbond/crates/xbond-core/src/lib.rs
git commit -m "feat(xbond): add decaying flap damping for anchor candidates"
```

---

### Task 3: Prove-it trial state machine

**Files:**
- Modify: `xbond/crates/xbond-core/src/anchor.rs`
- Modify: `xbond/crates/xbond-core/src/lib.rs`

- [ ] **Step 1: Write the failing test**

Add to the `tests` module in `anchor.rs`:

```rust
    fn loaded_tick(kept_up: bool) -> TrialObservation {
        TrialObservation {
            eligible: true,
            kept_up_with_anchor: kept_up,
            mirrored_bytes_this_tick: 1_000_000,
        }
    }

    #[test]
    fn clean_trial_promotes_after_the_full_window() {
        let config = AnchorTrialConfig::default();
        let mut trial = AnchorTrial::new(2);

        for _ in 0..config.ticks - 1 {
            assert_eq!(trial.observe(loaded_tick(true), config), TrialOutcome::Running);
        }

        assert_eq!(trial.observe(loaded_tick(true), config), TrialOutcome::Promoted);
        assert_eq!(trial.ticks, config.ticks);
    }

    #[test]
    fn obstructed_trial_fails_under_load_as_soon_as_it_cannot_pass() {
        let config = AnchorTrialConfig::default();
        let mut trial = AnchorTrial::new(2);
        let allowed_bad_ticks = config.ticks - config.success_ticks;

        for _ in 0..allowed_bad_ticks {
            assert_eq!(trial.observe(loaded_tick(false), config), TrialOutcome::Running);
        }

        assert_eq!(
            trial.observe(loaded_tick(false), config),
            TrialOutcome::FailedUnderLoad
        );
    }

    #[test]
    fn hard_failure_during_trial_fails_immediately() {
        let config = AnchorTrialConfig::default();
        let mut trial = AnchorTrial::new(2);
        trial.observe(loaded_tick(true), config);

        let outcome = trial.observe(
            TrialObservation {
                eligible: false,
                kept_up_with_anchor: false,
                mirrored_bytes_this_tick: 0,
            },
            config,
        );

        assert_eq!(outcome, TrialOutcome::FailedUnderLoad);
    }

    #[test]
    fn idle_trial_that_stayed_clean_is_still_promoted() {
        // Near-zero traffic means switching is near-zero risk, so cleanliness is enough.
        let config = AnchorTrialConfig::default();
        let mut trial = AnchorTrial::new(2);
        let idle = TrialObservation {
            eligible: true,
            kept_up_with_anchor: true,
            mirrored_bytes_this_tick: 0,
        };

        let mut outcome = TrialOutcome::Running;
        for _ in 0..config.ticks {
            outcome = trial.observe(idle.clone(), config);
        }

        assert_eq!(outcome, TrialOutcome::Promoted);
        assert!(trial.mirrored_bytes < config.min_bytes);
    }

    #[test]
    fn idle_trial_that_degraded_is_inconclusive_not_a_failure() {
        // Without real load we cannot blame the path, so it must not earn a penalty.
        let config = AnchorTrialConfig::default();
        let mut trial = AnchorTrial::new(2);
        let idle_bad = TrialObservation {
            eligible: true,
            kept_up_with_anchor: false,
            mirrored_bytes_this_tick: 0,
        };

        let mut outcome = TrialOutcome::Running;
        for _ in 0..config.ticks {
            outcome = trial.observe(idle_bad.clone(), config);
        }

        assert_eq!(outcome, TrialOutcome::Inconclusive);
    }

    #[test]
    fn trial_status_reports_progress_against_configured_bars() {
        let config = AnchorTrialConfig::default();
        let mut trial = AnchorTrial::new(4);
        trial.observe(loaded_tick(true), config);
        trial.observe(loaded_tick(false), config);

        let status = trial.status(config);

        assert_eq!(status.path_id, 4);
        assert_eq!(status.ticks, 2);
        assert_eq!(status.success_ticks, 1);
        assert_eq!(status.mirrored_bytes, 2_000_000);
        assert_eq!(status.required_ticks, config.ticks);
        assert_eq!(status.required_success_ticks, config.success_ticks);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib anchor::`
Expected: FAIL — `cannot find type AnchorTrialConfig in this scope`.

- [ ] **Step 3: Write minimal implementation**

Append to `anchor.rs`, before the `#[cfg(test)]` module:

```rust
/// Rules for the load test a path must pass before it may take the anchor role.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct AnchorTrialConfig {
    /// When false, a candidate that clears hysteresis is promoted directly (legacy behaviour).
    pub enabled: bool,
    /// Length of the trial window in scheduler ticks.
    pub ticks: u32,
    /// How many of those ticks must keep up with the anchor under mirrored load.
    pub success_ticks: u32,
    /// Below this much mirrored payload the trial is treated as inconclusive.
    pub min_bytes: u64,
    /// Quiet period after any trial ends, so trials cannot run back to back.
    pub min_interval_ticks: u32,
}

impl Default for AnchorTrialConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            ticks: 20,
            success_ticks: 15,
            min_bytes: 5_000_000,
            min_interval_ticks: 60,
        }
    }
}

impl AnchorTrialConfig {
    /// Ticks the candidate may fail and still pass. Kept as a method so the early-abort
    /// check and the final verdict cannot drift apart.
    pub fn allowed_bad_ticks(&self) -> u32 {
        self.ticks.max(1).saturating_sub(self.effective_success_ticks())
    }

    pub fn effective_success_ticks(&self) -> u32 {
        self.success_ticks.min(self.ticks.max(1))
    }
}

/// What one scheduler tick observed about the path currently on trial.
#[derive(Debug, Clone, PartialEq)]
pub struct TrialObservation {
    /// False when the path hard-demoted or entered cooldown during the trial.
    pub eligible: bool,
    /// True when the trial path's raw loaded score held at or above the anchor's.
    pub kept_up_with_anchor: bool,
    pub mirrored_bytes_this_tick: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum TrialOutcome {
    /// Still gathering evidence.
    Running,
    /// Passed under load; the caller should hand it the anchor role.
    Promoted,
    /// Degraded while carrying real mirrored traffic. Earns a flap penalty.
    FailedUnderLoad,
    /// Degraded but there was too little traffic to blame the path. No penalty.
    Inconclusive,
    /// Abandoned for an external reason (anchor died, recovery engaged). No penalty.
    Cancelled,
}

/// A candidate carrying mirrored production traffic to prove it can hold the anchor role.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AnchorTrial {
    pub path_id: u16,
    pub ticks: u32,
    pub success_ticks: u32,
    pub mirrored_bytes: u64,
}

/// Serialisable progress report for status output.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AnchorTrialStatus {
    pub path_id: u16,
    pub ticks: u32,
    pub success_ticks: u32,
    pub mirrored_bytes: u64,
    pub required_ticks: u32,
    pub required_success_ticks: u32,
}

impl AnchorTrial {
    pub fn new(path_id: u16) -> Self {
        Self {
            path_id,
            ticks: 0,
            success_ticks: 0,
            mirrored_bytes: 0,
        }
    }

    pub fn observe(
        &mut self,
        observation: TrialObservation,
        config: AnchorTrialConfig,
    ) -> TrialOutcome {
        if !observation.eligible {
            // A hard failure while carrying mirrored traffic is the clearest possible
            // "not fit to be anchor" signal, regardless of how much data flowed.
            return TrialOutcome::FailedUnderLoad;
        }

        self.ticks = self.ticks.saturating_add(1);
        self.mirrored_bytes = self
            .mirrored_bytes
            .saturating_add(observation.mirrored_bytes_this_tick);
        if observation.kept_up_with_anchor {
            self.success_ticks = self.success_ticks.saturating_add(1);
        }

        let bad_ticks = self.ticks.saturating_sub(self.success_ticks);
        if bad_ticks > config.allowed_bad_ticks() {
            // Cannot reach the success bar any more; stop mirroring early.
            return self.failure_outcome(config);
        }

        if self.ticks < config.ticks.max(1) {
            return TrialOutcome::Running;
        }

        if self.success_ticks >= config.effective_success_ticks() {
            TrialOutcome::Promoted
        } else {
            self.failure_outcome(config)
        }
    }

    fn failure_outcome(&self, config: AnchorTrialConfig) -> TrialOutcome {
        if self.mirrored_bytes >= config.min_bytes {
            TrialOutcome::FailedUnderLoad
        } else {
            TrialOutcome::Inconclusive
        }
    }

    pub fn status(&self, config: AnchorTrialConfig) -> AnchorTrialStatus {
        AnchorTrialStatus {
            path_id: self.path_id,
            ticks: self.ticks,
            success_ticks: self.success_ticks,
            mirrored_bytes: self.mirrored_bytes,
            required_ticks: config.ticks.max(1),
            required_success_ticks: config.effective_success_ticks(),
        }
    }
}
```

Extend the `pub use` in `lib.rs`:

```rust
pub use anchor::{
    AnchorTrial, AnchorTrialConfig, AnchorTrialStatus, FlapDamping, FlapDampingConfig,
    PathScoreSmoothing, ScoreSmoothingConfig, TrialObservation, TrialOutcome,
};
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core --lib anchor::`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/anchor.rs xbond/crates/xbond-core/src/lib.rs
git commit -m "feat(xbond): add prove-it anchor trial state machine"
```

---

### Task 4: Trial role and extended scored path

**Files:**
- Modify: `xbond/crates/xbond-core/src/health.rs:160-236`

- [ ] **Step 1: Write the failing test**

Add to the `tests` module at the bottom of `health.rs`:

```rust
    #[test]
    fn trial_role_serialises_as_kebab_case() {
        assert_eq!(
            serde_json::to_string(&PathRole::Trial).unwrap(),
            "\"trial\""
        );
    }

    #[test]
    fn scored_paths_expose_smoothed_and_effective_scores() {
        let mut state = RoleSelectionState::default();
        let roles = select_path_roles_with_state(
            &[path(1, "fiber", 15.0, 0.0, 0.0)],
            1,
            &mut state,
            RoleSelectionConfig::default(),
            false,
        );

        let fiber = &roles[0];
        // First tick seeds the filter, so all three views agree.
        assert_eq!(fiber.smoothed_score, fiber.score);
        assert_eq!(fiber.effective_score, fiber.score);
        assert_eq!(fiber.flap_penalty, 0.0);
    }

    #[test]
    fn hard_demoted_path_keeps_its_sentinel_effective_score() {
        let mut down = path(1, "down", 15.0, 0.0, 0.0);
        down.interface_up = false;
        let mut state = RoleSelectionState::default();

        // Build a good history first, then fail the interface: smoothing must not rescue it.
        for _ in 0..10 {
            select_path_roles_with_state(
                &[path(1, "down", 15.0, 0.0, 0.0), path(2, "other", 50.0, 0.0, 0.0)],
                1,
                &mut state,
                RoleSelectionConfig::default(),
                false,
            );
        }
        let roles = select_path_roles_with_state(
            &[down, path(2, "other", 50.0, 0.0, 0.0)],
            1,
            &mut state,
            RoleSelectionConfig::default(),
            false,
        );

        let failed = roles.iter().find(|p| p.path.path_id == 1).unwrap();
        assert_eq!(failed.effective_score, -1_000_000.0);
        assert_eq!(
            roles.iter().find(|p| p.role == PathRole::Anchor).unwrap().path.path_id,
            2
        );
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib health::`
Expected: FAIL — `no variant named Trial found for enum PathRole`, plus arity errors on `select_path_roles_with_state`.

- [ ] **Step 3: Write minimal implementation**

In `health.rs`, add the import at the top, after the existing `use serde::...` line:

```rust
use crate::anchor::{
    AnchorTrial, AnchorTrialConfig, AnchorTrialStatus, FlapDamping, FlapDampingConfig,
    PathScoreSmoothing, ScoreSmoothingConfig, TrialObservation, TrialOutcome,
};

use std::collections::BTreeMap;
```

Add the `Trial` variant to `PathRole` (keep the existing ones and their order):

```rust
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PathRole {
    Anchor,
    Backup,
    /// Carrying mirrored traffic to prove it can hold the anchor role.
    Trial,
    Probe,
    Cooldown,
    Unavailable,
}
```

Replace the `ScoredPath` struct with:

```rust
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ScoredPath {
    pub path: PathHealthSnapshot,
    /// Raw score for this tick. Used for hard demotion and under-load trial comparison.
    pub score: f64,
    /// EWMA of `score`.
    #[serde(default)]
    pub smoothed_score: f64,
    /// `smoothed_score` minus the instability penalty. This is what role selection ranks on.
    #[serde(default)]
    pub effective_score: f64,
    #[serde(default)]
    pub flap_penalty: f64,
    /// Present only on the path currently under trial.
    #[serde(default)]
    pub trial: Option<AnchorTrialStatus>,
    pub role: PathRole,
}
```

In `score_paths`, replace the `ScoredPath { path, score, role }` construction with:

```rust
            ScoredPath {
                path,
                score,
                smoothed_score: score,
                effective_score: score,
                flap_penalty: 0.0,
                trial: None,
                role,
            }
```

Replace `RoleSelectionConfig` and its `Default` impl with:

```rust
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RoleSelectionConfig {
    pub anchor_switch_score_margin: f64,
    pub backup_switch_score_margin: f64,
    pub stable_ticks_required: u8,
    pub smoothing: ScoreSmoothingConfig,
    pub flap: FlapDampingConfig,
    pub trial: AnchorTrialConfig,
}

impl Default for RoleSelectionConfig {
    fn default() -> Self {
        Self {
            // Raised from 150/5: five clean seconds is trivial for an intermittently
            // obstructed link to fake, which is what let the anchor flap.
            anchor_switch_score_margin: 200.0,
            backup_switch_score_margin: 100.0,
            stable_ticks_required: 10,
            smoothing: ScoreSmoothingConfig::default(),
            flap: FlapDampingConfig::default(),
            trial: AnchorTrialConfig::default(),
        }
    }
}
```

Replace `RoleSelectionState` (it can no longer derive `Default` because
`ticks_since_trial_end` must start high enough to allow the first trial):

```rust
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RoleSelectionState {
    pub anchor_path_id: Option<u16>,
    pub backup_path_ids: Vec<u16>,
    pub anchor_candidate_path_id: Option<u16>,
    pub anchor_candidate_ticks: u8,
    pub backup_candidate_path_ids: Vec<u16>,
    pub backup_candidate_ticks: u8,
    pub schedule_change_count: u64,
    #[serde(default)]
    pub smoothing: BTreeMap<u16, PathScoreSmoothing>,
    #[serde(default)]
    pub flap_damping: FlapDamping,
    #[serde(default)]
    pub trial: Option<AnchorTrial>,
    #[serde(default)]
    pub last_trial_outcome: Option<TrialOutcome>,
    #[serde(default)]
    pub ticks_since_trial_end: u32,
    #[serde(default)]
    pub trial_count: u64,
}

impl Default for RoleSelectionState {
    fn default() -> Self {
        Self {
            anchor_path_id: None,
            backup_path_ids: Vec::new(),
            anchor_candidate_path_id: None,
            anchor_candidate_ticks: 0,
            backup_candidate_path_ids: Vec::new(),
            backup_candidate_ticks: 0,
            schedule_change_count: 0,
            smoothing: BTreeMap::new(),
            flap_damping: FlapDamping::default(),
            trial: None,
            last_trial_outcome: None,
            // Start "long since" so the first legitimate upgrade is not delayed.
            ticks_since_trial_end: u32::MAX,
            trial_count: 0,
        }
    }
}
```

Finally, replace the body of `select_path_roles_with_state` with the version below. This
task only wires smoothing and the extra signature parameter; the trial and suppression
logic land in Task 5.

```rust
pub fn select_path_roles_with_state(
    paths: &[PathHealthSnapshot],
    max_backups: usize,
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    recovery_active: bool,
) -> Vec<ScoredPath> {
    let mut scored = score_paths(paths);
    let live_ids = scored
        .iter()
        .map(|path| path.path.path_id)
        .collect::<Vec<_>>();
    state.flap_damping.decay(config.flap.half_life_secs);
    state.flap_damping.retain_paths(&live_ids);
    apply_score_smoothing(&mut scored, state, config, &live_ids);

    let previous_anchor = state.anchor_path_id;
    let previous_backups = state.backup_path_ids.clone();
    let best_anchor_id = scored
        .iter()
        .find(|path| is_role_eligible(path))
        .map(path_id);
    let anchor_id = choose_anchor(&scored, state, best_anchor_id, config);
    let backup_ids = choose_backups(&scored, state, anchor_id, max_backups, config);

    let _ = recovery_active;
    assign_hysteresis_roles(&mut scored, anchor_id, &backup_ids);

    if previous_anchor != anchor_id || previous_backups != backup_ids {
        state.schedule_change_count = state.schedule_change_count.saturating_add(1);
    }
    state.anchor_path_id = anchor_id;
    state.backup_path_ids = backup_ids;

    scored
}

/// Updates the EWMA filters, publishes the derived scores onto `scored`, and re-sorts by
/// effective score so every downstream `find`/`take` picks the most *dependable* path.
fn apply_score_smoothing(
    scored: &mut [ScoredPath],
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    live_ids: &[u16],
) {
    state
        .smoothing
        .retain(|path_id, _| live_ids.contains(path_id));

    for scored_path in scored.iter_mut() {
        let smoothing = state
            .smoothing
            .entry(scored_path.path.path_id)
            .or_default();
        smoothing.observe(scored_path.score, config.smoothing.alpha);
        scored_path.smoothed_score = smoothing.smoothed;
        // A hard-demoted path keeps its sentinel score: a good history must never let a
        // dead interface outrank a live one.
        scored_path.effective_score = if scored_path.path.is_realtime_eligible() {
            smoothing.effective_score(config.smoothing.variance_penalty_weight)
        } else {
            scored_path.score
        };
        scored_path.flap_penalty = state.flap_damping.penalty(scored_path.path.path_id);
    }

    scored.sort_by(|a, b| b.effective_score.total_cmp(&a.effective_score));
}
```

Then change `choose_anchor` and `choose_backups` to rank on the effective score by
replacing every `score_for(...)` call and the `current_anchor.score` read with the
effective equivalents. Concretely, in `choose_anchor` replace:

```rust
    let best_score = score_for(scored, best_anchor_id).unwrap_or(f64::NEG_INFINITY);
    if best_score - current_anchor.score < config.anchor_switch_score_margin {
```

with:

```rust
    let best_score = effective_score_for(scored, best_anchor_id).unwrap_or(f64::NEG_INFINITY);
    if best_score - current_anchor.effective_score < config.anchor_switch_score_margin {
```

In `choose_backups` replace the two `score_for` calls:

```rust
    let weakest_current_score = current
        .iter()
        .filter_map(|id| effective_score_for(scored, *id))
        .min_by(f64::total_cmp)
        .unwrap_or(f64::NEG_INFINITY);
    let best_new_score = target
        .iter()
        .filter(|id| !current.contains(id))
        .filter_map(|id| effective_score_for(scored, *id))
        .max_by(f64::total_cmp)
        .unwrap_or(f64::NEG_INFINITY);
```

And replace the `score_for` helper with:

```rust
fn effective_score_for(scored: &[ScoredPath], path_id: u16) -> Option<f64> {
    scored
        .iter()
        .find(|path| path.path.path_id == path_id)
        .map(|path| path.effective_score)
}
```

Update the two existing hysteresis tests in `health.rs` that call
`select_path_roles_with_state` to pass the new `false` argument as the fifth parameter:
`hysteresis_holds_anchor_until_candidate_is_stable` and
`hysteresis_replaces_hard_demoted_anchor_immediately`.

Note: `hysteresis_holds_anchor_until_candidate_is_stable` asserts a switch happens after
`stable_ticks_required` ticks. With trials not yet wired (Task 5) it still passes; Task 5
updates it.

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core --lib`
Expected: PASS. Compilation of `xbond-client`/`xbond-server` is expected to fail at this
point because of the new parameter — that is fixed in Task 7. Verify only the core lib:
`cargo test -p xbond-core --lib` must be green.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/health.rs
git commit -m "feat(xbond): rank paths on smoothed effective score"
```

---

### Task 5: Suppression and trial-gated promotion in role selection

**Files:**
- Modify: `xbond/crates/xbond-core/src/health.rs`

- [ ] **Step 1: Write the failing test**

Add to the `tests` module in `health.rs`:

```rust
    fn tick(
        state: &mut RoleSelectionState,
        paths: &[PathHealthSnapshot],
        config: RoleSelectionConfig,
    ) -> Vec<ScoredPath> {
        select_path_roles_with_state(paths, 1, state, config, false)
    }

    fn anchor_of(roles: &[ScoredPath]) -> Option<u16> {
        roles
            .iter()
            .find(|path| path.role == PathRole::Anchor)
            .map(|path| path.path.path_id)
    }

    fn trial_of(roles: &[ScoredPath]) -> Option<u16> {
        roles
            .iter()
            .find(|path| path.role == PathRole::Trial)
            .map(|path| path.path.path_id)
    }

    /// Fresh state that already holds `anchor_path_id`, so tests exercise *switching*
    /// rather than the first-tick cold start (which always takes the best path outright).
    fn state_anchored_on(path_id: u16) -> RoleSelectionState {
        RoleSelectionState {
            anchor_path_id: Some(path_id),
            ..RoleSelectionState::default()
        }
    }

    #[test]
    fn clearing_hysteresis_starts_a_trial_instead_of_switching() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 100.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];

        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 1 {
            roles = tick(&mut state, &paths, config);
            assert_eq!(anchor_of(&roles), Some(1), "the anchor must not move");
        }

        assert_eq!(trial_of(&roles), Some(2));
        assert!(state.trial.is_some());
    }

    #[test]
    fn trial_that_stays_clean_under_load_wins_the_anchor() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let mut better = path(2, "better", 10.0, 0.0, 0.0);
        better.outbound_throughput_bps = 40_000_000;
        let paths = [path(1, "fiber", 100.0, 0.0, 0.0), better];

        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + config.trial.ticks + 2 {
            roles = tick(&mut state, &paths, config);
        }

        assert_eq!(anchor_of(&roles), Some(2));
        assert!(state.trial.is_none());
        assert_eq!(state.last_trial_outcome, Some(TrialOutcome::Promoted));
    }

    #[test]
    fn obstructed_candidate_fails_its_trial_and_gets_suppressed() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let anchor = path(1, "fiber", 100.0, 0.0, 0.0);
        let mut idle_starlink = path(2, "starlink", 10.0, 0.0, 0.0);
        idle_starlink.outbound_throughput_bps = 0;
        // Under mirrored load the obstruction shows up: heavy loss and latency. The
        // throughput figure matters — it is what makes the verdict FailedUnderLoad rather
        // than Inconclusive.
        let mut loaded_starlink = path(2, "starlink", 400.0, 0.30, 0.10);
        loaded_starlink.outbound_throughput_bps = 40_000_000;

        // Idle phase: Starlink looks great and earns a trial.
        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 1 {
            roles = tick(&mut state, &[anchor.clone(), idle_starlink.clone()], config);
        }
        assert_eq!(trial_of(&roles), Some(2));

        // Loaded phase: it degrades and the trial aborts.
        for _ in 0..config.trial.ticks + 1 {
            roles = tick(&mut state, &[anchor.clone(), loaded_starlink.clone()], config);
        }

        assert_eq!(anchor_of(&roles), Some(1), "anchor must never have moved");
        assert_eq!(state.last_trial_outcome, Some(TrialOutcome::FailedUnderLoad));
        assert!(state
            .flap_damping
            .is_suppressed(2, config.flap.suppress_threshold));
    }

    #[test]
    fn suppressed_path_cannot_start_another_trial() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        state
            .flap_damping
            .add(2, config.flap.penalty_demoted, config.flap.penalty_cap);
        let paths = [
            path(1, "fiber", 100.0, 0.0, 0.0),
            path(2, "suppressed", 10.0, 0.0, 0.0),
        ];

        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + config.trial.ticks + 5 {
            roles = tick(&mut state, &paths, config);
        }

        assert_eq!(anchor_of(&roles), Some(1));
        assert_eq!(trial_of(&roles), None);
        assert!(state.trial.is_none());
    }

    #[test]
    fn suppressed_path_still_takes_over_when_the_anchor_dies() {
        // Suppression gates upgrades only. Failover must never be blocked.
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        state
            .flap_damping
            .add(2, config.flap.penalty_cap, config.flap.penalty_cap);
        let mut dead = path(1, "fiber", 100.0, 0.0, 0.0);
        dead.interface_up = false;

        let roles = tick(
            &mut state,
            &[dead, path(2, "suppressed", 10.0, 0.0, 0.0)],
            config,
        );

        assert_eq!(anchor_of(&roles), Some(2));
    }

    #[test]
    fn losing_the_anchor_role_charges_a_flap_penalty() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let mut dead = path(1, "fiber", 100.0, 0.0, 0.0);
        dead.interface_up = false;

        tick(&mut state, &[dead, path(2, "other", 10.0, 0.0, 0.0)], config);

        assert_eq!(state.flap_damping.penalty(1), config.flap.penalty_demoted);
    }

    #[test]
    fn recovery_mode_promotes_directly_without_a_trial() {
        // Everything is duplicated during recovery, so a switch is already masked.
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 100.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];

        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 1 {
            roles = select_path_roles_with_state(&paths, 1, &mut state, config, true);
        }

        assert_eq!(anchor_of(&roles), Some(2));
        assert_eq!(trial_of(&roles), None);
    }

    #[test]
    fn recovery_cancels_an_in_flight_trial_without_penalty() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 100.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];
        for _ in 0..config.stable_ticks_required + 1 {
            tick(&mut state, &paths, config);
        }
        assert!(state.trial.is_some());

        select_path_roles_with_state(&paths, 1, &mut state, config, true);

        assert!(state.trial.is_none());
        assert_eq!(state.last_trial_outcome, Some(TrialOutcome::Cancelled));
        assert_eq!(state.flap_damping.penalty(2), 0.0);
    }

    #[test]
    fn trial_spacing_delays_the_next_trial() {
        let config = RoleSelectionConfig::default();
        let mut state = RoleSelectionState {
            ticks_since_trial_end: 0,
            ..state_anchored_on(1)
        };
        let paths = [
            path(1, "fiber", 100.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];

        for _ in 0..config.stable_ticks_required + 1 {
            tick(&mut state, &paths, config);
        }
        assert!(state.trial.is_none(), "spacing window must block the trial");

        for _ in 0..config.trial.min_interval_ticks {
            tick(&mut state, &paths, config);
        }
        assert!(state.trial.is_some());
    }

    #[test]
    fn suppressed_probe_explains_itself() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        state
            .flap_damping
            .add(2, config.flap.penalty_demoted, config.flap.penalty_cap);

        let roles = tick(
            &mut state,
            &[
                path(1, "fiber", 100.0, 0.0, 0.0),
                path(2, "suppressed", 10.0, 0.0, 0.0),
            ],
            config,
        );

        let suppressed = roles.iter().find(|p| p.path.path_id == 2).unwrap();
        assert_eq!(suppressed.role, PathRole::Probe);
        let reason = suppressed.path.role_reason.as_deref().unwrap();
        assert!(reason.contains("Suppressed"), "unexpected reason: {reason}");
    }

    #[test]
    fn trial_path_reports_progress_in_its_role_reason() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let paths = [
            path(1, "fiber", 100.0, 0.0, 0.0),
            path(2, "better", 10.0, 0.0, 0.0),
        ];
        let mut roles = Vec::new();
        for _ in 0..config.stable_ticks_required + 2 {
            roles = tick(&mut state, &paths, config);
        }

        let trial = roles.iter().find(|p| p.role == PathRole::Trial).unwrap();
        assert!(trial.trial.is_some());
        let reason = trial.path.role_reason.as_deref().unwrap();
        assert!(reason.contains("trial"), "unexpected reason: {reason}");
    }
```

Also update the existing `hysteresis_holds_anchor_until_candidate_is_stable` test: with
trials enabled the anchor no longer switches when hysteresis expires. Replace its final
assertion block (the one asserting the anchor became path 2) with:

```rust
        let roles = select_path_roles_with_state(
            &[
                path(1, "current", 100.0, 0.0, 0.0),
                path(2, "better", 10.0, 0.0, 0.0),
                path(3, "backup", 90.0, 0.0, 0.0),
            ],
            1,
            &mut state,
            config,
            false,
        );

        // Passing hysteresis now buys a trial, not the anchor role.
        assert_eq!(
            roles
                .iter()
                .find(|path| path.role == PathRole::Anchor)
                .unwrap()
                .path
                .path_id,
            1
        );
        assert_eq!(
            roles
                .iter()
                .find(|path| path.role == PathRole::Trial)
                .unwrap()
                .path
                .path_id,
            2
        );
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib health::`
Expected: FAIL — `clearing_hysteresis_starts_a_trial_instead_of_switching` fails with
`assertion left == right` (trial is `None`), plus the other new trial tests fail.

- [ ] **Step 3: Write minimal implementation**

In `health.rs`, **delete the whole `choose_anchor` function** — `resolve_anchor` below
replaces it, and leaving it in place is dead code that fails
`cargo clippy -- -D warnings`. Then replace the whole body of
`select_path_roles_with_state` (from Task 4) with this trial-aware version, and add the
helper functions below it:

```rust
pub fn select_path_roles_with_state(
    paths: &[PathHealthSnapshot],
    max_backups: usize,
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    recovery_active: bool,
) -> Vec<ScoredPath> {
    let mut scored = score_paths(paths);
    let live_ids = scored
        .iter()
        .map(|path| path.path.path_id)
        .collect::<Vec<_>>();
    state.flap_damping.decay(config.flap.half_life_secs);
    state.flap_damping.retain_paths(&live_ids);
    apply_score_smoothing(&mut scored, state, config, &live_ids);

    let previous_anchor = state.anchor_path_id;
    let previous_backups = state.backup_path_ids.clone();

    let anchor_id = resolve_anchor(&scored, state, config, recovery_active);
    let backup_ids = choose_backups(&scored, state, anchor_id, max_backups, config);
    let trial_path_id = state.trial.as_ref().map(|trial| trial.path_id);

    assign_hysteresis_roles(&mut scored, anchor_id, &backup_ids, trial_path_id, state, config);

    if previous_anchor != anchor_id {
        // Being displaced costs the outgoing anchor some trust, which is what stops
        // A -> B -> A ping-ponging.
        if let Some(outgoing) = previous_anchor {
            state.flap_damping.add(
                outgoing,
                config.flap.penalty_demoted,
                config.flap.penalty_cap,
            );
        }
    }
    if previous_anchor != anchor_id || previous_backups != backup_ids {
        state.schedule_change_count = state.schedule_change_count.saturating_add(1);
    }
    state.anchor_path_id = anchor_id;
    state.backup_path_ids = backup_ids;

    scored
}

/// Decides the anchor for this tick, advancing or starting a prove-it trial as needed.
fn resolve_anchor(
    scored: &[ScoredPath],
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    recovery_active: bool,
) -> Option<u16> {
    state.ticks_since_trial_end = state.ticks_since_trial_end.saturating_add(1);

    let best_promotable_id = scored
        .iter()
        .find(|path| {
            is_role_eligible(path)
                && !state
                    .flap_damping
                    .is_suppressed(path.path.path_id, config.flap.suppress_threshold)
        })
        .map(path_id);
    // Failover fallback: when every healthy path is suppressed we still need an anchor.
    let best_any_id = scored
        .iter()
        .find(|path| is_role_eligible(path))
        .map(path_id);

    let current_anchor = state
        .anchor_path_id
        .and_then(|id| scored.iter().find(|path| path.path.path_id == id))
        .filter(|path| is_role_eligible(path));

    // The anchor is gone: fail over now and abandon any trial (the trial path did nothing
    // wrong, so no penalty).
    let Some(current_anchor) = current_anchor else {
        end_trial(state, TrialOutcome::Cancelled);
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return best_promotable_id.or(best_any_id);
    };
    let current_anchor_id = current_anchor.path.path_id;

    if let Some(trial) = state.trial.clone() {
        if recovery_active {
            end_trial(state, TrialOutcome::Cancelled);
            return Some(current_anchor_id);
        }
        return Some(advance_trial(scored, state, config, trial, current_anchor));
    }

    let Some(best_promotable_id) = best_promotable_id else {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    };

    if current_anchor_id == best_promotable_id {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    }

    let best_score =
        effective_score_for(scored, best_promotable_id).unwrap_or(f64::NEG_INFINITY);
    if best_score - current_anchor.effective_score < config.anchor_switch_score_margin {
        state.anchor_candidate_path_id = None;
        state.anchor_candidate_ticks = 0;
        return Some(current_anchor_id);
    }

    if state.anchor_candidate_path_id == Some(best_promotable_id) {
        state.anchor_candidate_ticks = state.anchor_candidate_ticks.saturating_add(1);
    } else {
        state.anchor_candidate_path_id = Some(best_promotable_id);
        state.anchor_candidate_ticks = 1;
    }

    if state.anchor_candidate_ticks < config.stable_ticks_required {
        return Some(current_anchor_id);
    }

    state.anchor_candidate_path_id = None;
    state.anchor_candidate_ticks = 0;

    // Recovery duplicates everything, so a switch there is already masked and needs no trial.
    if !config.trial.enabled || recovery_active {
        return Some(best_promotable_id);
    }

    if state.ticks_since_trial_end < config.trial.min_interval_ticks {
        return Some(current_anchor_id);
    }

    state.trial = Some(AnchorTrial::new(best_promotable_id));
    state.trial_count = state.trial_count.saturating_add(1);
    Some(current_anchor_id)
}

/// Feeds one tick of evidence into the running trial and applies its verdict.
fn advance_trial(
    scored: &[ScoredPath],
    state: &mut RoleSelectionState,
    config: RoleSelectionConfig,
    mut trial: AnchorTrial,
    current_anchor: &ScoredPath,
) -> u16 {
    let current_anchor_id = current_anchor.path.path_id;
    let trial_path = scored
        .iter()
        .find(|path| path.path.path_id == trial.path_id);
    let observation = TrialObservation {
        eligible: trial_path.is_some_and(|path| is_role_eligible(path)),
        // Raw scores, because both paths are carrying the same mirrored load right now.
        kept_up_with_anchor: trial_path.is_some_and(|path| path.score >= current_anchor.score),
        mirrored_bytes_this_tick: trial_path
            .map(|path| path.path.outbound_throughput_bps / 8)
            .unwrap_or_default(),
    };

    let outcome = trial.observe(observation, config.trial);
    match outcome {
        TrialOutcome::Running => {
            state.trial = Some(trial);
            current_anchor_id
        }
        TrialOutcome::Promoted => {
            let promoted = trial.path_id;
            end_trial(state, outcome);
            promoted
        }
        TrialOutcome::FailedUnderLoad => {
            state.flap_damping.add(
                trial.path_id,
                config.flap.penalty_trial_failed,
                config.flap.penalty_cap,
            );
            end_trial(state, outcome);
            current_anchor_id
        }
        TrialOutcome::Inconclusive | TrialOutcome::Cancelled => {
            end_trial(state, outcome);
            current_anchor_id
        }
    }
}

fn end_trial(state: &mut RoleSelectionState, outcome: TrialOutcome) {
    if state.trial.is_none() {
        return;
    }
    state.trial = None;
    state.last_trial_outcome = Some(outcome);
    state.ticks_since_trial_end = 0;
}
```

Replace `assign_hysteresis_roles` with the trial- and suppression-aware version:

```rust
fn assign_hysteresis_roles(
    scored: &mut [ScoredPath],
    anchor_id: Option<u16>,
    backup_ids: &[u16],
    trial_path_id: Option<u16>,
    state: &RoleSelectionState,
    config: RoleSelectionConfig,
) {
    for scored_path in scored {
        if !is_role_eligible(scored_path) {
            if scored_path.path.role_reason.is_none() {
                scored_path.path.role_reason = scored_path.path.demotion_reason.clone();
            }
            continue;
        }

        let id = scored_path.path.path_id;
        if Some(id) == anchor_id {
            scored_path.role = PathRole::Anchor;
            scored_path.path.role_reason =
                Some("Selected as stable anchor by hysteresis scheduler.".to_string());
        } else if Some(id) == trial_path_id {
            let status = state
                .trial
                .as_ref()
                .map(|trial| trial.status(config.trial));
            scored_path.role = PathRole::Trial;
            scored_path.path.role_reason = Some(match &status {
                Some(status) => format!(
                    "On trial as anchor candidate: {}/{} ticks, {}/{} clean under load.",
                    status.ticks,
                    status.required_ticks,
                    status.success_ticks,
                    status.required_success_ticks
                ),
                None => "On trial as anchor candidate.".to_string(),
            });
            scored_path.trial = status;
        } else if backup_ids.contains(&id) {
            scored_path.role = PathRole::Backup;
            scored_path.path.role_reason = Some("Selected as stable redundant backup.".to_string());
        } else if state
            .flap_damping
            .is_suppressed(id, config.flap.suppress_threshold)
        {
            let penalty = state.flap_damping.penalty(id);
            let eta = state.flap_damping.seconds_until_clear(
                id,
                config.flap.suppress_threshold,
                config.flap.half_life_secs,
            );
            scored_path.role = PathRole::Probe;
            scored_path.path.role_reason = Some(format!(
                "Suppressed after anchor failure (penalty {:.0}, eligible again in ~{}).",
                penalty,
                format_suppression_eta(eta)
            ));
        } else {
            scored_path.role = PathRole::Probe;
            scored_path.path.role_reason =
                Some("Healthy but currently kept as probe/standby.".to_string());
        }
    }
}

fn format_suppression_eta(seconds: u64) -> String {
    if seconds >= 60 {
        format!("{}m", (seconds + 59) / 60)
    } else {
        format!("{seconds}s")
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core --lib`
Expected: PASS, all `anchor::` and `health::` tests green.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/health.rs
git commit -m "feat(xbond): gate anchor promotion behind load trials and flap suppression"
```

---

### Task 6: Mirror real traffic onto the trial path

**Files:**
- Modify: `xbond/crates/xbond-core/src/scheduler.rs`

- [ ] **Step 1: Write the failing test**

Add to the `tests` module in `scheduler.rs`:

```rust
    fn trial_schedule() -> SchedulePlan {
        SchedulePlan {
            mode: ScheduleMode::AnchorDuplicate1,
            anchor_path_id: Some(1),
            data_path_ids: vec![1],
            duplicate_path_ids: vec![2],
            fec_path_ids: Vec::new(),
            trial_path_ids: vec![3],
        }
    }

    #[test]
    fn build_schedule_collects_trial_paths_from_roles() {
        let mut roles = select_path_roles(&[path(1, 20.0, 0.0), path(2, 60.0, 0.0)], 1);
        roles[1].role = PathRole::Trial;

        let plan = build_schedule(ScheduleMode::AnchorDuplicate1, &roles);

        assert_eq!(plan.anchor_path_id, Some(1));
        assert_eq!(plan.data_path_ids, vec![1]);
        assert_eq!(plan.trial_path_ids, vec![2]);
        assert!(plan.duplicate_path_ids.is_empty(), "a trial path is not a backup");
    }

    #[test]
    fn trial_path_is_mirrored_as_duplicate() {
        let transmissions = build_transmission_plan(&trial_schedule());

        assert_eq!(
            transmissions,
            vec![
                ScheduledTransmission { path_id: 1, packet_kind: PacketKind::Data },
                ScheduledTransmission { path_id: 2, packet_kind: PacketKind::Duplicate },
                ScheduledTransmission { path_id: 3, packet_kind: PacketKind::Duplicate },
            ]
        );
    }

    #[test]
    fn trial_path_is_never_mirrored_twice() {
        let mut plan = trial_schedule();
        plan.trial_path_ids = vec![2, 3];

        let transmissions = build_transmission_plan(&plan);

        assert_eq!(
            transmissions
                .iter()
                .filter(|transmission| transmission.path_id == 2)
                .count(),
            1
        );
    }

    #[test]
    fn fast_policy_mirrors_bulk_onto_the_trial_path_even_when_healthy() {
        // This is the whole point of the trial: fast/balanced normally send bulk on the
        // anchor alone, so without this the candidate would never see real load.
        let health = vec![path(1, 20.0, 0.0), path(2, 60.0, 0.0), path(3, 30.0, 0.0)];

        let transmissions = build_transmission_plan_for_packet(
            &trial_schedule(),
            RedundancyPolicy::Fast,
            1_200,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert_eq!(
            transmissions,
            vec![
                ScheduledTransmission { path_id: 1, packet_kind: PacketKind::Data },
                ScheduledTransmission { path_id: 3, packet_kind: PacketKind::Duplicate },
            ]
        );
    }

    #[test]
    fn balanced_policy_mirrors_small_packets_onto_the_trial_path() {
        let health = vec![path(1, 20.0, 0.0), path(2, 60.0, 0.0), path(3, 30.0, 0.0)];

        let plans = precompute_transmission_plans(
            &trial_schedule(),
            RedundancyPolicy::Balanced,
            &health,
            RedundancyPolicyConfig::default(),
        );

        for plan in [&plans.small, &plans.bulk] {
            assert!(
                plan.iter().any(|transmission| transmission.path_id == 3
                    && transmission.packet_kind == PacketKind::Duplicate),
                "trial path missing from plan: {plan:?}"
            );
        }
    }

    #[test]
    fn down_trial_path_is_not_mirrored() {
        let mut trial_path = path(3, 30.0, 0.0);
        trial_path.interface_up = false;
        let health = vec![path(1, 20.0, 0.0), path(2, 60.0, 0.0), trial_path];

        let transmissions = build_transmission_plan_for_packet(
            &trial_schedule(),
            RedundancyPolicy::Fast,
            1_200,
            &health,
            RedundancyPolicyConfig::default(),
        );

        assert!(transmissions.iter().all(|transmission| transmission.path_id != 3));
    }

    #[test]
    fn schedule_plan_without_trial_ids_still_deserialises() {
        let json = r#"{
            "mode": "anchor-duplicate-1",
            "anchor_path_id": 1,
            "data_path_ids": [1],
            "duplicate_path_ids": [2],
            "fec_path_ids": []
        }"#;

        let plan = serde_json::from_str::<SchedulePlan>(json).unwrap();

        assert!(plan.trial_path_ids.is_empty());
    }
```

Add `PathRole` to the test module's imports:

```rust
    use crate::health::{select_path_roles, PathHealthSnapshot, PathRole};
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib scheduler::`
Expected: FAIL — `struct SchedulePlan has no field named trial_path_ids`.

- [ ] **Step 3: Write minimal implementation**

In `scheduler.rs`, add the field to `SchedulePlan`:

```rust
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SchedulePlan {
    pub mode: ScheduleMode,
    pub anchor_path_id: Option<u16>,
    pub data_path_ids: Vec<u16>,
    pub duplicate_path_ids: Vec<u16>,
    pub fec_path_ids: Vec<u16>,
    /// Anchor candidate under load test. Mirrored as duplicates in every policy so the
    /// candidate is judged on real traffic. Defaulted for wire compatibility.
    #[serde(default)]
    pub trial_path_ids: Vec<u16>,
}
```

Replace `build_schedule` with:

```rust
pub fn build_schedule(mode: ScheduleMode, roles: &[ScoredPath]) -> SchedulePlan {
    let anchor = roles
        .iter()
        .find(|path| path.role == PathRole::Anchor)
        .map(|path| path.path.path_id);
    let backups: Vec<u16> = roles
        .iter()
        .filter(|path| path.role == PathRole::Backup)
        .map(|path| path.path.path_id)
        .collect();
    let trials: Vec<u16> = roles
        .iter()
        .filter(|path| path.role == PathRole::Trial)
        .map(|path| path.path.path_id)
        .collect();

    match mode {
        ScheduleMode::AnchorOnly => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: Vec::new(),
            fec_path_ids: Vec::new(),
            trial_path_ids: trials,
        },
        ScheduleMode::AnchorDuplicate1 => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: backups.into_iter().take(1).collect(),
            fec_path_ids: Vec::new(),
            trial_path_ids: trials,
        },
        ScheduleMode::AnchorFec => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: Vec::new(),
            fec_path_ids: backups,
            trial_path_ids: trials,
        },
        ScheduleMode::FullDuplicateDebug => SchedulePlan {
            mode,
            anchor_path_id: anchor,
            data_path_ids: anchor.into_iter().collect(),
            duplicate_path_ids: backups,
            fec_path_ids: Vec::new(),
            trial_path_ids: trials,
        },
    }
}
```

In `build_transmission_plan`, append the trial mirror after the duplicate loop and before
the FEC loop:

```rust
    for path_id in &schedule.trial_path_ids {
        if schedule.data_path_ids.contains(path_id)
            || schedule.duplicate_path_ids.contains(path_id)
            || schedule.fec_path_ids.contains(path_id)
        {
            continue;
        }
        transmissions.push(ScheduledTransmission {
            path_id: *path_id,
            packet_kind: crate::protocol::PacketKind::Duplicate,
        });
    }
```

In `build_transmission_plan_for_packet`, change the `plan` construction to carry healthy
trial paths:

```rust
    let mut plan = SchedulePlan {
        mode: schedule.mode,
        anchor_path_id: schedule.anchor_path_id,
        data_path_ids: schedule.data_path_ids.clone(),
        duplicate_path_ids: Vec::new(),
        fec_path_ids: Vec::new(),
        trial_path_ids: healthy_trial_path_ids(schedule, paths),
    };
```

And add the helper next to the other private helpers in `scheduler.rs`:

```rust
/// Trial mirroring is unconditional across policies, but a trial path that just lost
/// carrier must not be handed packets.
fn healthy_trial_path_ids(schedule: &SchedulePlan, paths: &[PathHealthSnapshot]) -> Vec<u16> {
    schedule
        .trial_path_ids
        .iter()
        .copied()
        .filter(|path_id| {
            paths
                .iter()
                .find(|path| path.path_id == *path_id)
                .is_some_and(|path| path.interface_up && !path.in_cooldown)
        })
        .collect()
}
```

In `expand_schedule_for_recovery`, preserve trial ids in the returned plan:

```rust
    SchedulePlan {
        mode: schedule.mode,
        anchor_path_id: schedule.anchor_path_id,
        data_path_ids: schedule.data_path_ids.clone(),
        duplicate_path_ids,
        fec_path_ids: Vec::new(),
        trial_path_ids: schedule.trial_path_ids.clone(),
    }
```

In `stabilize_recovery_schedule`, do the same for its returned plan:

```rust
    SchedulePlan {
        mode: schedule.mode,
        anchor_path_id: schedule.anchor_path_id,
        data_path_ids: schedule.data_path_ids.clone(),
        duplicate_path_ids: state.stable_duplicate_path_ids.clone(),
        fec_path_ids: schedule.fec_path_ids.clone(),
        trial_path_ids: schedule.trial_path_ids.clone(),
    }
```

Then fix the remaining `SchedulePlan { .. }` literals in the existing `scheduler.rs` tests
by adding `trial_path_ids: Vec::new(),` to each
(`duplicate_transmission_plan_marks_anchor_data_and_backup_duplicate` and
`fec_transmission_plan_is_explicit_about_fec_paths`).

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core`
Expected: PASS, all core tests green.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/scheduler.rs
git commit -m "feat(xbond): mirror bulk traffic onto anchor trial paths in every policy"
```

---

### Task 7: Client config table and call-site wiring

**Files:**
- Modify: `xbond/crates/xbond-core/src/config.rs`
- Modify: `xbond/crates/xbond-client/src/main.rs:2825`, `:3096`
- Modify: `xbond/examples/client.example.toml`

- [ ] **Step 1: Write the failing test**

Add to the bottom of `config.rs` (create the `tests` module if the file has none):

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_role_selection_table_uses_sticky_defaults() {
        let config = ClientConfig::default();
        let role_selection = config.role_selection_config();

        assert_eq!(role_selection.anchor_switch_score_margin, 200.0);
        assert_eq!(role_selection.stable_ticks_required, 10);
        assert!(role_selection.trial.enabled);
        assert_eq!(role_selection.trial.ticks, 20);
    }

    #[test]
    fn role_selection_table_overrides_defaults() {
        let toml = r#"
enabled = true
server_addr = "127.0.0.1:8444"
mode = "anchor-duplicate-1"
max_active_backups = 1
realtime_deadline_ms = 500
paths = []

[role_selection]
anchor_switch_score_margin = 350.0
stable_ticks_required = 20
trial_enabled = false
trial_ticks = 45
flap_half_life_secs = 600
"#;

        let config = toml::from_str::<ClientConfig>(toml).unwrap();
        let role_selection = config.role_selection_config();

        assert_eq!(role_selection.anchor_switch_score_margin, 350.0);
        assert_eq!(role_selection.stable_ticks_required, 20);
        assert!(!role_selection.trial.enabled);
        assert_eq!(role_selection.trial.ticks, 45);
        assert_eq!(role_selection.flap.half_life_secs, 600);
    }

    #[test]
    fn role_selection_values_are_clamped_to_sane_ranges() {
        let settings = RoleSelectionSettings {
            smoothing_alpha: 9.0,
            stable_ticks_required: 0,
            trial_success_ticks: 999,
            trial_ticks: 10,
            ..RoleSelectionSettings::default()
        };
        let config = ClientConfig {
            role_selection: settings,
            ..ClientConfig::default()
        }
        .role_selection_config();

        assert_eq!(config.smoothing.alpha, 1.0);
        assert_eq!(config.stable_ticks_required, 1);
        // success_ticks can never exceed the window length.
        assert_eq!(config.trial.effective_success_ticks(), 10);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib config::`
Expected: FAIL — `no method named role_selection_config found for struct ClientConfig`.

- [ ] **Step 3: Write minimal implementation**

In `config.rs`, extend the imports:

```rust
use crate::anchor::{AnchorTrialConfig, FlapDampingConfig, ScoreSmoothingConfig};
use crate::health::RoleSelectionConfig;
use crate::scheduler::{RecoveryConfig, RedundancyPolicy, ScheduleMode};
```

Add the settings struct after `PathConfig`:

```rust
/// Operator-facing `[role_selection]` knobs. Every field is defaulted, so an existing
/// config file without the table keeps working and picks up the new sticky defaults.
#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct RoleSelectionSettings {
    #[serde(default = "default_anchor_switch_score_margin")]
    pub anchor_switch_score_margin: f64,
    #[serde(default = "default_backup_switch_score_margin")]
    pub backup_switch_score_margin: f64,
    #[serde(default = "default_stable_ticks_required")]
    pub stable_ticks_required: u8,
    #[serde(default = "default_smoothing_alpha")]
    pub smoothing_alpha: f64,
    #[serde(default = "default_variance_penalty_weight")]
    pub variance_penalty_weight: f64,
    #[serde(default = "default_flap_penalty_demoted")]
    pub flap_penalty_demoted: f64,
    #[serde(default = "default_flap_penalty_trial_failed")]
    pub flap_penalty_trial_failed: f64,
    #[serde(default = "default_flap_suppress_threshold")]
    pub flap_suppress_threshold: f64,
    #[serde(default = "default_flap_penalty_cap")]
    pub flap_penalty_cap: f64,
    #[serde(default = "default_flap_half_life_secs")]
    pub flap_half_life_secs: u64,
    #[serde(default = "default_trial_enabled")]
    pub trial_enabled: bool,
    #[serde(default = "default_trial_ticks")]
    pub trial_ticks: u32,
    #[serde(default = "default_trial_success_ticks")]
    pub trial_success_ticks: u32,
    #[serde(default = "default_trial_min_bytes")]
    pub trial_min_bytes: u64,
    #[serde(default = "default_trial_min_interval_ticks")]
    pub trial_min_interval_ticks: u32,
}

impl Default for RoleSelectionSettings {
    fn default() -> Self {
        Self {
            anchor_switch_score_margin: default_anchor_switch_score_margin(),
            backup_switch_score_margin: default_backup_switch_score_margin(),
            stable_ticks_required: default_stable_ticks_required(),
            smoothing_alpha: default_smoothing_alpha(),
            variance_penalty_weight: default_variance_penalty_weight(),
            flap_penalty_demoted: default_flap_penalty_demoted(),
            flap_penalty_trial_failed: default_flap_penalty_trial_failed(),
            flap_suppress_threshold: default_flap_suppress_threshold(),
            flap_penalty_cap: default_flap_penalty_cap(),
            flap_half_life_secs: default_flap_half_life_secs(),
            trial_enabled: default_trial_enabled(),
            trial_ticks: default_trial_ticks(),
            trial_success_ticks: default_trial_success_ticks(),
            trial_min_bytes: default_trial_min_bytes(),
            trial_min_interval_ticks: default_trial_min_interval_ticks(),
        }
    }
}

fn default_anchor_switch_score_margin() -> f64 {
    200.0
}

fn default_backup_switch_score_margin() -> f64 {
    100.0
}

fn default_stable_ticks_required() -> u8 {
    10
}

fn default_smoothing_alpha() -> f64 {
    0.2
}

fn default_variance_penalty_weight() -> f64 {
    2.0
}

fn default_flap_penalty_demoted() -> f64 {
    1_000.0
}

fn default_flap_penalty_trial_failed() -> f64 {
    600.0
}

fn default_flap_suppress_threshold() -> f64 {
    500.0
}

fn default_flap_penalty_cap() -> f64 {
    4_000.0
}

fn default_flap_half_life_secs() -> u64 {
    300
}

fn default_trial_enabled() -> bool {
    true
}

fn default_trial_ticks() -> u32 {
    20
}

fn default_trial_success_ticks() -> u32 {
    15
}

fn default_trial_min_bytes() -> u64 {
    5_000_000
}

fn default_trial_min_interval_ticks() -> u32 {
    60
}
```

Add the field to `ClientConfig`, just before `runtime_status_path`:

```rust
    #[serde(default)]
    pub role_selection: RoleSelectionSettings,
```

And to `ClientConfig::default()`, before `runtime_status_path`:

```rust
            role_selection: RoleSelectionSettings::default(),
```

Add the accessor to the `impl ClientConfig` block, next to `recovery_config`:

```rust
    pub fn role_selection_config(&self) -> RoleSelectionConfig {
        let settings = self.role_selection;
        let trial_ticks = settings.trial_ticks.max(1);
        RoleSelectionConfig {
            anchor_switch_score_margin: settings.anchor_switch_score_margin.max(0.0),
            backup_switch_score_margin: settings.backup_switch_score_margin.max(0.0),
            stable_ticks_required: settings.stable_ticks_required.max(1),
            smoothing: ScoreSmoothingConfig {
                alpha: settings.smoothing_alpha.clamp(0.01, 1.0),
                variance_penalty_weight: settings.variance_penalty_weight.max(0.0),
            },
            flap: FlapDampingConfig {
                penalty_demoted: settings.flap_penalty_demoted.max(0.0),
                penalty_trial_failed: settings.flap_penalty_trial_failed.max(0.0),
                suppress_threshold: settings.flap_suppress_threshold.max(0.0),
                penalty_cap: settings.flap_penalty_cap.max(0.0),
                half_life_secs: settings.flap_half_life_secs.max(1),
            },
            trial: AnchorTrialConfig {
                enabled: settings.trial_enabled,
                ticks: trial_ticks,
                success_ticks: settings.trial_success_ticks.min(trial_ticks),
                min_bytes: settings.trial_min_bytes,
                min_interval_ticks: settings.trial_min_interval_ticks,
            },
        }
    }
```

Export it from `lib.rs` by adding `RoleSelectionSettings` to the existing `config::` re-export list.

In `xbond-client/src/main.rs`, replace line 2820:

```rust
    let role_config = config.role_selection_config();
```

Replace the initial selection call at line 2825 (add the fifth argument):

```rust
    let mut roles = select_path_roles_with_state(
        &health,
        config.max_active_backups,
        &mut role_state,
        role_config,
        false,
    );
```

Replace the in-loop call at line 3096 (pass the previous tick's recovery state — it is
reassigned a few lines below, so at this point it still holds the prior value):

```rust
                roles = select_path_roles_with_state(
                    &health,
                    config.max_active_backups,
                    &mut role_state,
                    role_config,
                    recovery_status.active,
                );
```

In `xbond/examples/client.example.toml`, append:

```toml
# Anchor stability. Defaults are sticky on purpose: an anchor switch moves all bulk
# traffic in balanced/fast, so a candidate must beat the anchor by a wide margin, stay
# ahead for stable_ticks_required ticks, then survive a mirrored-load trial.
[role_selection]
anchor_switch_score_margin = 200.0
backup_switch_score_margin = 100.0
stable_ticks_required = 10
smoothing_alpha = 0.2
variance_penalty_weight = 2.0
flap_penalty_demoted = 1000.0
flap_penalty_trial_failed = 600.0
flap_suppress_threshold = 500.0
flap_penalty_cap = 4000.0
flap_half_life_secs = 300
trial_enabled = true
trial_ticks = 20
trial_success_ticks = 15
trial_min_bytes = 5000000
trial_min_interval_ticks = 60
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core --lib config::`
Expected: PASS, 3 tests.

Run: `cargo build --workspace`
Expected: success — the client call sites now match the new signature.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/config.rs xbond/crates/xbond-core/src/lib.rs xbond/crates/xbond-client/src/main.rs xbond/examples/client.example.toml
git commit -m "feat(xbond): expose role selection tuning in client config"
```

---

### Task 8: Surface the new signals in client status

**Files:**
- Modify: `xbond/crates/xbond-core/src/status.rs:43-160`

- [ ] **Step 1: Write the failing test**

Add to the bottom of `status.rs` (create a `tests` module if absent):

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crate::anchor::AnchorTrialStatus;
    use crate::health::{PathHealthSnapshot, PathRole, ScoredPath};

    fn snapshot() -> PathHealthSnapshot {
        PathHealthSnapshot {
            path_id: 2,
            name: "starlink".to_string(),
            interface_name: Some("enx1".to_string()),
            rtt_ms: Some(30.0),
            jitter_ms: Some(5.0),
            loss_rate: 0.0,
            late_rate: 0.0,
            queue_depth: 0,
            outbound_throughput_bps: 1_000_000,
            inbound_throughput_bps: 1_000_000,
            duplicate_inbound_throughput_bps: 0,
            raw_inbound_throughput_bps: 1_000_000,
            throughput_bps: 2_000_000,
            interface_up: true,
            in_cooldown: false,
            send_failure_streak: 0,
            stale_ack_ms: None,
            queue_pressure: 0.0,
            duplicate_usefulness: 1.0,
            throughput_collapse_score: 0.0,
            demotion_reason: None,
            role_reason: None,
            socket_generation: 0,
            socket_ifindex: None,
            socket_bind_addr: None,
            last_socket_error: None,
            last_rebind_reason: None,
            last_rebind_error: None,
            last_rebind_at_micros: None,
            rebind_count: 0,
            heartbeat_sent: 0,
            heartbeat_acked: 0,
            heartbeat_expired: 0,
            heartbeat_late_acks: 0,
            heartbeat_rebind_discarded: 0,
            pending_probes: 0,
            heartbeat_sample_count: 0,
            heartbeat_consecutive_misses: 0,
            heartbeat_consecutive_successes: 0,
            heartbeat_warming_up: false,
            heartbeat_failed: false,
        }
    }

    #[test]
    fn path_status_carries_anchor_stability_fields() {
        let scored = ScoredPath {
            path: snapshot(),
            score: 700.0,
            smoothed_score: 690.0,
            effective_score: 640.0,
            flap_penalty: 612.5,
            trial: Some(AnchorTrialStatus {
                path_id: 2,
                ticks: 5,
                success_ticks: 4,
                mirrored_bytes: 9_000_000,
                required_ticks: 20,
                required_success_ticks: 15,
            }),
            role: PathRole::Trial,
        };

        let status = XBondPathStatus::from(scored);

        assert_eq!(status.role, PathRole::Trial);
        assert_eq!(status.smoothed_score, 690.0);
        assert_eq!(status.effective_score, 640.0);
        assert_eq!(status.flap_penalty, 612.5);
        assert_eq!(status.trial.as_ref().unwrap().success_ticks, 4);

        let json = serde_json::to_string(&status).unwrap();
        assert!(json.contains("\"role\":\"trial\""), "{json}");
        assert!(json.contains("\"effective_score\":640.0"), "{json}");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib status::`
Expected: FAIL — `struct XBondPathStatus has no field named smoothed_score`.

- [ ] **Step 3: Write minimal implementation**

In `status.rs`, add the import:

```rust
use crate::anchor::AnchorTrialStatus;
```

Add these fields to `XBondPathStatus`, immediately after the existing `pub score: f64,`:

```rust
    #[serde(default)]
    pub smoothed_score: f64,
    #[serde(default)]
    pub effective_score: f64,
    #[serde(default)]
    pub flap_penalty: f64,
    #[serde(default)]
    pub trial: Option<AnchorTrialStatus>,
```

And in `impl From<ScoredPath> for XBondPathStatus`, after `score: value.score,`:

```rust
            smoothed_score: value.smoothed_score,
            effective_score: value.effective_score,
            flap_penalty: value.flap_penalty,
            trial: value.trial,
```

Note: `trial` must be moved before `value.path` fields are consumed — the existing code
reads `value.path.*` after `score`, and `value.trial` is a sibling of `value.path`, so
ordering is not an issue. If the compiler reports a partial-move error, bind
`let trial = value.trial;` at the top of the function and use `trial` in the initialiser.

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p xbond-core --lib status::`
Expected: PASS, 1 test.

Run: `cargo test --workspace`
Expected: PASS across all crates.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/status.rs
git commit -m "feat(xbond): report smoothing, flap penalty, and trial progress in status"
```

---

### Task 9: End-to-end obstruction cycle regression test

**Files:**
- Modify: `xbond/crates/xbond-core/src/health.rs` (tests module)

- [ ] **Step 1: Write the failing test**

Add to the `tests` module in `health.rs`:

```rust
    /// A loaded fiber line: bufferbloat inflates RTT and jitter, the queue is under
    /// pressure, and a few packets miss their deadline. This is the anchor.
    fn loaded_fiber() -> PathHealthSnapshot {
        let mut fiber = path(1, "globe-fiber", 60.0, 0.0, 0.04);
        fiber.jitter_ms = Some(35.0);
        fiber.queue_pressure = 0.35;
        fiber
    }

    /// The same dish while it is only passing heartbeats: nothing is loaded, so nothing
    /// looks wrong. This is the measurement asymmetry the design exists to defeat.
    fn idle_starlink() -> PathHealthSnapshot {
        let mut starlink = path(2, "starlink", 12.0, 0.0, 0.0);
        starlink.jitter_ms = Some(4.0);
        starlink.outbound_throughput_bps = 0;
        starlink.inbound_throughput_bps = 0;
        starlink.raw_inbound_throughput_bps = 0;
        starlink.throughput_bps = 0;
        starlink
    }

    /// The dish carrying mirrored trial traffic while the roof obstruction bites.
    fn obstructed_starlink() -> PathHealthSnapshot {
        let mut starlink = path(2, "starlink", 520.0, 0.35, 0.12);
        starlink.jitter_ms = Some(180.0);
        starlink.outbound_throughput_bps = 40_000_000;
        starlink
    }

    /// The dish carrying mirrored trial traffic with a clear view of the sky.
    fn clear_loaded_starlink() -> PathHealthSnapshot {
        let mut starlink = path(2, "starlink", 55.0, 0.005, 0.0);
        starlink.jitter_ms = Some(20.0);
        starlink.outbound_throughput_bps = 40_000_000;
        starlink
    }

    /// The reported production failure: a stable fiber anchor and a Starlink dish that
    /// looks pristine while idle and falls apart the moment it carries real traffic.
    /// Obstruction cycles are shorter than the trial window, so every trial must fail and
    /// the anchor must never move.
    #[test]
    fn obstruction_cycles_never_move_the_anchor() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);

        // Assert the premise, so a scoring change surfaces here rather than as a
        // confusing downstream failure.
        let premise = score_paths(&[loaded_fiber(), idle_starlink()]);
        let fiber_score = premise
            .iter()
            .find(|p| p.path.path_id == 1)
            .unwrap()
            .score;
        let idle_score = premise
            .iter()
            .find(|p| p.path.path_id == 2)
            .unwrap()
            .score;
        assert!(
            idle_score - fiber_score >= config.anchor_switch_score_margin,
            "fixture premise broken: idle starlink {idle_score} vs loaded fiber \
             {fiber_score} must differ by at least {}",
            config.anchor_switch_score_margin
        );

        let mut anchor_changes = 0u32;
        let mut previous_anchor = None;

        // 30 minutes of 10-second obstruction cycles. A 20-tick trial therefore always
        // spans more obstructed ticks than it is allowed to fail.
        for tick_index in 0..1_800u32 {
            let obstructed_now = (tick_index / 10) % 2 == 1;
            // Load only appears on the dish while it is on trial, because that is the
            // only time the scheduler mirrors traffic onto it.
            let starlink = match (state.trial.is_some(), obstructed_now) {
                (true, true) => obstructed_starlink(),
                (true, false) => clear_loaded_starlink(),
                (false, _) => idle_starlink(),
            };
            let roles = tick(&mut state, &[loaded_fiber(), starlink], config);

            let anchor = anchor_of(&roles);
            if previous_anchor.is_some() && previous_anchor != anchor {
                anchor_changes += 1;
            }
            previous_anchor = anchor;
        }

        assert_eq!(previous_anchor, Some(1), "fiber must still hold the anchor");
        assert_eq!(anchor_changes, 0, "the anchor must never have moved");
        assert!(
            state.trial_count >= 2,
            "the candidate should have been retried, saw {} trials",
            state.trial_count
        );
        // Without damping this would be roughly one trial per obstruction cycle (~90).
        assert!(
            state.trial_count <= 12,
            "flap damping should throttle retries, saw {} trials",
            state.trial_count
        );
    }

    /// The mirror image: a genuinely good path must still be able to win the anchor.
    #[test]
    fn a_genuinely_better_path_is_promoted_within_a_bounded_number_of_ticks() {
        let config = RoleSelectionConfig::default();
        let mut state = state_anchored_on(1);
        let mut fast = path(2, "new-fiber", 12.0, 0.0, 0.0);
        fast.outbound_throughput_bps = 40_000_000;
        let paths = [path(1, "old-dsl", 140.0, 0.0, 0.0), fast];

        let mut promoted_after = None;
        for tick_index in 0..200u32 {
            let roles = tick(&mut state, &paths, config);
            if anchor_of(&roles) == Some(2) {
                // One-based: this is the count of ticks that ran.
                promoted_after = Some(tick_index + 1);
                break;
            }
        }

        let promoted_after = promoted_after.expect("a clearly better path must eventually win");
        let earliest = u32::from(config.stable_ticks_required) + config.trial.ticks;
        assert!(
            promoted_after >= earliest,
            "promoted too eagerly after {promoted_after} ticks, expected at least {earliest}"
        );
        assert!(
            promoted_after <= earliest + 5,
            "promotion took too long: {promoted_after} ticks"
        );
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p xbond-core --lib health::obstruction`
Expected: PASS if Tasks 4–6 are complete. If it FAILS, the failure identifies a real
defect in the trial/damping wiring — fix `health.rs`/`anchor.rs`, do not weaken the test.

- [ ] **Step 3: Adjust implementation only if the test failed**

If `anchor_changes > 0`, the most likely causes, in order:
1. `advance_trial` compares effective instead of raw scores — it must use `path.score`.
2. `resolve_anchor` returns `best_promotable_id` instead of `current_anchor_id` when a
   trial starts.
3. `flap_damping.decay` is called more than once per tick.

If `trials_started > 6`, the suppression threshold is not being consulted in
`resolve_anchor`'s `best_promotable_id` filter.

- [ ] **Step 4: Run the whole core suite**

Run: `cargo test -p xbond-core`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add xbond/crates/xbond-core/src/health.rs
git commit -m "test(xbond): cover the reported starlink obstruction flapping scenario"
```

---

### Task 10: XNetwork models parse the new fields

**Files:**
- Modify: `XNetwork/Models/XBondStatus.cs:101-120`, `:190-255`
- Modify: `XNetwork/Models/XBondStatsSnapshot.cs`
- Create: `XNetwork.Tests/AnchorTrialSurfacingTests.cs`

- [ ] **Step 1: Write the failing test**

Create `XNetwork.Tests/AnchorTrialSurfacingTests.cs`:

```csharp
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Tests;

public class AnchorTrialSurfacingTests
{
    [Fact]
    public void TrialFieldsDeserializeFromClientStatus()
    {
        const string json = """
        {
          "path_id": 2,
          "name": "starlink",
          "role": "trial",
          "score": 700.0,
          "smoothed_score": 690.0,
          "effective_score": 640.0,
          "flap_penalty": 612.5,
          "trial": {
            "path_id": 2,
            "ticks": 5,
            "success_ticks": 4,
            "mirrored_bytes": 9000000,
            "required_ticks": 20,
            "required_success_ticks": 15
          },
          "loss_rate": 0.0,
          "late_rate": 0.0,
          "queue_depth": 0,
          "throughput_bps": 0,
          "interface_up": true,
          "in_cooldown": false
        }
        """;

        var path = JsonSerializer.Deserialize<XBondPathStatus>(json);

        Assert.NotNull(path);
        Assert.Equal("trial", path!.Role);
        Assert.Equal(640.0, path.EffectiveScore);
        Assert.Equal(612.5, path.FlapPenalty);
        Assert.Equal(4, path.Trial!.SuccessTicks);
        Assert.Equal(15, path.Trial.RequiredSuccessTicks);
    }

    [Fact]
    public void OldClientStatusWithoutNewFieldsStillParses()
    {
        const string json = """
        {
          "path_id": 1,
          "name": "fiber",
          "role": "anchor",
          "score": 800.0,
          "loss_rate": 0.0,
          "late_rate": 0.0,
          "queue_depth": 0,
          "throughput_bps": 0,
          "interface_up": true,
          "in_cooldown": false
        }
        """;

        var path = JsonSerializer.Deserialize<XBondPathStatus>(json);

        Assert.NotNull(path);
        Assert.Equal(0, path!.EffectiveScore);
        Assert.Null(path.Trial);
    }

    [Fact]
    public void SchedulePlanTrialPathIdsDefaultToEmpty()
    {
        const string json = """
        {
          "mode": "anchor-duplicate-1",
          "data_path_ids": [1],
          "duplicate_path_ids": [2],
          "fec_path_ids": []
        }
        """;

        var plan = JsonSerializer.Deserialize<XBondSchedulePlan>(json);

        Assert.NotNull(plan);
        Assert.Empty(plan!.TrialPathIds);
    }

    [Fact]
    public void TrialPathIsFlaggedAndDescribed()
    {
        var path = new XBondPathStatsSnapshot
        {
            Role = "trial",
            InterfaceUp = true,
            IsActive = true,
            Trial = new XBondPathTrialStatus
            {
                PathId = 2,
                Ticks = 5,
                SuccessTicks = 4,
                MirroredBytes = 9_000_000,
                RequiredTicks = 20,
                RequiredSuccessTicks = 15
            }
        };

        Assert.True(path.IsTrial);
        Assert.False(path.IsAnchor);
        Assert.Equal("Testing as anchor (5/20)", path.StateText);
    }

    [Fact]
    public void SuppressedPathIsFlaggedFromItsFlapPenalty()
    {
        var suppressed = new XBondPathStatsSnapshot { InterfaceUp = true, FlapPenalty = 900 };
        var trusted = new XBondPathStatsSnapshot { InterfaceUp = true, FlapPenalty = 100 };

        Assert.True(suppressed.IsSuppressed);
        Assert.False(trusted.IsSuppressed);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test XNetwork.Tests --filter AnchorTrialSurfacingTests`
Expected: FAIL — `XBondPathStatus does not contain a definition for EffectiveScore`.

- [ ] **Step 3: Write minimal implementation**

In `XNetwork/Models/XBondStatus.cs`, add to `XBondSchedulePlan` after `FecPathIds`:

```csharp
    [JsonPropertyName("trial_path_ids")]
    public List<int> TrialPathIds { get; set; } = new();
```

Add to `XBondPathStatus`, after the existing `Score` property (find it near
`[JsonPropertyName("score")]`):

```csharp
    [JsonPropertyName("smoothed_score")]
    public double SmoothedScore { get; set; }

    [JsonPropertyName("effective_score")]
    public double EffectiveScore { get; set; }

    [JsonPropertyName("flap_penalty")]
    public double FlapPenalty { get; set; }

    [JsonPropertyName("trial")]
    public XBondPathTrialStatus? Trial { get; set; }
```

Add this new class at the end of `XNetwork/Models/XBondStatus.cs`:

```csharp
/// <summary>Progress of the load test a candidate must pass before taking the anchor role.</summary>
public class XBondPathTrialStatus
{
    [JsonPropertyName("path_id")]
    public int PathId { get; set; }

    [JsonPropertyName("ticks")]
    public int Ticks { get; set; }

    [JsonPropertyName("success_ticks")]
    public int SuccessTicks { get; set; }

    [JsonPropertyName("mirrored_bytes")]
    public ulong MirroredBytes { get; set; }

    [JsonPropertyName("required_ticks")]
    public int RequiredTicks { get; set; }

    [JsonPropertyName("required_success_ticks")]
    public int RequiredSuccessTicks { get; set; }
}
```

In `XNetwork/Models/XBondStatsSnapshot.cs`, add to `XBondPathStatsSnapshot` after the
existing `public double Score { get; init; }`:

```csharp
    public double SmoothedScore { get; init; }

    public double EffectiveScore { get; init; }

    /// <summary>Decaying distrust after a failed stint or trial as anchor.</summary>
    public double FlapPenalty { get; init; }

    /// <summary>Threshold the client uses to suppress anchor promotion.</summary>
    public const double SuppressionThreshold = 500;

    public bool IsSuppressed => FlapPenalty >= SuppressionThreshold;

    public XBondPathTrialStatus? Trial { get; init; }

    public bool IsTrial => string.Equals(Role, "trial", StringComparison.OrdinalIgnoreCase);
```

And in the same class, insert a trial branch into `StateText` immediately before the final
`return IsActive ? "Active" : "Standby";`:

```csharp
            if (IsTrial)
            {
                return Trial is null
                    ? "Testing as anchor"
                    : $"Testing as anchor ({Trial.Ticks}/{Trial.RequiredTicks})";
            }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test XNetwork.Tests --filter AnchorTrialSurfacingTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Models/XBondStatus.cs XNetwork/Models/XBondStatsSnapshot.cs XNetwork.Tests/AnchorTrialSurfacingTests.cs
git commit -m "feat(xnetwork): parse anchor trial and flap penalty from client status"
```

---

### Task 11: Trial paths count as active and render a pill

**Files:**
- Modify: `XNetwork/Services/XBondStatsService.cs:196-199`, `:246-247`
- Modify: `XNetwork/Components/Pages/Home.razor:526`, `:934-947`
- Modify: `XNetwork/Components/Pages/XBond.razor:161-167`
- Modify: `XNetwork.Tests/AnchorTrialSurfacingTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `XNetwork.Tests/AnchorTrialSurfacingTests.cs`:

```csharp
    [Fact]
    public void TrialPathCountsAsActiveAndKeepsItsTrialRole()
    {
        var status = new XBondStatus
        {
            Schedule = new XBondSchedulePlan
            {
                AnchorPathId = 1,
                DataPathIds = [1],
                DuplicatePathIds = [],
                FecPathIds = [],
                TrialPathIds = [2]
            },
            Paths =
            [
                new XBondPathStatus
                {
                    PathId = 1,
                    InterfaceName = "enx1",
                    Role = "anchor",
                    InterfaceUp = true
                },
                new XBondPathStatus
                {
                    PathId = 2,
                    InterfaceName = "enx2",
                    Role = "trial",
                    InterfaceUp = true,
                    FlapPenalty = 0,
                    EffectiveScore = 640,
                    Trial = new XBondPathTrialStatus
                    {
                        PathId = 2,
                        Ticks = 5,
                        RequiredTicks = 20,
                        SuccessTicks = 5,
                        RequiredSuccessTicks = 15
                    }
                }
            ]
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            [],
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [],
            []);

        var trial = snapshot.Paths.Single(path => path.PathId == 2);
        Assert.True(trial.IsTrial);
        Assert.True(trial.IsActive, "a trial path is carrying mirrored traffic");
        Assert.Equal(5, trial.Trial!.Ticks);
        Assert.Equal(640, trial.EffectiveScore);
        Assert.Contains(snapshot.ActivePaths, path => path.PathId == 2);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test XNetwork.Tests --filter TrialPathCountsAsActiveAndKeepsItsTrialRole`
Expected: FAIL — `Assert.True() Failure` on `trial.IsActive` (trial ids are not in the
active set yet), or a compile error on `TrialPathIds` if Task 10 was skipped.

- [ ] **Step 3: Write minimal implementation**

In `XNetwork/Services/XBondStatsService.cs`, extend the `activeIds` set (line 196):

```csharp
        var activeIds = status.Schedule.DataPathIds
            .Concat(status.Schedule.DuplicatePathIds)
            .Concat(status.Schedule.FecPathIds)
            .Concat(status.Schedule.TrialPathIds)
            .ToHashSet();
```

And map the new per-path fields — add these next to the existing `Score = path.Score,`
inside the `new XBondPathStatsSnapshot { ... }` initialiser:

```csharp
                    SmoothedScore = path.SmoothedScore,
                    EffectiveScore = path.EffectiveScore,
                    FlapPenalty = path.FlapPenalty,
                    Trial = path.Trial,
```

In `XNetwork/Components/Pages/Home.razor`, add the pill inside the right-hand action
cluster. Replace the opening of that block at line 526:

```razor
            <div class="flex flex-shrink-0 items-center gap-2">
                @if (path.IsTrial)
                {
                    <span class="rounded-full border border-amber-400/40 bg-amber-400/10 px-2 py-0.5 text-xs font-semibold text-amber-200"
                          title="@(path.RoleReason ?? "Testing this adapter under real traffic before making it the main path")">
                        Trial
                    </span>
                }
```

Still in `Home.razor`, teach `RoleLabel` about the trial role by replacing the method
(lines 934-947):

```razor
    private static string RoleLabel(XBondPathStatsSnapshot path)
    {
        if (!path.IsConfigured)
        {
            return "not in uLink";
        }

        if (path.IsTrial)
        {
            return "trial";
        }

        if (path.IsActive && !path.IsAnchor && string.Equals(path.Role, "probe", StringComparison.OrdinalIgnoreCase))
        {
            return "backup";
        }

        return path.Role;
    }
```

In `XNetwork/Components/Pages/XBond.razor`, add a `trial` case to `RolePill` (line 161):

```razor
    private static string RolePill(XBondPathStatsSnapshot path) => path.Role.ToLowerInvariant() switch
    {
        "anchor" => "rounded-full border border-emerald-500/30 bg-emerald-500/10 px-2 py-0.5 text-xs font-semibold text-emerald-200",
        "backup" => "rounded-full border border-cyan-500/30 bg-cyan-500/10 px-2 py-0.5 text-xs font-semibold text-cyan-200",
        "trial" => "rounded-full border border-amber-400/40 bg-amber-400/10 px-2 py-0.5 text-xs font-semibold text-amber-200",
        "unavailable" => "rounded-full border border-slate-600 bg-slate-700/40 px-2 py-0.5 text-xs font-semibold text-slate-300",
        _ => "rounded-full border border-slate-600 bg-slate-700/40 px-2 py-0.5 text-xs font-semibold text-slate-300"
    };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test XNetwork.Tests --filter AnchorTrialSurfacingTests`
Expected: PASS, 6 tests.

Run: `dotnet build XNetwork/XNetwork.csproj -c Release`
Expected: build succeeded (Release, because the user's local Debug instance on :8080
locks the Debug output).

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Services/XBondStatsService.cs XNetwork/Components/Pages/Home.razor XNetwork/Components/Pages/XBond.razor XNetwork.Tests/AnchorTrialSurfacingTests.cs
git commit -m "feat(xnetwork): show anchor trial paths on the dashboard"
```

---

### Task 12: Full verification sweep

**Files:** none modified unless a failure is found.

- [ ] **Step 1: Run the Rust suite**

Run: `cargo test --workspace --manifest-path xbond/Cargo.toml`
Expected: PASS, no failures.

- [ ] **Step 2: Run clippy**

Run: `cargo clippy --workspace --manifest-path xbond/Cargo.toml --all-targets -- -D warnings`
Expected: no warnings. Fix any that appear (most likely `too_many_arguments` on
`assign_hysteresis_roles` — if so, add `#[allow(clippy::too_many_arguments)]` with a
comment explaining the parameters are all distinct selection inputs).

- [ ] **Step 3: Run the .NET suite**

Run: `dotnet test XNetwork.Tests -c Release`
Expected: PASS, all tests green (279+ existing plus the 6 new ones).

- [ ] **Step 4: Confirm no behaviour regressions in existing scheduler tests**

Run: `cargo test --manifest-path xbond/Cargo.toml -p xbond-core scheduler::`
Expected: PASS — in particular `balanced_policy_uses_anchor_only_for_healthy_bulk_packets`
must still pass, proving trial mirroring does not leak into normal operation.

- [ ] **Step 5: Commit any fixes**

```bash
git add -A
git commit -m "fix: address clippy and test findings for anchor stability"
```

(Skip if nothing changed.)

---

### Task 13: Release bookkeeping

**Files:**
- Modify: `XNetwork/Models/AppChangelog.cs`
- Modify: `XNetwork.Tests/AppChangelogTests.cs`
- Modify: `XNetwork.Tests/BuildInfoTests.cs`
- Modify: `AGENTS.md`

- [ ] **Step 1: Read the version-pinning tests**

Run: `dotnet test XNetwork.Tests --filter "AppChangelogTests|BuildInfoTests" -c Release`
Expected: PASS at the current version. Then read both test files to find the pinned
version string constants that must be updated together with `AppChangelog.CurrentVersion`.

- [ ] **Step 2: Bump the version and add the entry**

In `XNetwork/Models/AppChangelog.cs`, change:

```csharp
    public const string CurrentVersion = "ulink-2026.06.130";
```

And insert this entry as the first element of `Entries`, above the existing
`ulink-2026.06.129` entry:

```csharp
        new ChangelogEntry
        {
            Version = CurrentVersion,
            Date = "2026-08-18",
            Summary = "Stopped the main connection from switching to an unstable adapter.",
            Changes =
            [
                "Bumped the uLink interface version for anchor stability.",
                "Stopped Balanced and Fast from moving your traffic onto an adapter that only looks good while idle.",
                "Added a trial period: a candidate adapter now carries mirrored traffic and must stay clean before it becomes the main path.",
                "Adapters that fail as the main path are now distrusted for several minutes instead of seconds.",
                "Kept instant failover: if the main adapter dies, uLink still switches immediately.",
                "Showed a Trial badge and a plain-language reason for adapters under test or on cooldown."
            ]
        },
```

Update the pinned version strings in `XNetwork.Tests/AppChangelogTests.cs` and
`XNetwork.Tests/BuildInfoTests.cs` from `ulink-2026.06.129` to `ulink-2026.06.130`.

- [ ] **Step 3: Run the version tests**

Run: `dotnet test XNetwork.Tests --filter "AppChangelogTests|BuildInfoTests" -c Release`
Expected: PASS.

- [ ] **Step 4: Add the AGENTS.md journal entry**

Read the existing journal entries in `AGENTS.md` first and match their heading level and
field order exactly. The content to record:

- **Version:** `ulink-2026.06.130` (2026-08-18)
- **Change:** Anchor selection is now sticky. Scores are EWMA-smoothed with a penalty for
  instability, paths that fail as anchor accrue a decaying suppression penalty, and a
  candidate must survive a 20-tick mirrored-load trial before it can take the anchor role.
  Fixes traffic-visible flapping in `balanced`/`fast` between a loaded fiber line and an
  intermittently obstructed Starlink dish.
- **Touched:** `xbond-core` (`anchor.rs` new, `health.rs`, `scheduler.rs`, `config.rs`,
  `status.rs`), `xbond-client`, XNetwork models/service/`Home.razor`/`XBond.razor`.
- **Wire compatibility:** `SchedulePlan.trial_path_ids` and all new status fields are
  serde-defaulted; old status files and old peers still parse.
- **Spec:** `docs/superpowers/specs/2026-08-18-anchor-stability-design.md`
- **Plan:** `docs/superpowers/plans/2026-08-18-anchor-stability.md`
- **Verification:** `cargo test --workspace --manifest-path xbond/Cargo.toml`,
  `cargo clippy --workspace --manifest-path xbond/Cargo.toml --all-targets -- -D warnings`,
  `dotnet test XNetwork.Tests -c Release`, plus live verification on xeon-network after
  `deploy-xbond-paired.ps1`.
- **Open follow-up:** flapping is only provably fixed after a day of live observation in
  `balanced`; compare `schedule_change_count` growth against the previous build.

- [ ] **Step 5: Commit**

```bash
git add XNetwork/Models/AppChangelog.cs XNetwork.Tests/AppChangelogTests.cs XNetwork.Tests/BuildInfoTests.cs AGENTS.md
git commit -m "chore: release ulink-2026.06.130 anchor stability"
```

---

### Task 14: Review the implementation against the spec

**Files:** none modified unless a gap is found.

- [ ] **Step 1: Re-read the spec**

Read `docs/superpowers/specs/2026-08-18-anchor-stability-design.md` end to end.

- [ ] **Step 2: Check each spec section against the code**

Confirm each of these, naming the file and symbol that satisfies it:

| Spec requirement | Where to verify |
|---|---|
| EWMA smoothing + variance penalty | `anchor.rs::PathScoreSmoothing`, used by `health.rs::apply_score_smoothing` |
| Effective score drives role ranking | `health.rs::effective_score_for`, sort in `apply_score_smoothing` |
| Raw signals still drive hard demotion | `apply_score_smoothing` sentinel branch; `PathHealthSnapshot::hard_demotion_reason` untouched |
| Flap penalty on demotion (+1000) and failed trial (+600) | `health.rs::select_path_roles_with_state`, `health.rs::advance_trial` |
| Decay half-life 300 s, cap 4000 | `anchor.rs::FlapDampingConfig::default` |
| Suppression blocks candidacy but never failover | `health.rs::resolve_anchor` (`best_promotable_id` vs `best_any_id`) |
| Trial mirrors small AND bulk in every policy | `scheduler.rs::build_transmission_plan_for_packet` |
| Trial covers both directions via shared builders | server consumes `ScheduleControlMessage` through the same builders — confirm `xbond-server/src/main.rs` calls `precompute_transmission_plans`/`build_transmission_plan` on `return_control.schedule` |
| Promotion needs 15 of 20 clean loaded ticks | `anchor.rs::AnchorTrial::observe` |
| Insufficient load ⇒ promote if clean, else no penalty | `anchor.rs::AnchorTrial::failure_outcome` + `Promoted` branch ordering |
| One trial at a time, 60-tick spacing | `health.rs::resolve_anchor` (`state.trial.is_some()`, `ticks_since_trial_end`) |
| No trials during recovery | `health.rs::resolve_anchor` recovery branches |
| Anchor death cancels trial and fails over same tick | `resolve_anchor`'s `current_anchor` `None` branch |
| `PathRole::Trial` serialises as `"trial"` | `health.rs` test `trial_role_serialises_as_kebab_case` |
| Wire compat: `trial_path_ids` defaulted | `scheduler.rs` test `schedule_plan_without_trial_ids_still_deserialises` |
| Config table with all documented knobs | `config.rs::RoleSelectionSettings` vs the spec's TOML block — every key must match by name |
| Dashboard trial pill + suppression reason | `Home.razor`, `XBond.razor`, `XBondPathStatsSnapshot.IsTrial`/`IsSuppressed` |
| Old-client tolerance in XNetwork | test `OldClientStatusWithoutNewFieldsStillParses` |

- [ ] **Step 3: Verify the config key names match the spec exactly**

Run: `cargo test -p xbond-core --manifest-path xbond/Cargo.toml --lib config::role_selection_table_overrides_defaults`
Then diff the key list in `xbond/examples/client.example.toml` against the spec's TOML
block by eye. Any mismatch is a defect — fix the code, not the spec, unless the spec is
wrong (in which case correct the spec and say so).

- [ ] **Step 4: Fix any gaps found**

For each gap, add the missing behaviour with a test first, following the same TDD loop as
the earlier tasks.

- [ ] **Step 5: Commit any fixes**

```bash
git add -A
git commit -m "fix: close spec gaps found in anchor stability review"
```

(Skip if nothing changed.)

---

### Task 15: Deploy with the paired deployment script

**Files:** none.

- [ ] **Step 1: Confirm the working tree is clean and pushed**

Run: `git status --short`
Expected: empty output.

Run: `git push`
Expected: success.

- [ ] **Step 2: Read the deployment script before running it**

Read `deploy-xbond-paired.ps1`. This change touches Rust (`xbond-core`, `xbond-client`)
**and** the Blazor app, so the paired script is the correct tool — `./deploy.sh` is
app-only and would leave the router running the old client binary.

- [ ] **Step 3: Run the paired deployment**

Run: `./deploy-xbond-paired.ps1`
Expected: both hosts updated, services restarted, no errors. Capture the output.

- [ ] **Step 4: Verify on the live router**

Confirm, and report the actual values:
1. `ulink-2026.06.130` and the new commit hash appear on the build-info route.
2. The dashboard loads and lists paths with their roles.
3. `journalctl -u xbond-client` shows role reasons from the new selector and no panics.
4. Tunnel ping and internet ping both succeed.
5. `schedule_change_count` in the client status is stable over a few minutes.

- [ ] **Step 5: Report**

State plainly what deployed, what was verified with real output, and what still needs a
day of live observation in `balanced` before the flapping can be called fixed. Do not
claim the flapping is fixed on the basis of unit tests alone.
