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
    /// How long a hard-demoted ex-anchor has to recover before its penalty becomes final.
    /// A loaded line can saturate its queue for a single second; banishing it for a
    /// half-life over that would strand traffic on a worse path.
    pub transient_grace_ticks: u32,
    /// Consecutive eligible ticks a penalised path must serve to earn a credit back.
    ///
    /// Decay alone makes recovery a function of elapsed time, which treats a link that has
    /// genuinely recovered exactly like one still flapping. Worse, suppression removes a
    /// path from candidacy, and only candidates are trialled -- so a recovered path could
    /// not prove itself and had no way back except waiting out repeated exiles. Sustained
    /// health now buys penalty back, because that is the evidence actually worth crediting.
    pub rehabilitation_ticks: u32,
    /// Penalty forgiven each time `rehabilitation_ticks` of unbroken health is served.
    pub rehabilitation_credit: f64,
    /// Steadiness a path must also hold to earn rehabilitation credit.
    ///
    /// Eligibility alone is not enough. A link that cycles -- an obstructed dish, say --
    /// looks perfectly healthy during its good phase, so crediting mere eligibility let it
    /// buy its way out of suppression between obstructions and resume stealing the anchor,
    /// which is the exact behaviour flap damping exists to stop. Steadiness is measured over
    /// the stability window, so a cycling link fails this bar even mid-good-phase, while a
    /// genuinely recovered link passes it. Since the stability penalty also carries the
    /// unproven term, a path with too little history cannot clear this either.
    pub rehabilitation_max_stability_penalty: f64,
}

