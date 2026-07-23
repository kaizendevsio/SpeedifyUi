use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{ensure, Context, Result};
use xbond_core::{
    decode_sealed_payload_into, encode_sealed_payload_into, AuthenticatedSessionTracker,
    PacketKind, SessionChallengeOutcome, SessionProofOutcome, XBondControlMessage, XBondHeader,
    XBondKey,
};

const PAYLOAD_BYTES: usize = 1_200;
const CONTROL_PERIOD: Duration = Duration::from_millis(100);

struct VirtualSession {
    tracker: AuthenticatedSessionTracker,
    session_id: u64,
    sequence: u64,
}

fn main() -> Result<()> {
    let sessions = argument("--sessions", 500usize)?;
    let active = argument("--active", 50usize)?.min(sessions);
    let per_active_mbps = argument("--per-active-mbps", 10u64)?;
    let duration_seconds = argument("--duration-seconds", 8u64)?;
    let churn_per_second = argument("--churn-per-second", 10usize)?.min(sessions);
    ensure!(sessions > 0, "sessions must be positive");
    let key = XBondKey::from_passphrase(
        &std::env::var("XBOND_PSK").unwrap_or_else(|_| "xbond-local-lab-key".to_string()),
    );
    let mut virtual_sessions = Vec::with_capacity(sessions);
    for index in 0..sessions {
        virtual_sessions.push(authenticate(index as u64 + 1)?);
    }
    let rss_before = current_rss_bytes();
    let mut rss_peak = rss_before;
    let offered_numerator = per_active_mbps as u128 * 1_000_000 * CONTROL_PERIOD.as_micros();
    let offered_denominator = 8 * PAYLOAD_BYTES as u128 * 1_000_000;
    let offered_packets_per_tick = offered_numerator.div_ceil(offered_denominator).max(1) as usize;
    let offered_bps = active as u64 * per_active_mbps * 1_000_000;
    let payload = vec![0x5a; PAYLOAD_BYTES];
    let mut encoded = Vec::with_capacity(PAYLOAD_BYTES + 128);
    let mut decoded = Vec::with_capacity(PAYLOAD_BYTES);
    let started = Instant::now();
    let mut next_tick = started;
    let mut next_churn = started + Duration::from_secs(1);
    let mut control_latencies_micros =
        Vec::with_capacity(sessions * duration_seconds as usize * 10);
    let mut data_frames = 0u64;
    let mut control_frames = 0u64;
    let mut control_failures = 0u64;
    let mut churned = 0u64;
    while started.elapsed() < Duration::from_secs(duration_seconds) {
        next_tick += CONTROL_PERIOD;
        for session in &mut virtual_sessions {
            let frame_started = Instant::now();
            session.sequence = session.sequence.saturating_add(1);
            let header = XBondHeader::new(
                PacketKind::Heartbeat,
                session.session_id,
                session.sequence,
                now_micros(),
                1,
            );
            if encode_sealed_payload_into(&header, &[], &key, &mut encoded).is_err()
                || decode_sealed_payload_into(&encoded, &key, &mut decoded).is_err()
            {
                control_failures = control_failures.saturating_add(1);
            }
            control_frames = control_frames.saturating_add(1);
            control_latencies_micros.push(
                frame_started
                    .elapsed()
                    .as_micros()
                    .min(u128::from(u64::MAX)) as u64,
            );
        }
        for session in virtual_sessions.iter_mut().take(active) {
            for _ in 0..offered_packets_per_tick {
                session.sequence = session.sequence.saturating_add(1);
                let header = XBondHeader::new(
                    PacketKind::Data,
                    session.session_id,
                    session.sequence,
                    now_micros(),
                    1,
                );
                encode_sealed_payload_into(&header, &payload, &key, &mut encoded)?;
                let decoded_header = decode_sealed_payload_into(&encoded, &key, &mut decoded)?;
                ensure!(decoded_header.session_id == session.session_id);
                data_frames = data_frames.saturating_add(1);
            }
        }
        if Instant::now() >= next_churn {
            for index in 0..churn_per_second {
                let slot = (churned as usize + index) % sessions;
                let next_id = sessions as u64 + churned + index as u64 + 1;
                virtual_sessions[slot] = authenticate(next_id)?;
            }
            churned = churned.saturating_add(churn_per_second as u64);
            next_churn += Duration::from_secs(1);
        }
        rss_peak = rss_peak.max(current_rss_bytes());
        std::thread::sleep(next_tick.saturating_duration_since(Instant::now()));
    }
    let load_elapsed_seconds = started.elapsed().as_secs_f64();
    control_latencies_micros.sort_unstable();
    let p95_control_ms = percentile(&control_latencies_micros, 0.95) as f64 / 1_000.0;
    let achieved_bps =
        (data_frames as f64 * PAYLOAD_BYTES as f64 * 8.0 / load_elapsed_seconds) as u64;
    let responsive_sessions = virtual_sessions
        .iter()
        .filter(|session| session.tracker.accepts(session.session_id))
        .count();
    drop(control_latencies_micros);
    drop(decoded);
    drop(encoded);
    drop(payload);
    release_allocator_pages();
    std::thread::sleep(Duration::from_millis(250));
    let rss_after = current_rss_bytes();
    let memory_return_percent = if rss_peak > rss_before {
        100.0 * rss_after.saturating_sub(rss_before) as f64
            / rss_peak.saturating_sub(rss_before) as f64
    } else {
        0.0
    };
    println!(
        "{}",
        serde_json::json!({
            "sessions": sessions,
            "responsive_sessions": responsive_sessions,
            "active_sessions": active,
            "per_active_mbps": per_active_mbps,
            "offered_bps": offered_bps,
            "achieved_bps": achieved_bps,
            "elapsed_seconds": load_elapsed_seconds,
            "control_frames": control_frames,
            "control_failures": control_failures,
            "control_latency_p95_ms": p95_control_ms,
            "data_frames": data_frames,
            "churned_sessions": churned,
            "rss_before_bytes": rss_before,
            "rss_peak_bytes": rss_peak,
            "rss_after_bytes": rss_after,
            "memory_return_percent": memory_return_percent,
        })
    );
    Ok(())
}

