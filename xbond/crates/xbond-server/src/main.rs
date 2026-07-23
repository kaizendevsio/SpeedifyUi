use anyhow::{Context, Result};
use clap::{ArgAction, Parser};
use serde::Serialize;
use socket2::{Domain, Protocol, Socket, Type};
use std::collections::{HashMap, HashSet, VecDeque};
use std::io::ErrorKind;
use std::net::SocketAddr;
#[cfg(target_os = "linux")]
use std::net::{IpAddr, Ipv4Addr};
#[cfg(target_os = "linux")]
use std::os::fd::AsRawFd;
use std::path::PathBuf;
use std::sync::{
    atomic::{AtomicU64, Ordering},
    Arc, Mutex, OnceLock,
};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
#[cfg(target_os = "linux")]
use tokio::io::Interest;
use tokio::net::{TcpStream, UdpSocket};
use tokio::sync::{mpsc, Notify, RwLock};
use tokio::time;
use xbond_core::{
    build_transmission_plan, decode_sealed_payload_into, encode_sealed_payload_into,
    is_ipv4_packet, precompute_transmission_plans, read_linux_kernel_network_status,
    recommended_repair_cache_bytes, AuthenticatedSessionTracker, DuplicateOutcome, DuplicateWindow,
    FrameReceiver, LinuxKernelNetworkSnapshot, PacketKind, PacketReorderBuffer,
    PacketTransmissionPlans, PathHealthSnapshot, ReceiveOutcome, RedundancyPolicy,
    RedundancyPolicyConfig, ReorderStats, ReorderedPacket, ResendCache, ScheduleControlMessage,
    SchedulePlan, SessionChallengeOutcome, SessionHandshakeNonce, SessionProofOutcome,
    XBondControlMessage, XBondFrame, XBondHeader, XBondKey, XBondPacketPoolStatus,
    XBondRepairCacheStatus, XBondRepairStatus, XBondSaturationStatus, XBondServerHealthStatus,
    XBondServerHealthTargetStatus, XBondServerIngressReorderStatus, XBondServerRecoveryStatus,
    XBondSocketBufferStatus, XBondStageTimingStatus, XBondTun, XorFecBlock, FLAG_SERVER_TO_CLIENT,
};

const DEFAULT_TUN_QUEUE_CAPACITY: usize = 2048;
const DEFAULT_INBOUND_QUEUE_CAPACITY: usize = 4096;
const CONTROL_QUEUE_CAPACITY: usize = 256;
const HEARTBEAT_ACK_QUEUE_CAPACITY: usize = 256;
const HEARTBEAT_ACK_RETRY_SEND_TIMEOUT: Duration = Duration::from_millis(10);
const SEND_REPORT_QUEUE_CAPACITY: usize = 256;
const TUN_WORKER_EXIT_QUEUE_CAPACITY: usize = 4;
const DEFAULT_UDP_SOCKET_BUFFER_BYTES: usize = 16 * 1024 * 1024;
const DEFAULT_UDP_RECEIVE_BATCH_SIZE: usize = 32;
const MINIMUM_USABLE_UDP_SOCKET_BUFFER_BYTES: usize = 256 * 1024;
const SATURATION_SOFT_QUEUE_UTILIZATION: f64 = 0.60;
const SATURATION_HARD_QUEUE_UTILIZATION: f64 = 0.80;
const SATURATION_SOFT_OLDEST_AGE_MS: u64 = 20;
const SATURATION_HARD_OLDEST_AGE_MS: u64 = 50;
const SATURATION_HARD_RESTART_AFTER: Duration = Duration::from_secs(1);

static SERVER_SOCKET_BUFFER_STATUS: OnceLock<XBondSocketBufferStatus> = OnceLock::new();
static KERNEL_NETWORK_BASELINE: OnceLock<LinuxKernelNetworkSnapshot> = OnceLock::new();
static RECEIVE_MICROS_TOTAL: AtomicU64 = AtomicU64::new(0);
static RECEIVE_BATCHES: AtomicU64 = AtomicU64::new(0);
static RECEIVE_DATAGRAMS: AtomicU64 = AtomicU64::new(0);
static RECEIVE_BATCH_PEAK: AtomicU64 = AtomicU64::new(0);
static RECEIVE_DECODE_MICROS_TOTAL: AtomicU64 = AtomicU64::new(0);
static RECEIVE_ENQUEUE_MICROS_TOTAL: AtomicU64 = AtomicU64::new(0);
static SCHEDULE_MICROS_TOTAL: AtomicU64 = AtomicU64::new(0);
static TUN_MICROS_TOTAL: AtomicU64 = AtomicU64::new(0);
static PRIMARY_RETURN_DRAINED: AtomicU64 = AtomicU64::new(0);
static SERVER_SATURATION: OnceLock<Mutex<ServerSaturationRuntime>> = OnceLock::new();

#[derive(Debug, Default)]
struct ServerSaturationRuntime {
    hard_since: Option<Instant>,
    previous_active: bool,
    periods: u64,
    duplicate_suppressions: u64,
    fec_suppressions: u64,
    last_drain_count: u64,
    last_drain_sample: Option<Instant>,
    drain_packets_per_second: f64,
    status: XBondSaturationStatus,
}
const REPAIR_CACHE_CAPACITY: usize = 4096;
const DEFAULT_REPAIR_CACHE_BYTES: usize = 8 * 1024 * 1024;
const MIN_REPAIR_CACHE_BYTES: usize = 1024 * 1024;
const INITIAL_REPAIR_CACHE_BITS_PER_SECOND: u64 = 20_000_000;
const REPAIR_CACHE_TTL_MICROS: u64 = 3_000_000;
const REPAIR_REQUEST_INTERVAL_MICROS: u64 = 75_000;
const MAX_REPAIR_REQUESTS: usize = 64;
const PRE_RECOVERY_REPAIR_PENDING_THRESHOLD: usize = 4;
const PRE_RECOVERY_REPAIR_INTERVAL_MICROS: u64 = 250_000;
const PRE_RECOVERY_REPAIR_MAX_PER_REQUEST: usize = 4;
const PRE_RECOVERY_REPAIR_MAX_PER_SECOND: usize = 8;
const PEER_STALE_AFTER_MICROS: u64 = 15_000_000;
const RETURN_SCHEDULE_STALE_AFTER_MICROS: u64 = 10_000_000;
const RETURN_SCHEDULE_GRACE_MICROS: u64 = 5_000_000;
const MAX_UDP_DATAGRAM_BYTES: usize = 65_535;
const EXPECTED_UDP_PAYLOAD_BYTES: usize = 2048;
const PAYLOAD_POOL_CAPACITY: usize = 256;
const MAX_POOLED_PAYLOAD_CAPACITY: usize = EXPECTED_UDP_PAYLOAD_BYTES * 4;
const RETIRED_SESSION_CAPACITY: usize = 64;
const TUN_WRITE_ENQUEUE_DEADLINE: Duration = Duration::from_millis(250);
const PRIMARY_RETURN_QUEUE_DEADLINE: Duration = Duration::from_millis(250);
const PRIMARY_RETURN_SEND_DEADLINE: Duration = Duration::from_millis(250);
const PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES: u32 = 3;

#[derive(Debug, Parser)]
#[command(name = "xbond-server")]
#[command(about = "XBond server prototype")]
struct Args {
    #[arg(long, default_value = "0.0.0.0:8444")]
    bind: String,

    #[arg(long, default_value = "XBOND_PSK")]
    key_env: String,

    #[arg(long, default_value_t = 120)]
    realtime_deadline_ms: u64,

    #[arg(long, default_value_t = 768)]
    interactive_packet_threshold_bytes: usize,

    #[arg(long, default_value_t = 50)]
    ingress_reorder_normal_hold_ms: u64,

    #[arg(long, default_value_t = 500)]
    ingress_reorder_recovery_hold_ms: u64,

    #[arg(long, default_value_t = true, action = ArgAction::Set)]
    ingress_reorder_adaptive_recovery_hold: bool,

    #[arg(long, default_value_t = 150)]
    ingress_reorder_recovery_min_hold_ms: u64,

    #[arg(long, default_value_t = 100)]
    ingress_reorder_recovery_increase_step_ms: u64,

    #[arg(long, default_value_t = 50)]
    ingress_reorder_recovery_decrease_step_ms: u64,

    #[arg(long, default_value_t = 8192)]
    ingress_reorder_capacity: usize,

    #[arg(long, default_value = "/run/xbond/server-status.json")]
    status_path: PathBuf,

    #[arg(long)]
    json_events: bool,

    #[arg(long)]
    trace_packets: bool,

    #[arg(long)]
    tun_name: Option<String>,

    #[arg(long, default_value_t = 1400)]
    tun_mtu: u16,

    #[arg(long, default_value_t = DEFAULT_TUN_QUEUE_CAPACITY)]
    tun_queue_capacity: usize,

    #[arg(long, default_value_t = DEFAULT_INBOUND_QUEUE_CAPACITY)]
    inbound_queue_capacity: usize,

    #[arg(long, default_value_t = DEFAULT_UDP_SOCKET_BUFFER_BYTES)]
    udp_socket_buffer_bytes: usize,

    #[arg(long, default_value_t = DEFAULT_UDP_RECEIVE_BATCH_SIZE)]
    udp_receive_batch_size: usize,

    #[arg(long, default_value_t = DEFAULT_REPAIR_CACHE_BYTES)]
    repair_cache_bytes: usize,

    #[arg(long, hide = true, default_value_t = 0)]
    fault_tun_write_delay_ms: u64,

    #[arg(long, hide = true, default_value_t = 0)]
    fault_tun_write_fail_after_packets: u64,

    #[arg(long, hide = true, default_value_t = 0)]
    fault_tun_read_fail_after_packets: u64,

    #[arg(long, default_value_t = true, action = ArgAction::Set)]
    server_health_enabled: bool,

    #[arg(long, default_value_t = 10)]
    server_health_interval_seconds: u64,

    #[arg(long, default_value_t = 1500)]
    server_health_timeout_ms: u64,

    #[arg(
        long = "server-health-target",
        default_values_t = [
            "8.8.8.8:53".to_string(),
            "1.1.1.1:443".to_string()
        ]
    )]
    server_health_targets: Vec<String>,
}

#[derive(Debug)]
struct PacketPoolTelemetry {
    fallback_allocations: AtomicU64,
    discarded: AtomicU64,
}

impl PacketPoolTelemetry {
    fn new() -> Self {
        Self {
            fallback_allocations: AtomicU64::new(0),
            discarded: AtomicU64::new(0),
        }
    }

    fn record_fallback(&self) {
        self.fallback_allocations.fetch_add(1, Ordering::Relaxed);
    }

    fn record_discard(&self) {
        self.discarded.fetch_add(1, Ordering::Relaxed);
    }

    fn status(&self, retained: usize, capacity: usize) -> XBondPacketPoolStatus {
        XBondPacketPoolStatus {
            retained: retained.min(capacity),
            capacity,
            fallback_allocations: self.fallback_allocations.load(Ordering::Relaxed),
            discarded: self.discarded.load(Ordering::Relaxed),
        }
    }
}

#[derive(Debug, Clone)]
struct PayloadRecycle {
    tx: mpsc::Sender<Vec<u8>>,
    telemetry: Arc<PacketPoolTelemetry>,
}

impl PayloadRecycle {
    fn status(&self) -> XBondPacketPoolStatus {
        let capacity = self.tx.max_capacity();
        let retained = capacity.saturating_sub(self.tx.capacity());
        self.telemetry.status(retained, capacity)
    }
}

#[derive(Debug)]
struct InboundServerFrame {
    frame: XBondFrame,
    peer: SocketAddr,
    pre_admission_deduplicated: bool,
    payload_recycle: Option<PayloadRecycle>,
}

impl InboundServerFrame {
    fn new(
        frame: XBondFrame,
        peer: SocketAddr,
        pre_admission_deduplicated: bool,
        payload_recycle: Option<PayloadRecycle>,
    ) -> Self {
        Self {
            frame,
            peer,
            pre_admission_deduplicated,
            payload_recycle,
        }
    }

    fn take_payload(&mut self) -> RecyclablePayload {
        RecyclablePayload {
            payload: Some(std::mem::take(&mut self.frame.payload)),
            payload_recycle: self.payload_recycle.clone(),
        }
    }
}

impl Drop for InboundServerFrame {
    fn drop(&mut self) {
        if !self.frame.payload.is_empty() || self.frame.payload.capacity() > 0 {
            recycle_payload(
                self.payload_recycle.as_ref(),
                std::mem::take(&mut self.frame.payload),
            );
        }
    }
}

#[derive(Debug)]
struct RecyclablePayload {
    payload: Option<Vec<u8>>,
    payload_recycle: Option<PayloadRecycle>,
}

impl RecyclablePayload {
    fn as_slice(&self) -> &[u8] {
        self.payload.as_deref().unwrap_or_default()
    }

    fn into_vec(mut self) -> Vec<u8> {
        self.payload.take().unwrap_or_default()
    }
}