impl Default for FlapDampingConfig {
    fn default() -> Self {
        Self {
            penalty_demoted: 1_000.0,
            penalty_trial_failed: 600.0,
            suppress_threshold: 500.0,
            penalty_cap: 4_000.0,
            half_life_secs: 300,
            transient_grace_ticks: 5,
            // Two clean minutes clears a full displacement penalty, so a healthy runner-up
            // is trialable again in minutes rather than being stranded for a half-life.
            rehabilitation_ticks: 45,
            rehabilitation_credit: 250.0,
            rehabilitation_max_stability_penalty: 20.0,
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

    /// Refunds a penalty that turned out not to be deserved, e.g. an anchor that
    /// hard-demoted for a single tick and recovered immediately.
    pub fn forgive(&mut self, path_id: u16, amount: f64) {
        if let Some(penalty) = self.penalties.get_mut(&path_id) {
            *penalty -= amount.max(0.0);
            if *penalty < 1.0 {
                self.penalties.remove(&path_id);
            }
        }
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

/// How steadiness of latency and loss is rewarded, measured over a rolling window.
///
/// Separate from `ScoreSmoothingConfig`, which watches the composite score: that conflates
/// latency swings with throughput and queue movement, so a link with rock-steady latency
/// but variable throughput was being treated as unstable. The anchor carries interactive
/// traffic, so latency and loss steadiness specifically are what matter.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct StabilityConfig {
    /// Rolling window length in scheduler ticks (one per second).
    pub window_ticks: u32,
    /// Score points charged per millisecond of latency deviation.
    pub latency_deviation_penalty_per_ms: f64,
    /// Score points charged per unit of loss-rate deviation (loss is 0.0..1.0).
    pub loss_deviation_penalty_weight: f64,
    /// Charged to a path that has not yet filled its window, decaying to zero as it does.
    /// Without this a freshly appeared path shows zero deviation and would masquerade as
    /// the steadiest link on the router.
    pub unproven_penalty: f64,
    /// Ceiling so instability cannot swamp every other term in the score.
    pub penalty_cap: f64,
}

impl Default for StabilityConfig {
    fn default() -> Self {
        Self {
            window_ticks: 30,
            latency_deviation_penalty_per_ms: 3.0,
            loss_deviation_penalty_weight: 1_500.0,
            unproven_penalty: 60.0,
            penalty_cap: 250.0,
        }
    }
}

/// Rolling mean and mean-absolute-deviation of one path's latency and loss.
#[derive(Debug, Clone, Copy, PartialEq, Default, Serialize, Deserialize)]
pub struct PathStability {
    pub latency_mean_ms: f64,
    pub latency_deviation_ms: f64,
    pub loss_mean: f64,
    pub loss_deviation: f64,
    /// Observations recorded, saturating at the window length.
    pub samples: u32,
    latency_initialized: bool,
    loss_initialized: bool,
}

impl PathStability {
    pub fn observe(&mut self, rtt_ms: Option<f64>, loss_rate: f64, window_ticks: u32) {
        let alpha = (2.0 / f64::from(window_ticks.max(1) + 1)).clamp(0.001, 1.0);

        // An unmeasured RTT is a gap in knowledge, not a reading of zero; folding it in
        // would invent deviation that the link never exhibited.
        if let Some(rtt_ms) = rtt_ms {
            if self.latency_initialized {
                let error = rtt_ms - self.latency_mean_ms;
                self.latency_mean_ms += alpha * error;
                self.latency_deviation_ms += alpha * (error.abs() - self.latency_deviation_ms);
            } else {
                self.latency_mean_ms = rtt_ms;
                self.latency_deviation_ms = 0.0;
                self.latency_initialized = true;
            }
        }

        let loss_rate = loss_rate.clamp(0.0, 1.0);
        if self.loss_initialized {
            let error = loss_rate - self.loss_mean;
            self.loss_mean += alpha * error;
            self.loss_deviation += alpha * (error.abs() - self.loss_deviation);
        } else {
            self.loss_mean = loss_rate;
            self.loss_deviation = 0.0;
            self.loss_initialized = true;
        }

        self.samples = self.samples.saturating_add(1).min(window_ticks.max(1));
    }

    /// Score points to subtract. Zero means a fully proven, perfectly steady link.
    pub fn penalty(&self, config: StabilityConfig) -> f64 {
        let window = config.window_ticks.max(1);
        let latency = self.latency_deviation_ms * config.latency_deviation_penalty_per_ms.max(0.0);
        let loss = self.loss_deviation * config.loss_deviation_penalty_weight.max(0.0);
        let proven = f64::from(self.samples.min(window)) / f64::from(window);
        let unproven = config.unproven_penalty.max(0.0) * (1.0 - proven);
        (latency + loss + unproven).min(config.penalty_cap.max(0.0))
    }

    pub fn is_proven(&self, config: StabilityConfig) -> bool {
        self.samples >= config.window_ticks.max(1)
    }
}

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
    /// A trial tick only counts as clean when the candidate's loaded RTT stays within
    /// this margin of the anchor's; the same margin gates trial entry on smoothed RTT.
    pub latency_margin_ms: f64,
}

impl Default for AnchorTrialConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            ticks: 20,
            success_ticks: 15,
            min_bytes: 5_000_000,
            min_interval_ticks: 60,
            latency_margin_ms: 10.0,
        }
    }
}