#[cfg(target_os = "linux")]
fn release_allocator_pages() {
    unsafe {
        libc::malloc_trim(0);
    }
}

#[cfg(not(target_os = "linux"))]
fn release_allocator_pages() {}

fn authenticate(session_id: u64) -> Result<VirtualSession> {
    let request = nonce(session_id, 0x31);
    let challenge = nonce(session_id, 0xa7);
    let control = XBondControlMessage::SessionOpen {
        session_id,
        request_nonce: request,
    };
    let serialized = serde_json::to_vec(&control)?;
    let parsed: XBondControlMessage = serde_json::from_slice(&serialized)?;
    ensure!(matches!(parsed, XBondControlMessage::SessionOpen { .. }));
    let mut tracker = AuthenticatedSessionTracker::new(4);
    ensure!(matches!(
        tracker.issue_challenge(session_id, request, challenge, now_micros()),
        SessionChallengeOutcome::Issued { .. }
    ));
    ensure!(matches!(
        tracker.consume_proof(session_id, request, challenge, now_micros()),
        SessionProofOutcome::Opened
    ));
    Ok(VirtualSession {
        tracker,
        session_id,
        sequence: 0,
    })
}

fn nonce(session_id: u64, salt: u8) -> [u8; 16] {
    let mut value = [salt; 16];
    value[..8].copy_from_slice(&session_id.to_le_bytes());
    value
}

fn argument<T>(name: &str, default: T) -> Result<T>
where
    T: std::str::FromStr,
    T::Err: std::error::Error + Send + Sync + 'static,
{
    let mut args = std::env::args();
    while let Some(argument) = args.next() {
        if argument == name {
            return args
                .next()
                .with_context(|| format!("missing value for {name}"))?
                .parse()
                .with_context(|| format!("invalid value for {name}"));
        }
    }
    Ok(default)
}

fn percentile(values: &[u64], percentile: f64) -> u64 {
    if values.is_empty() {
        return 0;
    }
    values[((values.len() - 1) as f64 * percentile).ceil() as usize]
}

fn current_rss_bytes() -> u64 {
    std::fs::read_to_string("/proc/self/status")
        .ok()
        .and_then(|status| {
            status.lines().find_map(|line| {
                line.strip_prefix("VmRSS:")
                    .and_then(|value| value.split_whitespace().next())
                    .and_then(|value| value.parse::<u64>().ok())
                    .map(|kib| kib * 1024)
            })
        })
        .unwrap_or_default()
}

fn now_micros() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_micros()
        .min(u128::from(u64::MAX)) as u64
}