impl Drop for RecyclablePayload {
    fn drop(&mut self) {
        if let Some(payload) = self.payload.take() {
            recycle_payload(self.payload_recycle.as_ref(), payload);
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum CriticalControlKey {
    Handshake {
        control: ServerSessionHandshakeControl,
        route: ResponseRouteKey,
    },
    Other {
        session_id: u64,
        sequence: u64,
        route: ResponseRouteKey,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct ResponseRouteKey {
    session_id: u64,
    path_id: u16,
    peer: SocketAddr,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CoalescingQueuePush {
    Queued,
    Replaced,
    IgnoredStale,
    EvictedOldest,
    Full,
}

#[derive(Debug)]
struct BoundedCoalescingQueue<K> {
    entries: Mutex<VecDeque<(K, InboundServerFrame)>>,
    notify: Notify,
    capacity: usize,
    evict_oldest: bool,
}

impl<K: PartialEq> BoundedCoalescingQueue<K> {
    fn new(capacity: usize, evict_oldest: bool) -> Self {
        Self {
            entries: Mutex::new(VecDeque::new()),
            notify: Notify::new(),
            capacity: capacity.max(1),
            evict_oldest,
        }
    }

    fn push_with(
        &self,
        key: K,
        inbound: InboundServerFrame,
        should_replace: impl FnOnce(&InboundServerFrame, &InboundServerFrame) -> bool,
    ) -> CoalescingQueuePush {
        let mut entries = self
            .entries
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if let Some(index) = entries
            .iter()
            .position(|(existing_key, _)| existing_key == &key)
        {
            if !should_replace(&entries[index].1, &inbound) {
                return CoalescingQueuePush::IgnoredStale;
            }
            entries[index] = (key, inbound);
            self.notify.notify_one();
            return CoalescingQueuePush::Replaced;
        }

        let outcome = if entries.len() >= self.capacity {
            if !self.evict_oldest {
                return CoalescingQueuePush::Full;
            }
            entries.pop_front();
            CoalescingQueuePush::EvictedOldest
        } else {
            CoalescingQueuePush::Queued
        };
        entries.push_back((key, inbound));
        self.notify.notify_one();
        outcome
    }

    async fn recv(&self) -> InboundServerFrame {
        loop {
            let notified = self.notify.notified();
            if let Some((_, inbound)) = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .pop_front()
            {
                return inbound;
            }
            notified.await;
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum InboundDispatchOutcome {
    Queued,
    Coalesced,
    Dropped,
    CriticalSaturated,
    Closed,
}

#[derive(Debug)]
struct ControlSendWork {
    encoded: Vec<u8>,
    peer: SocketAddr,
}

#[derive(Debug, Default)]
struct HeartbeatAckMetrics {
    immediate_sent: AtomicU64,
    retry_queued: AtomicU64,
    retry_sent: AtomicU64,
    would_block: AtomicU64,
    retry_overflow: AtomicU64,
    retry_timeouts: AtomicU64,
    immediate_failures: AtomicU64,
    retry_failures: AtomicU64,
    depth: AtomicU64,
}

#[derive(Debug, Clone)]
struct HeartbeatAckSender {
    socket: Arc<UdpSocket>,
    retry_tx: mpsc::Sender<ControlSendWork>,
    metrics: Arc<HeartbeatAckMetrics>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum HeartbeatAckRetryResult {
    Sent,
    Timeout,
    Failed,
}

async fn await_heartbeat_ack_retry<F>(
    send: F,
    expected_len: usize,
    timeout: Duration,
) -> HeartbeatAckRetryResult
where
    F: std::future::Future<Output = std::io::Result<usize>>,
{
    match time::timeout(timeout, send).await {
        Ok(Ok(sent)) if sent == expected_len => HeartbeatAckRetryResult::Sent,
        Ok(Ok(_)) | Ok(Err(_)) => HeartbeatAckRetryResult::Failed,
        Err(_) => HeartbeatAckRetryResult::Timeout,
    }
}

impl HeartbeatAckSender {
    fn dispatch(&self, work: ControlSendWork) -> bool {
        match self.socket.try_send_to(&work.encoded, work.peer) {
            Ok(sent) if sent == work.encoded.len() => {
                self.metrics.immediate_sent.fetch_add(1, Ordering::Relaxed);
                true
            }
            Ok(_) => {
                self.metrics
                    .immediate_failures
                    .fetch_add(1, Ordering::Relaxed);
                false
            }
            Err(error) if error.kind() == ErrorKind::WouldBlock => self.enqueue_retry(work),
            Err(_) => {
                self.metrics
                    .immediate_failures
                    .fetch_add(1, Ordering::Relaxed);
                false
            }
        }
    }

    fn enqueue_retry(&self, work: ControlSendWork) -> bool {
        self.metrics.would_block.fetch_add(1, Ordering::Relaxed);
        self.metrics.depth.fetch_add(1, Ordering::Relaxed);
        match self.retry_tx.try_send(work) {
            Ok(()) => {
                self.metrics.retry_queued.fetch_add(1, Ordering::Relaxed);
                true
            }
            Err(mpsc::error::TrySendError::Full(_)) => {
                self.metrics.depth.fetch_sub(1, Ordering::Relaxed);
                self.metrics.retry_overflow.fetch_add(1, Ordering::Relaxed);
                false
            }
            Err(mpsc::error::TrySendError::Closed(_)) => {
                self.metrics.depth.fetch_sub(1, Ordering::Relaxed);
                self.metrics.retry_failures.fetch_add(1, Ordering::Relaxed);
                false
            }
        }
    }

    fn record_encode_failure(&self) {
        self.metrics
            .immediate_failures
            .fetch_add(1, Ordering::Relaxed);
    }
}

#[derive(Debug)]
struct ReturnSendWork {
    packet_kind: PacketKind,
    peer: SocketAddr,
    header: XBondHeader,
    payload: Arc<Vec<u8>>,
    deadline: Instant,
}

#[derive(Debug)]
struct ReturnSendReport {
    path_id: u16,
    packet_kind: PacketKind,
    success: bool,
    peer: SocketAddr,
    error: Option<String>,
    emsgsize: bool,
    deadline_expired: bool,
    force_restart: bool,
    attempted_datagram_bytes: usize,
}

#[derive(Debug)]
enum BoundedDatagramSendError {
    Io(std::io::Error),
    DeadlineExpired,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PrimaryReturnReserveError {
    Closed,
    DeadlineExpired,
}

#[derive(Debug)]
struct PendingPrimaryReturn {
    work: ReturnSendWork,
    alternate_copy_accepted: bool,
}

#[derive(Debug)]
struct PendingTunWriteBatch {
    packets: Vec<ReorderedPacket>,
    deadline: Instant,
    enqueue_deadline: Duration,
}

impl PendingTunWriteBatch {
    fn new(packets: Vec<ReorderedPacket>, enqueue_deadline: Duration) -> Self {
        Self {
            packets,
            deadline: Instant::now() + enqueue_deadline,
            enqueue_deadline,
        }
    }
}

#[derive(Debug)]
struct ReturnSenderHandle {
    control_tx: mpsc::Sender<ReturnSendWork>,
    data_tx: mpsc::Sender<ReturnSendWork>,
    task: tokio::task::JoinHandle<()>,
}

#[derive(Debug)]
struct TunWriteBatch {
    packets: Vec<ReorderedPacket>,
    enqueued_at_micros: u64,
}

#[derive(Debug)]
struct TunWorkerExit {
    worker: &'static str,
    reason: String,
}

#[derive(Debug, Default)]
struct TunWriterTelemetry {
    queue_depth: AtomicU64,
    max_queue_depth: AtomicU64,
    oldest_enqueued_at_micros: AtomicU64,
    queued_at_micros: Mutex<VecDeque<u64>>,
    last_write_latency_micros: AtomicU64,
    max_write_latency_micros: AtomicU64,
    write_errors: AtomicU64,
    packets_written: AtomicU64,
    repair_packets_written: AtomicU64,
    saturation_failures: AtomicU64,
}

impl TunWriterTelemetry {
    fn try_reserve_packets(
        &self,
        packet_count: usize,
        capacity: usize,
        enqueued_at_micros: u64,
    ) -> bool {
        let packet_count = packet_count as u64;
        let capacity = capacity as u64;
        let reservation =
            self.queue_depth
                .fetch_update(Ordering::AcqRel, Ordering::Relaxed, |current| {
                    current
                        .checked_add(packet_count)
                        .filter(|next| *next <= capacity)
                });
        let Ok(previous_depth) = reservation else {
            return false;
        };
        self.max_queue_depth.fetch_max(
            previous_depth.saturating_add(packet_count),
            Ordering::Relaxed,
        );

        let mut queued = self
            .queued_at_micros
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        queued.extend(std::iter::repeat_n(
            enqueued_at_micros,
            packet_count as usize,
        ));
        self.oldest_enqueued_at_micros.store(
            queued.front().copied().unwrap_or_default(),
            Ordering::Relaxed,
        );
        true
    }

    fn release_packets(&self, packet_count: usize, expected_enqueued_at_micros: u64) {
        let mut queued = self
            .queued_at_micros
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        for _ in 0..packet_count {
            let dequeued = queued.pop_front();
            debug_assert_eq!(dequeued, Some(expected_enqueued_at_micros));
        }
        self.queue_depth
            .fetch_sub(packet_count as u64, Ordering::AcqRel);
        self.oldest_enqueued_at_micros.store(
            queued.front().copied().unwrap_or_default(),
            Ordering::Relaxed,
        );
    }
}

#[derive(Debug)]
struct TunWriterHandle {
    tx: mpsc::Sender<TunWriteBatch>,
    telemetry: Arc<TunWriterTelemetry>,
    capacity: usize,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
struct TunEnqueueOutcome {
    enqueued: u64,
}

#[derive(Debug, Clone, Copy, Default)]
struct TunFaultInjection {
    write_delay_ms: u64,
    write_fail_after_packets: u64,
    read_fail_after_packets: u64,
}

#[derive(Debug, Clone, Copy, Default)]
struct IngressRepairLimiter {
    window_started_micros: u64,
    sent_in_window: usize,
}

impl IngressRepairLimiter {
    fn allowance(&mut self, now_micros: u64, recovery_active: bool) -> usize {
        if recovery_active {
            return MAX_REPAIR_REQUESTS;
        }
        if self.window_started_micros == 0
            || now_micros.saturating_sub(self.window_started_micros) >= 1_000_000
        {
            self.window_started_micros = now_micros;
            self.sent_in_window = 0;
        }
        PRE_RECOVERY_REPAIR_MAX_PER_SECOND
            .saturating_sub(self.sent_in_window)
            .min(PRE_RECOVERY_REPAIR_MAX_PER_REQUEST)
    }

    fn record(&mut self, count: usize, recovery_active: bool) {
        if !recovery_active {
            self.sent_in_window = self.sent_in_window.saturating_add(count);
        }
    }

    fn reset(&mut self) {
        *self = Self::default();
    }
}

fn ingress_repair_request_parameters(
    limiter: &mut IngressRepairLimiter,
    now_micros: u64,
    recovery_active: bool,
    pending_depth: usize,
) -> Option<(u64, usize)> {
    if !recovery_active && pending_depth < PRE_RECOVERY_REPAIR_PENDING_THRESHOLD {
        return None;
    }
    let allowance = limiter.allowance(now_micros, recovery_active);
    (allowance > 0).then_some((
        if recovery_active {
            REPAIR_REQUEST_INTERVAL_MICROS
        } else {
            PRE_RECOVERY_REPAIR_INTERVAL_MICROS
        },
        allowance,
    ))
}

fn injected_tun_failure(after_packets: u64, processed_packets: u64) -> bool {
    after_packets > 0 && processed_packets >= after_packets
}

#[derive(Debug, Default, Clone, Serialize)]
struct ServerReturnPmtuStatus {
    emsgsize_errors: u64,
    emsgsize_errors_by_path: HashMap<u16, u64>,
}

#[derive(Debug, Default, Clone, Copy)]
struct ReturnCopyEnqueueAccounting {
    selected: usize,
    accepted: usize,
}

impl ReturnCopyEnqueueAccounting {
    fn selected(&mut self) {
        self.selected = self.selected.saturating_add(1);
    }

    fn accepted(&mut self) {
        self.accepted = self.accepted.saturating_add(1);
    }

    fn all_copies_dropped(self, primary_pending: bool) -> bool {
        !primary_pending && (self.selected == 0 || self.accepted == 0)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct PeerState {
    addr: SocketAddr,
    last_seen_micros: u64,
}

#[derive(Debug, Clone, PartialEq)]
struct ReturnControl {
    schedule_generation: u64,
    received_at_micros: u64,
    schedule: SchedulePlan,
    policy: RedundancyPolicy,
    policy_config: RedundancyPolicyConfig,
    recovery_active: bool,
    transmission_plans: PacketTransmissionPlans,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ServerSessionOpenDecision {
    AcceptedNew,
    AcceptedCurrent,
    RestartRequired,
    Rejected,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ServerSessionHandshakeControl {
    Open {
        session_id: u64,
        request_nonce: SessionHandshakeNonce,
    },
    Proof {
        session_id: u64,
        request_nonce: SessionHandshakeNonce,
        challenge: SessionHandshakeNonce,
    },
}

#[derive(Debug)]
struct ServerSessionGate {
    tracker: AuthenticatedSessionTracker,
    forwarding_active: bool,
}

impl ServerSessionGate {
    fn new() -> Self {
        Self {
            tracker: AuthenticatedSessionTracker::new(RETIRED_SESSION_CAPACITY),
            forwarding_active: false,
        }
    }

    #[cfg(test)]
    fn current(&self) -> Option<u64> {
        self.tracker.current()
    }

    fn accepts(&self, session_id: u64) -> bool {
        self.forwarding_active && self.tracker.accepts(session_id)
    }

    fn restart_required_for(&self, session_id: u64) -> bool {
        !self.forwarding_active
            && (self.tracker.current().is_none() || self.tracker.current() == Some(session_id))
    }

    fn issue_challenge(
        &mut self,
        header_session_id: u64,
        control_session_id: u64,
        request_nonce: SessionHandshakeNonce,
        fresh_challenge: SessionHandshakeNonce,
        now_micros: u64,
    ) -> Option<SessionHandshakeNonce> {
        if header_session_id != control_session_id {
            return None;
        }

        match self.tracker.issue_challenge(
            control_session_id,
            request_nonce,
            fresh_challenge,
            now_micros,
        ) {
            SessionChallengeOutcome::Issued { challenge }
            | SessionChallengeOutcome::Existing { challenge } => Some(challenge),
            SessionChallengeOutcome::RejectedZeroSession
            | SessionChallengeOutcome::RejectedZeroRequestNonce
            | SessionChallengeOutcome::RejectedZeroChallenge
            | SessionChallengeOutcome::RejectedRetiredSession => None,
        }
    }

    fn prove(
        &mut self,
        header_session_id: u64,
        control_session_id: u64,
        request_nonce: SessionHandshakeNonce,
        challenge: SessionHandshakeNonce,
        now_micros: u64,
    ) -> ServerSessionOpenDecision {
        if header_session_id != control_session_id {
            return ServerSessionOpenDecision::Rejected;
        }

        match self
            .tracker
            .consume_proof(control_session_id, request_nonce, challenge, now_micros)
        {
            SessionProofOutcome::Opened => {
                self.forwarding_active = true;
                ServerSessionOpenDecision::AcceptedNew
            }
            SessionProofOutcome::AlreadyCurrent if self.forwarding_active => {
                ServerSessionOpenDecision::AcceptedCurrent
            }
            SessionProofOutcome::AlreadyCurrent => ServerSessionOpenDecision::RestartRequired,
            SessionProofOutcome::RejectedZeroSession
            | SessionProofOutcome::RejectedZeroRequestNonce
            | SessionProofOutcome::RejectedZeroChallenge
            | SessionProofOutcome::RejectedMissingChallenge
            | SessionProofOutcome::RejectedMismatchedChallenge
            | SessionProofOutcome::RejectedExpiredChallenge
            | SessionProofOutcome::RejectedRetiredSession => ServerSessionOpenDecision::Rejected,
        }
    }

    fn require_restart(&mut self) -> Option<u64> {
        let session_id = self.tracker.current()?;
        self.forwarding_active = false;
        Some(session_id)
    }
}

#[derive(Debug, Serialize)]
struct ServerRuntimeStatus {
    running: bool,
    bind: String,
    tun: Option<String>,
    updated_at_micros: u64,
    schedule_required: bool,
    schedule_generation: u64,
    schedule_age_ms: u64,
    return_schedule: Option<ServerReturnScheduleStatus>,
    ingress_reorder: ServerIngressReorderStatus,
    repair: XBondRepairStatus,
    server_health: XBondServerHealthStatus,
    control_plane: ServerControlPlaneStatus,
    return_pmtu: ServerReturnPmtuStatus,
    socket_buffers: Vec<XBondSocketBufferStatus>,
    kernel_network: xbond_core::XBondKernelNetworkStatus,
    saturation: XBondSaturationStatus,
    stage_timings: XBondStageTimingStatus,
    counters: TunnelCounters,
}

#[derive(Debug, Default, Clone, Copy, Serialize)]
struct ServerControlPlaneStatus {
    prioritized_frames_processed: u64,
    last_control_progress_at_micros: u64,
    protocol_acks_queued: u64,
    schedule_updates_accepted: u64,
    ingress_control_frames_coalesced: u64,
    ingress_repair_queue_drops: u64,
    ingress_payload_queue_drops: u64,
    ingress_duplicates_coalesced: u64,
    primary_return_queue_full: u64,
    primary_return_enqueued: u64,
    primary_return_enqueue_deadline_expiries: u64,
    primary_return_send_deadline_expiries: u64,
    primary_return_forced_restarts: u64,
    all_return_copies_dropped: u64,
    repair_return_queue_full: u64,
    control_send_queue_full: u64,
    control_datagrams_sent: u64,
    control_send_failures: u64,
    heartbeat_acks_queued: u64,
    heartbeat_acks_sent: u64,
    heartbeat_ack_backpressure: u64,
    heartbeat_ack_drops: u64,
    heartbeat_ack_failures: u64,
    heartbeat_ack_queue_depth: u64,
    heartbeat_ack_immediate_sent: u64,
    heartbeat_ack_retry_queued: u64,
    heartbeat_ack_retry_sent: u64,
    heartbeat_ack_would_block: u64,
    heartbeat_ack_retry_overflow: u64,
    heartbeat_ack_retry_timeouts: u64,
    heartbeat_ack_immediate_failures: u64,
    heartbeat_ack_retry_failures: u64,
    tun_write_queue_depth: u64,
    tun_write_queue_peak_depth: u64,
    tun_write_queue_capacity: u64,
    tun_write_oldest_age_ms: u64,
    tun_write_latency_micros: u64,
    tun_write_max_latency_micros: u64,
    tun_write_errors: u64,
    tun_packets_written: u64,
    tun_write_queue_saturated_drops: u64,
    tun_write_queue_saturation_failures: u64,
    repair_cache: XBondRepairCacheStatus,
    receive_payload_pool: XBondPacketPoolStatus,
}

#[derive(Debug, Serialize)]
struct ServerReturnScheduleStatus {
    policy: RedundancyPolicy,
    recovery_active: bool,
    schedule_generation: u64,
    schedule_age_ms: u64,
    schedule_stale: bool,
    schedule: SchedulePlan,
}

#[derive(Debug, Serialize)]
struct ServerIngressReorderStatus {
    current_hold_ms: u64,
    normal_hold_ms: u64,
    recovery_hold_ms: u64,
    recovery_min_hold_ms: u64,
    recovery_max_hold_ms: u64,
    adaptive_recovery_hold_enabled: bool,
    adaptive_calm_samples: u32,
    adaptive_last_adjustment_reason: String,
    capacity: usize,
    stats: ReorderStats,
}

#[derive(Debug, Clone, Copy)]
struct IngressReorderHoldSettings {
    adaptive_enabled: bool,
    normal_hold_micros: u64,
    recovery_min_hold_micros: u64,
    recovery_max_hold_micros: u64,
    increase_step_micros: u64,
    decrease_step_micros: u64,
}

#[derive(Debug, Clone, Serialize)]
struct AdaptiveIngressReorderStatus {
    adaptive_enabled: bool,
    current_hold_ms: u64,
    normal_hold_ms: u64,
    recovery_min_hold_ms: u64,
    recovery_max_hold_ms: u64,
    calm_samples: u32,
    last_adjustment_reason: String,
}

#[derive(Debug)]
struct IngressReorderHoldController {
    settings: IngressReorderHoldSettings,
    current_hold_micros: u64,
    recovery_active: bool,
    calm_samples: u32,
    previous_reorder_stats: ReorderStats,
    previous_repair_requests_sent: u64,
    last_adjustment_reason: String,
}

const ADAPTIVE_RECOVERY_CALM_SAMPLES_TO_DECREASE: u32 = 5;
const ADAPTIVE_RECOVERY_LOW_PENDING_DEPTH: u64 = 4;

impl IngressReorderHoldSettings {
    fn from_args(args: &Args) -> Self {
        let normal_hold_micros = args.ingress_reorder_normal_hold_ms.max(1) * 1_000;
        let recovery_max_hold_micros = args.ingress_reorder_recovery_hold_ms.max(1) * 1_000;
        let recovery_min_hold_micros = (args.ingress_reorder_recovery_min_hold_ms.max(1) * 1_000)
            .min(recovery_max_hold_micros);
        Self {
            adaptive_enabled: args.ingress_reorder_adaptive_recovery_hold,
            normal_hold_micros,
            recovery_min_hold_micros,
            recovery_max_hold_micros,
            increase_step_micros: args.ingress_reorder_recovery_increase_step_ms.max(1) * 1_000,
            decrease_step_micros: args.ingress_reorder_recovery_decrease_step_ms.max(1) * 1_000,
        }
    }
}

impl IngressReorderHoldController {
    fn new(settings: IngressReorderHoldSettings) -> Self {
        Self {
            current_hold_micros: settings.normal_hold_micros,
            settings,
            recovery_active: false,
            calm_samples: 0,
            previous_reorder_stats: ReorderStats::default(),
            previous_repair_requests_sent: 0,
            last_adjustment_reason: "Normal ingress reorder hold.".to_string(),
        }
    }

    fn current_hold_micros(&self) -> u64 {
        self.current_hold_micros
    }

    fn set_recovery_active(
        &mut self,
        recovery_active: bool,
        reorder_stats: ReorderStats,
        repair: &XBondRepairStatus,
    ) -> bool {
        if self.recovery_active == recovery_active {
            return false;
        }

        self.recovery_active = recovery_active;
        self.calm_samples = 0;
        self.previous_reorder_stats = reorder_stats;
        self.previous_repair_requests_sent = repair.requests_sent;

        let previous_hold = self.current_hold_micros;
        if recovery_active {
            self.current_hold_micros = if self.settings.adaptive_enabled {
                self.settings.recovery_min_hold_micros
            } else {
                self.settings.recovery_max_hold_micros
            };
            self.last_adjustment_reason = if self.settings.adaptive_enabled {
                "Recovery entered; using minimum adaptive hold.".to_string()
            } else {
                "Recovery entered; using fixed maximum hold.".to_string()
            };
        } else {
            self.current_hold_micros = self.settings.normal_hold_micros;
            self.last_adjustment_reason = "Recovery exited; reset to normal hold.".to_string();
        }

        previous_hold != self.current_hold_micros
    }

    fn reset_to_normal(&mut self, reorder_stats: ReorderStats, repair: &XBondRepairStatus) -> bool {
        let previous_hold = self.current_hold_micros;
        self.recovery_active = false;
        self.calm_samples = 0;
        self.previous_reorder_stats = reorder_stats;
        self.previous_repair_requests_sent = repair.requests_sent;
        self.current_hold_micros = self.settings.normal_hold_micros;
        self.last_adjustment_reason = "Session changed; reset to normal hold.".to_string();
        previous_hold != self.current_hold_micros
    }

    fn observe(
        &mut self,
        recovery_active: bool,
        reorder_stats: ReorderStats,
        repair: &XBondRepairStatus,
    ) -> bool {
        if self.recovery_active != recovery_active {
            return self.set_recovery_active(recovery_active, reorder_stats, repair);
        }

        if !recovery_active {
            self.previous_reorder_stats = reorder_stats;
            self.previous_repair_requests_sent = repair.requests_sent;
            return false;
        }

        if !self.settings.adaptive_enabled {
            self.previous_reorder_stats = reorder_stats;
            self.previous_repair_requests_sent = repair.requests_sent;
            let previous_hold = self.current_hold_micros;
            self.current_hold_micros = self.settings.recovery_max_hold_micros;
            self.last_adjustment_reason = "Fixed recovery hold is active.".to_string();
            return previous_hold != self.current_hold_micros;
        }

        let timeout_delta = reorder_stats
            .timeout_releases
            .saturating_sub(self.previous_reorder_stats.timeout_releases);
        let gap_delta = reorder_stats
            .released_gap_packets
            .saturating_sub(self.previous_reorder_stats.released_gap_packets);
        let late_delta = reorder_stats
            .late_duplicates
            .saturating_sub(self.previous_reorder_stats.late_duplicates);
        let repair_request_delta = repair
            .requests_sent
            .saturating_sub(self.previous_repair_requests_sent);

        self.previous_reorder_stats = reorder_stats;
        self.previous_repair_requests_sent = repair.requests_sent;

        let previous_hold = self.current_hold_micros;
        let pressure =
            timeout_delta > 0 || gap_delta > 0 || late_delta > 0 || repair_request_delta > 0;
        if pressure {
            self.calm_samples = 0;
            self.current_hold_micros = self
                .current_hold_micros
                .saturating_add(self.settings.increase_step_micros)
                .min(self.settings.recovery_max_hold_micros);
            self.last_adjustment_reason = format!(
                "Recovery pressure observed: timeout={timeout_delta}, gaps={gap_delta}, late={late_delta}, repair_requests={repair_request_delta}."
            );
            return previous_hold != self.current_hold_micros;
        }

        if reorder_stats.pending_depth <= ADAPTIVE_RECOVERY_LOW_PENDING_DEPTH {
            self.calm_samples = self.calm_samples.saturating_add(1);
            if self.calm_samples >= ADAPTIVE_RECOVERY_CALM_SAMPLES_TO_DECREASE
                && self.current_hold_micros > self.settings.recovery_min_hold_micros
            {
                self.current_hold_micros = self
                    .current_hold_micros
                    .saturating_sub(self.settings.decrease_step_micros)
                    .max(self.settings.recovery_min_hold_micros);
                self.calm_samples = 0;
                self.last_adjustment_reason =
                    "Recovery calm window observed; decreasing hold.".to_string();
                return previous_hold != self.current_hold_micros;
            }
            self.last_adjustment_reason = "Recovery calm sample observed.".to_string();
        } else {
            self.calm_samples = 0;
            self.last_adjustment_reason =
                "Recovery hold unchanged; pending reorder depth remains elevated.".to_string();
        }

        false
    }

    fn status(&self) -> AdaptiveIngressReorderStatus {
        AdaptiveIngressReorderStatus {
            adaptive_enabled: self.settings.adaptive_enabled,
            current_hold_ms: self.current_hold_micros / 1_000,
            normal_hold_ms: self.settings.normal_hold_micros / 1_000,
            recovery_min_hold_ms: self.settings.recovery_min_hold_micros / 1_000,
            recovery_max_hold_ms: self.settings.recovery_max_hold_micros / 1_000,
            calm_samples: self.calm_samples,
            last_adjustment_reason: self.last_adjustment_reason.clone(),
        }
    }
}

fn is_prioritized_inbound(kind: PacketKind) -> bool {
    matches!(
        kind,
        PacketKind::Heartbeat | PacketKind::Control | PacketKind::Repair
    )
}

#[derive(Debug)]
enum InboundLane {
    Critical(CriticalControlKey),
    LatestSchedule(ResponseRouteKey),
    LatestHeartbeat(ResponseRouteKey),
    Repair,
    Payload,
}

fn response_route(inbound: &InboundServerFrame) -> ResponseRouteKey {
    ResponseRouteKey {
        session_id: inbound.frame.header.session_id,
        path_id: inbound.frame.header.path_id,
        peer: inbound.peer,
    }
}

fn classify_inbound_lane(inbound: &InboundServerFrame) -> InboundLane {
    let frame = &inbound.frame;
    let route = response_route(inbound);
    match frame.header.kind {
        PacketKind::Heartbeat => InboundLane::LatestHeartbeat(route),
        PacketKind::Repair => InboundLane::Repair,
        PacketKind::Control => {
            if let Some(handshake) = parse_session_handshake(frame) {
                return InboundLane::Critical(CriticalControlKey::Handshake {
                    control: handshake,
                    route,
                });
            }
            if serde_json::from_slice::<ScheduleControlMessage>(&frame.payload).is_ok()
                || serde_json::from_slice::<SchedulePlan>(&frame.payload).is_ok()
            {
                return InboundLane::LatestSchedule(route);
            }
            if parse_repair_request(frame).is_some() {
                return InboundLane::Repair;
            }
            InboundLane::Critical(CriticalControlKey::Other {
                session_id: frame.header.session_id,
                sequence: frame.header.sequence,
                route,
            })
        }
        PacketKind::Data | PacketKind::Duplicate | PacketKind::Fec => InboundLane::Payload,
    }
}

fn schedule_control_version(frame: &XBondFrame) -> (u64, u64) {
    (
        parse_schedule_control_generation(frame).unwrap_or_default(),
        frame.header.sequence,
    )
}

fn recycle_payload(payload_recycle: Option<&PayloadRecycle>, mut payload: Vec<u8>) -> bool {
    let Some(payload_recycle) = payload_recycle else {
        return false;
    };
    if payload.capacity() == 0 || payload.capacity() > MAX_POOLED_PAYLOAD_CAPACITY {
        payload_recycle.telemetry.record_discard();
        return false;
    }
    payload.clear();
    if payload_recycle.tx.try_send(payload).is_ok() {
        true
    } else {
        payload_recycle.telemetry.record_discard();
        false
    }
}

fn take_payload_buffer(
    payload_recycle_rx: &mut mpsc::Receiver<Vec<u8>>,
    telemetry: &PacketPoolTelemetry,
) -> Vec<u8> {
    match payload_recycle_rx.try_recv() {
        Ok(mut payload) => {
            payload.clear();
            payload
        }
        Err(mpsc::error::TryRecvError::Empty | mpsc::error::TryRecvError::Disconnected) => {
            telemetry.record_fallback();
            Vec::with_capacity(EXPECTED_UDP_PAYLOAD_BYTES)
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn dispatch_inbound_frame(
    critical_control: &BoundedCoalescingQueue<CriticalControlKey>,
    latest_schedule: &BoundedCoalescingQueue<ResponseRouteKey>,
    latest_heartbeat: &BoundedCoalescingQueue<ResponseRouteKey>,
    repair_tx: &mpsc::Sender<InboundServerFrame>,
    payload_tx: &mpsc::Sender<InboundServerFrame>,
    inbound: InboundServerFrame,
    payload_queue_drops: &AtomicU64,
    repair_queue_drops: &AtomicU64,
    control_frames_coalesced: &AtomicU64,
) -> InboundDispatchOutcome {
    match classify_inbound_lane(&inbound) {
        InboundLane::Critical(key) => {
            return control_dispatch_outcome(
                critical_control.push_with(key, inbound, |existing, candidate| {
                    candidate.frame.header.sequence >= existing.frame.header.sequence
                }),
                control_frames_coalesced,
            );
        }
        InboundLane::LatestSchedule(route) => {
            return control_dispatch_outcome(
                latest_schedule.push_with(route, inbound, |existing, candidate| {
                    schedule_control_version(&candidate.frame)
                        >= schedule_control_version(&existing.frame)
                }),
                control_frames_coalesced,
            );
        }
        InboundLane::LatestHeartbeat(route) => {
            return control_dispatch_outcome(
                latest_heartbeat.push_with(route, inbound, |existing, candidate| {
                    candidate.frame.header.sequence >= existing.frame.header.sequence
                }),
                control_frames_coalesced,
            );
        }
        InboundLane::Repair => {
            return match repair_tx.try_send(inbound) {
                Ok(()) => InboundDispatchOutcome::Queued,
                Err(mpsc::error::TrySendError::Full(_)) => {
                    repair_queue_drops.fetch_add(1, Ordering::Relaxed);
                    InboundDispatchOutcome::Dropped
                }
                Err(mpsc::error::TrySendError::Closed(_)) => InboundDispatchOutcome::Closed,
            };
        }
        InboundLane::Payload => {}
    }

    match payload_tx.try_send(inbound) {
        Ok(()) => InboundDispatchOutcome::Queued,
        Err(mpsc::error::TrySendError::Full(_)) => {
            payload_queue_drops.fetch_add(1, Ordering::Relaxed);
            InboundDispatchOutcome::Dropped
        }
        Err(mpsc::error::TrySendError::Closed(_)) => InboundDispatchOutcome::Closed,
    }
}

fn pre_admission_duplicate_class(kind: PacketKind) -> Option<u32> {
    match kind {
        PacketKind::Data | PacketKind::Duplicate | PacketKind::Repair => Some(0),
        PacketKind::Fec => Some(1),
        PacketKind::Heartbeat | PacketKind::Control => None,
    }
}

fn control_dispatch_outcome(
    queue_outcome: CoalescingQueuePush,
    control_frames_coalesced: &AtomicU64,
) -> InboundDispatchOutcome {
    match queue_outcome {
        CoalescingQueuePush::Queued => InboundDispatchOutcome::Queued,
        CoalescingQueuePush::Replaced
        | CoalescingQueuePush::IgnoredStale
        | CoalescingQueuePush::EvictedOldest => {
            control_frames_coalesced.fetch_add(1, Ordering::Relaxed);
            InboundDispatchOutcome::Coalesced
        }
        CoalescingQueuePush::Full => InboundDispatchOutcome::CriticalSaturated,
    }
}

async fn receive_prioritized_frame(
    critical_control: &BoundedCoalescingQueue<CriticalControlKey>,
    latest_schedule: &BoundedCoalescingQueue<ResponseRouteKey>,
    latest_heartbeat: &BoundedCoalescingQueue<ResponseRouteKey>,
    repair_rx: &mut mpsc::Receiver<InboundServerFrame>,
    payload_rx: &mut mpsc::Receiver<InboundServerFrame>,
    payload_enabled: bool,
) -> Option<InboundServerFrame> {
    if !payload_enabled {
        return Some(tokio::select! {
            biased;
            inbound = critical_control.recv() => inbound,
            inbound = latest_schedule.recv() => inbound,
            inbound = latest_heartbeat.recv() => inbound,
        });
    }

    tokio::select! {
        biased;
        inbound = critical_control.recv() => Some(inbound),
        inbound = latest_schedule.recv() => Some(inbound),
        inbound = latest_heartbeat.recv() => Some(inbound),
        inbound = repair_rx.recv() => inbound,
        inbound = payload_rx.recv() => inbound,
    }
}

fn spawn_control_sender(
    socket: Arc<UdpSocket>,
    json_events: bool,
) -> (
    mpsc::Sender<ControlSendWork>,
    Arc<AtomicU64>,
    Arc<AtomicU64>,
) {
    let (tx, mut rx) = mpsc::channel::<ControlSendWork>(CONTROL_QUEUE_CAPACITY);
    let sent = Arc::new(AtomicU64::new(0));
    let failures = Arc::new(AtomicU64::new(0));
    let task_sent = sent.clone();
    let task_failures = failures.clone();
    tokio::spawn(async move {
        while let Some(work) = rx.recv().await {
            match socket.send_to(&work.encoded, work.peer).await {
                Ok(_) => {
                    task_sent.fetch_add(1, Ordering::Relaxed);
                }
                Err(error) => {
                    task_failures.fetch_add(1, Ordering::Relaxed);
                    if json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "control-datagram-send-failed",
                                "peer": work.peer.to_string(),
                                "error": error.to_string(),
                            })
                        );
                    }
                }
            }
        }
    });
    (tx, sent, failures)
}

fn spawn_heartbeat_ack_sender(socket: Arc<UdpSocket>, json_events: bool) -> HeartbeatAckSender {
    let (retry_tx, mut retry_rx) = mpsc::channel::<ControlSendWork>(HEARTBEAT_ACK_QUEUE_CAPACITY);
    let metrics = Arc::new(HeartbeatAckMetrics::default());
    let task_metrics = metrics.clone();
    let task_socket = socket.clone();
    tokio::spawn(async move {
        while let Some(work) = retry_rx.recv().await {
            task_metrics.depth.fetch_sub(1, Ordering::Relaxed);
            match await_heartbeat_ack_retry(
                task_socket.send_to(&work.encoded, work.peer),
                work.encoded.len(),
                HEARTBEAT_ACK_RETRY_SEND_TIMEOUT,
            )
            .await
            {
                HeartbeatAckRetryResult::Sent => {
                    task_metrics.retry_sent.fetch_add(1, Ordering::Relaxed);
                }
                HeartbeatAckRetryResult::Failed => {
                    task_metrics.retry_failures.fetch_add(1, Ordering::Relaxed);
                    if json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "heartbeat-ack-send-failed",
                                "peer": work.peer.to_string(),
                            })
                        );
                    }
                }
                HeartbeatAckRetryResult::Timeout => {
                    task_metrics.retry_timeouts.fetch_add(1, Ordering::Relaxed);
                    if json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "heartbeat-ack-send-timeout",
                                "peer": work.peer.to_string(),
                                "timeout_ms": HEARTBEAT_ACK_RETRY_SEND_TIMEOUT.as_millis(),
                            })
                        );
                    }
                }
            }
        }
    });

    if json_events {
        let report_metrics = metrics.clone();
        tokio::spawn(async move {
            let mut interval = time::interval(Duration::from_secs(1));
            let mut last_overflow = 0;
            let mut last_immediate_failures = 0;
            loop {
                interval.tick().await;
                let overflow = report_metrics.retry_overflow.load(Ordering::Relaxed);
                let immediate_failures = report_metrics.immediate_failures.load(Ordering::Relaxed);
                if overflow != last_overflow || immediate_failures != last_immediate_failures {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "heartbeat-ack-delivery-pressure",
                            "retry_overflow_total": overflow,
                            "retry_overflow_delta": overflow.saturating_sub(last_overflow),
                            "immediate_failures_total": immediate_failures,
                            "immediate_failures_delta": immediate_failures.saturating_sub(last_immediate_failures),
                            "retry_queue_depth": report_metrics.depth.load(Ordering::Relaxed),
                        })
                    );
                    last_overflow = overflow;
                    last_immediate_failures = immediate_failures;
                }
            }
        });
    }

    HeartbeatAckSender {
        socket,
        retry_tx,
        metrics,
    }
}

fn sync_heartbeat_ack_metrics(
    sender: &HeartbeatAckSender,
    control_plane: &mut ServerControlPlaneStatus,
) {
    let immediate_sent = sender.metrics.immediate_sent.load(Ordering::Relaxed);
    let retry_sent = sender.metrics.retry_sent.load(Ordering::Relaxed);
    let immediate_failures = sender.metrics.immediate_failures.load(Ordering::Relaxed);
    let retry_failures = sender.metrics.retry_failures.load(Ordering::Relaxed);
    let retry_timeouts = sender.metrics.retry_timeouts.load(Ordering::Relaxed);
    control_plane.heartbeat_acks_queued = sender.metrics.retry_queued.load(Ordering::Relaxed);
    control_plane.heartbeat_acks_sent = immediate_sent.saturating_add(retry_sent);
    control_plane.heartbeat_ack_backpressure = sender.metrics.would_block.load(Ordering::Relaxed);
    control_plane.heartbeat_ack_drops = sender.metrics.retry_overflow.load(Ordering::Relaxed);
    control_plane.heartbeat_ack_failures = immediate_failures
        .saturating_add(retry_failures)
        .saturating_add(retry_timeouts);
    control_plane.heartbeat_ack_queue_depth = sender.metrics.depth.load(Ordering::Relaxed);
    control_plane.heartbeat_ack_immediate_sent = immediate_sent;
    control_plane.heartbeat_ack_retry_queued = sender.metrics.retry_queued.load(Ordering::Relaxed);
    control_plane.heartbeat_ack_retry_sent = retry_sent;
    control_plane.heartbeat_ack_would_block = sender.metrics.would_block.load(Ordering::Relaxed);
    control_plane.heartbeat_ack_retry_overflow =
        sender.metrics.retry_overflow.load(Ordering::Relaxed);
    control_plane.heartbeat_ack_retry_timeouts = retry_timeouts;
    control_plane.heartbeat_ack_immediate_failures = immediate_failures;
    control_plane.heartbeat_ack_retry_failures = retry_failures;
}

fn acknowledge_authenticated_heartbeat(
    header: &XBondHeader,
    key: &XBondKey,
    peer: SocketAddr,
    sender: &HeartbeatAckSender,
    json_events: bool,
) -> bool {
    let reply = build_ack_frame_from_header(header);
    match reply.encode_sealed(key) {
        Ok(encoded) => sender.dispatch(ControlSendWork { encoded, peer }),
        Err(error) => {
            sender.record_encode_failure();
            if json_events {
                println!(
                    "{}",
                    serde_json::json!({
                        "event": "heartbeat-ack-encode-failed",
                        "peer": peer.to_string(),
                        "error": error.to_string(),
                    })
                );
            }
            false
        }
    }
}

fn enqueue_control_datagram(
    tx: &mpsc::Sender<ControlSendWork>,
    encoded: Vec<u8>,
    peer: SocketAddr,
    control_plane: &mut ServerControlPlaneStatus,
) -> bool {
    match tx.try_send(ControlSendWork { encoded, peer }) {
        Ok(()) => true,
        Err(mpsc::error::TrySendError::Full(_)) => {
            control_plane.control_send_queue_full =
                control_plane.control_send_queue_full.saturating_add(1);
            false
        }
        Err(mpsc::error::TrySendError::Closed(_)) => false,
    }
}

fn enqueue_control_message(
    tx: &mpsc::Sender<ControlSendWork>,
    key: &XBondKey,
    peer: SocketAddr,
    session_id: u64,
    control_sequence: &mut u64,
    message: &XBondControlMessage,
    control_plane: &mut ServerControlPlaneStatus,
) -> Result<bool> {
    *control_sequence = control_sequence.saturating_add(1);
    let mut header = XBondHeader::new(
        PacketKind::Control,
        session_id,
        *control_sequence,
        now_micros(),
        0,
    );
    header.flags = FLAG_SERVER_TO_CLIENT;
    let frame = XBondFrame::new(header, serde_json::to_vec(message)?);
    Ok(enqueue_control_datagram(
        tx,
        frame.encode_sealed(key)?,
        peer,
        control_plane,
    ))
}

fn enqueue_schedule_accepted(
    tx: &mpsc::Sender<ControlSendWork>,
    key: &XBondKey,
    peer: SocketAddr,
    session_id: u64,
    schedule_generation: u64,
    control_sequence: &mut u64,
    control_plane: &mut ServerControlPlaneStatus,
) -> Result<bool> {
    enqueue_control_message(
        tx,
        key,
        peer,
        session_id,
        control_sequence,
        &XBondControlMessage::ScheduleAccepted {
            session_id,
            schedule_generation,
        },
        control_plane,
    )
}

fn enqueue_control_message_to_peers(
    tx: &mpsc::Sender<ControlSendWork>,
    key: &XBondKey,
    peers: &HashMap<u16, PeerState>,
    session_id: u64,
    control_sequence: &mut u64,
    message: &XBondControlMessage,
    control_plane: &mut ServerControlPlaneStatus,
) -> Result<usize> {
    let mut queued = 0usize;
    for (_, peer) in fresh_peers(peers, monotonic_micros()) {
        queued += usize::from(enqueue_control_message(
            tx,
            key,
            peer,
            session_id,
            control_sequence,
            message,
            control_plane,
        )?);
    }
    Ok(queued)
}

fn spawn_tun_reader(
    mut tun_reader: XBondTun,
    tun_packet_tx: mpsc::Sender<Vec<u8>>,
    tun_mtu: usize,
    worker_exit_tx: mpsc::Sender<TunWorkerExit>,
    fault: TunFaultInjection,
) {
    let tun_name = tun_reader.name().to_string();
    let task = tokio::task::spawn_blocking(move || -> std::result::Result<(), String> {
        let mut buf = vec![0u8; tun_mtu];
        let mut packets_read = 0u64;
        loop {
            if injected_tun_failure(fault.read_fail_after_packets, packets_read) {
                return Err(format!(
                    "injected TUN reader failure after {packets_read} packets"
                ));
            }
            match tun_reader.read_packet(&mut buf) {
                Ok(len) => {
                    packets_read = packets_read.saturating_add(1);
                    if tun_packet_tx.blocking_send(buf[..len].to_vec()).is_err() {
                        return Err("server tunnel stopped accepting TUN packets".to_string());
                    }
                }
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) => return Err(error.to_string()),
            }
        }
    });

    tokio::spawn(async move {
        let reason = match task.await {
            Ok(Ok(())) => "TUN reader exited unexpectedly".to_string(),
            Ok(Err(error)) => error,
            Err(error) => format!("blocking TUN reader task failed: {error}"),
        };
        let _ = worker_exit_tx
            .send(TunWorkerExit {
                worker: "reader",
                reason: format!("{tun_name}: {reason}"),
            })
            .await;
    });
}

fn spawn_tun_writer(
    mut tun_writer: XBondTun,
    capacity: usize,
    worker_exit_tx: mpsc::Sender<TunWorkerExit>,
    payload_recycle: PayloadRecycle,
    fault: TunFaultInjection,
) -> TunWriterHandle {
    let capacity = capacity.max(1);
    let (tx, mut rx) = mpsc::channel::<TunWriteBatch>(capacity);
    let telemetry = Arc::new(TunWriterTelemetry::default());
    let worker_telemetry = telemetry.clone();
    let tun_name = tun_writer.name().to_string();
    let task = tokio::task::spawn_blocking(move || -> std::result::Result<(), String> {
        let mut packets_written = 0u64;
        while let Some(batch) = rx.blocking_recv() {
            let packet_count = batch.packets.len();
            let enqueued_at_micros = batch.enqueued_at_micros;
            let write_result = (|| -> std::result::Result<(), String> {
                for mut packet in batch.packets {
                    if injected_tun_failure(fault.write_fail_after_packets, packets_written) {
                        worker_telemetry
                            .write_errors
                            .fetch_add(1, Ordering::Relaxed);
                        return Err(format!(
                            "injected TUN writer failure after {packets_written} packets"
                        ));
                    }
                    if fault.write_delay_ms > 0 {
                        std::thread::sleep(Duration::from_millis(fault.write_delay_ms));
                    }
                    let started = Instant::now();
                    let write_result = tun_writer.write_packet(&packet.payload);
                    recycle_payload(Some(&payload_recycle), std::mem::take(&mut packet.payload));
                    if let Err(error) = write_result {
                        worker_telemetry
                            .write_errors
                            .fetch_add(1, Ordering::Relaxed);
                        return Err(error.to_string());
                    }

                    let latency_micros =
                        started.elapsed().as_micros().min(u128::from(u64::MAX)) as u64;
                    TUN_MICROS_TOTAL.fetch_add(latency_micros, Ordering::Relaxed);
                    worker_telemetry
                        .last_write_latency_micros
                        .store(latency_micros, Ordering::Relaxed);
                    worker_telemetry
                        .max_write_latency_micros
                        .fetch_max(latency_micros, Ordering::Relaxed);
                    worker_telemetry
                        .packets_written
                        .fetch_add(1, Ordering::Relaxed);
                    packets_written = packets_written.saturating_add(1);
                    if packet.path_id == u16::MAX {
                        worker_telemetry
                            .repair_packets_written
                            .fetch_add(1, Ordering::Relaxed);
                    }
                }
                Ok(())
            })();
            worker_telemetry.release_packets(packet_count, enqueued_at_micros);
            write_result?;
        }
        Err("TUN writer queue closed unexpectedly".to_string())
    });

    tokio::spawn(async move {
        let reason = match task.await {
            Ok(Ok(())) => "TUN writer exited unexpectedly".to_string(),
            Ok(Err(error)) => error,
            Err(error) => format!("blocking TUN writer task failed: {error}"),
        };
        let _ = worker_exit_tx
            .send(TunWorkerExit {
                worker: "writer",
                reason: format!("{tun_name}: {reason}"),
            })
            .await;
    });

    TunWriterHandle {
        tx,
        telemetry,
        capacity,
    }
}

fn try_enqueue_pending_tun_write(
    writer: &TunWriterHandle,
    pending: &mut Option<PendingTunWriteBatch>,
) -> Result<Option<TunEnqueueOutcome>> {
    let pending_packet_count = pending
        .as_ref()
        .map(|batch| batch.packets.len())
        .unwrap_or_default();
    if pending_packet_count == 0 {
        return Ok(Some(TunEnqueueOutcome::default()));
    }
    let current_depth = writer.telemetry.queue_depth.load(Ordering::Relaxed) as usize;
    let available = writer.capacity.saturating_sub(current_depth);
    let packet_count = pending_packet_count.min(available);
    if packet_count == 0 {
        if writer.tx.is_closed() {
            return Err(anyhow::anyhow!("XBond server TUN writer stopped"));
        }
        return Ok(None);
    }
    let enqueued_at_micros = monotonic_micros();
    if !writer
        .telemetry
        .try_reserve_packets(packet_count, writer.capacity, enqueued_at_micros)
    {
        if writer.tx.is_closed() {
            return Err(anyhow::anyhow!("XBond server TUN writer stopped"));
        }
        return Ok(None);
    }
    let mut admission = pending.take().expect("pending TUN write disappeared");
    let remaining_packets = admission.packets.split_off(packet_count);
    if !remaining_packets.is_empty() {
        *pending = Some(PendingTunWriteBatch {
            packets: remaining_packets,
            deadline: admission.deadline,
            enqueue_deadline: admission.enqueue_deadline,
        });
    }
    match writer.tx.try_send(TunWriteBatch {
        packets: admission.packets,
        enqueued_at_micros,
    }) {
        Ok(()) => Ok(Some(TunEnqueueOutcome {
            enqueued: packet_count as u64,
        })),
        Err(mpsc::error::TrySendError::Closed(batch)) => {
            writer
                .telemetry
                .release_packets(batch.packets.len(), batch.enqueued_at_micros);
            Err(anyhow::anyhow!("XBond server TUN writer stopped"))
        }
        Err(mpsc::error::TrySendError::Full(batch)) => {
            writer
                .telemetry
                .release_packets(batch.packets.len(), batch.enqueued_at_micros);
            let mut restored_packets = batch.packets;
            if let Some(remaining) = pending.take() {
                restored_packets.extend(remaining.packets);
                *pending = Some(PendingTunWriteBatch {
                    packets: restored_packets,
                    deadline: remaining.deadline,
                    enqueue_deadline: remaining.enqueue_deadline,
                });
            } else {
                *pending = Some(PendingTunWriteBatch {
                    packets: restored_packets,
                    deadline: admission.deadline,
                    enqueue_deadline: admission.enqueue_deadline,
                });
            }
            Ok(None)
        }
    }
}

fn start_or_append_tun_write(
    writer: &TunWriterHandle,
    pending: &mut Option<PendingTunWriteBatch>,
    packets: Vec<ReorderedPacket>,
    max_pending_packets: usize,
) -> Result<TunEnqueueOutcome> {
    if packets.is_empty() {
        return Ok(TunEnqueueOutcome::default());
    }
    if packets.len() > max_pending_packets {
        writer
            .telemetry
            .saturation_failures
            .fetch_add(1, Ordering::Relaxed);
        return Err(anyhow::anyhow!(
            "XBond server pending TUN admission received {} packets, exceeding its bounded {max_pending_packets}-packet limit; terminating the tunnel for a clean restart",
            packets.len()
        ));
    }
    if let Some(batch) = pending.as_mut() {
        if batch.packets.len().saturating_add(packets.len()) > max_pending_packets {
            writer
                .telemetry
                .saturation_failures
                .fetch_add(1, Ordering::Relaxed);
            return Err(anyhow::anyhow!(
                "XBond server pending TUN admission exceeded its bounded {max_pending_packets}-packet limit; terminating the tunnel for a clean restart"
            ));
        }
        batch.packets.extend(packets);
        return Ok(TunEnqueueOutcome::default());
    }

    *pending = Some(PendingTunWriteBatch::new(
        packets,
        TUN_WRITE_ENQUEUE_DEADLINE,
    ));
    Ok(try_enqueue_pending_tun_write(writer, pending)?.unwrap_or_default())
}

fn tun_admission_timeout_error(
    writer: &TunWriterHandle,
    pending: &PendingTunWriteBatch,
) -> anyhow::Error {
    writer
        .telemetry
        .saturation_failures
        .fetch_add(1, Ordering::Relaxed);
    anyhow::anyhow!(
        "XBond server TUN writer remained saturated and could not atomically admit {} packets within {} ms; terminating the tunnel for a clean restart",
        pending.packets.len(),
        pending.enqueue_deadline.as_millis()
    )
}

#[cfg(test)]
async fn enqueue_tun_packets_with_deadline(
    writer: &TunWriterHandle,
    packets: Vec<ReorderedPacket>,
    enqueue_deadline: Duration,
) -> Result<TunEnqueueOutcome> {
    let mut pending = Some(PendingTunWriteBatch::new(packets, enqueue_deadline));
    let mut total = TunEnqueueOutcome::default();
    loop {
        if let Some(outcome) = try_enqueue_pending_tun_write(writer, &mut pending)? {
            total.enqueued = total.enqueued.saturating_add(outcome.enqueued);
            if pending.is_none() {
                return Ok(total);
            }
        }
        let admission = pending.as_ref().expect("pending TUN write disappeared");
        let remaining = admission.deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(tun_admission_timeout_error(writer, admission));
        }
        time::sleep(remaining.min(Duration::from_millis(2))).await;
    }
}

fn sync_tun_writer_telemetry(
    writer: Option<&TunWriterHandle>,
    control_plane: &mut ServerControlPlaneStatus,
    repair: &mut XBondRepairStatus,
    data_packets_forwarded: &mut u64,
) {
    let Some(writer) = writer else {
        return;
    };
    let telemetry = &writer.telemetry;
    let now = monotonic_micros();
    let oldest = telemetry.oldest_enqueued_at_micros.load(Ordering::Relaxed);
    control_plane.tun_write_queue_depth = telemetry.queue_depth.load(Ordering::Relaxed);
    control_plane.tun_write_queue_peak_depth = telemetry.max_queue_depth.load(Ordering::Relaxed);
    control_plane.tun_write_queue_capacity = writer.capacity as u64;
    control_plane.tun_write_oldest_age_ms = if oldest == 0 {
        0
    } else {
        now.saturating_sub(oldest) / 1_000
    };
    control_plane.tun_write_latency_micros =
        telemetry.last_write_latency_micros.load(Ordering::Relaxed);
    control_plane.tun_write_max_latency_micros =
        telemetry.max_write_latency_micros.load(Ordering::Relaxed);
    control_plane.tun_write_errors = telemetry.write_errors.load(Ordering::Relaxed);
    control_plane.tun_packets_written = telemetry.packets_written.load(Ordering::Relaxed);
    control_plane.tun_write_queue_saturated_drops = 0;
    control_plane.tun_write_queue_saturation_failures =
        telemetry.saturation_failures.load(Ordering::Relaxed);
    *data_packets_forwarded = control_plane.tun_packets_written;
    repair.frames_delivered = telemetry.repair_packets_written.load(Ordering::Relaxed);
}

fn is_emsgsize(error: &std::io::Error) -> bool {
    matches!(error.raw_os_error(), Some(90) | Some(10040))
        || error
            .to_string()
            .to_ascii_lowercase()
            .contains("message too long")
}

fn record_return_pmtu_error(
    status: &mut ServerReturnPmtuStatus,
    report: &ReturnSendReport,
) -> bool {
    if !report.emsgsize {
        return false;
    }
    status.emsgsize_errors = status.emsgsize_errors.saturating_add(1);
    let count = status
        .emsgsize_errors_by_path
        .entry(report.path_id)
        .or_default();
    *count = count.saturating_add(1);
    true
}

async fn send_datagram_before_deadline(
    socket: &UdpSocket,
    encoded: &[u8],
    peer: SocketAddr,
    deadline: Instant,
) -> std::result::Result<usize, BoundedDatagramSendError> {
    let remaining = deadline.saturating_duration_since(Instant::now());
    if remaining.is_zero() {
        return Err(BoundedDatagramSendError::DeadlineExpired);
    }
    match time::timeout(remaining, socket.send_to(encoded, peer)).await {
        Ok(Ok(bytes)) => Ok(bytes),
        Ok(Err(error)) => Err(BoundedDatagramSendError::Io(error)),
        Err(_) => Err(BoundedDatagramSendError::DeadlineExpired),
    }
}

async fn reserve_primary_return_slot(
    tx: mpsc::Sender<ReturnSendWork>,
    deadline: Instant,
) -> std::result::Result<mpsc::OwnedPermit<ReturnSendWork>, PrimaryReturnReserveError> {
    let remaining = deadline.saturating_duration_since(Instant::now());
    if remaining.is_zero() {
        return Err(PrimaryReturnReserveError::DeadlineExpired);
    }
    match time::timeout(remaining, tx.reserve_owned()).await {
        Ok(Ok(permit)) => Ok(permit),
        Ok(Err(_)) => Err(PrimaryReturnReserveError::Closed),
        Err(_) => Err(PrimaryReturnReserveError::DeadlineExpired),
    }
}

fn spawn_primary_return_sender(
    socket: Arc<UdpSocket>,
    key: XBondKey,
    capacity: usize,
    report_tx: mpsc::Sender<ReturnSendReport>,
) -> mpsc::Sender<ReturnSendWork> {
    let (tx, mut rx) = mpsc::channel::<ReturnSendWork>(capacity.max(1));
    tokio::spawn(async move {
        let mut encoded = Vec::with_capacity(4096);
        let mut consecutive_deadline_expiries = 0u32;
        while let Some(work) = rx.recv().await {
            let report = match encode_sealed_payload_into(
                &work.header,
                work.payload.as_slice(),
                &key,
                &mut encoded,
            ) {
                Ok(()) => {
                    let attempted_datagram_bytes = encoded.len();
                    match send_datagram_before_deadline(&socket, &encoded, work.peer, work.deadline)
                        .await
                    {
                        Ok(_) => {
                            PRIMARY_RETURN_DRAINED.fetch_add(1, Ordering::Relaxed);
                            consecutive_deadline_expiries = 0;
                            ReturnSendReport {
                                path_id: work.header.path_id,
                                packet_kind: work.packet_kind,
                                success: true,
                                peer: work.peer,
                                error: None,
                                emsgsize: false,
                                deadline_expired: false,
                                force_restart: false,
                                attempted_datagram_bytes,
                            }
                        }
                        Err(BoundedDatagramSendError::Io(error)) => {
                            consecutive_deadline_expiries = 0;
                            ReturnSendReport {
                                path_id: work.header.path_id,
                                packet_kind: work.packet_kind,
                                success: false,
                                peer: work.peer,
                                error: Some(error.to_string()),
                                emsgsize: is_emsgsize(&error),
                                deadline_expired: false,
                                force_restart: false,
                                attempted_datagram_bytes,
                            }
                        }
                        Err(BoundedDatagramSendError::DeadlineExpired) => {
                            consecutive_deadline_expiries =
                                consecutive_deadline_expiries.saturating_add(1);
                            ReturnSendReport {
                                path_id: work.header.path_id,
                                packet_kind: work.packet_kind,
                                success: false,
                                peer: work.peer,
                                error: Some(format!(
                                    "primary return UDP send exceeded its {} ms deadline",
                                    PRIMARY_RETURN_SEND_DEADLINE.as_millis()
                                )),
                                emsgsize: false,
                                deadline_expired: true,
                                force_restart: consecutive_deadline_expiries
                                    >= PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES,
                                attempted_datagram_bytes,
                            }
                        }
                    }
                }
                Err(error) => ReturnSendReport {
                    path_id: work.header.path_id,
                    packet_kind: work.packet_kind,
                    success: false,
                    peer: work.peer,
                    error: Some(error.to_string()),
                    emsgsize: false,
                    deadline_expired: false,
                    force_restart: false,
                    attempted_datagram_bytes: 0,
                },
            };
            if !report.success && report_tx.send(report).await.is_err() {
                break;
            }
        }
    });
    tx
}

fn recommended_server_repair_cache_bytes(
    observed_bits_per_second: u64,
    configured_maximum_bytes: usize,
) -> usize {
    let maximum_bytes = configured_maximum_bytes.max(1);
    recommended_repair_cache_bytes(
        observed_bits_per_second,
        REPAIR_CACHE_TTL_MICROS,
        MIN_REPAIR_CACHE_BYTES.min(maximum_bytes),
        maximum_bytes,
    )
}

fn new_server_resend_cache(configured_maximum_bytes: usize) -> ResendCache {
    ResendCache::new_with_byte_capacity(
        REPAIR_CACHE_CAPACITY,
        recommended_server_repair_cache_bytes(
            INITIAL_REPAIR_CACHE_BITS_PER_SECOND,
            configured_maximum_bytes,
        ),
        REPAIR_CACHE_TTL_MICROS,
    )
}

fn refresh_server_repair_cache_status(
    resend_cache: &mut ResendCache,
    status: &mut XBondRepairCacheStatus,
    now_micros: u64,
) {
    let entries_before = resend_cache.len();
    let accounted_bytes_before = resend_cache.accounted_bytes_len();
    resend_cache.prune(now_micros);
    status.entries = resend_cache.len();
    status.accounted_bytes = resend_cache.accounted_bytes_len();
    status.byte_capacity = resend_cache.byte_capacity();
    status.prune_runs = status.prune_runs.saturating_add(1);
    status.last_pruned_at_micros = now_micros;
    status.last_pruned_entries = entries_before.saturating_sub(status.entries);
    status.last_pruned_accounted_bytes =
        accounted_bytes_before.saturating_sub(status.accounted_bytes);
    status.total_pruned_entries = status
        .total_pruned_entries
        .saturating_add(status.last_pruned_entries as u64);
    status.total_pruned_accounted_bytes = status
        .total_pruned_accounted_bytes
        .saturating_add(status.last_pruned_accounted_bytes as u64);
    status.quiescent = status.entries == 0 && status.accounted_bytes == 0;
    if status.quiescent {
        status.quiescent_since_micros = status.quiescent_since_micros.or(Some(now_micros));
    } else {
        status.quiescent_since_micros = None;
    }
}

#[cfg(target_os = "linux")]
struct ServerUdpBatchReceiver {
    buffers: Vec<Vec<u8>>,
    _iovecs: Vec<libc::iovec>,
    addresses: Vec<libc::sockaddr_storage>,
    messages: Vec<libc::mmsghdr>,
}

// The raw pointers reference this receiver's fixed heap allocations, which are never resized and
// are accessed exclusively through `&mut self` by one receive task.
#[cfg(target_os = "linux")]
unsafe impl Send for ServerUdpBatchReceiver {}

#[cfg(target_os = "linux")]
impl ServerUdpBatchReceiver {
    fn new(capacity: usize) -> Self {
        let mut buffers = (0..capacity)
            .map(|_| vec![0u8; MAX_UDP_DATAGRAM_BYTES])
            .collect::<Vec<_>>();
        let mut iovecs = buffers
            .iter_mut()
            .map(|buffer| libc::iovec {
                iov_base: buffer.as_mut_ptr().cast(),
                iov_len: buffer.len(),
            })
            .collect::<Vec<_>>();
        let mut addresses = vec![unsafe { std::mem::zeroed::<libc::sockaddr_storage>() }; capacity];
        let messages = iovecs
            .iter_mut()
            .zip(addresses.iter_mut())
            .map(|(iov, address)| {
                let mut message = unsafe { std::mem::zeroed::<libc::mmsghdr>() };
                message.msg_hdr.msg_iov = std::ptr::from_mut(iov);
                message.msg_hdr.msg_iovlen = 1;
                message.msg_hdr.msg_name = std::ptr::from_mut(address).cast();
                message.msg_hdr.msg_namelen =
                    std::mem::size_of::<libc::sockaddr_storage>() as libc::socklen_t;
                message
            })
            .collect::<Vec<_>>();
        Self {
            buffers,
            _iovecs: iovecs,
            addresses,
            messages,
        }
    }

    fn receive(&mut self, socket: &UdpSocket) -> std::io::Result<usize> {
        for message in &mut self.messages {
            message.msg_len = 0;
            message.msg_hdr.msg_namelen =
                std::mem::size_of::<libc::sockaddr_storage>() as libc::socklen_t;
        }
        socket.try_io(Interest::READABLE, || {
            let count = unsafe {
                libc::recvmmsg(
                    socket.as_raw_fd(),
                    self.messages.as_mut_ptr(),
                    self.messages.len().min(u32::MAX as usize) as u32,
                    libc::MSG_DONTWAIT,
                    std::ptr::null_mut(),
                )
            };
            if count < 0 {
                return Err(std::io::Error::last_os_error());
            }
            Ok(count as usize)
        })
    }

    fn length(&self, index: usize) -> usize {
        self.messages[index].msg_len as usize
    }

    fn peer(&self, index: usize) -> std::io::Result<SocketAddr> {
        socket_addr_from_storage(&self.addresses[index])
    }

    fn packet(&self, index: usize, length: usize) -> &[u8] {
        &self.buffers[index][..length]
    }
}

#[cfg(target_os = "linux")]
fn socket_addr_from_storage(address: &libc::sockaddr_storage) -> std::io::Result<SocketAddr> {
    if i32::from(address.ss_family) != libc::AF_INET {
        return Err(std::io::Error::new(
            ErrorKind::Unsupported,
            "uLink currently accepts IPv4 UDP peers only",
        ));
    }
    let address = unsafe { &*std::ptr::from_ref(address).cast::<libc::sockaddr_in>() };
    Ok(SocketAddr::new(
        IpAddr::V4(Ipv4Addr::from(address.sin_addr.s_addr.to_ne_bytes())),
        u16::from_be(address.sin_port),
    ))
}

#[cfg(not(target_os = "linux"))]
struct ServerUdpBatchReceiver {
    buffers: Vec<Vec<u8>>,
    lengths: Vec<usize>,
    peers: Vec<Option<SocketAddr>>,
}

#[cfg(not(target_os = "linux"))]
impl ServerUdpBatchReceiver {
    fn new(capacity: usize) -> Self {
        Self {
            buffers: (0..capacity)
                .map(|_| vec![0u8; MAX_UDP_DATAGRAM_BYTES])
                .collect(),
            lengths: vec![0; capacity],
            peers: vec![None; capacity],
        }
    }

    fn receive(&mut self, socket: &UdpSocket) -> std::io::Result<usize> {
        let mut count = 0;
        for index in 0..self.buffers.len() {
            match socket.try_recv_from(&mut self.buffers[index]) {
                Ok((length, peer)) => {
                    self.lengths[index] = length;
                    self.peers[index] = Some(peer);
                    count += 1;
                }
                Err(error) if error.kind() == ErrorKind::WouldBlock => break,
                Err(error) => return Err(error),
            }
        }
        Ok(count)
    }

    fn length(&self, index: usize) -> usize {
        self.lengths[index]
    }

    fn peer(&self, index: usize) -> std::io::Result<SocketAddr> {
        self.peers[index]
            .ok_or_else(|| std::io::Error::new(ErrorKind::InvalidData, "missing UDP peer address"))
    }

    fn packet(&self, index: usize, length: usize) -> &[u8] {
        &self.buffers[index][..length]
    }
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let key_text = std::env::var(&args.key_env)?;
    let key = XBondKey::from_passphrase(&key_text);
    let server_health = start_server_health_monitor(&args);
    let socket = Arc::new(bind_udp_socket(&args.bind, args.udp_socket_buffer_bytes).await?);
    let (control_send_tx, control_datagrams_sent, control_send_failures) =
        spawn_control_sender(socket.clone(), args.json_events);
    let heartbeat_ack_sender = spawn_heartbeat_ack_sender(socket.clone(), args.json_events);
    let mut receiver = FrameReceiver::new(args.realtime_deadline_ms * 1_000, 8192);
    let tun = match args.tun_name.as_deref() {
        Some(name) => Some(XBondTun::open(name, args.tun_mtu)?),
        None => None,
    };
    let mut hold_controller =
        IngressReorderHoldController::new(IngressReorderHoldSettings::from_args(&args));
    let mut current_ingress_hold_micros = hold_controller.current_hold_micros();
    let mut data_packets_received = 0u64;
    let mut data_packets_forwarded = 0u64;
    let mut fec_packets_received = 0u64;
    let mut fec_packets_recovered = 0u64;
    let mut invalid_fec_packets_dropped = 0u64;
    let mut non_ipv4_packets_dropped = 0u64;
    let mut fec_recovery = FecRecovery::new(8192);
    let mut reorder = PacketReorderBuffer::with_initial_sequence(
        args.ingress_reorder_capacity,
        current_ingress_hold_micros,
        1,
    );
    let mut peers: HashMap<u16, PeerState> = HashMap::new();
    let mut return_senders: HashMap<u16, ReturnSenderHandle> = HashMap::new();
    let mut return_control: Option<ReturnControl> = None;
    let mut resend_cache = new_server_resend_cache(args.repair_cache_bytes);
    let mut repair = XBondRepairStatus::default();
    let mut ingress_repair_limiter = IngressRepairLimiter::default();
    let mut return_pmtu = ServerReturnPmtuStatus::default();
    let mut return_payload_bytes_since_budget_update = 0u64;
    let mut repair_budget_last_updated = Instant::now();
    let mut reverse_sequence = initial_reverse_sequence();
    let mut control_sequence = initial_control_sequence();
    let mut last_server_recovery_status_sent = Instant::now();
    let mut last_session_id = 0u64;
    let mut session_gate = ServerSessionGate::new();
    let mut last_restart_required_sent: Option<Instant> = None;
    let mut reorder_session_id = 0u64;
    let mut reorder_tick = time::interval(Duration::from_millis(
        args.realtime_deadline_ms.clamp(5, 50),
    ));
    let mut tun_admission_tick = time::interval(Duration::from_millis(2));
    tun_admission_tick.set_missed_tick_behavior(time::MissedTickBehavior::Skip);
    let mut status_tick = time::interval(Duration::from_secs(1));
    let mut json_status_event_counter = 0u32;
    let mut control_plane = ServerControlPlaneStatus::default();
    let mut pending_primary_return: Option<PendingPrimaryReturn> = None;
    let mut pending_tun_write: Option<PendingTunWriteBatch> = None;
    let mut consecutive_primary_return_enqueue_expiries = 0u32;
    let tun_fault = TunFaultInjection {
        write_delay_ms: args.fault_tun_write_delay_ms,
        write_fail_after_packets: args.fault_tun_write_fail_after_packets,
        read_fail_after_packets: args.fault_tun_read_fail_after_packets,
    };

    let critical_control_frames =
        Arc::new(BoundedCoalescingQueue::new(CONTROL_QUEUE_CAPACITY, false));
    let latest_schedule_frames =
        Arc::new(BoundedCoalescingQueue::new(RETIRED_SESSION_CAPACITY, false));
    let latest_heartbeat_frames =
        Arc::new(BoundedCoalescingQueue::new(CONTROL_QUEUE_CAPACITY, true));
    let (repair_frame_tx, mut repair_frame_rx) =
        mpsc::channel::<InboundServerFrame>(CONTROL_QUEUE_CAPACITY);
    let (payload_frame_tx, mut payload_frame_rx) =
        mpsc::channel::<InboundServerFrame>(args.inbound_queue_capacity.max(1));
    let (payload_recycle_tx, mut payload_recycle_rx) =
        mpsc::channel::<Vec<u8>>(PAYLOAD_POOL_CAPACITY);
    let payload_pool_telemetry = Arc::new(PacketPoolTelemetry::new());
    let payload_recycle = PayloadRecycle {
        tx: payload_recycle_tx,
        telemetry: payload_pool_telemetry.clone(),
    };
    let ingress_payload_queue_drops = Arc::new(AtomicU64::new(0));
    let ingress_repair_queue_drops = Arc::new(AtomicU64::new(0));
    let ingress_control_frames_coalesced = Arc::new(AtomicU64::new(0));
    let ingress_duplicates_coalesced = Arc::new(AtomicU64::new(0));
    let (send_report_tx, mut send_report_rx) =
        mpsc::channel::<ReturnSendReport>(SEND_REPORT_QUEUE_CAPACITY);
    let primary_return_tx = spawn_primary_return_sender(
        socket.clone(),
        key.clone(),
        args.tun_queue_capacity.max(1),
        send_report_tx.clone(),
    );
    let recv_socket = socket.clone();
    let recv_key = key.clone();
    let recv_critical_control_frames = critical_control_frames.clone();
    let recv_latest_schedule_frames = latest_schedule_frames.clone();
    let recv_latest_heartbeat_frames = latest_heartbeat_frames.clone();
    let recv_payload_queue_drops = ingress_payload_queue_drops.clone();
    let recv_repair_queue_drops = ingress_repair_queue_drops.clone();
    let recv_control_frames_coalesced = ingress_control_frames_coalesced.clone();
    let recv_duplicates_coalesced = ingress_duplicates_coalesced.clone();
    let recv_payload_recycle = payload_recycle.clone();
    let recv_payload_pool_telemetry = payload_pool_telemetry.clone();
    let recv_heartbeat_ack_sender = heartbeat_ack_sender.clone();
    let recv_json_events = args.json_events;
    let recv_batch_size = args.udp_receive_batch_size.clamp(1, 256);
    let mut udp_receiver_task = tokio::spawn(async move {
        let mut batch_receiver = ServerUdpBatchReceiver::new(recv_batch_size);
        let mut payload =
            take_payload_buffer(&mut payload_recycle_rx, &recv_payload_pool_telemetry);
        let mut pre_admission_duplicates = DuplicateWindow::new(8192);
        loop {
            if let Err(error) = recv_socket.readable().await {
                eprintln!("xbond server UDP readiness error: {error}");
                time::sleep(Duration::from_millis(50)).await;
                continue;
            }
            let batch_started = Instant::now();
            let batch_count = match batch_receiver.receive(&recv_socket) {
                Ok(count) => count,
                Err(error) if error.kind() == ErrorKind::WouldBlock => 0,
                Err(error) => {
                    eprintln!("xbond server UDP recv error: {error}");
                    0
                }
            };
            for index in 0..batch_count {
                let len = batch_receiver.length(index);
                let peer = match batch_receiver.peer(index) {
                    Ok(peer) => peer,
                    Err(error) => {
                        eprintln!("xbond server UDP peer decode error: {error}");
                        continue;
                    }
                };
                RECEIVE_DATAGRAMS.fetch_add(1, Ordering::Relaxed);
                let decode_started = Instant::now();
                let Ok(header) = decode_sealed_payload_into(
                    batch_receiver.packet(index, len),
                    &recv_key,
                    &mut payload,
                ) else {
                    continue;
                };
                RECEIVE_DECODE_MICROS_TOTAL.fetch_add(
                    decode_started
                        .elapsed()
                        .as_micros()
                        .min(u128::from(u64::MAX)) as u64,
                    Ordering::Relaxed,
                );
                if !is_client_originated_frame(&header) {
                    payload.clear();
                    continue;
                }
                if header.kind == PacketKind::Heartbeat {
                    acknowledge_authenticated_heartbeat(
                        &header,
                        &recv_key,
                        peer,
                        &recv_heartbeat_ack_sender,
                        recv_json_events,
                    );
                }
                let duplicate_class = pre_admission_duplicate_class(header.kind);
                if duplicate_class.is_some_and(|class| {
                    pre_admission_duplicates.contains_key_class(
                        header.session_id,
                        header.sequence,
                        class,
                    )
                }) {
                    recv_duplicates_coalesced.fetch_add(1, Ordering::Relaxed);
                    payload.clear();
                    continue;
                }
                let frame_session_id = header.session_id;
                let frame_sequence = header.sequence;
                let frame_payload = std::mem::replace(
                    &mut payload,
                    take_payload_buffer(&mut payload_recycle_rx, &recv_payload_pool_telemetry),
                );
                let inbound = InboundServerFrame::new(
                    XBondFrame::new(header, frame_payload),
                    peer,
                    duplicate_class.is_some(),
                    Some(recv_payload_recycle.clone()),
                );
                let enqueue_started = Instant::now();
                match dispatch_inbound_frame(
                    &recv_critical_control_frames,
                    &recv_latest_schedule_frames,
                    &recv_latest_heartbeat_frames,
                    &repair_frame_tx,
                    &payload_frame_tx,
                    inbound,
                    &recv_payload_queue_drops,
                    &recv_repair_queue_drops,
                    &recv_control_frames_coalesced,
                ) {
                    InboundDispatchOutcome::Queued => {
                        if let Some(class) = duplicate_class {
                            debug_assert_eq!(
                                pre_admission_duplicates.observe_key_class(
                                    frame_session_id,
                                    frame_sequence,
                                    class,
                                ),
                                DuplicateOutcome::FirstArrival,
                            );
                        }
                    }
                    InboundDispatchOutcome::Coalesced => {}
                    InboundDispatchOutcome::Dropped => {}
                    InboundDispatchOutcome::CriticalSaturated => {
                        eprintln!(
                            "xbond server critical inbound control queue saturated; terminating \
                         cleanly rather than dropping session control"
                        );
                        return;
                    }
                    InboundDispatchOutcome::Closed => return,
                }
                RECEIVE_ENQUEUE_MICROS_TOTAL.fetch_add(
                    enqueue_started
                        .elapsed()
                        .as_micros()
                        .min(u128::from(u64::MAX)) as u64,
                    Ordering::Relaxed,
                );
            }
            RECEIVE_BATCHES.fetch_add(1, Ordering::Relaxed);
            RECEIVE_BATCH_PEAK.fetch_max(batch_count as u64, Ordering::Relaxed);
            RECEIVE_MICROS_TOTAL.fetch_add(
                batch_started
                    .elapsed()
                    .as_micros()
                    .min(u128::from(u64::MAX)) as u64,
                Ordering::Relaxed,
            );
            tokio::task::yield_now().await;
        }
    });

    let (tun_worker_exit_tx, mut tun_worker_exit_rx) =
        mpsc::channel::<TunWorkerExit>(TUN_WORKER_EXIT_QUEUE_CAPACITY);
    let (tun_packet_tx, mut tun_packet_rx) =
        mpsc::channel::<Vec<u8>>(args.tun_queue_capacity.max(1));
    let tun_writer = if let Some(tun_ref) = &tun {
        Some(spawn_tun_writer(
            tun_ref.try_clone()?,
            args.tun_queue_capacity,
            tun_worker_exit_tx.clone(),
            payload_recycle.clone(),
            tun_fault,
        ))
    } else {
        None
    };
    if let Some(tun_ref) = &tun {
        let tun_mtu = usize::from(args.tun_mtu).max(2048);
        spawn_tun_reader(
            tun_ref.try_clone()?,
            tun_packet_tx,
            tun_mtu,
            tun_worker_exit_tx.clone(),
            tun_fault,
        );
    } else {
        drop(tun_packet_tx);
    }

    if args.json_events {
        println!(
            "{}",
            serde_json::json!({
                "event": "listening",
                "bind": args.bind,
                "tun": tun.as_ref().map(|tun| tun.name()),
                "ingress_reorder_normal_hold_ms": args.ingress_reorder_normal_hold_ms,
                "ingress_reorder_recovery_hold_ms": args.ingress_reorder_recovery_hold_ms,
                "ingress_reorder_adaptive_recovery_hold": args.ingress_reorder_adaptive_recovery_hold,
                "ingress_reorder_recovery_min_hold_ms": args.ingress_reorder_recovery_min_hold_ms,
                "ingress_reorder_recovery_increase_step_ms": args.ingress_reorder_recovery_increase_step_ms,
                "ingress_reorder_recovery_decrease_step_ms": args.ingress_reorder_recovery_decrease_step_ms,
                "ingress_reorder_capacity": args.ingress_reorder_capacity,
                "server_health_enabled": args.server_health_enabled,
                "server_health_interval_seconds": args.server_health_interval_seconds,
                "server_health_timeout_ms": args.server_health_timeout_ms,
                "server_health_targets": &args.server_health_targets,
                "repair_cache_byte_capacity": resend_cache.byte_capacity(),
                "fault_injection": {
                    "tun_write_delay_ms": args.fault_tun_write_delay_ms,
                    "tun_write_fail_after_packets": args.fault_tun_write_fail_after_packets,
                    "tun_read_fail_after_packets": args.fault_tun_read_fail_after_packets,
                },
            })
        );
    } else {
        println!("xbond-server listening on {}", args.bind);
    }
    sync_tun_writer_telemetry(
        tun_writer.as_ref(),
        &mut control_plane,
        &mut repair,
        &mut data_packets_forwarded,
    );
    let server_health_status = server_health.read().await.clone();
    refresh_server_repair_cache_status(
        &mut resend_cache,
        &mut control_plane.repair_cache,
        monotonic_micros(),
    );
    control_plane.receive_payload_pool = payload_recycle.status();
    write_server_status(
        &args,
        tun.as_ref(),
        return_control.as_ref(),
        &reorder,
        TunnelCounters {
            data_packets_received,
            data_packets_forwarded,
            fec_packets_received,
            fec_packets_recovered,
            invalid_fec_packets_dropped,
            non_ipv4_packets_dropped,
        },
        &repair,
        &server_health_status,
        &hold_controller,
        control_plane,
        &return_pmtu,
    )?;
    loop {
        tokio::select! {
            receiver_result = &mut udp_receiver_task => {
                return Err(anyhow::anyhow!(
                    "XBond server UDP receiver stopped unexpectedly: {}",
                    receiver_result
                        .err()
                        .map(|error| error.to_string())
                        .unwrap_or_else(|| "receiver task exited".to_string())
                ));
            }

            Some(exit) = tun_worker_exit_rx.recv() => {
                return Err(anyhow::anyhow!(
                    "XBond server TUN {} worker failed; terminating tunnel cleanly: {}",
                    exit.worker,
                    exit.reason
                ));
            }

            _ = tun_admission_tick.tick(), if pending_tun_write.is_some() => {
                let tun_writer = tun_writer
                    .as_ref()
                    .ok_or_else(|| anyhow::anyhow!("pending XBond server TUN write without a TUN writer"))?;
                if let Some(delivered) =
                    try_enqueue_pending_tun_write(tun_writer, &mut pending_tun_write)?
                {
                    data_packets_forwarded =
                        data_packets_forwarded.saturating_add(delivered.enqueued);
                } else if pending_tun_write
                    .as_ref()
                    .is_some_and(|pending| pending.deadline <= Instant::now())
                {
                    let pending = pending_tun_write
                        .as_ref()
                        .expect("pending TUN write disappeared");
                    return Err(tun_admission_timeout_error(tun_writer, pending));
                }
            }

            Some(inbound) = receive_prioritized_frame(
                &critical_control_frames,
                &latest_schedule_frames,
                &latest_heartbeat_frames,
                &mut repair_frame_rx,
                &mut payload_frame_rx,
                pending_tun_write.is_none(),
            ) => {
                let mut inbound = inbound;
                let pre_admission_deduplicated = inbound.pre_admission_deduplicated;
                let peer = inbound.peer;
                let receive_micros = monotonic_micros();
                if is_prioritized_inbound(inbound.frame.header.kind) {
                    control_plane.prioritized_frames_processed = control_plane
                        .prioritized_frames_processed
                        .saturating_add(1);
                    control_plane.last_control_progress_at_micros = now_micros();
                }

                if let Some(handshake) = parse_session_handshake(&inbound.frame) {
                    match handshake {
                        ServerSessionHandshakeControl::Open {
                            session_id,
                            request_nonce,
                        } => {
                            let fresh_challenge = random_handshake_nonce()?;
                            if let Some(challenge) = session_gate.issue_challenge(
                                inbound.frame.header.session_id,
                                session_id,
                                request_nonce,
                                fresh_challenge,
                                receive_micros,
                            ) {
                                let response = XBondControlMessage::SessionChallenge {
                                    session_id,
                                    request_nonce,
                                    challenge,
                                };
                                enqueue_control_message(
                                    &control_send_tx,
                                    &key,
                                    peer,
                                    session_id,
                                    &mut control_sequence,
                                    &response,
                                    &mut control_plane,
                                )?;
                            } else if args.json_events {
                                println!(
                                    "{}",
                                    serde_json::json!({
                                        "event": "session-challenge-rejected",
                                        "header_session_id": inbound.frame.header.session_id,
                                        "control_session_id": session_id,
                                        "peer": peer.to_string(),
                                    })
                                );
                            }
                        }
                        ServerSessionHandshakeControl::Proof {
                            session_id,
                            request_nonce,
                            challenge,
                        } => {
                            let decision = session_gate.prove(
                                inbound.frame.header.session_id,
                                session_id,
                                request_nonce,
                                challenge,
                                receive_micros,
                            );
                            match decision {
                                ServerSessionOpenDecision::AcceptedNew => {
                                    last_restart_required_sent = None;
                                    abort_return_senders(&mut return_senders);
                                    clear_session_forwarding_state(
                                        &mut receiver,
                                        args.realtime_deadline_ms * 1_000,
                                        &mut peers,
                                        &mut return_control,
                                        &mut resend_cache,
                                        &mut control_plane.repair_cache,
                                        &mut reorder,
                                        &mut fec_recovery,
                                        &mut reorder_session_id,
                                        &mut repair,
                                        &mut pending_primary_return,
                                        &mut pending_tun_write,
                                    );
                                    ingress_repair_limiter.reset();
                                    reorder_session_id = session_id;
                                    last_session_id = session_id;
                                    reverse_sequence = initial_reverse_sequence();
                                    if hold_controller.reset_to_normal(reorder.stats(), &repair) {
                                        current_ingress_hold_micros =
                                            hold_controller.current_hold_micros();
                                        reorder.set_hold_micros(current_ingress_hold_micros);
                                    }
                                    let accepted = XBondControlMessage::SessionAccepted {
                                        session_id,
                                        request_nonce,
                                        challenge,
                                    };
                                    enqueue_control_message(
                                        &control_send_tx,
                                        &key,
                                        peer,
                                        session_id,
                                        &mut control_sequence,
                                        &accepted,
                                        &mut control_plane,
                                    )?;
                                    if args.json_events {
                                        println!(
                                            "{}",
                                            serde_json::json!({
                                                "event": "session-opened",
                                                "session_id": session_id,
                                                "peer": peer.to_string(),
                                            })
                                        );
                                    }
                                }
                                ServerSessionOpenDecision::AcceptedCurrent => {
                                    let accepted = XBondControlMessage::SessionAccepted {
                                        session_id,
                                        request_nonce,
                                        challenge,
                                    };
                                    enqueue_control_message(
                                        &control_send_tx,
                                        &key,
                                        peer,
                                        session_id,
                                        &mut control_sequence,
                                        &accepted,
                                        &mut control_plane,
                                    )?;
                                }
                                ServerSessionOpenDecision::RestartRequired => {
                                    let restart = XBondControlMessage::SessionRestartRequired {
                                        session_id,
                                        reason: "This session was closed after its return schedule expired; open a new random session epoch.".to_string(),
                                    };
                                    enqueue_control_message(
                                        &control_send_tx,
                                        &key,
                                        peer,
                                        session_id,
                                        &mut control_sequence,
                                        &restart,
                                        &mut control_plane,
                                    )?;
                                }
                                ServerSessionOpenDecision::Rejected => {
                                    if args.json_events {
                                        println!(
                                            "{}",
                                            serde_json::json!({
                                                "event": "session-proof-rejected",
                                                "header_session_id": inbound.frame.header.session_id,
                                                "control_session_id": session_id,
                                                "peer": peer.to_string(),
                                            })
                                        );
                                    }
                                }
                            }
                        }
                    }
                    continue;
                }

                if !session_gate.accepts(inbound.frame.header.session_id) {
                    let restart_repeat_due = last_restart_required_sent
                        .is_none_or(|sent_at| sent_at.elapsed() >= Duration::from_secs(1));
                    if restart_repeat_due
                        && session_gate.restart_required_for(inbound.frame.header.session_id)
                    {
                        let restart = XBondControlMessage::SessionRestartRequired {
                            session_id: inbound.frame.header.session_id,
                            reason: "The server no longer has active forwarding state for this session; open a new random session epoch.".to_string(),
                        };
                        enqueue_control_message(
                            &control_send_tx,
                            &key,
                            peer,
                            inbound.frame.header.session_id,
                            &mut control_sequence,
                            &restart,
                            &mut control_plane,
                        )?;
                        last_restart_required_sent = Some(Instant::now());
                        if args.json_events {
                            println!(
                                "{}",
                                serde_json::json!({
                                    "event": "session-restart-required-repeated",
                                    "session_id": inbound.frame.header.session_id,
                                    "peer": peer.to_string(),
                                })
                            );
                        }
                    }
                    continue;
                }

                let should_ack = inbound.frame.header.kind == PacketKind::Control;
                let outcome = if pre_admission_deduplicated {
                    receiver.accept_prechecked()
                } else {
                    receiver.observe(&inbound.frame, now_micros())
                };
                let ack_sent = should_ack
                    && matches!(
                        outcome,
                        ReceiveOutcome::Accepted | ReceiveOutcome::Duplicate
                    );
                if ack_sent {
                    let reply = build_ack_frame(&inbound.frame);
                    let encoded = reply.encode_sealed(&key)?;
                    if enqueue_control_datagram(
                        &control_send_tx,
                        encoded,
                        peer,
                        &mut control_plane,
                    ) {
                        control_plane.protocol_acks_queued =
                            control_plane.protocol_acks_queued.saturating_add(1);
                    }
                }

                if args.json_events && args.trace_packets {
                    print_packet_event(
                        event_name(outcome),
                        outcome,
                        &inbound.frame,
                        &peer.to_string(),
                        ack_sent,
                        &receiver,
                    );
                }

                let schedule_ack_generation =
                    parse_schedule_control_generation(&inbound.frame);
                let parsed_control = parse_return_control(&inbound.frame, receive_micros);
                if outcome != ReceiveOutcome::Accepted {
                    if outcome == ReceiveOutcome::Duplicate {
                        if let (Some(schedule_generation), Some(candidate)) =
                            (schedule_ack_generation, parsed_control.as_ref())
                        {
                            if return_control_matches_current(
                                return_control.as_ref(),
                                candidate,
                            ) {
                                enqueue_schedule_accepted(
                                    &control_send_tx,
                                    &key,
                                    peer,
                                    inbound.frame.header.session_id,
                                    schedule_generation,
                                    &mut control_sequence,
                                    &mut control_plane,
                                )?;
                            }
                        }
                    }
                    if inbound.frame.header.kind == PacketKind::Repair {
                        repair.late_frames = repair.late_frames.saturating_add(1);
                    }
                    continue;
                }

                if parsed_control.as_ref().is_some_and(|candidate| {
                    !return_control_update_is_valid(
                        return_control.as_ref(),
                        candidate,
                    )
                }) {
                    if args.json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "return-schedule-rejected",
                                "session_id": inbound.frame.header.session_id,
                                "schedule_generation": parsed_control.as_ref().map(|control| control.schedule_generation),
                                "current_schedule_generation": return_control.as_ref().map(|control| control.schedule_generation),
                            })
                        );
                    }
                    continue;
                }

                expire_stale_peers(&mut peers, receive_micros);
                if is_tunnel_payload(inbound.frame.header.kind)
                    && reorder_session_id != inbound.frame.header.session_id
                {
                    reorder.reset();
                    fec_recovery = FecRecovery::new(8192);
                    reorder_session_id = inbound.frame.header.session_id;
                }
                if inbound.frame.header.path_id != 0 {
                    peers.insert(
                        inbound.frame.header.path_id,
                        PeerState {
                            addr: peer,
                            last_seen_micros: receive_micros,
                        },
                    );
                }
                if let Some(control) = parsed_control {
                    let schedule_changed = return_control.as_ref().is_none_or(|previous| {
                        previous.schedule != control.schedule
                            || previous.policy != control.policy
                            || previous.policy_config != control.policy_config
                            || previous.recovery_active != control.recovery_active
                    });
                    if hold_controller.set_recovery_active(control.recovery_active, reorder.stats(), &repair) {
                        current_ingress_hold_micros = hold_controller.current_hold_micros();
                        reorder.set_hold_micros(current_ingress_hold_micros);
                        if args.json_events {
                            let adaptive = hold_controller.status();
                            println!(
                                "{}",
                                serde_json::json!({
                                    "event": "ingress-reorder-hold-updated",
                                    "session_id": inbound.frame.header.session_id,
                                    "recovery_active": control.recovery_active,
                                    "adaptive": adaptive,
                                })
                            );
                        }
                    }
                    return_control = Some(control);
                    control_plane.schedule_updates_accepted = control_plane
                        .schedule_updates_accepted
                        .saturating_add(1);
                    if let Some(schedule_generation) = schedule_ack_generation {
                        enqueue_schedule_accepted(
                            &control_send_tx,
                            &key,
                            peer,
                            last_session_id,
                            schedule_generation,
                            &mut control_sequence,
                            &mut control_plane,
                        )?;
                    }
                    if args.json_events && schedule_changed {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "return-schedule-updated",
                                "session_id": inbound.frame.header.session_id,
                                "policy": return_control.as_ref().map(|control| control.policy),
                                "recovery_active": return_control.as_ref().is_some_and(|control| control.recovery_active),
                                "ingress_reorder_hold_ms": current_ingress_hold_micros / 1_000,
                                "paths": return_control.as_ref().map(|control| build_transmission_plan(&control.schedule))
                                    .unwrap_or_default()
                                    .iter()
                                    .map(|transmission| serde_json::json!({
                                        "path_id": transmission.path_id,
                                        "packet_kind": transmission.packet_kind,
                                    }))
                                    .collect::<Vec<_>>(),
                            })
                        );
                    }
                    let now = monotonic_micros();
                    let (schedule_required, schedule_generation, schedule_age_ms) =
                        schedule_sync_status(return_control.as_ref(), now);
                    let server_health_status = server_health.read().await.clone();
                    let status = build_server_recovery_status(
                        return_control
                            .as_ref()
                            .is_some_and(|control| control.recovery_active),
                        schedule_required,
                        schedule_generation,
                        schedule_age_ms,
                        &reorder,
                        &repair,
                        &server_health_status,
                        &hold_controller,
                        args.ingress_reorder_capacity,
                    );
                    send_server_recovery_status(
                        &control_send_tx,
                        &key,
                        &peers,
                        last_session_id,
                        &mut control_sequence,
                        status,
                        args.json_events,
                        &mut control_plane,
                    )?;
                    last_server_recovery_status_sent = Instant::now();
                }

                if let Some(sequences) = parse_repair_request(&inbound.frame) {
                    repair.requests_received = repair
                        .requests_received
                        .saturating_add(sequences.len() as u64);
                    send_repair_frames_from_server_cache(
                        &args,
                        &socket,
                        &key,
                        &mut return_senders,
                        return_control.as_ref(),
                        &peers,
                        &mut resend_cache,
                        last_session_id,
                        &sequences,
                        &send_report_tx,
                        &mut repair,
                        &mut control_plane,
                    )?;
                    continue;
                }

                let mut forwarded_packets = 0u64;
                let mut dropped_reason = None;
                let trace_tunnel_payload =
                    (args.json_events
                        && args.trace_packets
                        && is_tunnel_payload(inbound.frame.header.kind))
                    .then(|| (inbound.frame.header.clone(), inbound.frame.payload.len()));
                if outcome == ReceiveOutcome::Accepted
                    && is_data_like(inbound.frame.header.kind)
                {
                    data_packets_received += 1;
                    let header = inbound.frame.header.clone();
                    let payload = inbound.take_payload();
                    let fec_is_active = return_control
                        .as_ref()
                        .is_some_and(|control| !control.schedule.fec_path_ids.is_empty());
                    let recovered_packets = if fec_is_active {
                        fec_recovery.observe_data(
                            header.session_id,
                            header.sequence,
                            payload.as_slice().to_vec(),
                        )
                    } else {
                        Vec::new()
                    };
                    if fec_recovery.mark_delivered(header.session_id, header.sequence) {
                        if is_ipv4_packet(payload.as_slice()) {
                            if let Some(tun_writer) = &tun_writer {
                                let ready = reorder.push(
                                    header.sequence,
                                    if header.kind == PacketKind::Repair {
                                        u16::MAX
                                    } else {
                                        header.path_id
                                    },
                                    payload.into_vec(),
                                    monotonic_micros(),
                                    0,
                                );
                                let delivered = start_or_append_tun_write(
                                    tun_writer,
                                    &mut pending_tun_write,
                                    ready,
                                    args.ingress_reorder_capacity,
                                )?;
                                data_packets_forwarded =
                                    data_packets_forwarded.saturating_add(delivered.enqueued);
                                forwarded_packets =
                                    forwarded_packets.saturating_add(delivered.enqueued);
                                send_repair_requests_for_ingress_gaps(
                                    &control_send_tx,
                                    &key,
                                    &peers,
                                    last_session_id,
                                    &mut control_sequence,
                                    &mut reorder,
                                    &mut repair,
                                    return_control
                                        .as_ref()
                                        .is_some_and(|control| control.recovery_active),
                                    &mut ingress_repair_limiter,
                                    &mut control_plane,
                                )?;
                            }
                        } else {
                            non_ipv4_packets_dropped += 1;
                            dropped_reason = Some("payload is not an IPv4 packet");
                        }
                    }

                    for recovered in recovered_packets {
                        if !fec_recovery.mark_delivered(header.session_id, recovered.sequence) {
                            continue;
                        }
                        if is_ipv4_packet(&recovered.payload) {
                            if let Some(tun_writer) = &tun_writer {
                                let ready = reorder.push(
                                    recovered.sequence,
                                    0,
                                    recovered.payload,
                                    monotonic_micros(),
                                    0,
                                );
                                let delivered = start_or_append_tun_write(
                                    tun_writer,
                                    &mut pending_tun_write,
                                    ready,
                                    args.ingress_reorder_capacity,
                                )?;
                                data_packets_forwarded =
                                    data_packets_forwarded.saturating_add(delivered.enqueued);
                                forwarded_packets =
                                    forwarded_packets.saturating_add(delivered.enqueued);
                            }
                            fec_packets_recovered += 1;
                        } else {
                            non_ipv4_packets_dropped += 1;
                        }
                    }
                } else if outcome == ReceiveOutcome::Accepted
                    && inbound.frame.header.kind == PacketKind::Fec
                {
                    fec_packets_received += 1;
                    let session_id = inbound.frame.header.session_id;
                    match XorFecBlock::decode(&inbound.frame.payload) {
                        Ok(block) => {
                            for recovered in fec_recovery.observe_fec(session_id, block) {
                                if !fec_recovery.mark_delivered(session_id, recovered.sequence)
                                {
                                    continue;
                                }
                                if is_ipv4_packet(&recovered.payload) {
                                    if let Some(tun_writer) = &tun_writer {
                                        let ready = reorder.push(
                                            recovered.sequence,
                                            0,
                                            recovered.payload,
                                            monotonic_micros(),
                                            0,
                                        );
                                        let delivered = start_or_append_tun_write(
                                            tun_writer,
                                            &mut pending_tun_write,
                                            ready,
                                            args.ingress_reorder_capacity,
                                        )?;
                                        data_packets_forwarded =
                                            data_packets_forwarded
                                                .saturating_add(delivered.enqueued);
                                        forwarded_packets = forwarded_packets
                                            .saturating_add(delivered.enqueued);
                                    }
                                    fec_packets_recovered += 1;
                                } else {
                                    non_ipv4_packets_dropped += 1;
                                }
                            }
                        }
                        Err(_) => {
                            invalid_fec_packets_dropped += 1;
                            dropped_reason = Some("invalid FEC payload");
                        }
                    }
                }

                if let Some((header, payload_len)) = trace_tunnel_payload {
                    print_data_event(
                        &header,
                        payload_len,
                        forwarded_packets,
                        dropped_reason,
                        TunnelCounters {
                            data_packets_received,
                            data_packets_forwarded,
                            fec_packets_received,
                            fec_packets_recovered,
                            invalid_fec_packets_dropped,
                            non_ipv4_packets_dropped,
                        },
                    );
                }
            }

            Some(report) = send_report_rx.recv() => {
                if report.success && report.packet_kind == PacketKind::Repair {
                    repair.frames_sent = repair.frames_sent.saturating_add(1);
                }
                if report.deadline_expired && report.packet_kind == PacketKind::Data {
                    control_plane.primary_return_send_deadline_expiries = control_plane
                        .primary_return_send_deadline_expiries
                        .saturating_add(1);
                }
                let pmtu_error = record_return_pmtu_error(&mut return_pmtu, &report);
                if pmtu_error && args.json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "server-return-pmtu-error",
                            "peer": report.peer.to_string(),
                            "path_id": report.path_id,
                            "packet_kind": report.packet_kind,
                            "attempted_datagram_bytes": report.attempted_datagram_bytes,
                            "tun_mtu": args.tun_mtu,
                            "emsgsize_errors": return_pmtu.emsgsize_errors,
                            "path_emsgsize_errors": return_pmtu
                                .emsgsize_errors_by_path
                                .get(&report.path_id)
                                .copied()
                                .unwrap_or_default(),
                        })
                    );
                }
                if !report.success && args.json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "return-path-sender-failed",
                            "peer": report.peer.to_string(),
                            "path_id": report.path_id,
                            "packet_kind": report.packet_kind,
                            "error": report.error.unwrap_or_else(|| "unknown send failure".to_string()),
                        })
                    );
                }
                if report.force_restart {
                    control_plane.primary_return_forced_restarts = control_plane
                        .primary_return_forced_restarts
                        .saturating_add(1);
                    return Err(anyhow::anyhow!(
                        "XBond primary return UDP sends exceeded their deadline \
                         {PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES} consecutive times; \
                         terminating for a clean session restart"
                    ));
                }
            }

            permit = reserve_primary_return_slot(
                primary_return_tx.clone(),
                pending_primary_return
                    .as_ref()
                    .map(|pending| pending.work.deadline)
                    .unwrap_or_else(Instant::now),
            ), if pending_primary_return.is_some() => {
                match permit {
                    Ok(permit) => {
                        let mut pending = pending_primary_return
                            .take()
                            .expect("primary return work must exist while reserve branch is enabled");
                        pending.work.deadline = Instant::now() + PRIMARY_RETURN_SEND_DEADLINE;
                        permit.send(pending.work);
                        control_plane.primary_return_enqueued =
                            control_plane.primary_return_enqueued.saturating_add(1);
                        consecutive_primary_return_enqueue_expiries = 0;
                    }
                    Err(PrimaryReturnReserveError::Closed) => {
                        return Err(anyhow::anyhow!(
                            "XBond primary return sender stopped during backpressure"
                        ));
                    }
                    Err(PrimaryReturnReserveError::DeadlineExpired) => {
                        let pending = pending_primary_return
                            .take()
                            .expect("expired primary return work must still be pending");
                        control_plane.primary_return_enqueue_deadline_expiries = control_plane
                            .primary_return_enqueue_deadline_expiries
                            .saturating_add(1);
                        if !pending.alternate_copy_accepted {
                            control_plane.all_return_copies_dropped = control_plane
                                .all_return_copies_dropped
                                .saturating_add(1);
                        }
                        consecutive_primary_return_enqueue_expiries =
                            consecutive_primary_return_enqueue_expiries.saturating_add(1);
                        if consecutive_primary_return_enqueue_expiries
                            >= PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES
                        {
                            control_plane.primary_return_forced_restarts = control_plane
                                .primary_return_forced_restarts
                                .saturating_add(1);
                            return Err(anyhow::anyhow!(
                                "XBond primary return queue remained saturated for \
                                 {PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES} consecutive \
                                 packets; terminating for a clean session restart"
                            ));
                        }
                    }
                }
            }

            _ = reorder_tick.tick() => {
                if let Some(tun_writer) = &tun_writer {
                    let delivered = start_or_append_tun_write(
                        tun_writer,
                        &mut pending_tun_write,
                        reorder.drain_ready(monotonic_micros()),
                        args.ingress_reorder_capacity,
                    )?;
                    data_packets_forwarded =
                        data_packets_forwarded.saturating_add(delivered.enqueued);
                    send_repair_requests_for_ingress_gaps(
                        &control_send_tx,
                        &key,
                        &peers,
                        last_session_id,
                        &mut control_sequence,
                        &mut reorder,
                        &mut repair,
                        return_control
                            .as_ref()
                            .is_some_and(|control| control.recovery_active),
                        &mut ingress_repair_limiter,
                        &mut control_plane,
                    )?;
                }
            }

            _ = status_tick.tick() => {
                control_plane.ingress_control_frames_coalesced =
                    ingress_control_frames_coalesced.load(Ordering::Relaxed);
                control_plane.ingress_repair_queue_drops =
                    ingress_repair_queue_drops.load(Ordering::Relaxed);
                control_plane.ingress_payload_queue_drops =
                    ingress_payload_queue_drops.load(Ordering::Relaxed);
                control_plane.ingress_duplicates_coalesced =
                    ingress_duplicates_coalesced.load(Ordering::Relaxed);
                control_plane.control_datagrams_sent =
                    control_datagrams_sent.load(Ordering::Relaxed);
                control_plane.control_send_failures =
                    control_send_failures.load(Ordering::Relaxed);
                sync_heartbeat_ack_metrics(&heartbeat_ack_sender, &mut control_plane);
                control_plane.receive_payload_pool = payload_recycle.status();
                sync_tun_writer_telemetry(
                    tun_writer.as_ref(),
                    &mut control_plane,
                    &mut repair,
                    &mut data_packets_forwarded,
                );
                let schedule_expired = return_control
                    .as_ref()
                    .is_some_and(|control| {
                        return_schedule_grace_expired(control, monotonic_micros())
                    });
                if schedule_expired {
                    if let Some(expired_session_id) = session_gate.require_restart() {
                        let restart = XBondControlMessage::SessionRestartRequired {
                            session_id: expired_session_id,
                            reason: "The authoritative return schedule expired beyond its grace period; open a new random session epoch.".to_string(),
                        };
                        enqueue_control_message_to_peers(
                            &control_send_tx,
                            &key,
                            &peers,
                            expired_session_id,
                            &mut control_sequence,
                            &restart,
                            &mut control_plane,
                        )?;
                        last_restart_required_sent = Some(Instant::now());
                        if args.json_events {
                            println!(
                                "{}",
                                serde_json::json!({
                                    "event": "session-restart-required",
                                    "session_id": expired_session_id,
                                    "reason": "return schedule expired beyond grace",
                                })
                            );
                        }
                    }
                    abort_return_senders(&mut return_senders);
                    clear_session_forwarding_state(
                        &mut receiver,
                        args.realtime_deadline_ms * 1_000,
                        &mut peers,
                        &mut return_control,
                        &mut resend_cache,
                        &mut control_plane.repair_cache,
                        &mut reorder,
                        &mut fec_recovery,
                        &mut reorder_session_id,
                        &mut repair,
                        &mut pending_primary_return,
                        &mut pending_tun_write,
                    );
                    ingress_repair_limiter.reset();
                    last_session_id = 0;
                    if hold_controller.reset_to_normal(reorder.stats(), &repair) {
                        current_ingress_hold_micros = hold_controller.current_hold_micros();
                        reorder.set_hold_micros(current_ingress_hold_micros);
                    }
                }
                let repair_budget_elapsed = repair_budget_last_updated.elapsed();
                if repair_budget_elapsed >= Duration::from_secs(1) {
                    if return_payload_bytes_since_budget_update > 0 {
                        let elapsed_micros = repair_budget_elapsed.as_micros().max(1);
                        let observed_bits_per_second = u64::try_from(
                            u128::from(return_payload_bytes_since_budget_update)
                                .saturating_mul(8_000_000)
                                .saturating_div(elapsed_micros),
                        )
                        .unwrap_or(u64::MAX);
                        let byte_capacity = recommended_server_repair_cache_bytes(
                            observed_bits_per_second,
                            args.repair_cache_bytes,
                        );
                        resend_cache.set_byte_capacity(byte_capacity, monotonic_micros());
                    }
                    return_payload_bytes_since_budget_update = 0;
                    repair_budget_last_updated = Instant::now();
                }
                refresh_server_repair_cache_status(
                    &mut resend_cache,
                    &mut control_plane.repair_cache,
                    monotonic_micros(),
                );
                repair.cache_entries = control_plane.repair_cache.entries;
                let recovery_active = return_control
                    .as_ref()
                    .is_some_and(|control| control.recovery_active);
                if hold_controller.observe(recovery_active, reorder.stats(), &repair) {
                    current_ingress_hold_micros = hold_controller.current_hold_micros();
                    reorder.set_hold_micros(current_ingress_hold_micros);
                    if args.json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "ingress-reorder-hold-updated",
                                "recovery_active": recovery_active,
                                "adaptive": hold_controller.status(),
                            })
                        );
                    }
                }
                let counters = TunnelCounters {
                    data_packets_received,
                    data_packets_forwarded,
                    fec_packets_received,
                    fec_packets_recovered,
                    invalid_fec_packets_dropped,
                    non_ipv4_packets_dropped,
                };
                let server_health_status = server_health.read().await.clone();
                write_server_status(
                    &args,
                    tun.as_ref(),
                    return_control.as_ref(),
                    &reorder,
                    counters,
                    &repair,
                    &server_health_status,
                    &hold_controller,
                    control_plane,
                    &return_pmtu,
                )?;
                if args.json_events {
                    json_status_event_counter = json_status_event_counter.saturating_add(1);
                    if json_status_event_counter >= 5 {
                        json_status_event_counter = 0;
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "server-status",
                                "recovery_active": recovery_active,
                                "ingress_reorder": {
                                    "adaptive": hold_controller.status(),
                                    "stats": reorder.stats(),
                                },
                                "counters": counters,
                                "control_plane": control_plane,
                                "return_pmtu": &return_pmtu,
                            })
                        );
                    }
                }
                if last_server_recovery_status_sent.elapsed() >= Duration::from_secs(1) {
                    let now = monotonic_micros();
                    let (schedule_required, schedule_generation, schedule_age_ms) =
                        schedule_sync_status(return_control.as_ref(), now);
                    let server_health_status = server_health.read().await.clone();
                    let status = build_server_recovery_status(
                        recovery_active,
                        schedule_required,
                        schedule_generation,
                        schedule_age_ms,
                        &reorder,
                        &repair,
                        &server_health_status,
                        &hold_controller,
                        args.ingress_reorder_capacity,
                    );
                    send_server_recovery_status(
                        &control_send_tx,
                        &key,
                        &peers,
                        last_session_id,
                        &mut control_sequence,
                        status,
                        args.json_events && args.trace_packets,
                        &mut control_plane,
                    )?;
                    last_server_recovery_status_sent = Instant::now();
                }
            }

            Some(packet) = tun_packet_rx.recv(), if pending_primary_return.is_none() => {
                if !is_ipv4_packet(&packet) || peers.is_empty() || last_session_id == 0 {
                    continue;
                }

                let (soft_saturated, sustained_hard, pacing_delay_micros) =
                    observe_server_saturation(&primary_return_tx, pending_primary_return.as_ref());
                if sustained_hard {
                    return Err(anyhow::anyhow!(
                        "server return queue remained hard-saturated for more than one second; restarting the session cleanly"
                    ));
                }
                if soft_saturated && packet.len() > args.interactive_packet_threshold_bytes {
                    time::sleep(Duration::from_micros(pacing_delay_micros.min(250))).await;
                }

                return_payload_bytes_since_budget_update =
                    return_payload_bytes_since_budget_update.saturating_add(packet.len() as u64);
                let packet_payload = Arc::new(packet);
                reverse_sequence += 1;
                resend_cache.insert(
                    last_session_id,
                    reverse_sequence,
                    packet_payload.clone(),
                    monotonic_micros(),
                );
                repair.cache_entries = resend_cache.len();
                let send_micros = now_micros();
                let schedule_started = Instant::now();
                let mut return_targets = select_return_targets(
                    return_control.as_ref(),
                    &peers,
                    packet_payload.len(),
                    monotonic_micros(),
                );
                SCHEDULE_MICROS_TOTAL.fetch_add(
                    schedule_started
                        .elapsed()
                        .as_micros()
                        .min(u128::from(u64::MAX)) as u64,
                    Ordering::Relaxed,
                );
                if soft_saturated {
                    let duplicate_suppressions = return_targets
                        .iter()
                        .filter(|(_, _, kind)| *kind == PacketKind::Duplicate)
                        .count() as u64;
                    let fec_suppressions = return_targets
                        .iter()
                        .filter(|(_, _, kind)| *kind == PacketKind::Fec)
                        .count() as u64;
                    record_server_saturation_suppressions(
                        duplicate_suppressions,
                        fec_suppressions,
                    );
                    return_targets.retain(|(_, _, kind)| {
                        !matches!(kind, PacketKind::Duplicate | PacketKind::Fec)
                    });
                }
                let mut sent_paths = 0usize;
                let mut enqueue_accounting = ReturnCopyEnqueueAccounting::default();
                let mut deferred_primary_work = None;
                for (path_id, peer, kind) in return_targets {
                    enqueue_accounting.selected();
                    let mut header = XBondHeader::new(
                        kind,
                        last_session_id,
                        reverse_sequence,
                        send_micros,
                        path_id,
                    );
                    header.flags = FLAG_SERVER_TO_CLIENT;
                    let work = ReturnSendWork {
                        packet_kind: kind,
                        peer,
                        header,
                        payload: packet_payload.clone(),
                        deadline: Instant::now() + PRIMARY_RETURN_QUEUE_DEADLINE,
                    };
                    let enqueue_result = if kind == PacketKind::Data {
                        match primary_return_tx.try_send(work) {
                            Ok(()) => {
                                control_plane.primary_return_enqueued =
                                    control_plane.primary_return_enqueued.saturating_add(1);
                                Ok(())
                            }
                            Err(mpsc::error::TrySendError::Full(work)) => {
                                control_plane.primary_return_queue_full = control_plane
                                    .primary_return_queue_full
                                    .saturating_add(1);
                                deferred_primary_work = Some(work);
                                continue;
                            }
                            Err(mpsc::error::TrySendError::Closed(_)) => {
                                return Err(anyhow::anyhow!(
                                    "XBond primary return sender stopped"
                                ));
                            }
                        }
                    } else {
                        let sender = return_senders
                            .entry(path_id)
                            .or_insert_with(|| {
                                spawn_return_sender(
                                    path_id,
                                    socket.clone(),
                                    key.clone(),
                                    args.tun_queue_capacity.max(1),
                                    send_report_tx.clone(),
                                )
                            });
                        match sender.data_tx.try_send(work) {
                            Ok(()) => Ok(()),
                            Err(mpsc::error::TrySendError::Full(_)) => continue,
                            Err(mpsc::error::TrySendError::Closed(_)) => Err(
                                std::io::Error::new(
                                    ErrorKind::BrokenPipe,
                                    "XBond return path sender stopped",
                                ),
                            ),
                        }
                    };
                    match enqueue_result {
                        Ok(()) => {
                            sent_paths += 1;
                            enqueue_accounting.accepted();
                            if kind == PacketKind::Data {
                                consecutive_primary_return_enqueue_expiries = 0;
                            }
                        }
                        Err(error) => {
                            if args.json_events {
                                println!(
                                    "{}",
                                    serde_json::json!({
                                        "event": "return-packet-send-failed",
                                        "peer": peer.to_string(),
                                        "path_id": path_id,
                                        "sequence": reverse_sequence,
                                        "error": error.to_string(),
                                    })
                                );
                            }
                        }
                    }
                }
                let primary_deferred = deferred_primary_work.is_some();
                if let Some(work) = deferred_primary_work {
                    pending_primary_return = Some(PendingPrimaryReturn {
                        work,
                        alternate_copy_accepted: enqueue_accounting.accepted > 0,
                    });
                }
                if enqueue_accounting.all_copies_dropped(primary_deferred) {
                    control_plane.all_return_copies_dropped = control_plane
                        .all_return_copies_dropped
                        .saturating_add(1);
                }

                if args.json_events && args.trace_packets {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "return-packet-sent",
                            "sequence": reverse_sequence,
                            "bytes": packet_payload.len(),
                            "paths": sent_paths,
                        })
                    );
                }
            }

            else => break,
        }
    }

    Ok(())
}