impl AnchorTrialConfig {
    /// Ticks the candidate may fail and still pass. Kept as a method so the early-abort
    /// check and the final verdict cannot drift apart.
    pub fn allowed_bad_ticks(&self) -> u32 {
        self.ticks
            .max(1)
            .saturating_sub(self.effective_success_ticks())
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

    fn settle(stability: &mut PathStability, rtt: f64, loss: f64, ticks: u32, window: u32) {
        for _ in 0..ticks {
            stability.observe(Some(rtt), loss, window);
        }
    }

    #[test]
    fn a_steady_link_outranks_an_erratic_one_with_the_same_average() {
        let config = StabilityConfig::default();
        let mut steady = PathStability::default();
        let mut erratic = PathStability::default();

        for tick in 0..60 {
            steady.observe(Some(60.0), 0.01, config.window_ticks);
            // Same 60ms mean and same 1% mean loss, but swinging hard around them.
            let (rtt, loss) = if tick % 2 == 0 {
                (20.0, 0.0)
            } else {
                (100.0, 0.02)
            };
            erratic.observe(Some(rtt), loss, config.window_ticks);
        }

        assert!(
            (steady.latency_mean_ms - erratic.latency_mean_ms).abs() < 12.0,
            "fixture broken: means should be close, got {} vs {}",
            steady.latency_mean_ms,
            erratic.latency_mean_ms
        );
        assert!(
            steady.penalty(config) < 1.0,
            "a steady link should be unpenalised"
        );
        assert!(
            erratic.penalty(config) > 60.0,
            "an erratic link should be heavily penalised, got {}",
            erratic.penalty(config)
        );
    }

    #[test]
    fn a_new_path_must_earn_its_stability_rating() {
        let config = StabilityConfig::default();
        let mut fresh = PathStability::default();
        fresh.observe(Some(30.0), 0.0, config.window_ticks);

        // Zero deviation but almost no evidence: it must not outrank a proven link.
        assert!(fresh.latency_deviation_ms == 0.0);
        assert!(!fresh.is_proven(config));
        let early = fresh.penalty(config);
        assert!(
            early > 50.0,
            "unproven path should be penalised, got {early}"
        );

        let mut proven = PathStability::default();
        settle(
            &mut proven,
            30.0,
            0.0,
            config.window_ticks,
            config.window_ticks,
        );
        assert!(proven.is_proven(config));
        assert!(proven.penalty(config) < early);
        assert!(proven.penalty(config) < 1.0);
    }

    #[test]
    fn penalty_is_capped_so_instability_cannot_swamp_the_score() {
        let config = StabilityConfig::default();
        let mut wild = PathStability::default();
        for tick in 0..200 {
            let (rtt, loss) = if tick % 2 == 0 {
                (5.0, 0.0)
            } else {
                (1_500.0, 1.0)
            };
            wild.observe(Some(rtt), loss, config.window_ticks);
        }

        assert_eq!(wild.penalty(config), config.penalty_cap);
    }

    #[test]
    fn an_unmeasured_rtt_does_not_invent_latency_deviation() {
        let config = StabilityConfig::default();
        let mut stability = PathStability::default();
        settle(&mut stability, 40.0, 0.0, 20, config.window_ticks);
        let before = stability.latency_deviation_ms;

        for _ in 0..5 {
            stability.observe(None, 0.0, config.window_ticks);
        }

        assert_eq!(stability.latency_deviation_ms, before);
        assert_eq!(stability.latency_mean_ms, 40.0);
        // Loss still counts, so the window keeps filling.
        assert!(stability.samples > 20);
    }

    #[test]
    fn loss_instability_is_penalised_even_when_latency_is_perfect() {
        let config = StabilityConfig::default();
        let mut flappy_loss = PathStability::default();
        for tick in 0..60 {
            flappy_loss.observe(
                Some(30.0),
                if tick % 2 == 0 { 0.0 } else { 0.08 },
                config.window_ticks,
            );
        }

        assert_eq!(flappy_loss.latency_deviation_ms, 0.0);
        assert!(
            flappy_loss.penalty(config) > 30.0,
            "loss swings must be punished on their own, got {}",
            flappy_loss.penalty(config)
        );
    }

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
            assert_eq!(
                trial.observe(loaded_tick(true), config),
                TrialOutcome::Running
            );
        }

        assert_eq!(
            trial.observe(loaded_tick(true), config),
            TrialOutcome::Promoted
        );
        assert_eq!(trial.ticks, config.ticks);
    }

    #[test]
    fn obstructed_trial_fails_under_load_as_soon_as_it_cannot_pass() {
        let config = AnchorTrialConfig::default();
        let mut trial = AnchorTrial::new(2);
        let allowed_bad_ticks = config.ticks - config.success_ticks;

        for _ in 0..allowed_bad_ticks {
            assert_eq!(
                trial.observe(loaded_tick(false), config),
                TrialOutcome::Running
            );
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
}