fn observe_server_saturation(
    primary_return_tx: &mpsc::Sender<ReturnSendWork>,
    pending: Option<&PendingPrimaryReturn>,
) -> (bool, bool, u64) {
    let now = Instant::now();
    let capacity = primary_return_tx.max_capacity().max(1);
    let depth = capacity.saturating_sub(primary_return_tx.capacity());
    let utilization = depth as f64 / capacity as f64;
    let oldest_age_ms = pending
        .map(|pending| {
            let enqueued_at = pending
                .work
                .deadline
                .checked_sub(PRIMARY_RETURN_QUEUE_DEADLINE)
                .unwrap_or(now);
            now.saturating_duration_since(enqueued_at)
                .as_millis()
                .min(u128::from(u64::MAX)) as u64
        })
        .unwrap_or_default();
    let soft = utilization >= SATURATION_SOFT_QUEUE_UTILIZATION
        || oldest_age_ms >= SATURATION_SOFT_OLDEST_AGE_MS;
    let hard = utilization >= SATURATION_HARD_QUEUE_UTILIZATION
        || oldest_age_ms >= SATURATION_HARD_OLDEST_AGE_MS;

    let mut runtime = SERVER_SATURATION
        .get_or_init(|| Mutex::new(ServerSaturationRuntime::default()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let drained = PRIMARY_RETURN_DRAINED.load(Ordering::Relaxed);
    if let Some(last_sample) = runtime.last_drain_sample {
        let elapsed = now.saturating_duration_since(last_sample).as_secs_f64();
        if elapsed >= 0.1 {
            runtime.drain_packets_per_second =
                drained.saturating_sub(runtime.last_drain_count) as f64 / elapsed;
            runtime.last_drain_count = drained;
            runtime.last_drain_sample = Some(now);
        }
    } else {
        runtime.last_drain_count = drained;
        runtime.last_drain_sample = Some(now);
    }
    if soft && !runtime.previous_active {
        runtime.periods = runtime.periods.saturating_add(1);
    }
    runtime.previous_active = soft;
    if hard {
        runtime.hard_since.get_or_insert(now);
    } else {
        runtime.hard_since = None;
    }
    let hard_duration = runtime
        .hard_since
        .map(|started| now.saturating_duration_since(started))
        .unwrap_or_default();
    let pacing_delay_micros = if soft && runtime.drain_packets_per_second > 0.0 {
        (1_000_000.0 / runtime.drain_packets_per_second)
            .ceil()
            .clamp(1.0, 250.0) as u64
    } else {
        0
    };
    runtime.status = XBondSaturationStatus {
        state: if hard {
            "hard"
        } else if soft {
            "soft"
        } else {
            "normal"
        }
        .to_string(),
        reason: if utilization
            >= if hard {
                SATURATION_HARD_QUEUE_UTILIZATION
            } else {
                SATURATION_SOFT_QUEUE_UTILIZATION
            } {
            Some("queue-utilization".to_string())
        } else if soft {
            Some("oldest-queue-age".to_string())
        } else {
            None
        },
        queue_utilization: utilization,
        oldest_age_ms,
        recent_drain_packets_per_second: runtime.drain_packets_per_second,
        pacing_delay_micros,
        duplicate_suppressions: runtime.duplicate_suppressions,
        fec_suppressions: runtime.fec_suppressions,
        saturation_periods: runtime.periods,
        hard_duration_ms: hard_duration.as_millis().min(u128::from(u64::MAX)) as u64,
    };
    (
        soft,
        hard_duration >= SATURATION_HARD_RESTART_AFTER,
        pacing_delay_micros,
    )
}

fn record_server_saturation_suppressions(duplicates: u64, fec: u64) {
    let mut runtime = SERVER_SATURATION
        .get_or_init(|| Mutex::new(ServerSaturationRuntime::default()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    runtime.duplicate_suppressions = runtime.duplicate_suppressions.saturating_add(duplicates);
    runtime.fec_suppressions = runtime.fec_suppressions.saturating_add(fec);
    runtime.status.duplicate_suppressions = runtime.duplicate_suppressions;
    runtime.status.fec_suppressions = runtime.fec_suppressions;
}

fn server_saturation_status() -> XBondSaturationStatus {
    SERVER_SATURATION
        .get_or_init(|| Mutex::new(ServerSaturationRuntime::default()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .status
        .clone()
}

fn server_stage_timings() -> XBondStageTimingStatus {
    XBondStageTimingStatus {
        receive_micros_total: RECEIVE_MICROS_TOTAL.load(Ordering::Relaxed),
        receive_batches: RECEIVE_BATCHES.load(Ordering::Relaxed),
        receive_datagrams: RECEIVE_DATAGRAMS.load(Ordering::Relaxed),
        receive_batch_peak: RECEIVE_BATCH_PEAK.load(Ordering::Relaxed),
        decode_micros_total: RECEIVE_DECODE_MICROS_TOTAL.load(Ordering::Relaxed),
        schedule_micros_total: SCHEDULE_MICROS_TOTAL.load(Ordering::Relaxed),
        enqueue_micros_total: RECEIVE_ENQUEUE_MICROS_TOTAL.load(Ordering::Relaxed),
        tun_micros_total: TUN_MICROS_TOTAL.load(Ordering::Relaxed),
    }
}

async fn bind_udp_socket(bind_addr: &str, socket_buffer_bytes: usize) -> Result<UdpSocket> {
    let bind_addr = bind_addr
        .parse::<SocketAddr>()
        .with_context(|| format!("failed to parse bind address {bind_addr}"))?;
    let socket = Socket::new(
        Domain::for_address(bind_addr),
        Type::DGRAM,
        Some(Protocol::UDP),
    )?;
    socket.set_reuse_address(true)?;
    let buffer_status = apply_udp_socket_buffers(&socket, socket_buffer_bytes, "server-listener")?;
    socket
        .bind(&bind_addr.into())
        .with_context(|| format!("failed to bind UDP socket to {bind_addr}"))?;
    socket.set_nonblocking(true)?;
    let std_socket: std::net::UdpSocket = socket.into();
    let _ = SERVER_SOCKET_BUFFER_STATUS.set(buffer_status);
    Ok(UdpSocket::from_std(std_socket)?)
}

fn start_server_health_monitor(args: &Args) -> Arc<RwLock<XBondServerHealthStatus>> {
    let updated_at_micros = now_micros();
    let state = Arc::new(RwLock::new(if args.server_health_enabled {
        XBondServerHealthStatus::default()
    } else {
        XBondServerHealthStatus::disabled(&args.server_health_targets, updated_at_micros)
    }));

    if !args.server_health_enabled {
        return state;
    }

    let state_for_task = state.clone();
    let targets = args.server_health_targets.clone();
    let interval = Duration::from_secs(args.server_health_interval_seconds.max(1));
    let timeout = Duration::from_millis(args.server_health_timeout_ms.max(1));
    let json_events = args.json_events;
    tokio::spawn(async move {
        run_server_health_monitor(state_for_task, targets, interval, timeout, json_events).await;
    });

    state
}

async fn run_server_health_monitor(
    state: Arc<RwLock<XBondServerHealthStatus>>,
    targets: Vec<String>,
    interval: Duration,
    timeout_duration: Duration,
    json_events: bool,
) {
    let mut consecutive_failures = 0u32;
    let mut last_success_at: Option<Instant> = None;

    loop {
        let (status, next_failures, next_success_at) = probe_server_health_once(
            &targets,
            timeout_duration,
            consecutive_failures,
            last_success_at,
        )
        .await;
        consecutive_failures = next_failures;
        last_success_at = next_success_at;

        if json_events {
            println!(
                "{}",
                serde_json::json!({
                    "event": "server-health",
                    "status": status.status,
                    "success_rate": status.success_rate,
                    "avg_connect_ms": status.avg_connect_ms,
                    "consecutive_failures": status.consecutive_failures,
                    "targets": status.targets,
                })
            );
        }

        *state.write().await = status;
        time::sleep(interval).await;
    }
}

async fn probe_server_health_once(
    targets: &[String],
    timeout_duration: Duration,
    previous_consecutive_failures: u32,
    previous_last_success_at: Option<Instant>,
) -> (XBondServerHealthStatus, u32, Option<Instant>) {
    let mut target_statuses = Vec::with_capacity(targets.len());
    for target in targets {
        target_statuses.push(probe_server_health_target(target, timeout_duration).await);
    }

    let has_success = target_statuses.iter().any(|target| target.success);
    let consecutive_failures = if has_success {
        0
    } else {
        previous_consecutive_failures.saturating_add(1)
    };
    let last_success_at = if has_success {
        Some(Instant::now())
    } else {
        previous_last_success_at
    };
    let last_success_age_ms = last_success_at.map(|instant| instant.elapsed().as_millis() as u64);
    let status = XBondServerHealthStatus::classify(
        target_statuses,
        consecutive_failures,
        last_success_age_ms,
        now_micros(),
    );

    (status, consecutive_failures, last_success_at)
}

async fn probe_server_health_target(
    target: &str,
    timeout_duration: Duration,
) -> XBondServerHealthTargetStatus {
    let updated_at_micros = now_micros();
    let started = Instant::now();
    match time::timeout(timeout_duration, TcpStream::connect(target)).await {
        Ok(Ok(_stream)) => XBondServerHealthTargetStatus {
            target: target.to_string(),
            success: true,
            connect_ms: Some(started.elapsed().as_secs_f64() * 1_000.0),
            error: None,
            updated_at_micros,
        },
        Ok(Err(error)) => XBondServerHealthTargetStatus {
            target: target.to_string(),
            success: false,
            connect_ms: None,
            error: Some(error.to_string()),
            updated_at_micros,
        },
        Err(_elapsed) => XBondServerHealthTargetStatus {
            target: target.to_string(),
            success: false,
            connect_ms: None,
            error: Some(format!(
                "timed out after {} ms",
                timeout_duration.as_millis()
            )),
            updated_at_micros,
        },
    }
}

fn spawn_return_sender(
    path_id: u16,
    socket: Arc<UdpSocket>,
    key: XBondKey,
    capacity: usize,
    report_tx: mpsc::Sender<ReturnSendReport>,
) -> ReturnSenderHandle {
    let (control_tx, mut control_rx) = mpsc::channel::<ReturnSendWork>(CONTROL_QUEUE_CAPACITY);
    let (data_tx, mut data_rx) = mpsc::channel::<ReturnSendWork>(capacity.max(1));
    let task = tokio::spawn(async move {
        let mut encoded = Vec::with_capacity(4096);
        while let Some(work) = receive_prioritized_return_work(&mut control_rx, &mut data_rx).await
        {
            let report = match encode_sealed_payload_into(
                &work.header,
                work.payload.as_slice(),
                &key,
                &mut encoded,
            ) {
                Ok(()) => {
                    let attempted_datagram_bytes = encoded.len();
                    match send_datagram_before_deadline(&socket, &encoded, work.peer, work.deadline)
                        .await
                    {
                        Ok(_) => ReturnSendReport {
                            path_id,
                            packet_kind: work.packet_kind,
                            success: true,
                            peer: work.peer,
                            error: None,
                            emsgsize: false,
                            deadline_expired: false,
                            force_restart: false,
                            attempted_datagram_bytes,
                        },
                        Err(BoundedDatagramSendError::Io(error)) => ReturnSendReport {
                            path_id,
                            packet_kind: work.packet_kind,
                            success: false,
                            peer: work.peer,
                            error: Some(error.to_string()),
                            emsgsize: is_emsgsize(&error),
                            deadline_expired: false,
                            force_restart: false,
                            attempted_datagram_bytes,
                        },
                        Err(BoundedDatagramSendError::DeadlineExpired) => ReturnSendReport {
                            path_id,
                            packet_kind: work.packet_kind,
                            success: false,
                            peer: work.peer,
                            error: Some("return path UDP send deadline expired".to_string()),
                            emsgsize: false,
                            deadline_expired: true,
                            force_restart: false,
                            attempted_datagram_bytes,
                        },
                    }
                }
                Err(error) => ReturnSendReport {
                    path_id,
                    packet_kind: work.packet_kind,
                    success: false,
                    peer: work.peer,
                    error: Some(error.to_string()),
                    emsgsize: false,
                    deadline_expired: false,
                    force_restart: false,
                    attempted_datagram_bytes: 0,
                },
            };
            if report.packet_kind == PacketKind::Repair || !report.success {
                let _ = report_tx.try_send(report);
            }
        }
    });
    ReturnSenderHandle {
        control_tx,
        data_tx,
        task,
    }
}

fn abort_return_senders(return_senders: &mut HashMap<u16, ReturnSenderHandle>) {
    for (_, sender) in return_senders.drain() {
        sender.task.abort();
    }
}

async fn receive_prioritized_return_work(
    control_rx: &mut mpsc::Receiver<ReturnSendWork>,
    data_rx: &mut mpsc::Receiver<ReturnSendWork>,
) -> Option<ReturnSendWork> {
    tokio::select! {
        biased;
        work = control_rx.recv() => match work {
            Some(work) => Some(work),
            None => data_rx.recv().await,
        },
        work = data_rx.recv() => match work {
            Some(work) => Some(work),
            None => control_rx.recv().await,
        },
    }
}

fn apply_udp_socket_buffers(
    socket: &Socket,
    socket_buffer_bytes: usize,
    scope: &str,
) -> Result<XBondSocketBufferStatus> {
    if socket_buffer_bytes > 0 {
        socket
            .set_recv_buffer_size(socket_buffer_bytes)
            .context("failed to request UDP receive buffer")?;
        socket
            .set_send_buffer_size(socket_buffer_bytes)
            .context("failed to request UDP send buffer")?;
    }
    let mut effective_receive_bytes = socket
        .recv_buffer_size()
        .context("failed to read effective UDP receive buffer")?;
    let mut effective_send_bytes = socket
        .send_buffer_size()
        .context("failed to read effective UDP send buffer")?;
    if socket_buffer_bytes > 0
        && (effective_receive_bytes < socket_buffer_bytes
            || effective_send_bytes < socket_buffer_bytes)
    {
        force_udp_socket_buffers(socket, socket_buffer_bytes);
        effective_receive_bytes = socket
            .recv_buffer_size()
            .context("failed to read forced UDP receive buffer")?;
        effective_send_bytes = socket
            .send_buffer_size()
            .context("failed to read forced UDP send buffer")?;
    }
    if effective_receive_bytes < MINIMUM_USABLE_UDP_SOCKET_BUFFER_BYTES
        || effective_send_bytes < MINIMUM_USABLE_UDP_SOCKET_BUFFER_BYTES
    {
        anyhow::bail!(
            "effective UDP socket buffers are unusably small for {scope}: receive={effective_receive_bytes}, send={effective_send_bytes}, minimum={MINIMUM_USABLE_UDP_SOCKET_BUFFER_BYTES}"
        );
    }
    let below_requested = socket_buffer_bytes > 0
        && (effective_receive_bytes < socket_buffer_bytes
            || effective_send_bytes < socket_buffer_bytes);
    if below_requested {
        eprintln!(
            "xbond UDP socket buffer warning for {scope}: requested={socket_buffer_bytes}, effective_receive={effective_receive_bytes}, effective_send={effective_send_bytes}"
        );
    }
    Ok(XBondSocketBufferStatus {
        scope: scope.to_string(),
        requested_receive_bytes: socket_buffer_bytes,
        requested_send_bytes: socket_buffer_bytes,
        effective_receive_bytes,
        effective_send_bytes,
        below_requested,
    })
}

#[cfg(target_os = "linux")]
fn force_udp_socket_buffers(socket: &Socket, socket_buffer_bytes: usize) {
    let value = socket_buffer_bytes.min(i32::MAX as usize) as libc::c_int;
    let length = std::mem::size_of_val(&value) as libc::socklen_t;
    unsafe {
        libc::setsockopt(
            socket.as_raw_fd(),
            libc::SOL_SOCKET,
            libc::SO_RCVBUFFORCE,
            std::ptr::addr_of!(value).cast(),
            length,
        );
        libc::setsockopt(
            socket.as_raw_fd(),
            libc::SOL_SOCKET,
            libc::SO_SNDBUFFORCE,
            std::ptr::addr_of!(value).cast(),
            length,
        );
    }
}

#[cfg(not(target_os = "linux"))]
fn force_udp_socket_buffers(_socket: &Socket, _socket_buffer_bytes: usize) {}

#[derive(Debug, Clone, PartialEq, Eq)]
struct RecoveredPacket {
    sequence: u64,
    payload: Vec<u8>,
}

#[derive(Debug)]
struct FecRecovery {
    capacity: usize,
    data: HashMap<(u64, u64), Vec<u8>>,
    fec: HashMap<(u64, u64), XorFecBlock>,
    delivered: HashSet<(u64, u64)>,
    data_order: VecDeque<(u64, u64)>,
    fec_order: VecDeque<(u64, u64)>,
    delivered_order: VecDeque<(u64, u64)>,
}

impl FecRecovery {
    fn new(capacity: usize) -> Self {
        Self {
            capacity: capacity.max(1),
            data: HashMap::new(),
            fec: HashMap::new(),
            delivered: HashSet::new(),
            data_order: VecDeque::new(),
            fec_order: VecDeque::new(),
            delivered_order: VecDeque::new(),
        }
    }

    fn observe_data(
        &mut self,
        session_id: u64,
        sequence: u64,
        payload: Vec<u8>,
    ) -> Vec<RecoveredPacket> {
        self.remember_data(session_id, sequence, payload);
        let base_sequence = fec_base_sequence(sequence);
        self.try_recover(session_id, base_sequence)
            .into_iter()
            .collect()
    }

    fn observe_fec(&mut self, session_id: u64, block: XorFecBlock) -> Vec<RecoveredPacket> {
        let base_sequence = block.base_sequence;
        let key = (session_id, base_sequence);
        if !self.fec.contains_key(&key) {
            self.fec_order.push_back(key);
        }
        self.fec.insert(key, block);
        self.prune_fec();
        self.try_recover(session_id, base_sequence)
            .into_iter()
            .collect()
    }

    fn mark_delivered(&mut self, session_id: u64, sequence: u64) -> bool {
        let key = (session_id, sequence);
        if !self.delivered.insert(key) {
            return false;
        }
        self.delivered_order.push_back(key);
        self.prune_delivered();
        true
    }

    fn remember_data(&mut self, session_id: u64, sequence: u64, payload: Vec<u8>) {
        let key = (session_id, sequence);
        if !self.data.contains_key(&key) {
            self.data_order.push_back(key);
        }
        self.data.insert(key, payload);
        self.prune_data();
    }

    fn try_recover(&self, session_id: u64, base_sequence: u64) -> Option<RecoveredPacket> {
        let block = self.fec.get(&(session_id, base_sequence))?;
        let first_key = (session_id, base_sequence);
        let second_key = (session_id, base_sequence + 1);

        let first = self.data.get(&first_key);
        let second = self.data.get(&second_key);

        if second.is_none() && !self.delivered.contains(&second_key) {
            if let Some(first) = first {
                let (sequence, payload) = block.recover_missing(base_sequence, first)?;
                return Some(RecoveredPacket { sequence, payload });
            }
        }

        if first.is_none() && !self.delivered.contains(&first_key) {
            if let Some(second) = second {
                let (sequence, payload) = block.recover_missing(base_sequence + 1, second)?;
                return Some(RecoveredPacket { sequence, payload });
            }
        }

        None
    }

    fn prune_data(&mut self) {
        while self.data_order.len() > self.capacity {
            if let Some(key) = self.data_order.pop_front() {
                self.data.remove(&key);
            }
        }
    }

    fn prune_fec(&mut self) {
        while self.fec_order.len() > self.capacity {
            if let Some(key) = self.fec_order.pop_front() {
                self.fec.remove(&key);
            }
        }
    }

    fn prune_delivered(&mut self) {
        while self.delivered_order.len() > self.capacity {
            if let Some(key) = self.delivered_order.pop_front() {
                self.delivered.remove(&key);
            }
        }
    }
}

fn fec_base_sequence(sequence: u64) -> u64 {
    if sequence % 2 == 0 {
        sequence.saturating_sub(1)
    } else {
        sequence
    }
}

#[derive(Debug, Clone, Copy, Serialize)]
struct TunnelCounters {
    data_packets_received: u64,
    data_packets_forwarded: u64,
    fec_packets_received: u64,
    fec_packets_recovered: u64,
    invalid_fec_packets_dropped: u64,
    non_ipv4_packets_dropped: u64,
}

#[allow(clippy::too_many_arguments)]
fn write_server_status(
    args: &Args,
    tun: Option<&XBondTun>,
    return_control: Option<&ReturnControl>,
    reorder: &PacketReorderBuffer,
    counters: TunnelCounters,
    repair: &XBondRepairStatus,
    server_health: &XBondServerHealthStatus,
    hold_controller: &IngressReorderHoldController,
    control_plane: ServerControlPlaneStatus,
    return_pmtu: &ServerReturnPmtuStatus,
) -> Result<()> {
    if let Some(parent) = args.status_path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }

    let now = monotonic_micros();
    let (schedule_required, schedule_generation, current_schedule_age_ms) =
        schedule_sync_status(return_control, now);
    let status = ServerRuntimeStatus {
        running: true,
        bind: args.bind.clone(),
        tun: tun.map(|tun| tun.name().to_string()),
        updated_at_micros: now_micros(),
        schedule_required,
        schedule_generation,
        schedule_age_ms: current_schedule_age_ms,
        return_schedule: return_control.map(|control| ServerReturnScheduleStatus {
            policy: control.policy,
            recovery_active: control.recovery_active,
            schedule_generation: control.schedule_generation,
            schedule_age_ms: schedule_age_ms(control, now),
            schedule_stale: return_schedule_is_stale(control, now),
            schedule: control.schedule.clone(),
        }),
        ingress_reorder: ServerIngressReorderStatus {
            current_hold_ms: hold_controller.current_hold_micros() / 1_000,
            normal_hold_ms: args.ingress_reorder_normal_hold_ms,
            recovery_hold_ms: args.ingress_reorder_recovery_hold_ms,
            recovery_min_hold_ms: hold_controller.status().recovery_min_hold_ms,
            recovery_max_hold_ms: hold_controller.status().recovery_max_hold_ms,
            adaptive_recovery_hold_enabled: hold_controller.status().adaptive_enabled,
            adaptive_calm_samples: hold_controller.status().calm_samples,
            adaptive_last_adjustment_reason: hold_controller.status().last_adjustment_reason,
            capacity: args.ingress_reorder_capacity,
            stats: reorder.stats(),
        },
        repair: repair.clone(),
        server_health: server_health.clone(),
        control_plane,
        return_pmtu: return_pmtu.clone(),
        socket_buffers: SERVER_SOCKET_BUFFER_STATUS
            .get()
            .cloned()
            .into_iter()
            .collect(),
        kernel_network: read_linux_kernel_network_status(tun.map(XBondTun::name)).delta(
            *KERNEL_NETWORK_BASELINE
                .get_or_init(|| read_linux_kernel_network_status(tun.map(XBondTun::name))),
        ),
        saturation: server_saturation_status(),
        stage_timings: server_stage_timings(),
        counters,
    };
    let json = serde_json::to_vec(&status)?;
    std::fs::write(&args.status_path, json)
        .with_context(|| format!("failed to write {}", args.status_path.display()))
}

fn build_ack_frame(frame: &XBondFrame) -> XBondFrame {
    build_ack_frame_from_header(&frame.header)
}

fn build_ack_frame_from_header(header: &XBondHeader) -> XBondFrame {
    let mut header = XBondHeader::new(
        header.kind,
        header.session_id,
        header.sequence,
        header.send_micros,
        header.path_id,
    );
    header.flags = FLAG_SERVER_TO_CLIENT;
    XBondFrame::new(header, b"ack".to_vec())
}

fn is_client_originated_frame(header: &XBondHeader) -> bool {
    header.flags & FLAG_SERVER_TO_CLIENT == 0
}

fn event_name(outcome: ReceiveOutcome) -> &'static str {
    match outcome {
        ReceiveOutcome::Accepted => "first-arrival",
        ReceiveOutcome::Duplicate => "duplicate-dropped",
        ReceiveOutcome::Expired => "late-dropped",
    }
}

fn is_data_like(kind: PacketKind) -> bool {
    matches!(
        kind,
        PacketKind::Data | PacketKind::Duplicate | PacketKind::Repair
    )
}

fn is_tunnel_payload(kind: PacketKind) -> bool {
    matches!(
        kind,
        PacketKind::Data | PacketKind::Duplicate | PacketKind::Repair | PacketKind::Fec
    )
}

fn parse_session_handshake(frame: &XBondFrame) -> Option<ServerSessionHandshakeControl> {
    if frame.header.kind != PacketKind::Control {
        return None;
    }
    match serde_json::from_slice::<XBondControlMessage>(&frame.payload).ok()? {
        XBondControlMessage::SessionOpen {
            session_id,
            request_nonce,
        } => Some(ServerSessionHandshakeControl::Open {
            session_id,
            request_nonce,
        }),
        XBondControlMessage::SessionProof {
            session_id,
            request_nonce,
            challenge,
        } => Some(ServerSessionHandshakeControl::Proof {
            session_id,
            request_nonce,
            challenge,
        }),
        _ => None,
    }
}

fn parse_schedule_control_generation(frame: &XBondFrame) -> Option<u64> {
    if frame.header.kind != PacketKind::Control {
        return None;
    }
    serde_json::from_slice::<ScheduleControlMessage>(&frame.payload)
        .ok()
        .map(|control| control.schedule_generation)
}

fn parse_return_control(frame: &XBondFrame, received_at_micros: u64) -> Option<ReturnControl> {
    if frame.header.kind != PacketKind::Control {
        return None;
    }

    if let Ok(control) = serde_json::from_slice::<ScheduleControlMessage>(&frame.payload) {
        return Some(build_return_control(
            control.schedule_generation,
            received_at_micros,
            control.schedule,
            control.redundancy_policy,
            control.policy_config,
            control.paths,
            control.recovery_active,
        ));
    }

    let schedule = serde_json::from_slice::<SchedulePlan>(&frame.payload).ok()?;
    Some(build_return_control(
        0,
        received_at_micros,
        schedule,
        RedundancyPolicy::Reliable,
        RedundancyPolicyConfig::default(),
        Vec::new(),
        false,
    ))
}

fn parse_repair_request(frame: &XBondFrame) -> Option<Vec<u64>> {
    if frame.header.kind != PacketKind::Control {
        return None;
    }

    let mut sequences = match serde_json::from_slice::<XBondControlMessage>(&frame.payload).ok()? {
        XBondControlMessage::RepairRequest { sequences } => sequences,
        XBondControlMessage::SessionOpen { .. }
        | XBondControlMessage::SessionChallenge { .. }
        | XBondControlMessage::SessionProof { .. }
        | XBondControlMessage::SessionAccepted { .. }
        | XBondControlMessage::SessionRestartRequired { .. }
        | XBondControlMessage::ScheduleAccepted { .. }
        | XBondControlMessage::ServerRecoveryStatus { .. } => return None,
    };
    sequences.sort_unstable();
    sequences.dedup();
    sequences.truncate(MAX_REPAIR_REQUESTS);
    (!sequences.is_empty()).then_some(sequences)
}

#[allow(clippy::too_many_arguments)]
fn build_server_recovery_status(
    recovery_active: bool,
    schedule_required: bool,
    schedule_generation: u64,
    schedule_age_ms: u64,
    reorder: &PacketReorderBuffer,
    repair: &XBondRepairStatus,
    server_health: &XBondServerHealthStatus,
    hold_controller: &IngressReorderHoldController,
    capacity: usize,
) -> XBondServerRecoveryStatus {
    let adaptive = hold_controller.status();
    XBondServerRecoveryStatus {
        reported: true,
        recovery_active,
        schedule_required,
        schedule_generation,
        schedule_age_ms,
        ingress_reorder: XBondServerIngressReorderStatus {
            current_hold_ms: adaptive.current_hold_ms,
            normal_hold_ms: adaptive.normal_hold_ms,
            recovery_min_hold_ms: adaptive.recovery_min_hold_ms,
            recovery_max_hold_ms: adaptive.recovery_max_hold_ms,
            adaptive_recovery_hold_enabled: adaptive.adaptive_enabled,
            adaptive_calm_samples: adaptive.calm_samples,
            adaptive_last_adjustment_reason: adaptive.last_adjustment_reason,
            capacity,
            stats: reorder.stats(),
        },
        repair: repair.clone(),
        server_health: server_health.clone(),
        updated_at_micros: now_micros(),
    }
}

#[allow(clippy::too_many_arguments)]
fn send_server_recovery_status(
    control_send_tx: &mpsc::Sender<ControlSendWork>,
    key: &XBondKey,
    peers: &HashMap<u16, PeerState>,
    session_id: u64,
    control_sequence: &mut u64,
    status: XBondServerRecoveryStatus,
    json_events: bool,
    control_plane: &mut ServerControlPlaneStatus,
) -> Result<()> {
    let now = monotonic_micros();
    let peers = fresh_peers(peers, now);
    if peers.is_empty() || session_id == 0 {
        return Ok(());
    }

    *control_sequence = control_sequence.saturating_add(1);
    let sequence = *control_sequence;
    let payload = serde_json::to_vec(&XBondControlMessage::ServerRecoveryStatus {
        status: Box::new(status),
    })?;

    for (path_id, peer) in &peers {
        let mut header = XBondHeader::new(
            PacketKind::Control,
            session_id,
            sequence,
            now_micros(),
            *path_id,
        );
        header.flags = FLAG_SERVER_TO_CLIENT;
        let frame = XBondFrame::new(header, payload.clone());
        let encoded = frame.encode_sealed(key)?;
        enqueue_control_datagram(control_send_tx, encoded, *peer, control_plane);
    }

    if json_events {
        println!(
            "{}",
            serde_json::json!({
                "event": "server-recovery-status-sent",
                "sequence": sequence,
                "paths": peers.iter().map(|(path_id, _)| *path_id).collect::<Vec<_>>(),
            })
        );
    }

    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn send_repair_requests_for_ingress_gaps(
    control_send_tx: &mpsc::Sender<ControlSendWork>,
    key: &XBondKey,
    peers: &HashMap<u16, PeerState>,
    session_id: u64,
    control_sequence: &mut u64,
    reorder: &mut PacketReorderBuffer,
    repair: &mut XBondRepairStatus,
    recovery_active: bool,
    limiter: &mut IngressRepairLimiter,
    control_plane: &mut ServerControlPlaneStatus,
) -> Result<()> {
    let now = monotonic_micros();
    let peers = fresh_peers(peers, now);
    if peers.is_empty() || session_id == 0 {
        return Ok(());
    }

    let Some((request_interval_micros, request_limit)) =
        ingress_repair_request_parameters(limiter, now, recovery_active, reorder.pending_len())
    else {
        return Ok(());
    };
    let sequences = reorder.repair_requests(now, request_interval_micros, request_limit);
    if sequences.is_empty() {
        return Ok(());
    }
    limiter.record(sequences.len(), recovery_active);

    *control_sequence = control_sequence.saturating_add(1);
    let control_id = *control_sequence;
    let payload = serde_json::to_vec(&XBondControlMessage::RepairRequest {
        sequences: sequences.clone(),
    })?;
    repair.requests_sent = repair.requests_sent.saturating_add(sequences.len() as u64);

    for (path_id, peer) in peers {
        let mut header = XBondHeader::new(
            PacketKind::Control,
            session_id,
            control_id,
            now_micros(),
            path_id,
        );
        header.flags = FLAG_SERVER_TO_CLIENT;
        let frame = XBondFrame::new(header, payload.clone());
        let encoded = frame.encode_sealed(key)?;
        enqueue_control_datagram(control_send_tx, encoded, peer, control_plane);
    }

    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn send_repair_frames_from_server_cache(
    args: &Args,
    socket: &Arc<UdpSocket>,
    key: &XBondKey,
    return_senders: &mut HashMap<u16, ReturnSenderHandle>,
    return_control: Option<&ReturnControl>,
    peers: &HashMap<u16, PeerState>,
    resend_cache: &mut ResendCache,
    session_id: u64,
    sequences: &[u64],
    send_report_tx: &mpsc::Sender<ReturnSendReport>,
    repair: &mut XBondRepairStatus,
    control_plane: &mut ServerControlPlaneStatus,
) -> Result<()> {
    for sequence in sequences.iter().copied().take(MAX_REPAIR_REQUESTS) {
        let now = monotonic_micros();
        let Some(payload) = resend_cache.get(session_id, sequence, now) else {
            repair.cache_misses = repair.cache_misses.saturating_add(1);
            continue;
        };

        let targets =
            select_return_targets(return_control, peers, payload.len(), monotonic_micros());
        if targets.is_empty() {
            repair.cache_misses = repair.cache_misses.saturating_add(1);
            continue;
        }

        let send_micros = now_micros();
        for (path_id, peer, _kind) in targets {
            let control_tx = return_senders
                .entry(path_id)
                .or_insert_with(|| {
                    spawn_return_sender(
                        path_id,
                        socket.clone(),
                        key.clone(),
                        args.tun_queue_capacity.max(1),
                        send_report_tx.clone(),
                    )
                })
                .control_tx
                .clone();
            let mut header = XBondHeader::new(
                PacketKind::Repair,
                session_id,
                sequence,
                send_micros,
                path_id,
            );
            header.flags = FLAG_SERVER_TO_CLIENT;
            let work = ReturnSendWork {
                packet_kind: PacketKind::Repair,
                peer,
                header,
                payload: payload.clone(),
                deadline: Instant::now() + PRIMARY_RETURN_SEND_DEADLINE,
            };
            match control_tx.try_send(work) {
                Ok(()) => {}
                Err(mpsc::error::TrySendError::Full(_)) => {
                    repair.queue_drops = repair.queue_drops.saturating_add(1);
                    control_plane.repair_return_queue_full =
                        control_plane.repair_return_queue_full.saturating_add(1);
                }
                Err(mpsc::error::TrySendError::Closed(_)) => {
                    repair.queue_drops = repair.queue_drops.saturating_add(1);
                }
            }
        }
    }

    repair.cache_entries = resend_cache.len();
    Ok(())
}

fn build_return_control(
    schedule_generation: u64,
    received_at_micros: u64,
    schedule: SchedulePlan,
    policy: RedundancyPolicy,
    policy_config: RedundancyPolicyConfig,
    paths: Vec<PathHealthSnapshot>,
    recovery_active: bool,
) -> ReturnControl {
    let transmission_plans =
        precompute_transmission_plans(&schedule, policy, &paths, policy_config);
    ReturnControl {
        schedule_generation,
        received_at_micros,
        schedule,
        policy,
        policy_config,
        recovery_active,
        transmission_plans,
    }
}

#[allow(clippy::too_many_arguments)]
fn clear_session_forwarding_state(
    receiver: &mut FrameReceiver,
    realtime_deadline_micros: u64,
    peers: &mut HashMap<u16, PeerState>,
    return_control: &mut Option<ReturnControl>,
    resend_cache: &mut ResendCache,
    repair_cache_status: &mut XBondRepairCacheStatus,
    reorder: &mut PacketReorderBuffer,
    fec_recovery: &mut FecRecovery,
    reorder_session_id: &mut u64,
    repair: &mut XBondRepairStatus,
    pending_primary_return: &mut Option<PendingPrimaryReturn>,
    pending_tun_write: &mut Option<PendingTunWriteBatch>,
) {
    *receiver = FrameReceiver::new(realtime_deadline_micros, 8192);
    peers.clear();
    *return_control = None;
    let repair_cache_byte_capacity = resend_cache.byte_capacity();
    *resend_cache = ResendCache::new_with_byte_capacity(
        REPAIR_CACHE_CAPACITY,
        repair_cache_byte_capacity,
        REPAIR_CACHE_TTL_MICROS,
    );
    *repair_cache_status = XBondRepairCacheStatus::default();
    reorder.reset();
    *fec_recovery = FecRecovery::new(8192);
    *reorder_session_id = 0;
    *repair = XBondRepairStatus::default();
    *pending_primary_return = None;
    *pending_tun_write = None;
}

fn return_control_update_is_valid(
    current: Option<&ReturnControl>,
    candidate: &ReturnControl,
) -> bool {
    let Some(current) = current else {
        return true;
    };

    if candidate.schedule_generation != current.schedule_generation {
        return candidate.schedule_generation > current.schedule_generation;
    }

    candidate.schedule == current.schedule
        && candidate.policy == current.policy
        && candidate.policy_config == current.policy_config
        && candidate.recovery_active == current.recovery_active
}

fn return_control_matches_current(
    current: Option<&ReturnControl>,
    candidate: &ReturnControl,
) -> bool {
    current.is_some_and(|current| {
        candidate.schedule_generation == current.schedule_generation
            && return_control_update_is_valid(Some(current), candidate)
    })
}

fn select_return_targets(
    control: Option<&ReturnControl>,
    peers: &HashMap<u16, PeerState>,
    packet_len: usize,
    now_micros: u64,
) -> Vec<(u16, SocketAddr, PacketKind)> {
    let Some(control) = control else {
        return Vec::new();
    };
    if return_schedule_grace_expired(control, now_micros) {
        return Vec::new();
    }

    let transmissions = control
        .transmission_plans
        .for_packet_len(
            packet_len,
            control.policy_config.interactive_packet_threshold_bytes,
        )
        .iter()
        .filter(|transmission| {
            matches!(
                transmission.packet_kind,
                PacketKind::Data | PacketKind::Duplicate
            )
        })
        .collect::<Vec<_>>();
    let mut scheduled = transmissions
        .into_iter()
        .filter_map(|transmission| {
            peers
                .get(&transmission.path_id)
                .filter(|peer| peer_is_fresh(peer, now_micros))
                .map(|peer| (transmission.path_id, peer.addr, transmission.packet_kind))
        })
        .collect::<Vec<_>>();

    if !scheduled.is_empty() {
        if !scheduled
            .iter()
            .any(|(_, _, kind)| *kind == PacketKind::Data)
        {
            if let Some(first) = scheduled.first_mut() {
                first.2 = PacketKind::Data;
            }
        }
        return scheduled;
    }

    Vec::new()
}

fn peer_is_fresh(peer: &PeerState, now_micros: u64) -> bool {
    now_micros.saturating_sub(peer.last_seen_micros) <= PEER_STALE_AFTER_MICROS
}

fn return_schedule_is_stale(control: &ReturnControl, now_micros: u64) -> bool {
    now_micros.saturating_sub(control.received_at_micros) > RETURN_SCHEDULE_STALE_AFTER_MICROS
}

fn return_schedule_grace_expired(control: &ReturnControl, now_micros: u64) -> bool {
    now_micros.saturating_sub(control.received_at_micros)
        > RETURN_SCHEDULE_STALE_AFTER_MICROS.saturating_add(RETURN_SCHEDULE_GRACE_MICROS)
}

fn schedule_age_ms(control: &ReturnControl, now_micros: u64) -> u64 {
    now_micros
        .saturating_sub(control.received_at_micros)
        .saturating_div(1_000)
}

fn schedule_sync_status(
    return_control: Option<&ReturnControl>,
    now_micros: u64,
) -> (bool, u64, u64) {
    match return_control {
        Some(control) => (
            return_schedule_is_stale(control, now_micros),
            control.schedule_generation,
            schedule_age_ms(control, now_micros),
        ),
        None => (true, 0, 0),
    }
}

fn fresh_peers(peers: &HashMap<u16, PeerState>, now_micros: u64) -> Vec<(u16, SocketAddr)> {
    let mut fresh = peers
        .iter()
        .filter(|(_, peer)| peer_is_fresh(peer, now_micros))
        .map(|(path_id, peer)| (*path_id, peer.addr))
        .collect::<Vec<_>>();
    fresh.sort_by_key(|(path_id, _)| *path_id);
    fresh
}

fn expire_stale_peers(peers: &mut HashMap<u16, PeerState>, now_micros: u64) {
    peers.retain(|_, peer| peer_is_fresh(peer, now_micros));
}

fn print_packet_event(
    event: &str,
    outcome: ReceiveOutcome,
    frame: &XBondFrame,
    peer: &str,
    ack_sent: bool,
    receiver: &FrameReceiver,
) {
    let stats = receiver.stats();
    println!(
        "{}",
        serde_json::json!({
            "event": event,
            "outcome": format!("{outcome:?}"),
            "session_id": frame.header.session_id,
            "sequence": frame.header.sequence,
            "path_id": frame.header.path_id,
            "packet_kind": frame.header.kind,
            "peer": peer,
            "ack_sent": ack_sent,
            "counters": {
                "first_arrivals": stats.accepted_packets,
                "duplicates_dropped": stats.duplicate_packets_dropped,
                "late_packets_dropped": stats.late_packets_dropped,
            }
        })
    );
}

fn print_data_event(
    header: &XBondHeader,
    payload_len: usize,
    forwarded_packets: u64,
    dropped_reason: Option<&str>,
    counters: TunnelCounters,
) {
    println!(
        "{}",
        serde_json::json!({
            "event": "data-decapsulated",
            "session_id": header.session_id,
            "sequence": header.sequence,
            "path_id": header.path_id,
            "packet_kind": header.kind,
            "bytes": payload_len,
            "forwarded_to_tun": forwarded_packets > 0,
            "forwarded_packets": forwarded_packets,
            "dropped_reason": dropped_reason,
            "counters": {
                "data_packets_received": counters.data_packets_received,
                "data_packets_forwarded": counters.data_packets_forwarded,
                "fec_packets_received": counters.fec_packets_received,
                "fec_packets_recovered": counters.fec_packets_recovered,
                "invalid_fec_packets_dropped": counters.invalid_fec_packets_dropped,
                "non_ipv4_packets_dropped": counters.non_ipv4_packets_dropped,
            }
        })
    );
}

fn now_micros() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_micros().min(u128::from(u64::MAX)) as u64)
        .unwrap_or_default()
}

fn monotonic_micros() -> u64 {
    static START: OnceLock<Instant> = OnceLock::new();
    START
        .get_or_init(Instant::now)
        .elapsed()
        .as_micros()
        .min(u128::from(u64::MAX)) as u64
}

fn initial_control_sequence() -> u64 {
    control_sequence_epoch(now_micros())
}

fn control_sequence_epoch(unix_micros: u64) -> u64 {
    // Leave ample sequence space between process starts so a restarted server's
    // authenticated controls remain ahead of the client's existing replay window.
    unix_micros.saturating_mul(1_024).max(2_000_000_000_000)
}

fn random_handshake_nonce() -> Result<SessionHandshakeNonce> {
    loop {
        let mut nonce = [0_u8; 16];
        getrandom::getrandom(&mut nonce).map_err(|error| {
            anyhow::anyhow!("generate authenticated session handshake nonce: {error}")
        })?;
        if nonce.iter().any(|byte| *byte != 0) {
            return Ok(nonce);
        }
    }
}

fn initial_reverse_sequence() -> u64 {
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    use xbond_core::ScheduleMode;

    fn nonce(value: u8) -> SessionHandshakeNonce {
        [value; 16]
    }

    fn inbound_frame(
        kind: PacketKind,
        session_id: u64,
        sequence: u64,
        path_id: u16,
        payload: Vec<u8>,
    ) -> InboundServerFrame {
        inbound_frame_from(
            "192.0.2.1:1000".parse().unwrap(),
            kind,
            session_id,
            sequence,
            path_id,
            payload,
        )
    }

    fn inbound_frame_from(
        peer: SocketAddr,
        kind: PacketKind,
        session_id: u64,
        sequence: u64,
        path_id: u16,
        payload: Vec<u8>,
    ) -> InboundServerFrame {
        InboundServerFrame::new(
            XBondFrame::new(
                XBondHeader::new(kind, session_id, sequence, now_micros(), path_id),
                payload,
            ),
            peer,
            false,
            None,
        )
    }

    fn schedule_frame(session_id: u64, sequence: u64, generation: u64) -> InboundServerFrame {
        schedule_frame_from(
            "192.0.2.1:1000".parse().unwrap(),
            session_id,
            sequence,
            generation,
            1,
        )
    }

    fn schedule_frame_from(
        peer: SocketAddr,
        session_id: u64,
        sequence: u64,
        generation: u64,
        path_id: u16,
    ) -> InboundServerFrame {
        inbound_frame_from(
            peer,
            PacketKind::Control,
            session_id,
            sequence,
            path_id,
            serde_json::to_vec(&ScheduleControlMessage {
                schedule_generation: generation,
                schedule: SchedulePlan {
                    mode: ScheduleMode::AnchorOnly,
                    anchor_path_id: Some(1),
                    data_path_ids: vec![1],
                    duplicate_path_ids: Vec::new(),
                    fec_path_ids: Vec::new(),
                },
                redundancy_policy: RedundancyPolicy::Balanced,
                policy_config: RedundancyPolicyConfig::default(),
                paths: Vec::new(),
                recovery_active: false,
            })
            .unwrap(),
        )
    }

    #[test]
    fn restarted_server_control_epoch_advances_with_wall_clock() {
        let previous = control_sequence_epoch(1_700_000_000_000_000);
        let restarted = control_sequence_epoch(1_700_000_000_001_000);

        assert!(restarted > previous);
        assert!(restarted.saturating_sub(previous) > 8192);
    }

    fn open_session(
        gate: &mut ServerSessionGate,
        session_id: u64,
        request_nonce: SessionHandshakeNonce,
        challenge: SessionHandshakeNonce,
        now_micros: u64,
    ) -> ServerSessionOpenDecision {
        assert_eq!(
            gate.issue_challenge(session_id, session_id, request_nonce, challenge, now_micros,),
            Some(challenge)
        );
        gate.prove(
            session_id,
            session_id,
            request_nonce,
            challenge,
            now_micros + 1,
        )
    }

    #[test]
    fn ack_preserves_heartbeat_or_control_kind() {
        for kind in [PacketKind::Heartbeat, PacketKind::Control] {
            let frame = XBondFrame::new(XBondHeader::new(kind, 1, 2, 3, 4), b"hello".to_vec());
            let ack = build_ack_frame(&frame);

            assert_eq!(ack.header.kind, kind);
            assert_eq!(ack.header.sequence, frame.header.sequence);
            assert_eq!(ack.header.path_id, frame.header.path_id);
            assert_eq!(ack.header.flags, FLAG_SERVER_TO_CLIENT);
            assert_eq!(ack.payload, b"ack");
        }
    }

    #[test]
    fn ack_uses_a_distinct_authenticated_direction_and_round_trips() {
        let key = XBondKey::from_passphrase("test-key");
        let request = XBondFrame::new(
            XBondHeader::new(PacketKind::Heartbeat, 1, 2, 3, 4),
            b"heartbeat".to_vec(),
        );
        let ack = build_ack_frame(&request);

        let request_encoded = request.encode_sealed(&key).unwrap();
        let ack_encoded = ack.encode_sealed(&key).unwrap();

        assert_ne!(request_encoded, ack_encoded);
        assert_eq!(XBondFrame::decode_sealed(&ack_encoded, &key).unwrap(), ack);
        assert!(is_client_originated_frame(&request.header));
        assert!(!is_client_originated_frame(&ack.header));
    }

    #[test]
    fn server_wire_path_uses_sealed_frames() {
        let key = XBondKey::from_passphrase("test-key");
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Heartbeat, 1, 2, 3, 4),
            b"heartbeat".to_vec(),
        );

        let encoded = frame.encode_sealed(&key).unwrap();
        assert!(XBondFrame::decode(&encoded).is_ok());
        assert!(!encoded.windows(9).any(|window| window == b"heartbeat"));
        assert_eq!(XBondFrame::decode_sealed(&encoded, &key).unwrap(), frame);
    }

    #[test]
    fn authenticated_session_accepts_lower_random_epoch_and_rejects_captured_proof() {
        let mut gate = ServerSessionGate::new();

        assert_eq!(
            open_session(&mut gate, 9_000, nonce(1), nonce(2), 10),
            ServerSessionOpenDecision::AcceptedNew
        );
        assert_eq!(
            open_session(&mut gate, 42, nonce(3), nonce(4), 20),
            ServerSessionOpenDecision::AcceptedNew
        );
        assert_eq!(gate.current(), Some(42));
        assert!(gate.accepts(42));
        assert!(!gate.accepts(9_000));

        assert_eq!(
            gate.issue_challenge(9_000, 9_000, nonce(1), nonce(9), 30),
            None
        );
        assert_eq!(
            gate.prove(9_000, 9_000, nonce(1), nonce(2), 31),
            ServerSessionOpenDecision::Rejected
        );
        assert_eq!(gate.current(), Some(42));
    }

    #[test]
    fn current_session_open_is_idempotent_until_restart_is_required() {
        let mut gate = ServerSessionGate::new();

        assert_eq!(
            open_session(&mut gate, 42, nonce(1), nonce(2), 10),
            ServerSessionOpenDecision::AcceptedNew
        );
        assert_eq!(
            open_session(&mut gate, 42, nonce(3), nonce(4), 20),
            ServerSessionOpenDecision::AcceptedCurrent
        );
        assert_eq!(gate.require_restart(), Some(42));
        assert!(!gate.accepts(42));
        assert!(gate.restart_required_for(42));
        assert!(!gate.restart_required_for(7));
        assert_eq!(
            open_session(&mut gate, 42, nonce(5), nonce(6), 30),
            ServerSessionOpenDecision::RestartRequired
        );
        assert_eq!(
            open_session(&mut gate, 7, nonce(7), nonce(8), 40),
            ServerSessionOpenDecision::AcceptedNew
        );
        assert!(gate.accepts(7));
        assert!(!gate.restart_required_for(7));
    }

    #[test]
    fn fresh_server_requests_restart_for_an_authenticated_unknown_session() {
        let gate = ServerSessionGate::new();

        assert!(!gate.accepts(42));
        assert!(gate.restart_required_for(42));
    }

    #[test]
    fn session_open_requires_matching_authenticated_header_epoch() {
        let mut gate = ServerSessionGate::new();
        assert_eq!(gate.issue_challenge(42, 7, nonce(1), nonce(2), 10), None);
        assert_eq!(
            gate.prove(42, 7, nonce(1), nonce(2), 11),
            ServerSessionOpenDecision::Rejected
        );
        assert_eq!(gate.current(), None);
    }

    #[test]
    fn opening_new_session_clears_forwarding_state() {
        let mut receiver = FrameReceiver::new(1_000_000, 16);
        let accepted = XBondFrame::new(
            XBondHeader::new(PacketKind::Data, 9, 1, now_micros(), 1),
            vec![0x45],
        );
        assert_eq!(
            receiver.observe(&accepted, now_micros()),
            ReceiveOutcome::Accepted
        );
        let peer: SocketAddr = "192.0.2.1:1000".parse().unwrap();
        let mut peers = HashMap::from([(1, peer_state(peer, monotonic_micros()))]);
        let mut return_control = Some(build_return_control(
            3,
            monotonic_micros(),
            SchedulePlan {
                mode: ScheduleMode::AnchorOnly,
                anchor_path_id: Some(1),
                data_path_ids: vec![1],
                duplicate_path_ids: Vec::new(),
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Balanced,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        ));
        let mut resend_cache = ResendCache::new_with_byte_capacity(8, 16, 1_000_000);
        resend_cache.insert(9, 1, Arc::new(vec![0x45]), monotonic_micros());
        let mut reorder = PacketReorderBuffer::new(8, 50_000);
        reorder.push(2, 1, vec![0x45], monotonic_micros(), 0);
        let mut fec_recovery = FecRecovery::new(8);
        let mut reorder_session_id = 9;
        let mut repair = XBondRepairStatus {
            requests_sent: 1,
            ..XBondRepairStatus::default()
        };
        let mut repair_cache_status = XBondRepairCacheStatus {
            entries: 4,
            accounted_bytes: 128,
            quiescent: true,
            quiescent_since_micros: Some(42),
            ..XBondRepairCacheStatus::default()
        };
        let mut pending_primary_return = Some(PendingPrimaryReturn {
            work: ReturnSendWork {
                packet_kind: PacketKind::Data,
                peer,
                header: XBondHeader::new(PacketKind::Data, 9, 1, now_micros(), 1),
                payload: Arc::new(vec![0x45]),
                deadline: Instant::now() + PRIMARY_RETURN_QUEUE_DEADLINE,
            },
            alternate_copy_accepted: false,
        });
        let mut pending_tun_write = Some(PendingTunWriteBatch::new(
            vec![ReorderedPacket {
                sequence: 1,
                path_id: 1,
                payload: vec![0x45],
            }],
            TUN_WRITE_ENQUEUE_DEADLINE,
        ));

        clear_session_forwarding_state(
            &mut receiver,
            1_000_000,
            &mut peers,
            &mut return_control,
            &mut resend_cache,
            &mut repair_cache_status,
            &mut reorder,
            &mut fec_recovery,
            &mut reorder_session_id,
            &mut repair,
            &mut pending_primary_return,
            &mut pending_tun_write,
        );

        assert!(peers.is_empty());
        assert!(return_control.is_none());
        assert_eq!(resend_cache.len(), 0);
        assert_eq!(resend_cache.byte_capacity(), 16);
        assert_eq!(repair_cache_status, XBondRepairCacheStatus::default());
        assert_eq!(reorder.stats().pending_depth, 0);
        assert_eq!(reorder_session_id, 0);
        assert_eq!(repair.requests_sent, 0);
        assert!(pending_primary_return.is_none());
        assert!(pending_tun_write.is_none());
        assert_eq!(receiver.stats().accepted_packets, 0);
    }

    #[tokio::test]
    async fn schedule_acceptance_and_restart_messages_are_explicit_control_frames() {
        let key = XBondKey::from_passphrase("test-key");
        let peer: SocketAddr = "192.0.2.1:1000".parse().unwrap();
        let (tx, mut rx) = mpsc::channel(2);
        let mut sequence = 10;
        let mut telemetry = ServerControlPlaneStatus::default();

        for message in [
            XBondControlMessage::ScheduleAccepted {
                session_id: 42,
                schedule_generation: 7,
            },
            XBondControlMessage::SessionRestartRequired {
                session_id: 42,
                reason: "schedule expired".to_string(),
            },
        ] {
            assert!(enqueue_control_message(
                &tx,
                &key,
                peer,
                42,
                &mut sequence,
                &message,
                &mut telemetry,
            )
            .unwrap());
            let work = rx.recv().await.unwrap();
            let frame = XBondFrame::decode_sealed(&work.encoded, &key).unwrap();
            assert_eq!(frame.header.kind, PacketKind::Control);
            assert_eq!(frame.header.session_id, 42);
            assert_eq!(frame.header.flags, FLAG_SERVER_TO_CLIENT);
            assert_eq!(
                serde_json::from_slice::<XBondControlMessage>(&frame.payload).unwrap(),
                message
            );
        }
    }

    #[test]
    fn data_and_duplicate_are_tunnel_payload_kinds() {
        assert!(is_data_like(PacketKind::Data));
        assert!(is_data_like(PacketKind::Duplicate));
        assert!(is_data_like(PacketKind::Repair));
        assert!(!is_data_like(PacketKind::Heartbeat));
        assert!(!is_data_like(PacketKind::Fec));
    }

    #[tokio::test]
    async fn prioritized_control_progresses_when_payload_queue_is_saturated() {
        let critical = BoundedCoalescingQueue::new(1, false);
        let schedules = BoundedCoalescingQueue::new(1, false);
        let heartbeats = BoundedCoalescingQueue::new(1, true);
        let (repair_tx, mut repair_rx) = mpsc::channel(1);
        let (payload_tx, mut payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);

        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                inbound_frame(PacketKind::Data, 1, 1, 1, vec![0x45]),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Queued,
        );
        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                inbound_frame(PacketKind::Data, 1, 2, 1, vec![0x45]),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Dropped,
        );
        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                inbound_frame(PacketKind::Heartbeat, 1, 3, 1, Vec::new()),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Queued,
        );

        let received = time::timeout(
            Duration::from_millis(50),
            receive_prioritized_frame(
                &critical,
                &schedules,
                &heartbeats,
                &mut repair_rx,
                &mut payload_rx,
                true,
            ),
        )
        .await
        .unwrap()
        .unwrap();
        assert_eq!(received.frame.header.kind, PacketKind::Heartbeat);
        assert_eq!(payload_rx.len(), 1);
        assert_eq!(payload_drops.load(Ordering::Relaxed), 1);
    }

    #[tokio::test]
    async fn pending_tun_admission_pauses_payload_without_blocking_control() {
        let critical = BoundedCoalescingQueue::new(1, false);
        let schedules = BoundedCoalescingQueue::new(1, false);
        let heartbeats = BoundedCoalescingQueue::new(1, true);
        let (_repair_tx, mut repair_rx) = mpsc::channel(1);
        let (payload_tx, mut payload_rx) = mpsc::channel(1);

        payload_tx
            .send(inbound_frame(PacketKind::Data, 1, 1, 1, vec![0x45]))
            .await
            .unwrap();
        assert_eq!(
            heartbeats.push_with(
                ResponseRouteKey {
                    session_id: 1,
                    path_id: 1,
                    peer: "192.0.2.1:1000".parse().unwrap(),
                },
                inbound_frame(PacketKind::Heartbeat, 1, 2, 1, Vec::new()),
                |_, _| true,
            ),
            CoalescingQueuePush::Queued
        );

        let received = time::timeout(
            Duration::from_millis(50),
            receive_prioritized_frame(
                &critical,
                &schedules,
                &heartbeats,
                &mut repair_rx,
                &mut payload_rx,
                false,
            ),
        )
        .await
        .unwrap()
        .unwrap();

        assert_eq!(received.frame.header.kind, PacketKind::Heartbeat);
        assert_eq!(payload_rx.len(), 1);
    }

    #[tokio::test]
    async fn saturated_control_lanes_coalesce_latest_and_fail_closed_for_unique_critical_work() {
        let critical = BoundedCoalescingQueue::new(1, false);
        let schedules = BoundedCoalescingQueue::new(1, false);
        let heartbeats = BoundedCoalescingQueue::new(1, true);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let (payload_tx, _payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);
        let open = |sequence| {
            inbound_frame(
                PacketKind::Control,
                7,
                sequence,
                1,
                serde_json::to_vec(&XBondControlMessage::SessionOpen {
                    session_id: 7,
                    request_nonce: nonce(1),
                })
                .unwrap(),
            )
        };

        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                open(1),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Queued
        );
        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                open(2),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Coalesced
        );
        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                inbound_frame(
                    PacketKind::Control,
                    7,
                    3,
                    1,
                    serde_json::to_vec(&XBondControlMessage::SessionProof {
                        session_id: 7,
                        request_nonce: nonce(1),
                        challenge: nonce(2),
                    })
                    .unwrap(),
                ),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::CriticalSaturated
        );

        let queued = time::timeout(Duration::from_millis(50), critical.recv())
            .await
            .unwrap();
        assert_eq!(queued.frame.header.sequence, 2);
        assert_eq!(coalesced.load(Ordering::Relaxed), 1);
    }

    #[tokio::test]
    async fn saturated_schedule_slot_keeps_newest_generation() {
        let critical = BoundedCoalescingQueue::new(1, false);
        let schedules = BoundedCoalescingQueue::new(1, false);
        let heartbeats = BoundedCoalescingQueue::new(1, true);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let (payload_tx, _payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);

        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                schedule_frame(9, 10, 10),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Queued
        );
        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                schedule_frame(9, 11, 11),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Coalesced
        );
        assert_eq!(
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                schedule_frame(9, 9, 9),
                &payload_drops,
                &repair_drops,
                &coalesced,
            ),
            InboundDispatchOutcome::Coalesced
        );

        let queued = time::timeout(Duration::from_millis(50), schedules.recv())
            .await
            .unwrap();
        assert_eq!(parse_schedule_control_generation(&queued.frame), Some(11));
        assert_eq!(queued.frame.header.sequence, 11);
        assert_eq!(coalesced.load(Ordering::Relaxed), 2);
    }

    #[tokio::test]
    async fn saturated_heartbeat_slot_keeps_latest_sequence_per_path() {
        let critical = BoundedCoalescingQueue::new(1, false);
        let schedules = BoundedCoalescingQueue::new(1, false);
        let heartbeats = BoundedCoalescingQueue::new(1, true);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let (payload_tx, _payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);

        for (sequence, expected) in [
            (10, InboundDispatchOutcome::Queued),
            (11, InboundDispatchOutcome::Coalesced),
            (9, InboundDispatchOutcome::Coalesced),
        ] {
            assert_eq!(
                dispatch_inbound_frame(
                    &critical,
                    &schedules,
                    &heartbeats,
                    &repair_tx,
                    &payload_tx,
                    inbound_frame(PacketKind::Heartbeat, 4, sequence, 2, Vec::new()),
                    &payload_drops,
                    &repair_drops,
                    &coalesced,
                ),
                expected
            );
        }

        let queued = time::timeout(Duration::from_millis(50), heartbeats.recv())
            .await
            .unwrap();
        assert_eq!(queued.frame.header.sequence, 11);
        assert_eq!(coalesced.load(Ordering::Relaxed), 2);
    }

    #[tokio::test]
    async fn every_authenticated_heartbeat_is_acked_before_coalescing() {
        let key = XBondKey::from_passphrase("heartbeat-integrity-test");
        let send_socket = Arc::new(UdpSocket::bind("127.0.0.1:0").await.unwrap());
        let receive_socket = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let peer = receive_socket.local_addr().unwrap();
        let ack_sender = spawn_heartbeat_ack_sender(send_socket, false);
        let critical = BoundedCoalescingQueue::new(1, false);
        let schedules = BoundedCoalescingQueue::new(1, false);
        let heartbeats = BoundedCoalescingQueue::new(1, true);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let (payload_tx, _payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);

        for sequence in [10, 11] {
            let header = XBondHeader::new(PacketKind::Heartbeat, 4, sequence, now_micros(), 2);
            assert!(acknowledge_authenticated_heartbeat(
                &header,
                &key,
                peer,
                &ack_sender,
                false,
            ));
            dispatch_inbound_frame(
                &critical,
                &schedules,
                &heartbeats,
                &repair_tx,
                &payload_tx,
                inbound_frame(PacketKind::Heartbeat, 4, sequence, 2, Vec::new()),
                &payload_drops,
                &repair_drops,
                &coalesced,
            );
        }

        let mut received_sequences = Vec::new();
        let mut buffer = [0u8; 512];
        for _ in 0..2 {
            let (length, _) = time::timeout(
                Duration::from_millis(100),
                receive_socket.recv_from(&mut buffer),
            )
            .await
            .unwrap()
            .unwrap();
            let ack = XBondFrame::decode_sealed(&buffer[..length], &key).unwrap();
            received_sequences.push(ack.header.sequence);
        }

        assert_eq!(received_sequences, vec![10, 11]);
        assert_eq!(coalesced.load(Ordering::Relaxed), 1);
        assert_eq!(
            ack_sender.metrics.immediate_sent.load(Ordering::Relaxed)
                + ack_sender.metrics.retry_sent.load(Ordering::Relaxed),
            2
        );
        assert_eq!(ack_sender.metrics.retry_overflow.load(Ordering::Relaxed), 0);
        assert_eq!(ack_sender.metrics.depth.load(Ordering::Relaxed), 0);
    }

    #[tokio::test]
    async fn heartbeat_ack_preserves_per_path_and_aggregate_namespaces() {
        let key = XBondKey::from_passphrase("heartbeat-namespace-test");
        let send_socket = Arc::new(UdpSocket::bind("127.0.0.1:0").await.unwrap());
        let receive_socket = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let peer = receive_socket.local_addr().unwrap();
        let ack_sender = spawn_heartbeat_ack_sender(send_socket, false);
        let sequences = [0x0001_0000_0000_0042, 0xffff_0000_0000_0042];

        for (path_id, sequence) in [(3, sequences[0]), (1, sequences[1])] {
            let header =
                XBondHeader::new(PacketKind::Heartbeat, 9, sequence, now_micros(), path_id);
            assert!(acknowledge_authenticated_heartbeat(
                &header,
                &key,
                peer,
                &ack_sender,
                false,
            ));
        }

        let mut observed = Vec::new();
        let mut buffer = [0u8; 512];
        for _ in 0..2 {
            let (length, _) = receive_socket.recv_from(&mut buffer).await.unwrap();
            let ack = XBondFrame::decode_sealed(&buffer[..length], &key).unwrap();
            observed.push((ack.header.path_id, ack.header.sequence));
        }
        assert_eq!(observed, vec![(3, sequences[0]), (1, sequences[1])]);
    }

    #[tokio::test]
    async fn saturated_general_control_queue_cannot_drop_heartbeat_ack() {
        let (general_tx, _general_rx) = mpsc::channel(1);
        general_tx
            .try_send(ControlSendWork {
                encoded: vec![1],
                peer: "127.0.0.1:9".parse().unwrap(),
            })
            .unwrap();
        assert!(general_tx.try_reserve().is_err());

        let key = XBondKey::from_passphrase("isolated-ack-lane-test");
        let send_socket = Arc::new(UdpSocket::bind("127.0.0.1:0").await.unwrap());
        let receive_socket = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let peer = receive_socket.local_addr().unwrap();
        let ack_sender = spawn_heartbeat_ack_sender(send_socket, false);
        let header = XBondHeader::new(PacketKind::Heartbeat, 5, 7, now_micros(), 1);

        assert!(acknowledge_authenticated_heartbeat(
            &header,
            &key,
            peer,
            &ack_sender,
            false,
        ));
        let mut buffer = [0u8; 512];
        let received = time::timeout(
            Duration::from_millis(100),
            receive_socket.recv_from(&mut buffer),
        )
        .await;
        assert!(received.is_ok());
    }

    #[tokio::test]
    async fn heartbeat_ack_retry_lane_is_nonblocking_and_accounts_for_saturation() {
        let socket = Arc::new(UdpSocket::bind("127.0.0.1:0").await.unwrap());
        let (retry_tx, _retry_rx) = mpsc::channel(1);
        let metrics = Arc::new(HeartbeatAckMetrics::default());
        let sender = HeartbeatAckSender {
            socket,
            retry_tx,
            metrics: metrics.clone(),
        };
        let work = || ControlSendWork {
            encoded: vec![1],
            peer: "127.0.0.1:9".parse().unwrap(),
        };

        let started = Instant::now();
        assert!(sender.enqueue_retry(work()));
        assert!(!sender.enqueue_retry(work()));
        assert!(started.elapsed() < Duration::from_millis(5));
        assert_eq!(metrics.would_block.load(Ordering::Relaxed), 2);
        assert_eq!(metrics.retry_queued.load(Ordering::Relaxed), 1);
        assert_eq!(metrics.retry_overflow.load(Ordering::Relaxed), 1);
        assert_eq!(metrics.depth.load(Ordering::Relaxed), 1);
    }

    #[tokio::test]
    async fn heartbeat_ack_retry_send_is_bounded_by_timeout() {
        let result = await_heartbeat_ack_retry(
            std::future::pending::<std::io::Result<usize>>(),
            1,
            Duration::from_millis(2),
        )
        .await;
        assert_eq!(result, HeartbeatAckRetryResult::Timeout);
    }

    #[tokio::test]
    async fn normal_heartbeat_rate_uses_immediate_send_without_retry_saturation() {
        let key = XBondKey::from_passphrase("heartbeat-rate-test");
        let send_socket = Arc::new(UdpSocket::bind("127.0.0.1:0").await.unwrap());
        let receive_socket = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let peer = receive_socket.local_addr().unwrap();
        let ack_sender = spawn_heartbeat_ack_sender(send_socket, false);
        let heartbeat_count = HEARTBEAT_ACK_QUEUE_CAPACITY as u64 * 2;
        let receiver = tokio::spawn(async move {
            let mut buffer = [0u8; 512];
            for _ in 0..heartbeat_count {
                receive_socket.recv_from(&mut buffer).await.unwrap();
            }
        });

        for sequence in 0..heartbeat_count {
            let header = XBondHeader::new(PacketKind::Heartbeat, 5, sequence, now_micros(), 1);
            assert!(acknowledge_authenticated_heartbeat(
                &header,
                &key,
                peer,
                &ack_sender,
                false,
            ));
            time::sleep(Duration::from_millis(1)).await;
        }
        time::timeout(Duration::from_secs(2), receiver)
            .await
            .unwrap()
            .unwrap();

        assert_eq!(
            ack_sender.metrics.immediate_sent.load(Ordering::Relaxed)
                + ack_sender.metrics.retry_sent.load(Ordering::Relaxed),
            heartbeat_count
        );
        assert_eq!(ack_sender.metrics.retry_overflow.load(Ordering::Relaxed), 0);
        assert_eq!(ack_sender.metrics.retry_timeouts.load(Ordering::Relaxed), 0);
        assert_eq!(ack_sender.metrics.retry_failures.load(Ordering::Relaxed), 0);
        assert_eq!(ack_sender.metrics.depth.load(Ordering::Relaxed), 0);
    }

    #[tokio::test]
    async fn multipath_handshake_responses_reach_viable_peer_when_other_reverse_path_is_broken() {
        let broken_peer: SocketAddr = "192.0.2.10:1000".parse().unwrap();
        let viable_peer: SocketAddr = "192.0.2.11:1000".parse().unwrap();
        let critical = BoundedCoalescingQueue::new(4, false);
        let schedules = BoundedCoalescingQueue::new(4, false);
        let heartbeats = BoundedCoalescingQueue::new(4, true);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let (payload_tx, _payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);
        let request_nonce = nonce(1);
        let open_payload = serde_json::to_vec(&XBondControlMessage::SessionOpen {
            session_id: 7,
            request_nonce,
        })
        .unwrap();

        for (peer, path_id) in [(broken_peer, 1), (viable_peer, 2)] {
            assert_eq!(
                dispatch_inbound_frame(
                    &critical,
                    &schedules,
                    &heartbeats,
                    &repair_tx,
                    &payload_tx,
                    inbound_frame_from(
                        peer,
                        PacketKind::Control,
                        7,
                        10,
                        path_id,
                        open_payload.clone(),
                    ),
                    &payload_drops,
                    &repair_drops,
                    &coalesced,
                ),
                InboundDispatchOutcome::Queued
            );
        }

        let key = XBondKey::from_passphrase("test-key");
        let (response_tx, mut response_rx) = mpsc::channel(8);
        let mut control_sequence = 100;
        let mut control_plane = ServerControlPlaneStatus::default();
        let mut gate = ServerSessionGate::new();
        let mut challenge = None;
        for fresh_challenge in [nonce(2), nonce(3)] {
            let inbound = critical.recv().await;
            let ServerSessionHandshakeControl::Open {
                session_id,
                request_nonce,
            } = parse_session_handshake(&inbound.frame).unwrap()
            else {
                panic!("expected session open");
            };
            let issued = gate
                .issue_challenge(
                    inbound.frame.header.session_id,
                    session_id,
                    request_nonce,
                    fresh_challenge,
                    1_000,
                )
                .unwrap();
            challenge.get_or_insert(issued);
            enqueue_control_message(
                &response_tx,
                &key,
                inbound.peer,
                session_id,
                &mut control_sequence,
                &XBondControlMessage::SessionChallenge {
                    session_id,
                    request_nonce,
                    challenge: issued,
                },
                &mut control_plane,
            )
            .unwrap();
        }
        let challenge = challenge.unwrap();
        let challenge_responses = [
            response_rx.recv().await.unwrap(),
            response_rx.recv().await.unwrap(),
        ];
        assert!(challenge_responses.iter().any(|work| {
            work.peer == viable_peer
                && matches!(
                    XBondFrame::decode_sealed(&work.encoded, &key)
                        .ok()
                        .and_then(|frame| serde_json::from_slice(&frame.payload).ok()),
                    Some(XBondControlMessage::SessionChallenge { .. })
                )
        }));

        let proof_payload = serde_json::to_vec(&XBondControlMessage::SessionProof {
            session_id: 7,
            request_nonce,
            challenge,
        })
        .unwrap();
        for (peer, path_id) in [(broken_peer, 1), (viable_peer, 2)] {
            assert_eq!(
                dispatch_inbound_frame(
                    &critical,
                    &schedules,
                    &heartbeats,
                    &repair_tx,
                    &payload_tx,
                    inbound_frame_from(
                        peer,
                        PacketKind::Control,
                        7,
                        11,
                        path_id,
                        proof_payload.clone(),
                    ),
                    &payload_drops,
                    &repair_drops,
                    &coalesced,
                ),
                InboundDispatchOutcome::Queued
            );
        }

        for _ in 0..2 {
            let inbound = critical.recv().await;
            let ServerSessionHandshakeControl::Proof {
                session_id,
                request_nonce,
                challenge,
            } = parse_session_handshake(&inbound.frame).unwrap()
            else {
                panic!("expected session proof");
            };
            assert!(matches!(
                gate.prove(
                    inbound.frame.header.session_id,
                    session_id,
                    request_nonce,
                    challenge,
                    1_001,
                ),
                ServerSessionOpenDecision::AcceptedNew | ServerSessionOpenDecision::AcceptedCurrent
            ));
            enqueue_control_message(
                &response_tx,
                &key,
                inbound.peer,
                session_id,
                &mut control_sequence,
                &XBondControlMessage::SessionAccepted {
                    session_id,
                    request_nonce,
                    challenge,
                },
                &mut control_plane,
            )
            .unwrap();
        }
        let acceptance_responses = [
            response_rx.recv().await.unwrap(),
            response_rx.recv().await.unwrap(),
        ];
        assert!(acceptance_responses.iter().any(|work| {
            work.peer == viable_peer
                && matches!(
                    XBondFrame::decode_sealed(&work.encoded, &key)
                        .ok()
                        .and_then(|frame| serde_json::from_slice(&frame.payload).ok()),
                    Some(XBondControlMessage::SessionAccepted { .. })
                )
        }));
    }

    #[tokio::test]
    async fn multipath_schedule_ack_reaches_viable_peer_when_other_reverse_path_is_broken() {
        let broken_peer: SocketAddr = "192.0.2.20:1000".parse().unwrap();
        let viable_peer: SocketAddr = "192.0.2.21:1000".parse().unwrap();
        let critical = BoundedCoalescingQueue::new(4, false);
        let schedules = BoundedCoalescingQueue::new(4, false);
        let heartbeats = BoundedCoalescingQueue::new(4, true);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let (payload_tx, _payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);

        for (peer, path_id) in [(broken_peer, 1), (viable_peer, 2)] {
            assert_eq!(
                dispatch_inbound_frame(
                    &critical,
                    &schedules,
                    &heartbeats,
                    &repair_tx,
                    &payload_tx,
                    schedule_frame_from(peer, 9, 20, 12, path_id),
                    &payload_drops,
                    &repair_drops,
                    &coalesced,
                ),
                InboundDispatchOutcome::Queued
            );
        }

        let key = XBondKey::from_passphrase("test-key");
        let (response_tx, mut response_rx) = mpsc::channel(8);
        let mut control_sequence = 200;
        let mut control_plane = ServerControlPlaneStatus::default();
        let mut receiver = FrameReceiver::new(500_000, 32);
        let mut current_control = None;
        let mut saw_duplicate = false;
        for _ in 0..2 {
            let inbound = schedules.recv().await;
            let outcome = receiver.observe(&inbound.frame, now_micros());
            let generation = parse_schedule_control_generation(&inbound.frame).unwrap();
            let candidate = parse_return_control(&inbound.frame, monotonic_micros()).unwrap();
            if outcome == ReceiveOutcome::Accepted {
                current_control = Some(candidate);
                enqueue_schedule_accepted(
                    &response_tx,
                    &key,
                    inbound.peer,
                    inbound.frame.header.session_id,
                    generation,
                    &mut control_sequence,
                    &mut control_plane,
                )
                .unwrap();
            } else if outcome == ReceiveOutcome::Duplicate
                && return_control_matches_current(current_control.as_ref(), &candidate)
            {
                saw_duplicate = true;
                enqueue_schedule_accepted(
                    &response_tx,
                    &key,
                    inbound.peer,
                    inbound.frame.header.session_id,
                    generation,
                    &mut control_sequence,
                    &mut control_plane,
                )
                .unwrap();
            }
        }
        assert!(
            saw_duplicate,
            "the viable route must receive explicit acceptance even for a duplicate schedule copy"
        );

        let responses = [
            response_rx.recv().await.unwrap(),
            response_rx.recv().await.unwrap(),
        ];
        assert!(responses.iter().any(|work| {
            work.peer == viable_peer
                && matches!(
                    XBondFrame::decode_sealed(&work.encoded, &key)
                        .ok()
                        .and_then(|frame| serde_json::from_slice(&frame.payload).ok()),
                    Some(XBondControlMessage::ScheduleAccepted {
                        session_id: 9,
                        schedule_generation: 12,
                    })
                )
        }));
    }

    #[test]
    fn repair_lane_saturation_is_bounded_and_nonblocking() {
        let critical = BoundedCoalescingQueue::new(1, false);
        let schedules = BoundedCoalescingQueue::new(1, false);
        let heartbeats = BoundedCoalescingQueue::new(1, true);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let (payload_tx, _payload_rx) = mpsc::channel(1);
        let payload_drops = AtomicU64::new(0);
        let repair_drops = AtomicU64::new(0);
        let coalesced = AtomicU64::new(0);

        for (sequence, expected) in [
            (1, InboundDispatchOutcome::Queued),
            (2, InboundDispatchOutcome::Dropped),
        ] {
            assert_eq!(
                dispatch_inbound_frame(
                    &critical,
                    &schedules,
                    &heartbeats,
                    &repair_tx,
                    &payload_tx,
                    inbound_frame(PacketKind::Repair, 1, sequence, 1, vec![0x45]),
                    &payload_drops,
                    &repair_drops,
                    &coalesced,
                ),
                expected
            );
        }
        assert_eq!(repair_drops.load(Ordering::Relaxed), 1);
    }

    #[test]
    fn payload_pool_reuses_returned_allocation_and_falls_back_when_empty() {
        let (recycle_tx, mut recycle_rx) = mpsc::channel(1);
        let telemetry = Arc::new(PacketPoolTelemetry::new());
        let recycle = PayloadRecycle {
            tx: recycle_tx,
            telemetry: telemetry.clone(),
        };
        let payload = take_payload_buffer(&mut recycle_rx, &telemetry);
        let allocation = payload.as_ptr();

        assert!(recycle_payload(Some(&recycle), payload));
        assert_eq!(recycle.status().retained, 1);
        let reused = take_payload_buffer(&mut recycle_rx, &telemetry);
        assert_eq!(reused.as_ptr(), allocation);
        assert_eq!(recycle.status().retained, 0);

        let fallback = take_payload_buffer(&mut recycle_rx, &telemetry);
        assert!(fallback.capacity() >= EXPECTED_UDP_PAYLOAD_BYTES);
        assert_eq!(recycle.status().fallback_allocations, 2);
    }

    #[test]
    fn inbound_frame_drop_returns_accepted_payload_to_pool() {
        let (recycle_tx, mut recycle_rx) = mpsc::channel(1);
        let telemetry = Arc::new(PacketPoolTelemetry::new());
        let recycle = PayloadRecycle {
            tx: recycle_tx,
            telemetry: telemetry.clone(),
        };
        let payload = Vec::with_capacity(EXPECTED_UDP_PAYLOAD_BYTES);
        let allocation = payload.as_ptr();
        let inbound = InboundServerFrame::new(
            XBondFrame::new(
                XBondHeader::new(PacketKind::Heartbeat, 1, 1, now_micros(), 1),
                payload,
            ),
            "192.0.2.1:1000".parse().unwrap(),
            false,
            Some(recycle),
        );

        drop(inbound);
        let recycled = take_payload_buffer(&mut recycle_rx, &telemetry);

        assert_eq!(
            recycled.as_ptr(),
            allocation,
            "dropping a processed accepted frame should return its allocation"
        );
    }

    #[test]
    fn payload_pool_caps_retained_capacity_and_never_waits_when_full() {
        let (recycle_tx, mut recycle_rx) = mpsc::channel(1);
        let telemetry = Arc::new(PacketPoolTelemetry::new());
        let recycle = PayloadRecycle {
            tx: recycle_tx,
            telemetry: telemetry.clone(),
        };
        assert!(recycle_payload(
            Some(&recycle),
            Vec::with_capacity(EXPECTED_UDP_PAYLOAD_BYTES)
        ));
        assert!(!recycle_payload(
            Some(&recycle),
            Vec::with_capacity(EXPECTED_UDP_PAYLOAD_BYTES)
        ));
        assert!(!recycle_payload(
            Some(&recycle),
            Vec::with_capacity(MAX_POOLED_PAYLOAD_CAPACITY + 1)
        ));
        assert_eq!(
            recycle.status(),
            XBondPacketPoolStatus {
                retained: 1,
                capacity: 1,
                fallback_allocations: 0,
                discarded: 2,
            }
        );

        assert_eq!(
            take_payload_buffer(&mut recycle_rx, &telemetry).capacity(),
            EXPECTED_UDP_PAYLOAD_BYTES
        );
        assert_eq!(recycle.status().retained, 0);
    }

    #[test]
    fn payload_pool_never_retains_zero_capacity_buffers() {
        let (recycle_tx, _recycle_rx) = mpsc::channel(1);
        let telemetry = Arc::new(PacketPoolTelemetry::new());
        let recycle = PayloadRecycle {
            tx: recycle_tx,
            telemetry,
        };

        assert!(!recycle_payload(Some(&recycle), Vec::new()));
        assert_eq!(
            recycle.status(),
            XBondPacketPoolStatus {
                retained: 0,
                capacity: 1,
                fallback_allocations: 0,
                discarded: 1,
            }
        );
    }

    #[test]
    fn server_runtime_status_serializes_receive_payload_pool_telemetry() {
        let status = ServerRuntimeStatus {
            running: true,
            bind: "0.0.0.0:8444".to_string(),
            tun: Some("xbonds0".to_string()),
            updated_at_micros: 1,
            schedule_required: false,
            schedule_generation: 0,
            schedule_age_ms: 0,
            return_schedule: None,
            ingress_reorder: ServerIngressReorderStatus {
                current_hold_ms: 50,
                normal_hold_ms: 50,
                recovery_hold_ms: 500,
                recovery_min_hold_ms: 150,
                recovery_max_hold_ms: 500,
                adaptive_recovery_hold_enabled: true,
                adaptive_calm_samples: 0,
                adaptive_last_adjustment_reason: String::new(),
                capacity: 8192,
                stats: ReorderStats::default(),
            },
            repair: XBondRepairStatus::default(),
            server_health: XBondServerHealthStatus::default(),
            control_plane: ServerControlPlaneStatus {
                receive_payload_pool: XBondPacketPoolStatus {
                    retained: 6,
                    capacity: 32,
                    fallback_allocations: 2,
                    discarded: 4,
                },
                ..ServerControlPlaneStatus::default()
            },
            return_pmtu: ServerReturnPmtuStatus::default(),
            socket_buffers: Vec::new(),
            kernel_network: xbond_core::XBondKernelNetworkStatus::default(),
            saturation: XBondSaturationStatus::default(),
            stage_timings: XBondStageTimingStatus::default(),
            counters: TunnelCounters {
                data_packets_received: 0,
                data_packets_forwarded: 0,
                fec_packets_received: 0,
                fec_packets_recovered: 0,
                invalid_fec_packets_dropped: 0,
                non_ipv4_packets_dropped: 0,
            },
        };

        let value = serde_json::to_value(status).unwrap();

        assert_eq!(
            value["control_plane"]["receive_payload_pool"]["retained"],
            6
        );
        assert_eq!(
            value["control_plane"]["receive_payload_pool"]["capacity"],
            32
        );
        assert_eq!(
            value["control_plane"]["receive_payload_pool"]["fallback_allocations"],
            2
        );
        assert_eq!(
            value["control_plane"]["receive_payload_pool"]["discarded"],
            4
        );
    }

    #[test]
    fn pre_admission_deduplication_coalesces_data_duplicate_and_repair() {
        assert_eq!(pre_admission_duplicate_class(PacketKind::Data), Some(0));
        assert_eq!(
            pre_admission_duplicate_class(PacketKind::Duplicate),
            Some(0)
        );
        assert_eq!(pre_admission_duplicate_class(PacketKind::Repair), Some(0));
        assert_eq!(pre_admission_duplicate_class(PacketKind::Fec), Some(1));
        assert_eq!(pre_admission_duplicate_class(PacketKind::Heartbeat), None);
        assert_eq!(pre_admission_duplicate_class(PacketKind::Control), None);
    }

    #[tokio::test]
    async fn repair_work_precedes_queued_return_payload() {
        let (control_tx, mut control_rx) = mpsc::channel(1);
        let (data_tx, mut data_rx) = mpsc::channel(1);
        let peer = "192.0.2.1:1000".parse().unwrap();
        let work = |kind, sequence| ReturnSendWork {
            packet_kind: kind,
            peer,
            header: XBondHeader::new(kind, 1, sequence, now_micros(), 1),
            payload: Arc::new(vec![0x45]),
            deadline: Instant::now() + PRIMARY_RETURN_SEND_DEADLINE,
        };
        data_tx.try_send(work(PacketKind::Data, 1)).unwrap();
        control_tx.try_send(work(PacketKind::Repair, 2)).unwrap();

        let received = receive_prioritized_return_work(&mut control_rx, &mut data_rx)
            .await
            .unwrap();
        assert_eq!(received.packet_kind, PacketKind::Repair);
        assert_eq!(data_rx.len(), 1);
    }

    #[test]
    fn all_return_copies_dropped_is_counted_when_no_target_exists() {
        let accounting = ReturnCopyEnqueueAccounting::default();

        assert!(accounting.all_copies_dropped(false));
    }

    #[test]
    fn all_return_copies_dropped_is_counted_when_every_queue_rejects_packet() {
        let mut accounting = ReturnCopyEnqueueAccounting::default();
        accounting.selected();
        accounting.selected();

        assert!(accounting.all_copies_dropped(false));
    }

    #[test]
    fn accepting_any_return_copy_prevents_all_copies_dropped_count() {
        let mut accounting = ReturnCopyEnqueueAccounting::default();
        accounting.selected();
        accounting.selected();
        accounting.accepted();

        assert!(!accounting.all_copies_dropped(false));
    }

    #[test]
    fn pending_primary_backpressure_prevents_false_all_copies_dropped_count() {
        let mut accounting = ReturnCopyEnqueueAccounting::default();
        accounting.selected();

        assert!(!accounting.all_copies_dropped(true));
    }

    #[tokio::test]
    async fn primary_return_reserve_has_a_bounded_deadline() {
        let (tx, _rx) = mpsc::channel(1);
        let peer = "192.0.2.1:1000".parse().unwrap();
        tx.send(ReturnSendWork {
            packet_kind: PacketKind::Data,
            peer,
            header: XBondHeader::new(PacketKind::Data, 1, 1, now_micros(), 1),
            payload: Arc::new(vec![0x45]),
            deadline: Instant::now() + PRIMARY_RETURN_SEND_DEADLINE,
        })
        .await
        .unwrap();

        let error = reserve_primary_return_slot(tx, Instant::now() + Duration::from_millis(10))
            .await
            .unwrap_err();

        assert_eq!(error, PrimaryReturnReserveError::DeadlineExpired);
    }

    #[tokio::test]
    async fn repeated_primary_return_send_deadlines_request_clean_restart() {
        let socket = Arc::new(UdpSocket::bind("127.0.0.1:0").await.unwrap());
        let peer = "127.0.0.1:9".parse().unwrap();
        let (report_tx, mut report_rx) = mpsc::channel(8);
        let sender = spawn_primary_return_sender(
            socket,
            XBondKey::from_passphrase("test-key"),
            4,
            report_tx,
        );
        let expired = Instant::now()
            .checked_sub(Duration::from_millis(1))
            .unwrap();

        for sequence in 1..=PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES {
            sender
                .send(ReturnSendWork {
                    packet_kind: PacketKind::Data,
                    peer,
                    header: XBondHeader::new(
                        PacketKind::Data,
                        1,
                        u64::from(sequence),
                        now_micros(),
                        1,
                    ),
                    payload: Arc::new(vec![0x45]),
                    deadline: expired,
                })
                .await
                .unwrap();
        }

        for index in 1..=PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES {
            let report = time::timeout(Duration::from_millis(100), report_rx.recv())
                .await
                .unwrap()
                .unwrap();
            assert!(report.deadline_expired);
            assert_eq!(
                report.force_restart,
                index == PRIMARY_RETURN_MAX_CONSECUTIVE_DEADLINE_EXPIRIES
            );
        }
    }

    #[tokio::test]
    async fn tun_writer_queue_admits_a_reordered_batch_atomically() {
        let (tx, mut rx) = mpsc::channel(1);
        let telemetry = Arc::new(TunWriterTelemetry::default());
        let writer = TunWriterHandle {
            tx,
            telemetry: telemetry.clone(),
            capacity: 2,
        };
        let packet = |sequence| ReorderedPacket {
            sequence,
            path_id: 1,
            payload: vec![0x45],
        };

        assert_eq!(
            enqueue_tun_packets_with_deadline(
                &writer,
                vec![packet(1), packet(2)],
                Duration::from_millis(100),
            )
            .await
            .unwrap(),
            TunEnqueueOutcome { enqueued: 2 }
        );
        let batch = rx.recv().await.unwrap();
        assert_eq!(
            batch
                .packets
                .iter()
                .map(|packet| packet.sequence)
                .collect::<Vec<_>>(),
            vec![1, 2]
        );
        telemetry.release_packets(batch.packets.len(), batch.enqueued_at_micros);
        assert_eq!(telemetry.queue_depth.load(Ordering::Relaxed), 0);
        assert_eq!(telemetry.max_queue_depth.load(Ordering::Relaxed), 2);
        assert_eq!(telemetry.saturation_failures.load(Ordering::Relaxed), 0);
    }

    #[tokio::test]
    async fn tun_writer_chunks_oversized_reorder_flush_within_capacity() {
        let (tx, mut rx) = mpsc::channel(1);
        let telemetry = Arc::new(TunWriterTelemetry::default());
        let writer = TunWriterHandle {
            tx,
            telemetry: telemetry.clone(),
            capacity: 2,
        };
        let packets = (1..=4)
            .map(|sequence| ReorderedPacket {
                sequence,
                path_id: 1,
                payload: vec![0x45],
            })
            .collect();

        let release_telemetry = telemetry.clone();
        let consumer = tokio::spawn(async move {
            let mut sequences = Vec::new();
            while sequences.len() < 4 {
                let batch = rx.recv().await.unwrap();
                assert!(batch.packets.len() <= 2);
                sequences.extend(batch.packets.iter().map(|packet| packet.sequence));
                release_telemetry.release_packets(batch.packets.len(), batch.enqueued_at_micros);
            }
            sequences
        });
        assert_eq!(
            enqueue_tun_packets_with_deadline(&writer, packets, Duration::from_millis(100),)
                .await
                .unwrap(),
            TunEnqueueOutcome { enqueued: 4 }
        );
        assert_eq!(consumer.await.unwrap(), vec![1, 2, 3, 4]);
        assert_eq!(telemetry.queue_depth.load(Ordering::Relaxed), 0);
        assert!(telemetry.max_queue_depth.load(Ordering::Relaxed) <= 2);
    }

    #[test]
    fn initial_pending_tun_batch_cannot_bypass_packet_bound() {
        let (tx, _rx) = mpsc::channel(1);
        let telemetry = Arc::new(TunWriterTelemetry::default());
        let writer = TunWriterHandle {
            tx,
            telemetry: telemetry.clone(),
            capacity: 2,
        };
        let packets = (1..=3)
            .map(|sequence| ReorderedPacket {
                sequence,
                path_id: 1,
                payload: vec![0x45],
            })
            .collect();
        let mut pending = None;

        let error = start_or_append_tun_write(&writer, &mut pending, packets, 2).unwrap_err();

        assert!(error
            .to_string()
            .contains("exceeding its bounded 2-packet limit"));
        assert!(pending.is_none());
        assert_eq!(telemetry.saturation_failures.load(Ordering::Relaxed), 1);
    }

    #[tokio::test]
    async fn tun_writer_transient_saturation_waits_for_atomic_capacity() {
        let (tun_tx, mut tun_rx) = mpsc::channel(2);
        let telemetry = Arc::new(TunWriterTelemetry::default());
        let writer = TunWriterHandle {
            tx: tun_tx,
            telemetry: telemetry.clone(),
            capacity: 1,
        };
        let packet = |sequence| ReorderedPacket {
            sequence,
            path_id: 1,
            payload: vec![0x45],
        };
        enqueue_tun_packets_with_deadline(&writer, vec![packet(1)], Duration::from_millis(100))
            .await
            .unwrap();

        let release_telemetry = telemetry.clone();
        let releaser = tokio::spawn(async move {
            time::sleep(Duration::from_millis(15)).await;
            let batch = tun_rx.recv().await.unwrap();
            release_telemetry.release_packets(batch.packets.len(), batch.enqueued_at_micros);
            let batch = tun_rx.recv().await.unwrap();
            release_telemetry.release_packets(batch.packets.len(), batch.enqueued_at_micros);
        });

        let started = Instant::now();
        enqueue_tun_packets_with_deadline(&writer, vec![packet(2)], Duration::from_millis(100))
            .await
            .unwrap();
        assert!(started.elapsed() >= Duration::from_millis(10));
        assert!(started.elapsed() < Duration::from_millis(100));
        releaser.await.unwrap();
        assert_eq!(telemetry.queue_depth.load(Ordering::Relaxed), 0);
        assert_eq!(telemetry.saturation_failures.load(Ordering::Relaxed), 0);
    }

    #[tokio::test]
    async fn tun_writer_sustained_saturation_is_a_fatal_explicit_failure() {
        let (tun_tx, _tun_rx) = mpsc::channel(1);
        let telemetry = Arc::new(TunWriterTelemetry::default());
        let writer = TunWriterHandle {
            tx: tun_tx,
            telemetry: telemetry.clone(),
            capacity: 1,
        };
        let packet = |sequence| ReorderedPacket {
            sequence,
            path_id: 1,
            payload: vec![0x45],
        };
        enqueue_tun_packets_with_deadline(&writer, vec![packet(1)], Duration::from_millis(20))
            .await
            .unwrap();
        let started = Instant::now();
        let error =
            enqueue_tun_packets_with_deadline(&writer, vec![packet(2)], Duration::from_millis(10))
                .await
                .unwrap_err();
        assert!(error
            .to_string()
            .contains("terminating the tunnel for a clean restart"));
        assert!(started.elapsed() >= Duration::from_millis(8));
        assert_eq!(telemetry.queue_depth.load(Ordering::Relaxed), 1);
        assert_eq!(telemetry.saturation_failures.load(Ordering::Relaxed), 1);
    }

    #[test]
    fn tun_writer_oldest_age_tracks_the_actual_queue_head() {
        let telemetry = TunWriterTelemetry::default();

        assert!(telemetry.try_reserve_packets(2, 4, 100));
        assert!(telemetry.try_reserve_packets(1, 4, 250));
        assert_eq!(
            telemetry.oldest_enqueued_at_micros.load(Ordering::Relaxed),
            100
        );
        assert_eq!(telemetry.queue_depth.load(Ordering::Relaxed), 3);

        telemetry.release_packets(2, 100);
        assert_eq!(
            telemetry.oldest_enqueued_at_micros.load(Ordering::Relaxed),
            250
        );
        assert_eq!(telemetry.queue_depth.load(Ordering::Relaxed), 1);

        telemetry.release_packets(1, 250);
        assert_eq!(
            telemetry.oldest_enqueued_at_micros.load(Ordering::Relaxed),
            0
        );
        assert_eq!(telemetry.queue_depth.load(Ordering::Relaxed), 0);
    }

    #[test]
    fn tun_fault_injection_is_inert_by_default_and_deterministic_when_enabled() {
        let fault = TunFaultInjection::default();

        assert!(!injected_tun_failure(
            fault.write_fail_after_packets,
            u64::MAX
        ));
        assert!(!injected_tun_failure(
            fault.read_fail_after_packets,
            u64::MAX
        ));
        assert!(!injected_tun_failure(3, 2));
        assert!(injected_tun_failure(3, 3));
        assert!(injected_tun_failure(3, 4));
    }

    #[test]
    fn server_repair_cache_is_byte_bounded_and_uses_recommended_budget() {
        let maximum = 2 * 1024 * 1024;
        let mut cache = new_server_resend_cache(maximum);
        let expected =
            recommended_server_repair_cache_bytes(INITIAL_REPAIR_CACHE_BITS_PER_SECOND, maximum);

        assert_eq!(cache.byte_capacity(), expected);
        cache.set_byte_capacity(10, 0);
        cache.insert(1, 1, Arc::new(vec![0; 8]), 1);
        cache.insert(1, 2, Arc::new(vec![0; 8]), 2);

        assert!(cache.bytes_len() <= 10);
        assert!(cache.len() <= 1);
    }

    #[test]
    fn server_repair_cache_status_explicitly_prunes_and_marks_quiescence() {
        let mut cache = ResendCache::new(4, 100);
        cache.insert(1, 1, Arc::new(vec![1, 2, 3]), 1);
        let accounted_bytes = cache.accounted_bytes_len();
        let mut status = XBondRepairCacheStatus::default();

        refresh_server_repair_cache_status(&mut cache, &mut status, 102);

        assert_eq!(status.entries, 0);
        assert_eq!(status.accounted_bytes, 0);
        assert_eq!(status.prune_runs, 1);
        assert_eq!(status.last_pruned_at_micros, 102);
        assert_eq!(status.last_pruned_entries, 1);
        assert_eq!(status.last_pruned_accounted_bytes, accounted_bytes);
        assert_eq!(status.total_pruned_entries, 1);
        assert_eq!(status.total_pruned_accounted_bytes, accounted_bytes as u64);
        assert!(status.quiescent);
        assert_eq!(status.quiescent_since_micros, Some(102));
    }

    #[test]
    fn pre_recovery_gap_repairs_are_thresholded_and_rate_limited() {
        let mut limiter = IngressRepairLimiter::default();

        assert_eq!(
            ingress_repair_request_parameters(&mut limiter, 1, false, 3),
            None
        );
        assert_eq!(
            ingress_repair_request_parameters(&mut limiter, 1, false, 4),
            Some((
                PRE_RECOVERY_REPAIR_INTERVAL_MICROS,
                PRE_RECOVERY_REPAIR_MAX_PER_REQUEST
            ))
        );
        limiter.record(PRE_RECOVERY_REPAIR_MAX_PER_REQUEST, false);
        assert_eq!(
            ingress_repair_request_parameters(&mut limiter, 2, false, 4),
            Some((
                PRE_RECOVERY_REPAIR_INTERVAL_MICROS,
                PRE_RECOVERY_REPAIR_MAX_PER_REQUEST
            ))
        );
        limiter.record(PRE_RECOVERY_REPAIR_MAX_PER_REQUEST, false);
        assert_eq!(
            ingress_repair_request_parameters(&mut limiter, 3, false, 4),
            None
        );
        assert_eq!(
            ingress_repair_request_parameters(&mut limiter, 1_000_001, false, 4),
            Some((
                PRE_RECOVERY_REPAIR_INTERVAL_MICROS,
                PRE_RECOVERY_REPAIR_MAX_PER_REQUEST
            ))
        );
    }

    #[test]
    fn recovery_gap_repairs_keep_the_existing_larger_allowance() {
        let mut limiter = IngressRepairLimiter::default();

        assert_eq!(
            ingress_repair_request_parameters(&mut limiter, 1, true, 1),
            Some((REPAIR_REQUEST_INTERVAL_MICROS, MAX_REPAIR_REQUESTS))
        );
    }

    #[test]
    fn emsgsize_detection_and_per_path_accounting_are_explicit() {
        assert!(is_emsgsize(&std::io::Error::from_raw_os_error(90)));
        assert!(is_emsgsize(&std::io::Error::from_raw_os_error(10040)));
        assert!(!is_emsgsize(&std::io::Error::from_raw_os_error(111)));

        let report = ReturnSendReport {
            path_id: 7,
            packet_kind: PacketKind::Data,
            success: false,
            peer: "192.0.2.1:1000".parse().unwrap(),
            error: Some("message too long".to_string()),
            emsgsize: true,
            deadline_expired: false,
            force_restart: false,
            attempted_datagram_bytes: 1500,
        };
        let mut status = ServerReturnPmtuStatus::default();

        assert!(record_return_pmtu_error(&mut status, &report));
        assert!(record_return_pmtu_error(&mut status, &report));
        assert_eq!(status.emsgsize_errors, 2);
        assert_eq!(status.emsgsize_errors_by_path.get(&7), Some(&2));
    }

    #[tokio::test]
    async fn clearing_return_senders_aborts_queued_sender_tasks() {
        struct DropFlag(Arc<std::sync::atomic::AtomicBool>);

        impl Drop for DropFlag {
            fn drop(&mut self) {
                self.0.store(true, Ordering::Relaxed);
            }
        }

        let dropped = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let task_dropped = dropped.clone();
        let (started_tx, started_rx) = tokio::sync::oneshot::channel();
        let task = tokio::spawn(async move {
            let _drop_flag = DropFlag(task_dropped);
            let _ = started_tx.send(());
            std::future::pending::<()>().await;
        });
        started_rx.await.unwrap();
        let (control_tx, _control_rx) = mpsc::channel(1);
        let (data_tx, _data_rx) = mpsc::channel(1);
        let mut senders = HashMap::from([(
            1,
            ReturnSenderHandle {
                control_tx,
                data_tx,
                task,
            },
        )]);

        abort_return_senders(&mut senders);

        assert!(senders.is_empty());
        time::timeout(Duration::from_millis(50), async {
            while !dropped.load(Ordering::Relaxed) {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
    }

    #[test]
    fn repair_request_control_frame_parses_sequences() {
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Control, 1, 2, 3, 4),
            serde_json::to_vec(&XBondControlMessage::RepairRequest {
                sequences: vec![10, 11, 10],
            })
            .unwrap(),
        );

        assert_eq!(parse_repair_request(&frame), Some(vec![10, 11]));
    }

    #[test]
    fn reverse_sequence_starts_before_first_data_packet() {
        assert_eq!(initial_reverse_sequence(), 0);
    }

    #[test]
    fn adaptive_recovery_entry_starts_at_min_hold() {
        let mut controller = test_adaptive_hold_controller();
        let repair = XBondRepairStatus::default();

        assert!(controller.set_recovery_active(true, ReorderStats::default(), &repair));

        let status = controller.status();
        assert_eq!(status.current_hold_ms, 150);
        assert_eq!(status.recovery_min_hold_ms, 150);
        assert_eq!(status.recovery_max_hold_ms, 500);
        assert!(status.adaptive_enabled);
    }

    #[test]
    fn adaptive_recovery_pressure_increases_hold_and_caps_at_max() {
        let mut controller = test_adaptive_hold_controller();
        let mut repair = XBondRepairStatus::default();
        controller.set_recovery_active(true, ReorderStats::default(), &repair);

        for pressure_count in 1..=8 {
            repair.requests_sent = pressure_count;
            let stats = ReorderStats {
                timeout_releases: pressure_count,
                released_gap_packets: pressure_count,
                late_duplicates: pressure_count,
                ..ReorderStats::default()
            };
            controller.observe(true, stats, &repair);
        }

        assert_eq!(controller.status().current_hold_ms, 500);
    }

    #[test]
    fn adaptive_recovery_calm_samples_decrease_hold_and_floor_at_min() {
        let mut controller = test_adaptive_hold_controller();
        let mut repair = XBondRepairStatus::default();
        controller.set_recovery_active(true, ReorderStats::default(), &repair);

        repair.requests_sent = 1;
        controller.observe(
            true,
            ReorderStats {
                timeout_releases: 1,
                ..ReorderStats::default()
            },
            &repair,
        );
        assert_eq!(controller.status().current_hold_ms, 250);

        for _ in 0..ADAPTIVE_RECOVERY_CALM_SAMPLES_TO_DECREASE {
            controller.observe(true, ReorderStats::default(), &repair);
        }
        assert_eq!(controller.status().current_hold_ms, 200);

        for _ in 0..(ADAPTIVE_RECOVERY_CALM_SAMPLES_TO_DECREASE * 4) {
            controller.observe(true, ReorderStats::default(), &repair);
        }
        assert_eq!(controller.status().current_hold_ms, 150);
    }

    #[test]
    fn adaptive_recovery_exit_resets_to_normal_hold() {
        let mut controller = test_adaptive_hold_controller();
        let mut repair = XBondRepairStatus::default();
        controller.set_recovery_active(true, ReorderStats::default(), &repair);
        repair.requests_sent = 1;
        controller.observe(
            true,
            ReorderStats {
                timeout_releases: 1,
                ..ReorderStats::default()
            },
            &repair,
        );

        assert!(controller.set_recovery_active(false, ReorderStats::default(), &repair));

        let status = controller.status();
        assert_eq!(status.current_hold_ms, 50);
        assert_eq!(status.calm_samples, 0);
        assert!(status.last_adjustment_reason.contains("reset to normal"));
    }

    #[test]
    fn ingress_reorder_status_serializes_adaptive_fields() {
        let status = ServerIngressReorderStatus {
            current_hold_ms: 150,
            normal_hold_ms: 50,
            recovery_hold_ms: 500,
            recovery_min_hold_ms: 150,
            recovery_max_hold_ms: 500,
            adaptive_recovery_hold_enabled: true,
            adaptive_calm_samples: 3,
            adaptive_last_adjustment_reason: "Recovery calm sample observed.".to_string(),
            capacity: 8192,
            stats: ReorderStats::default(),
        };

        let value = serde_json::to_value(status).unwrap();

        assert_eq!(value["adaptive_recovery_hold_enabled"], true);
        assert_eq!(value["recovery_min_hold_ms"], 150);
        assert_eq!(value["recovery_max_hold_ms"], 500);
        assert_eq!(value["adaptive_calm_samples"], 3);
        assert_eq!(
            value["adaptive_last_adjustment_reason"],
            "Recovery calm sample observed."
        );
    }

    fn test_adaptive_hold_controller() -> IngressReorderHoldController {
        IngressReorderHoldController::new(IngressReorderHoldSettings {
            adaptive_enabled: true,
            normal_hold_micros: 50_000,
            recovery_min_hold_micros: 150_000,
            recovery_max_hold_micros: 500_000,
            increase_step_micros: 100_000,
            decrease_step_micros: 50_000,
        })
    }

    #[test]
    fn control_frame_updates_return_schedule() {
        let schedule = SchedulePlan {
            mode: ScheduleMode::AnchorDuplicate1,
            anchor_path_id: Some(5),
            data_path_ids: vec![5],
            duplicate_path_ids: vec![3],
            fec_path_ids: Vec::new(),
        };
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Control, 7, 1, 2, 5),
            serde_json::to_vec(&schedule).unwrap(),
        );

        let control = parse_return_control(&frame, 10_000).unwrap();

        assert_eq!(control.schedule, schedule);
        assert_eq!(control.policy, RedundancyPolicy::Reliable);
        assert!(!control.recovery_active);
        assert_eq!(control.schedule_generation, 0);
    }

    #[test]
    fn schedule_control_frame_carries_recovery_state() {
        let schedule = SchedulePlan {
            mode: ScheduleMode::AnchorDuplicate1,
            anchor_path_id: Some(5),
            data_path_ids: vec![5],
            duplicate_path_ids: vec![3],
            fec_path_ids: Vec::new(),
        };
        let message = ScheduleControlMessage {
            schedule_generation: 42,
            schedule: schedule.clone(),
            redundancy_policy: RedundancyPolicy::Reliable,
            policy_config: RedundancyPolicyConfig::default(),
            paths: Vec::new(),
            recovery_active: true,
        };
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Control, 7, 1, 2, 5),
            serde_json::to_vec(&message).unwrap(),
        );

        let control = parse_return_control(&frame, 10_000).unwrap();

        assert_eq!(control.schedule, schedule);
        assert!(control.recovery_active);
        assert_eq!(control.schedule_generation, 42);
    }

    #[test]
    fn schedule_generation_rejects_regression_and_conflicting_replay() {
        let schedule = SchedulePlan {
            mode: ScheduleMode::AnchorOnly,
            anchor_path_id: Some(1),
            data_path_ids: vec![1],
            duplicate_path_ids: Vec::new(),
            fec_path_ids: Vec::new(),
        };
        let current = build_return_control(
            7,
            1,
            schedule.clone(),
            RedundancyPolicy::Balanced,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );
        let regressed = build_return_control(
            6,
            2,
            schedule.clone(),
            RedundancyPolicy::Balanced,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );
        let conflicting = build_return_control(
            7,
            3,
            SchedulePlan {
                anchor_path_id: Some(2),
                data_path_ids: vec![2],
                ..schedule.clone()
            },
            RedundancyPolicy::Balanced,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );
        let refresh = build_return_control(
            7,
            4,
            schedule,
            RedundancyPolicy::Balanced,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );

        assert!(!return_control_update_is_valid(Some(&current), &regressed));
        assert!(!return_control_update_is_valid(
            Some(&current),
            &conflicting
        ));
        assert!(return_control_update_is_valid(Some(&current), &refresh));
    }

    #[test]
    fn scheduled_return_targets_follow_client_schedule_order() {
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let now = 1_000_000;
        let peers = HashMap::from([(3, peer_state(peer_3, now)), (5, peer_state(peer_5, now))]);
        let control = build_return_control(
            1,
            now,
            SchedulePlan {
                mode: ScheduleMode::AnchorDuplicate1,
                anchor_path_id: Some(5),
                data_path_ids: vec![5],
                duplicate_path_ids: vec![3],
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Reliable,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );

        assert_eq!(
            select_return_targets(Some(&control), &peers, 1_200, now),
            vec![
                (5, peer_5, PacketKind::Data),
                (3, peer_3, PacketKind::Duplicate),
            ]
        );
    }

    #[test]
    fn return_targets_are_empty_without_authoritative_schedule() {
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let now = 1_000_000;
        let peers = HashMap::from([(5, peer_state(peer_5, now)), (3, peer_state(peer_3, now))]);

        assert!(select_return_targets(None, &peers, 1_200, now).is_empty());
    }

    #[test]
    fn balanced_return_targets_do_not_duplicate_healthy_bulk_packets() {
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let now = 1_000_000;
        let peers = HashMap::from([(3, peer_state(peer_3, now)), (5, peer_state(peer_5, now))]);
        let control = build_return_control(
            1,
            now,
            SchedulePlan {
                mode: ScheduleMode::AnchorDuplicate1,
                anchor_path_id: Some(5),
                data_path_ids: vec![5],
                duplicate_path_ids: vec![3],
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Balanced,
            RedundancyPolicyConfig::default(),
            vec![healthy_path(5, 20.0, 0.0), healthy_path(3, 60.0, 0.0)],
            false,
        );

        assert_eq!(
            select_return_targets(Some(&control), &peers, 1_200, now),
            vec![(5, peer_5, PacketKind::Data)]
        );
    }

    #[test]
    fn recovery_return_targets_duplicate_bulk_to_all_scheduled_paths() {
        let peer_2: SocketAddr = "192.0.2.2:2000".parse().unwrap();
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let now = 1_000_000;
        let peers = HashMap::from([
            (2, peer_state(peer_2, now)),
            (3, peer_state(peer_3, now)),
            (5, peer_state(peer_5, now)),
        ]);
        let control = build_return_control(
            1,
            now,
            SchedulePlan {
                mode: ScheduleMode::AnchorDuplicate1,
                anchor_path_id: Some(5),
                data_path_ids: vec![5],
                duplicate_path_ids: vec![2, 3],
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Reliable,
            RedundancyPolicyConfig::default(),
            vec![
                healthy_path(5, 20.0, 0.10),
                healthy_path(2, 80.0, 0.12),
                healthy_path(3, 90.0, 0.14),
            ],
            true,
        );

        assert_eq!(
            select_return_targets(Some(&control), &peers, 1_200, now),
            vec![
                (5, peer_5, PacketKind::Data),
                (2, peer_2, PacketKind::Duplicate),
                (3, peer_3, PacketKind::Duplicate),
            ]
        );
    }

    #[test]
    fn stale_scheduled_peers_are_not_return_targets() {
        let stale_peer: SocketAddr = "192.0.2.1:1000".parse().unwrap();
        let fresh_peer: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let now = 30_000_000;
        let peers = HashMap::from([
            (1, peer_state(stale_peer, 0)),
            (3, peer_state(fresh_peer, now)),
        ]);
        let control = build_return_control(
            1,
            now,
            SchedulePlan {
                mode: ScheduleMode::AnchorDuplicate1,
                anchor_path_id: Some(1),
                data_path_ids: vec![1],
                duplicate_path_ids: vec![3],
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Reliable,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );

        assert_eq!(
            select_return_targets(Some(&control), &peers, 1_200, now),
            vec![(3, fresh_peer, PacketKind::Data)]
        );
    }

    #[test]
    fn no_schedule_returns_no_targets_even_with_fresh_peers() {
        let stale_peer: SocketAddr = "192.0.2.1:1000".parse().unwrap();
        let fresh_peer: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let now = 30_000_000;
        let peers = HashMap::from([
            (1, peer_state(stale_peer, 0)),
            (3, peer_state(fresh_peer, now)),
        ]);

        assert!(select_return_targets(None, &peers, 1_200, now).is_empty());
    }

    #[test]
    fn stale_return_schedule_uses_bounded_grace_with_exact_fresh_peers() {
        let peer_1: SocketAddr = "192.0.2.1:1000".parse().unwrap();
        let peer_2: SocketAddr = "192.0.2.2:2000".parse().unwrap();
        let now = RETURN_SCHEDULE_STALE_AFTER_MICROS + 2_000_000;
        let peers = HashMap::from([(1, peer_state(peer_1, now)), (2, peer_state(peer_2, now))]);
        let control = build_return_control(
            7,
            1,
            SchedulePlan {
                mode: ScheduleMode::AnchorDuplicate1,
                anchor_path_id: Some(1),
                data_path_ids: vec![1],
                duplicate_path_ids: vec![2],
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Reliable,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );

        assert!(return_schedule_is_stale(&control, now));
        assert!(!return_schedule_grace_expired(&control, now));
        assert_eq!(
            select_return_targets(Some(&control), &peers, 1_200, now),
            vec![
                (1, peer_1, PacketKind::Data),
                (2, peer_2, PacketKind::Duplicate),
            ]
        );

        let missing_peer = HashMap::from([(1, peer_state(peer_1, now))]);
        assert_eq!(
            select_return_targets(Some(&control), &missing_peer, 1_200, now),
            vec![(1, peer_1, PacketKind::Data)]
        );
    }

    #[test]
    fn stale_return_schedule_promotes_the_only_fresh_scheduled_backup_to_primary() {
        let stale_anchor: SocketAddr = "192.0.2.1:1000".parse().unwrap();
        let fresh_backup: SocketAddr = "192.0.2.2:2000".parse().unwrap();
        let now = 30_000_000;
        let peers = HashMap::from([
            (1, peer_state(stale_anchor, 0)),
            (2, peer_state(fresh_backup, now)),
        ]);
        let control = build_return_control(
            7,
            now.saturating_sub(RETURN_SCHEDULE_STALE_AFTER_MICROS + 2_000_000),
            SchedulePlan {
                mode: ScheduleMode::AnchorDuplicate1,
                anchor_path_id: Some(1),
                data_path_ids: vec![1],
                duplicate_path_ids: vec![2],
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Reliable,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );

        assert!(return_schedule_is_stale(&control, now));
        assert!(!return_schedule_grace_expired(&control, now));
        assert_eq!(
            select_return_targets(Some(&control), &peers, 1_200, now),
            vec![(2, fresh_backup, PacketKind::Data)]
        );
    }

    #[test]
    fn return_schedule_fails_closed_after_grace_even_with_fresh_peers() {
        let peer: SocketAddr = "192.0.2.1:1000".parse().unwrap();
        let now = RETURN_SCHEDULE_STALE_AFTER_MICROS + RETURN_SCHEDULE_GRACE_MICROS + 1;
        let peers = HashMap::from([(1, peer_state(peer, now))]);
        let control = build_return_control(
            7,
            0,
            SchedulePlan {
                mode: ScheduleMode::AnchorOnly,
                anchor_path_id: Some(1),
                data_path_ids: vec![1],
                duplicate_path_ids: Vec::new(),
                fec_path_ids: Vec::new(),
            },
            RedundancyPolicy::Balanced,
            RedundancyPolicyConfig::default(),
            Vec::new(),
            false,
        );

        assert!(return_schedule_grace_expired(&control, now));
        assert!(select_return_targets(Some(&control), &peers, 1_200, now).is_empty());
    }

    fn healthy_path(path_id: u16, rtt_ms: f64, loss_rate: f64) -> PathHealthSnapshot {
        PathHealthSnapshot {
            path_id,
            name: format!("path-{path_id}"),
            interface_name: None,
            rtt_ms: Some(rtt_ms),
            jitter_ms: Some(5.0),
            loss_rate,
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
            heartbeat_sample_count: 100,
            heartbeat_consecutive_misses: 0,
            heartbeat_consecutive_successes: 100,
            heartbeat_warming_up: false,
            heartbeat_failed: false,
        }
    }

    fn peer_state(addr: SocketAddr, last_seen_micros: u64) -> PeerState {
        PeerState {
            addr,
            last_seen_micros,
        }
    }

    #[test]
    fn fec_recovery_recovers_missing_first_packet() {
        let first = vec![0x45, 0, 0, 20];
        let second = vec![0x45, 1, 2, 3, 4, 5];
        let block =
            XorFecBlock::decode(&XorFecBlock::encode(11, &first, &second).unwrap()).unwrap();
        let mut recovery = FecRecovery::new(16);

        recovery.mark_delivered(1, 12);
        assert!(recovery.observe_data(1, 12, second.clone()).is_empty());
        let recovered = recovery.observe_fec(1, block);

        assert_eq!(
            recovered,
            vec![RecoveredPacket {
                sequence: 11,
                payload: first
            }]
        );
    }

    #[test]
    fn fec_recovery_recovers_missing_second_packet() {
        let first = vec![0x45, 0, 0, 20];
        let second = vec![0x45, 1, 2, 3, 4, 5];
        let block =
            XorFecBlock::decode(&XorFecBlock::encode(21, &first, &second).unwrap()).unwrap();
        let mut recovery = FecRecovery::new(16);

        assert!(recovery.observe_fec(1, block).is_empty());
        recovery.mark_delivered(1, 21);
        let recovered = recovery.observe_data(1, 21, first.clone());

        assert_eq!(
            recovered,
            vec![RecoveredPacket {
                sequence: 22,
                payload: second
            }]
        );
    }

    #[test]
    fn fec_base_sequence_pairs_odd_even_packets() {
        assert_eq!(fec_base_sequence(1), 1);
        assert_eq!(fec_base_sequence(2), 1);
        assert_eq!(fec_base_sequence(3), 3);
        assert_eq!(fec_base_sequence(4), 3);
    }
}
