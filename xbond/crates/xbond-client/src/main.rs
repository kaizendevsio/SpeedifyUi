use std::collections::{HashMap, HashSet, VecDeque};
use std::io::ErrorKind;
use std::net::{IpAddr, SocketAddr, ToSocketAddrs};
use std::path::PathBuf;
use std::process::Command as ProcessCommand;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{bail, Context, Result};
use clap::{Parser, Subcommand};
use serde::{Deserialize, Serialize};
use socket2::{Domain, Protocol, Socket, Type};
#[cfg(unix)]
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::UdpSocket;
#[cfg(unix)]
use tokio::net::{UnixListener, UnixStream};
use tokio::sync::{mpsc, oneshot, Notify};
use tokio::task::JoinHandle;
use tokio::time;
use xbond_core::{
    build_schedule, decode_sealed_payload_into, default_udp_socket_buffer_bytes,
    encode_sealed_payload_into, expand_schedule_for_recovery, is_ipv4_packet,
    precompute_transmission_plans, recommended_repair_cache_bytes, select_path_roles,
    select_path_roles_with_state, stabilize_recovery_schedule, update_recovery_state, ClientConfig,
    FrameReceiver, PacketKind, PacketReorderBuffer, PacketTransmissionPlans, PathHealthSnapshot,
    PathIsolationStatus, ProbeAggregate, ProbePathStats, ReceiveOutcome, RecoveryConfig,
    RecoveryScheduleStabilityState, RecoveryState, RecoveryStatus, RedundancyPolicy,
    RedundancyPolicyConfig, ReorderedPacket, ResendCache, RoleSelectionConfig, RoleSelectionState,
    RouteVerification, ScheduleControlMessage, ScheduleMode, SchedulePlan, SessionHandshakeNonce,
    XBondControlMessage, XBondDiagnosticOverrideStatus, XBondFecStatus, XBondFrame, XBondHeader,
    XBondKey, XBondPathStatus, XBondProcessStatus, XBondReorderStatus, XBondRepairStatus,
    XBondRuntimeStatus, XBondServerRecoveryStatus, XBondStatus, XBondTun, XBondTunnelStatus,
    XorFecBlock,
};

#[derive(Debug, Parser)]
#[command(name = "xbond-client")]
#[command(about = "XBond client prototype")]
struct Args {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    Status {
        #[arg(long, default_value = "/etc/xbond/client.toml")]
        config: PathBuf,
        #[arg(long)]
        json: bool,
    },
    Plan {
        #[arg(long, default_value = "/etc/xbond/client.toml")]
        config: PathBuf,
    },
    Ping {
        #[arg(long)]
        server: String,
        #[arg(long, default_value = "0.0.0.0:0")]
        bind: String,
        #[arg(long)]
        interface: Option<String>,
        #[arg(long, default_value_t = 1)]
        path_id: u16,
        #[arg(long)]
        session_id: Option<u64>,
        #[arg(long, default_value_t = 10)]
        count: u32,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
        #[arg(long, default_value_t = 1000)]
        timeout_ms: u64,
        #[arg(long, default_value = "XBOND_PSK")]
        key_env: String,
        #[arg(long)]
        json: bool,
    },
    MultiPing {
        #[arg(long, default_value = "/etc/xbond/client.toml")]
        config: PathBuf,
        #[arg(long)]
        server: Option<String>,
        #[arg(long = "path-id")]
        path_ids: Vec<u16>,
        #[arg(long = "bind")]
        binds: Vec<String>,
        #[arg(long)]
        session_id: Option<u64>,
        #[arg(long, default_value_t = 10)]
        count: u32,
        #[arg(long, default_value_t = 250)]
        interval_ms: u64,
        #[arg(long, default_value_t = 1000)]
        timeout_ms: u64,
        #[arg(long, default_value = "XBOND_PSK")]
        key_env: String,
        #[arg(long)]
        json: bool,
    },
    Tunnel {
        #[arg(long, default_value = "/etc/xbond/client.toml")]
        config: PathBuf,
        #[arg(long, default_value = "xbond0")]
        tun_name: String,
        #[arg(long, default_value_t = 1400)]
        tun_mtu: u16,
        #[arg(long)]
        session_id: Option<u64>,
        #[arg(long, default_value = "XBOND_PSK")]
        key_env: String,
        #[arg(long)]
        packet_limit: Option<u64>,
        #[arg(long)]
        json_events: bool,
        #[arg(long)]
        trace_packets: bool,
        #[arg(long, default_value = "/run/xbond/client-control.sock")]
        control_socket: PathBuf,
        #[arg(long, hide = true)]
        lab_fail_tun_read_after_packets: Option<u64>,
        #[arg(long, default_value_t = 0, hide = true)]
        lab_tun_write_delay_ms: u64,
    },
    Override {
        #[command(subcommand)]
        action: OverrideCommand,
    },
    PathRebind {
        #[arg(long = "path-id")]
        path_id: Option<u16>,
        #[arg(long = "interface")]
        interface_name: Option<String>,
        #[arg(long, default_value = "/run/xbond/client-control.sock")]
        socket: PathBuf,
        #[arg(long)]
        json: bool,
    },
}

#[derive(Debug, Subcommand)]
enum OverrideCommand {
    Set {
        #[arg(long)]
        mode: ScheduleMode,
        #[arg(long, default_value = "diagnostic")]
        policy: RedundancyPolicy,
        #[arg(long, default_value_t = 60)]
        ttl_seconds: u64,
        #[arg(long, default_value = "/run/xbond/client-control.sock")]
        socket: PathBuf,
        #[arg(long)]
        json: bool,
    },
    Clear {
        #[arg(long, default_value = "/run/xbond/client-control.sock")]
        socket: PathBuf,
        #[arg(long)]
        json: bool,
    },
    Status {
        #[arg(long, default_value = "/run/xbond/client-control.sock")]
        socket: PathBuf,
        #[arg(long)]
        json: bool,
    },
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    match args.command {
        Command::Status { config, json } => {
            let status = load_status(&config)?;
            if json {
                println!("{}", serde_json::to_string_pretty(&status)?);
            } else {
                print_human_status(&status);
            }
        }
        Command::Plan { config } => {
            let status = load_status(&config)?;
            println!("{}", serde_json::to_string_pretty(&status.schedule)?);
        }
        Command::Ping {
            server,
            bind,
            interface,
            path_id,
            session_id,
            count,
            interval_ms,
            timeout_ms,
            key_env,
            json,
        } => {
            let result = run_ping(PingOptions {
                server,
                bind,
                bind_device: interface,
                path_id,
                session_id: resolve_session_id(session_id)?,
                count,
                interval_ms,
                timeout_ms,
                key_env,
            })
            .await?;
            if json {
                println!("{}", serde_json::to_string_pretty(&result.to_json())?);
            } else {
                print_ping_result(&result);
            }
        }
        Command::MultiPing {
            config,
            server,
            path_ids,
            binds,
            session_id,
            count,
            interval_ms,
            timeout_ms,
            key_env,
            json,
        } => {
            let result = run_multi_ping(MultiPingOptions {
                config,
                server,
                path_ids,
                binds,
                session_id: resolve_session_id(session_id)?,
                count,
                interval_ms,
                timeout_ms,
                key_env,
            })
            .await?;
            if json {
                println!("{}", serde_json::to_string_pretty(&result)?);
            } else {
                print_multi_ping_result(&result);
            }
        }
        Command::Tunnel {
            config,
            tun_name,
            tun_mtu,
            session_id,
            key_env,
            packet_limit,
            json_events,
            trace_packets,
            control_socket,
            lab_fail_tun_read_after_packets,
            lab_tun_write_delay_ms,
        } => {
            run_tunnel(TunnelOptions {
                config,
                tun_name,
                tun_mtu,
                session_id,
                key_env,
                packet_limit,
                json_events,
                trace_packets,
                control_socket,
                lab_fail_tun_read_after_packets,
                lab_tun_write_delay_ms,
            })
            .await?;
        }
        Command::Override { action } => {
            run_override_command(action).await?;
        }
        Command::PathRebind {
            path_id,
            interface_name,
            socket,
            json,
        } => {
            run_path_rebind_command(socket, path_id, interface_name, json).await?;
        }
    }
    Ok(())
}

#[derive(Debug)]
struct PingOptions {
    server: String,
    bind: String,
    bind_device: Option<String>,
    path_id: u16,
    session_id: u64,
    count: u32,
    interval_ms: u64,
    timeout_ms: u64,
    key_env: String,
}

#[derive(Debug)]
struct MultiPingOptions {
    config: PathBuf,
    server: Option<String>,
    path_ids: Vec<u16>,
    binds: Vec<String>,
    session_id: u64,
    count: u32,
    interval_ms: u64,
    timeout_ms: u64,
    key_env: String,
}

#[derive(Debug)]
struct TunnelOptions {
    config: PathBuf,
    tun_name: String,
    tun_mtu: u16,
    session_id: Option<u64>,
    key_env: String,
    packet_limit: Option<u64>,
    json_events: bool,
    trace_packets: bool,
    control_socket: PathBuf,
    lab_fail_tun_read_after_packets: Option<u64>,
    lab_tun_write_delay_ms: u64,
}

async fn run_override_command(action: OverrideCommand) -> Result<()> {
    let (socket, json, request) = match action {
        OverrideCommand::Set {
            mode,
            policy,
            ttl_seconds,
            socket,
            json,
        } => (
            socket,
            json,
            ControlRequest::SetOverride {
                mode,
                redundancy_policy: policy,
                ttl_seconds,
            },
        ),
        OverrideCommand::Clear { socket, json } => (socket, json, ControlRequest::ClearOverride),
        OverrideCommand::Status { socket, json } => (socket, json, ControlRequest::Status),
    };

    let response = send_control_request(&socket, request).await?;
    if json {
        println!("{}", serde_json::to_string_pretty(&response)?);
    } else {
        println!("{}", response.message);
        if let Some(active) = response.active_override {
            println!(
                "override: {:?} / {:?}, expires in {}s",
                active.mode, active.redundancy_policy, active.expires_in_seconds
            );
        }
    }

    if response.ok {
        Ok(())
    } else {
        bail!(response.message)
    }
}

async fn run_path_rebind_command(
    socket: PathBuf,
    path_id: Option<u16>,
    interface_name: Option<String>,
    json: bool,
) -> Result<()> {
    if path_id.is_none()
        && interface_name
            .as_deref()
            .unwrap_or_default()
            .trim()
            .is_empty()
    {
        bail!("provide --path-id or --interface for XBond path rebind")
    }

    let response = send_control_request(
        &socket,
        ControlRequest::RebindPath {
            path_id,
            interface_name,
        },
    )
    .await?;

    if json {
        println!("{}", serde_json::to_string_pretty(&response)?);
    } else {
        println!("{}", response.message);
    }

    if response.ok {
        Ok(())
    } else {
        bail!(response.message)
    }
}

#[cfg(unix)]
async fn send_control_request(
    socket: &PathBuf,
    request: ControlRequest,
) -> Result<ControlResponse> {
    let mut stream = UnixStream::connect(socket).await.with_context(|| {
        format!(
            "failed to connect to XBond control socket {}",
            socket.display()
        )
    })?;
    let request_json = serde_json::to_vec(&request)?;
    stream.write_all(&request_json).await?;
    stream.write_all(b"\n").await?;

    let mut reader = BufReader::new(stream);
    let mut response_json = String::new();
    reader.read_line(&mut response_json).await?;
    let response = serde_json::from_str::<ControlResponse>(&response_json)
        .context("invalid response from XBond control socket")?;
    Ok(response)
}

#[cfg(not(unix))]
async fn send_control_request(
    _socket: &PathBuf,
    _request: ControlRequest,
) -> Result<ControlResponse> {
    bail!("XBond live override control is only available on Unix-like systems")
}

#[derive(Debug, Clone)]
struct ProbePathSpec {
    path_id: u16,
    name: String,
    interface_name: Option<String>,
    bind_addr: Option<String>,
    bind_device: Option<String>,
}

struct ActiveProbePath {
    spec: ProbePathSpec,
    socket: UdpSocket,
    stats: ProbePathStats,
}

struct PreparedProbePath {
    active: Option<ActiveProbePath>,
    inactive_stats: Option<ProbePathStats>,
}

#[derive(Debug)]
struct InboundTunnelFrame {
    path_id: u16,
    frame: XBondFrame,
}

#[derive(Clone)]
struct InboundTunnelQueues {
    control_tx: mpsc::Sender<InboundTunnelFrame>,
    payload_tx: mpsc::Sender<InboundTunnelFrame>,
    payload_drops: Arc<AtomicU64>,
}

const INBOUND_CONTROL_QUEUE_CAPACITY: usize = 256;
const OPERATOR_CONTROL_QUEUE_CAPACITY: usize = 32;
const SOCKET_EVENT_QUEUE_CAPACITY: usize = 64;

fn is_prioritized_client_inbound(kind: PacketKind) -> bool {
    matches!(kind, PacketKind::Heartbeat | PacketKind::Control)
}

async fn receive_prioritized_tunnel_frame(
    control_rx: &mut mpsc::Receiver<InboundTunnelFrame>,
    payload_rx: &mut mpsc::Receiver<InboundTunnelFrame>,
) -> Option<InboundTunnelFrame> {
    tokio::select! {
        biased;
        inbound = control_rx.recv() => match inbound {
            Some(inbound) => Some(inbound),
            None => payload_rx.recv().await,
        },
        inbound = payload_rx.recv() => match inbound {
            Some(inbound) => Some(inbound),
            None => control_rx.recv().await,
        },
    }
}

fn inbound_queue_depth(
    control_rx: &mpsc::Receiver<InboundTunnelFrame>,
    payload_rx: &mpsc::Receiver<InboundTunnelFrame>,
) -> usize {
    control_rx.len().saturating_add(payload_rx.len())
}

#[derive(Debug)]
struct PathSendWork {
    packet_kind: PacketKind,
    header: XBondHeader,
    payload: Arc<Vec<u8>>,
    queued_at: Instant,
    deadline: Instant,
    lane: PathSendLane,
}

impl PathSendWork {
    fn data(packet_kind: PacketKind, header: XBondHeader, payload: Arc<Vec<u8>>) -> Self {
        Self {
            packet_kind,
            header,
            payload,
            queued_at: Instant::now(),
            deadline: Instant::now() + DATA_LANE_DEADLINE,
            lane: PathSendLane::Data,
        }
    }

    fn control(packet_kind: PacketKind, header: XBondHeader, payload: Arc<Vec<u8>>) -> Self {
        Self {
            packet_kind,
            header,
            payload,
            queued_at: Instant::now(),
            deadline: Instant::now() + CONTROL_LANE_DEADLINE,
            lane: PathSendLane::Control,
        }
    }

    fn repair(header: XBondHeader, payload: Arc<Vec<u8>>) -> Self {
        Self {
            packet_kind: PacketKind::Repair,
            header,
            payload,
            queued_at: Instant::now(),
            deadline: Instant::now() + REPAIR_LANE_DEADLINE,
            lane: PathSendLane::Repair,
        }
    }

    fn repair_control(header: XBondHeader, payload: Arc<Vec<u8>>) -> Self {
        Self {
            packet_kind: PacketKind::Control,
            header,
            payload,
            queued_at: Instant::now(),
            deadline: Instant::now() + REPAIR_LANE_DEADLINE,
            lane: PathSendLane::Repair,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PathSendLane {
    Control,
    Repair,
    Data,
}

#[derive(Debug)]
struct PathSendReport {
    path_id: u16,
    socket_generation: u64,
    packet_kind: PacketKind,
    encoded_bytes: u64,
    error: Option<String>,
    raw_os_error: Option<i32>,
    needs_rebind: bool,
    message_too_large: bool,
    lane: PathSendLane,
}

#[derive(Debug)]
struct PathSocketEvent {
    path_id: u16,
    socket_generation: u64,
    reason: String,
    error: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum TunReaderExit {
    PacketQueueClosed,
    ReadFailed(String),
    TaskFailed(String),
}

#[derive(Debug)]
struct SilentBlackholeProbeResult {
    path_id: u16,
    socket_generation: u64,
    reachable: bool,
    error: Option<String>,
}

#[derive(Debug)]
struct PathSenderHandle {
    socket_generation: u64,
    data_tx: mpsc::Sender<PathSendWork>,
    control_tx: mpsc::Sender<PathSendWork>,
    repair_tx: mpsc::Sender<PathSendWork>,
    latest_control: Arc<LatestControlSlot>,
    metrics: Arc<PathSenderMetrics>,
    task: JoinHandle<()>,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
struct LaneQueueSnapshot {
    depth: usize,
    capacity: usize,
    oldest_age_ms: u64,
    enqueue_drops: u64,
    deadline_drops: u64,
}

#[derive(Debug, Default)]
struct LaneQueueTelemetry {
    capacity: usize,
    queued_at: VecDeque<Instant>,
    enqueue_drops: u64,
    deadline_drops: u64,
}

impl LaneQueueTelemetry {
    fn new(capacity: usize) -> Self {
        Self {
            capacity,
            ..Self::default()
        }
    }

    fn record_enqueued(&mut self, queued_at: Instant) {
        self.queued_at.push_back(queued_at);
    }

    fn record_dequeued(&mut self, queued_at: Instant) {
        if self.queued_at.front().copied() == Some(queued_at) {
            self.queued_at.pop_front();
            return;
        }
        if let Some(index) = self.queued_at.iter().position(|value| *value == queued_at) {
            self.queued_at.remove(index);
        }
    }

    fn record_replaced(&mut self, old_queued_at: Instant, new_queued_at: Instant) {
        self.record_dequeued(old_queued_at);
        self.enqueue_drops = self.enqueue_drops.saturating_add(1);
        self.record_enqueued(new_queued_at);
    }

    fn snapshot(&self, now: Instant) -> LaneQueueSnapshot {
        LaneQueueSnapshot {
            depth: self.queued_at.len(),
            capacity: self.capacity,
            oldest_age_ms: self
                .queued_at
                .front()
                .map(|queued_at| {
                    now.saturating_duration_since(*queued_at)
                        .as_millis()
                        .min(u128::from(u64::MAX)) as u64
                })
                .unwrap_or_default(),
            enqueue_drops: self.enqueue_drops,
            deadline_drops: self.deadline_drops,
        }
    }
}

#[derive(Debug)]
struct PathSenderMetrics {
    control: StdMutex<LaneQueueTelemetry>,
    repair: StdMutex<LaneQueueTelemetry>,
    data: StdMutex<LaneQueueTelemetry>,
    completions: PathSenderCompletionCounters,
}

#[derive(Debug, Default)]
struct PathSenderCompletionCounters {
    encoded_frames: AtomicU64,
    encode_micros: AtomicU64,
    successful_bytes: AtomicU64,
    data_packets: AtomicU64,
    duplicate_packets: AtomicU64,
    fec_packets: AtomicU64,
    repair_packets: AtomicU64,
    sender_deadline_drops: AtomicU64,
    repair_deadline_drops: AtomicU64,
}

#[derive(Debug, Default, PartialEq, Eq)]
struct PathSenderCompletionSnapshot {
    encoded_frames: u64,
    encode_micros: u64,
    successful_bytes: u64,
    data_packets: u64,
    duplicate_packets: u64,
    fec_packets: u64,
    repair_packets: u64,
    sender_deadline_drops: u64,
    repair_deadline_drops: u64,
}

impl PathSenderCompletionCounters {
    fn record_encoded(&self, encode_micros: u64) {
        self.encoded_frames.fetch_add(1, Ordering::Relaxed);
        self.encode_micros
            .fetch_add(encode_micros, Ordering::Relaxed);
    }

    fn record_success(&self, packet_kind: PacketKind, encoded_bytes: u64) {
        self.successful_bytes
            .fetch_add(encoded_bytes, Ordering::Relaxed);
        let counter = match packet_kind {
            PacketKind::Data => &self.data_packets,
            PacketKind::Duplicate => &self.duplicate_packets,
            PacketKind::Fec => &self.fec_packets,
            PacketKind::Repair => &self.repair_packets,
            _ => return,
        };
        counter.fetch_add(1, Ordering::Relaxed);
    }

    fn record_deadline_drop(&self, lane: PathSendLane) {
        self.sender_deadline_drops.fetch_add(1, Ordering::Relaxed);
        if lane == PathSendLane::Repair {
            self.repair_deadline_drops.fetch_add(1, Ordering::Relaxed);
        }
    }

    fn take(&self) -> PathSenderCompletionSnapshot {
        PathSenderCompletionSnapshot {
            encoded_frames: self.encoded_frames.swap(0, Ordering::Relaxed),
            encode_micros: self.encode_micros.swap(0, Ordering::Relaxed),
            successful_bytes: self.successful_bytes.swap(0, Ordering::Relaxed),
            data_packets: self.data_packets.swap(0, Ordering::Relaxed),
            duplicate_packets: self.duplicate_packets.swap(0, Ordering::Relaxed),
            fec_packets: self.fec_packets.swap(0, Ordering::Relaxed),
            repair_packets: self.repair_packets.swap(0, Ordering::Relaxed),
            sender_deadline_drops: self.sender_deadline_drops.swap(0, Ordering::Relaxed),
            repair_deadline_drops: self.repair_deadline_drops.swap(0, Ordering::Relaxed),
        }
    }
}

impl PathSenderMetrics {
    fn new(data_capacity: usize) -> Self {
        Self {
            control: StdMutex::new(LaneQueueTelemetry::new(
                CONTROL_LANE_QUEUE_CAPACITY.saturating_add(1),
            )),
            repair: StdMutex::new(LaneQueueTelemetry::new(REPAIR_LANE_QUEUE_CAPACITY)),
            data: StdMutex::new(LaneQueueTelemetry::new(data_capacity.max(1))),
            completions: PathSenderCompletionCounters::default(),
        }
    }

    fn lane(&self, lane: PathSendLane) -> &StdMutex<LaneQueueTelemetry> {
        match lane {
            PathSendLane::Control => &self.control,
            PathSendLane::Repair => &self.repair,
            PathSendLane::Data => &self.data,
        }
    }

    fn record_enqueued(&self, work: &PathSendWork) {
        self.record_enqueued_at(work.lane, work.queued_at);
    }

    fn record_enqueued_at(&self, lane: PathSendLane, queued_at: Instant) {
        self.lane(lane)
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .record_enqueued(queued_at);
    }

    fn record_dequeued(&self, work: &PathSendWork) {
        self.lane(work.lane)
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .record_dequeued(work.queued_at);
    }

    fn record_enqueue_drop(&self, lane: PathSendLane) {
        let mut telemetry = self
            .lane(lane)
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        telemetry.enqueue_drops = telemetry.enqueue_drops.saturating_add(1);
    }

    fn record_deadline_drop(&self, lane: PathSendLane) {
        let mut telemetry = self
            .lane(lane)
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        telemetry.deadline_drops = telemetry.deadline_drops.saturating_add(1);
    }

    fn record_sender_deadline_drop(&self, lane: PathSendLane) {
        self.record_deadline_drop(lane);
        self.completions.record_deadline_drop(lane);
    }

    fn record_latest_replacement(&self, old: &PathSendWork, new: &PathSendWork) {
        self.lane(PathSendLane::Control)
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .record_replaced(old.queued_at, new.queued_at);
    }

    fn snapshot(&self, lane: PathSendLane, now: Instant) -> LaneQueueSnapshot {
        self.lane(lane)
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .snapshot(now)
    }

    fn record_encoded(&self, encode_micros: u64) {
        self.completions.record_encoded(encode_micros);
    }

    fn record_success(&self, packet_kind: PacketKind, encoded_bytes: u64) {
        self.completions.record_success(packet_kind, encoded_bytes);
    }

    fn take_completions(&self) -> PathSenderCompletionSnapshot {
        self.completions.take()
    }
}

#[derive(Debug)]
struct LatestControlSlot {
    work: StdMutex<Option<PathSendWork>>,
    notify: Notify,
    metrics: Arc<PathSenderMetrics>,
}

impl LatestControlSlot {
    fn new(metrics: Arc<PathSenderMetrics>) -> Self {
        Self {
            work: StdMutex::new(None),
            notify: Notify::new(),
            metrics,
        }
    }

    fn replace(&self, work: PathSendWork) -> bool {
        let mut slot = self.work.lock().unwrap_or_else(|error| error.into_inner());
        let replaced = if let Some(previous) = slot.as_ref() {
            self.metrics.record_latest_replacement(previous, &work);
            true
        } else {
            self.metrics.record_enqueued(&work);
            false
        };
        *slot = Some(work);
        drop(slot);
        self.notify.notify_one();
        replaced
    }

    async fn recv(&self) -> PathSendWork {
        loop {
            let notified = self.notify.notified();
            if let Some(work) = self
                .work
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .take()
            {
                return work;
            }
            notified.await;
        }
    }
}

#[derive(Debug)]
struct TunWriteBatch {
    packets: Vec<ReorderedPacket>,
    queued_at: Instant,
}

#[derive(Debug, Clone)]
struct TunWriterHandle {
    tx: mpsc::Sender<TunWriteBatch>,
    queued_packets: Arc<AtomicU64>,
    capacity: usize,
}

impl TunWriterHandle {
    fn queue_depth(&self) -> usize {
        self.queued_packets.load(Ordering::Relaxed) as usize
    }

    fn try_reserve_packets(&self, packet_count: usize) -> bool {
        let packet_count = packet_count as u64;
        let capacity = self.capacity as u64;
        self.queued_packets
            .fetch_update(Ordering::AcqRel, Ordering::Relaxed, |current| {
                current
                    .checked_add(packet_count)
                    .filter(|next| *next <= capacity)
            })
            .is_ok()
    }

    fn release_packets(&self, packet_count: usize) {
        self.queued_packets
            .fetch_sub(packet_count as u64, Ordering::AcqRel);
    }
}

#[derive(Debug, Default)]
struct TunWriterMetrics {
    packets: AtomicU64,
    payload_bytes: AtomicU64,
    queue_delay_micros: AtomicU64,
    write_micros: AtomicU64,
    failures: AtomicU64,
    repair_frames: AtomicU64,
    path_bytes: HashMap<u16, AtomicU64>,
}

#[derive(Debug, Default, PartialEq, Eq)]
struct TunWriterMetricsSnapshot {
    packets: u64,
    payload_bytes: u64,
    queue_delay_micros: u64,
    write_micros: u64,
    failures: u64,
    repair_frames: u64,
    path_bytes: HashMap<u16, u64>,
}

impl TunWriterMetrics {
    fn for_paths(path_ids: impl IntoIterator<Item = u16>) -> Self {
        Self {
            path_bytes: path_ids
                .into_iter()
                .map(|path_id| (path_id, AtomicU64::new(0)))
                .collect(),
            ..Self::default()
        }
    }

    fn record_success(
        &self,
        path_id: u16,
        payload_len: usize,
        queue_delay_micros: u64,
        write_micros: u64,
    ) {
        self.packets.fetch_add(1, Ordering::Relaxed);
        self.payload_bytes
            .fetch_add(payload_len as u64, Ordering::Relaxed);
        self.queue_delay_micros
            .fetch_add(queue_delay_micros, Ordering::Relaxed);
        self.write_micros.fetch_add(write_micros, Ordering::Relaxed);
        if path_id == u16::MAX {
            self.repair_frames.fetch_add(1, Ordering::Relaxed);
        } else if let Some(path_bytes) = self.path_bytes.get(&path_id) {
            path_bytes.fetch_add(payload_len as u64, Ordering::Relaxed);
        }
    }

    fn record_failure(&self) {
        self.failures.fetch_add(1, Ordering::Relaxed);
    }

    fn take_snapshot(&self) -> TunWriterMetricsSnapshot {
        TunWriterMetricsSnapshot {
            packets: self.packets.swap(0, Ordering::Relaxed),
            payload_bytes: self.payload_bytes.swap(0, Ordering::Relaxed),
            queue_delay_micros: self.queue_delay_micros.swap(0, Ordering::Relaxed),
            write_micros: self.write_micros.swap(0, Ordering::Relaxed),
            failures: self.failures.swap(0, Ordering::Relaxed),
            repair_frames: self.repair_frames.swap(0, Ordering::Relaxed),
            path_bytes: self
                .path_bytes
                .iter()
                .filter_map(|(path_id, bytes)| {
                    let bytes = bytes.swap(0, Ordering::Relaxed);
                    (bytes > 0).then_some((*path_id, bytes))
                })
                .collect(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum TunWriterExit {
    QueueClosed,
    WriteFailed(String),
    TaskFailed(String),
}

#[derive(Debug, Clone)]
struct ReceiverPayloadPool {
    buffers: Arc<StdMutex<Vec<Vec<u8>>>>,
    maximum_buffers: usize,
    maximum_capacity: usize,
}

impl ReceiverPayloadPool {
    fn new(maximum_buffers: usize, maximum_capacity: usize) -> Self {
        Self {
            buffers: Arc::new(StdMutex::new(Vec::with_capacity(maximum_buffers))),
            maximum_buffers: maximum_buffers.max(1),
            maximum_capacity: maximum_capacity.max(1),
        }
    }

    fn take(&self, minimum_capacity: usize) -> Vec<u8> {
        let mut buffers = self
            .buffers
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let index = buffers
            .iter()
            .position(|buffer| buffer.capacity() >= minimum_capacity);
        index
            .map(|index| buffers.swap_remove(index))
            .unwrap_or_else(|| Vec::with_capacity(minimum_capacity))
    }

    fn recycle(&self, mut buffer: Vec<u8>) {
        if buffer.capacity() > self.maximum_capacity {
            return;
        }
        buffer.clear();
        let mut buffers = self
            .buffers
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if buffers.len() < self.maximum_buffers {
            buffers.push(buffer);
        }
    }

    #[cfg(test)]
    fn len(&self) -> usize {
        self.buffers
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .len()
    }
}

#[derive(Debug)]
struct PrimarySendCompletion {
    path_id: u16,
    socket_generation: u64,
    success: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PrimaryEnqueueResult {
    Enqueued,
    Pending,
}

#[derive(Debug, Default)]
struct TunnelPathRuntime {
    send_failures: u32,
    bytes_sent: u64,
    last_bytes_sent: u64,
    bytes_received: u64,
    last_bytes_received: u64,
    duplicate_bytes_received: u64,
    last_duplicate_bytes_received: u64,
    outbound_throughput_bps: u64,
    inbound_throughput_bps: u64,
    duplicate_inbound_throughput_bps: u64,
    raw_inbound_throughput_bps: u64,
    throughput_bps: u64,
    health_sequence: u64,
    pending_heartbeats: HashMap<u64, Instant>,
    health_window: VecDeque<bool>,
    rtt_samples_ms: VecDeque<f64>,
    rtt_ms: Option<f64>,
    jitter_ms: Option<f64>,
    loss_rate: f64,
    last_ack_at: Option<Instant>,
    duplicate_useful_packets: u64,
    duplicate_late_packets: u64,
    peak_throughput_bps: u64,
    socket_generation: u64,
    socket_ifindex: Option<u32>,
    socket_bind_addr: Option<String>,
    socket_bind_device: Option<String>,
    last_socket_error: Option<String>,
    last_rebind_reason: Option<String>,
    last_rebind_error: Option<String>,
    last_rebind_at_micros: Option<u64>,
    rebind_count: u64,
    force_rebind_reason: Option<String>,
    force_rebind_bypass_rate_limit: bool,
    last_rebind_attempt: Option<Instant>,
    ineffective_rebinds: u32,
    socket_opened_at: Option<Instant>,
    sender_queue_depth: usize,
    sender_queue_capacity: usize,
    sender_data_oldest_age_ms: u64,
    sender_data_enqueue_drops: u64,
    sender_data_deadline_drops: u64,
    sender_control_queue_depth: usize,
    sender_control_queue_capacity: usize,
    sender_control_oldest_age_ms: u64,
    sender_control_enqueue_drops: u64,
    sender_control_deadline_drops: u64,
    sender_repair_queue_depth: usize,
    sender_repair_queue_capacity: usize,
    sender_repair_oldest_age_ms: u64,
    sender_repair_enqueue_drops: u64,
    sender_repair_deadline_drops: u64,
    stale_ack_ticks: u32,
    direct_probe_in_flight: bool,
    last_direct_probe_at: Option<Instant>,
    pmtu_error_count: u64,
    last_pmtu_encoded_bytes: Option<u64>,
}

#[derive(Debug, Default)]
struct TunnelAggregateHealthRuntime {
    health_sequence: u64,
    pending_heartbeats: HashMap<u64, Instant>,
    health_window: VecDeque<bool>,
    rtt_samples_ms: VecDeque<f64>,
    rtt_ms: Option<f64>,
    jitter_ms: Option<f64>,
    loss_rate: Option<f64>,
    success_rate: Option<f64>,
    last_success_at: Option<Instant>,
}

impl TunnelAggregateHealthRuntime {
    fn to_status(&self) -> XBondTunnelHealthStatus {
        let (status, reason) = classify_tunnel_health(self.rtt_ms, self.loss_rate);
        XBondTunnelHealthStatus {
            rtt_ms: self.rtt_ms,
            jitter_ms: self.jitter_ms,
            loss_rate: self.loss_rate,
            success_rate: self.success_rate,
            pending_probes: self.pending_heartbeats.len(),
            last_success_age_ms: self.last_success_at.map(|last_success_at| {
                Instant::now()
                    .duration_since(last_success_at)
                    .as_millis()
                    .min(u128::from(u64::MAX)) as u64
            }),
            status,
            reason,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
struct XBondTunnelHealthStatus {
    rtt_ms: Option<f64>,
    jitter_ms: Option<f64>,
    loss_rate: Option<f64>,
    success_rate: Option<f64>,
    pending_probes: usize,
    last_success_age_ms: Option<u64>,
    status: String,
    reason: String,
}

#[derive(Debug, Default)]
struct TunnelCounters {
    data_packets_sent: u64,
    duplicate_packets_sent: u64,
    fec_packets_sent: u64,
    fec_packets_skipped: u64,
    data_packets_received: u64,
    late_packets_dropped: u64,
    duplicate_packets_dropped: u64,
    data_bytes_sent: u64,
    data_bytes_received: u64,
    last_data_bytes_sent: u64,
    last_data_bytes_received: u64,
    outbound_throughput_bps: u64,
    inbound_throughput_bps: u64,
    encoded_frames: u64,
    decoded_frames: u64,
    encode_micros_total: u64,
    decode_micros_total: u64,
    tun_queue_drops: u64,
    inbound_queue_drops: u64,
    duplicate_send_skips: u64,
    fec_send_skips: u64,
    primary_queue_full_events: u64,
    supervisor_control_progress_ticks: u64,
    tun_write_queue_drops: u64,
    tun_write_failures: u64,
    tun_write_packets: u64,
    tun_write_queue_micros_total: u64,
    tun_write_micros_total: u64,
    control_lane_drops: u64,
    control_lane_coalesced: u64,
    repair_lane_drops: u64,
    sender_deadline_drops: u64,
    pmtu_errors: u64,
    repair_cache_evictions: u64,
    repair_cache_evicted_bytes: u64,
}

#[derive(Debug, Clone)]
struct PendingFecSource {
    sequence: u64,
    payload: Arc<Vec<u8>>,
    schedule_generation: u64,
    policy: RedundancyPolicy,
    recovery_active: bool,
}

type ConsecutiveFecPair = (u64, Arc<Vec<u8>>, Arc<Vec<u8>>);

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "command", rename_all = "kebab-case")]
enum ControlRequest {
    SetOverride {
        mode: ScheduleMode,
        #[serde(default)]
        redundancy_policy: RedundancyPolicy,
        ttl_seconds: u64,
    },
    ClearOverride,
    RebindPath {
        #[serde(default)]
        path_id: Option<u16>,
        #[serde(default)]
        interface_name: Option<String>,
    },
    Status,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
struct ControlResponse {
    ok: bool,
    message: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    active_override: Option<XBondDiagnosticOverrideStatus>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    path_id: Option<u16>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    interface_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    socket_generation: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    rebound: Option<bool>,
}

#[derive(Debug)]
struct ControlEnvelope {
    request: ControlRequest,
    response_tx: oneshot::Sender<ControlResponse>,
}

#[derive(Debug, Clone)]
struct ActiveScheduleOverride {
    mode: ScheduleMode,
    redundancy_policy: RedundancyPolicy,
    expires_at: Instant,
}

impl ActiveScheduleOverride {
    fn status(&self) -> XBondDiagnosticOverrideStatus {
        XBondDiagnosticOverrideStatus {
            mode: self.mode,
            redundancy_policy: self.redundancy_policy,
            expires_in_seconds: self
                .expires_at
                .saturating_duration_since(Instant::now())
                .as_secs(),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
struct ScheduleControlSignature {
    schedule: SchedulePlan,
    redundancy_policy: RedundancyPolicy,
    recovery_active: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum SynchronizationControlOutcome {
    Ignored,
    SessionChallenge {
        request_nonce: SessionHandshakeNonce,
        challenge: SessionHandshakeNonce,
    },
    SessionAccepted,
    ScheduleAccepted,
    RestartRequired(String),
}

#[derive(Debug)]
struct ClientSynchronizationState {
    session_id: u64,
    request_nonce: SessionHandshakeNonce,
    challenge: Option<SessionHandshakeNonce>,
    started_at: Instant,
    last_session_open_sent_at: Option<Instant>,
    session_accepted_at: Option<Instant>,
    expected_schedule_generation: u64,
    accepted_schedule_generation: Option<u64>,
    schedule_unsynchronized_since: Option<Instant>,
}

impl ClientSynchronizationState {
    fn new(
        session_id: u64,
        request_nonce: SessionHandshakeNonce,
        schedule_generation: u64,
        now: Instant,
    ) -> Self {
        Self {
            session_id,
            request_nonce,
            challenge: None,
            started_at: now,
            last_session_open_sent_at: None,
            session_accepted_at: None,
            expected_schedule_generation: schedule_generation,
            accepted_schedule_generation: None,
            schedule_unsynchronized_since: None,
        }
    }

    fn session_accepted(&self) -> bool {
        self.session_accepted_at.is_some()
    }

    fn data_plane_ready(&self) -> bool {
        self.session_accepted()
            && self.accepted_schedule_generation == Some(self.expected_schedule_generation)
    }

    fn should_send_session_open(&self, now: Instant) -> bool {
        !self.session_accepted()
            && self.last_session_open_sent_at.is_none_or(|sent_at| {
                now.saturating_duration_since(sent_at) >= SESSION_OPEN_RETRY_INTERVAL
            })
    }

    fn record_session_open_sent(&mut self, now: Instant) {
        self.last_session_open_sent_at = Some(now);
    }

    fn request_nonce(&self) -> SessionHandshakeNonce {
        self.request_nonce
    }

    fn require_schedule(&mut self, schedule_generation: u64, now: Instant) {
        self.expected_schedule_generation = schedule_generation;
        if self.accepted_schedule_generation != Some(schedule_generation) {
            self.accepted_schedule_generation = None;
            if self.session_accepted() {
                self.schedule_unsynchronized_since.get_or_insert(now);
            }
        }
    }

    fn mark_schedule_unsynchronized(&mut self, now: Instant) {
        self.accepted_schedule_generation = None;
        if self.session_accepted() {
            self.schedule_unsynchronized_since.get_or_insert(now);
        }
    }

    fn apply_control(
        &mut self,
        control: &XBondControlMessage,
        now: Instant,
    ) -> SynchronizationControlOutcome {
        match control {
            XBondControlMessage::SessionChallenge {
                session_id,
                request_nonce,
                challenge,
            } if !self.session_accepted()
                && *session_id == self.session_id
                && *request_nonce == self.request_nonce
                && challenge.iter().any(|byte| *byte != 0) =>
            {
                self.challenge = Some(*challenge);
                SynchronizationControlOutcome::SessionChallenge {
                    request_nonce: *request_nonce,
                    challenge: *challenge,
                }
            }
            XBondControlMessage::SessionAccepted {
                session_id,
                request_nonce,
                challenge,
            } if *session_id == self.session_id
                && *request_nonce == self.request_nonce
                && self.challenge == Some(*challenge) =>
            {
                if self.session_accepted_at.is_none() {
                    self.session_accepted_at = Some(now);
                    self.schedule_unsynchronized_since.get_or_insert(now);
                    return SynchronizationControlOutcome::SessionAccepted;
                }
                SynchronizationControlOutcome::Ignored
            }
            XBondControlMessage::ScheduleAccepted {
                session_id,
                schedule_generation,
            } if *session_id == self.session_id
                && self.session_accepted()
                && *schedule_generation == self.expected_schedule_generation =>
            {
                self.accepted_schedule_generation = Some(*schedule_generation);
                self.schedule_unsynchronized_since = None;
                SynchronizationControlOutcome::ScheduleAccepted
            }
            XBondControlMessage::SessionRestartRequired { session_id, reason }
                if *session_id == self.session_id && self.session_accepted() =>
            {
                SynchronizationControlOutcome::RestartRequired(reason.clone())
            }
            _ => SynchronizationControlOutcome::Ignored,
        }
    }

    fn synchronization_error(&self, now: Instant) -> Option<String> {
        if !self.session_accepted()
            && now.saturating_duration_since(self.started_at) >= SESSION_SYNCHRONIZATION_TIMEOUT
        {
            return Some(format!(
                "XBond session {} was not accepted within {} seconds",
                self.session_id,
                SESSION_SYNCHRONIZATION_TIMEOUT.as_secs()
            ));
        }

        let schedule_started = self.schedule_unsynchronized_since?;
        (now.saturating_duration_since(schedule_started) >= SCHEDULE_SYNCHRONIZATION_TIMEOUT).then(
            || {
                format!(
                    "XBond schedule generation {} was not accepted within {} seconds",
                    self.expected_schedule_generation,
                    SCHEDULE_SYNCHRONIZATION_TIMEOUT.as_secs()
                )
            },
        )
    }
}

#[derive(Debug, Clone)]
struct PingReply {
    sequence: u64,
    rtt_ms: f64,
}

#[derive(Debug)]
struct PingResult {
    server: String,
    bind: String,
    path_id: u16,
    session_id: u64,
    sent: u32,
    received: u32,
    replies: Vec<PingReply>,
}

impl PingResult {
    fn lost(&self) -> u32 {
        self.sent.saturating_sub(self.received)
    }

    fn loss_rate(&self) -> f64 {
        if self.sent == 0 {
            0.0
        } else {
            f64::from(self.lost()) / f64::from(self.sent)
        }
    }

    fn rtt_values(&self) -> Vec<f64> {
        self.replies.iter().map(|reply| reply.rtt_ms).collect()
    }

    fn min_rtt_ms(&self) -> Option<f64> {
        finite_min(&self.rtt_values())
    }

    fn avg_rtt_ms(&self) -> Option<f64> {
        let values = self.rtt_values();
        if values.is_empty() {
            None
        } else {
            Some(values.iter().sum::<f64>() / values.len() as f64)
        }
    }

    fn max_rtt_ms(&self) -> Option<f64> {
        finite_max(&self.rtt_values())
    }

    fn to_json(&self) -> serde_json::Value {
        serde_json::json!({
            "server": self.server,
            "bind": self.bind,
            "path_id": self.path_id,
            "session_id": self.session_id,
            "sent": self.sent,
            "received": self.received,
            "lost": self.lost(),
            "loss_rate": self.loss_rate(),
            "min_rtt_ms": self.min_rtt_ms(),
            "avg_rtt_ms": self.avg_rtt_ms(),
            "max_rtt_ms": self.max_rtt_ms(),
            "replies": self.replies.iter().map(|reply| {
                serde_json::json!({
                    "sequence": reply.sequence,
                    "rtt_ms": reply.rtt_ms,
                })
            }).collect::<Vec<_>>(),
        })
    }
}

async fn run_ping(options: PingOptions) -> Result<PingResult> {
    let key_text = std::env::var(&options.key_env)
        .with_context(|| format!("{} environment variable is required", options.key_env))?;
    let key = XBondKey::from_passphrase(&key_text);
    let (socket, _isolation) = create_isolated_udp_socket(
        &options.bind,
        options.bind_device.as_deref(),
        default_udp_socket_buffer_bytes(),
    )?;
    socket
        .connect(&options.server)
        .await
        .with_context(|| format!("failed to connect UDP socket to {}", options.server))?;

    let mut replies = Vec::new();
    let mut buf = vec![0u8; 2048];
    for sequence in 1..=u64::from(options.count) {
        let frame = XBondFrame::new(
            XBondHeader::new(
                PacketKind::Heartbeat,
                options.session_id,
                sequence,
                now_micros(),
                options.path_id,
            ),
            b"ping".to_vec(),
        );
        socket.send(&frame.encode_sealed(&key)?).await?;

        let deadline = Duration::from_millis(options.timeout_ms);
        match time::timeout(deadline, socket.recv(&mut buf)).await {
            Ok(Ok(len)) => {
                if let Ok(reply) = XBondFrame::decode_sealed(&buf[..len], &key) {
                    if reply.header.kind == PacketKind::Heartbeat
                        && reply.header.session_id == options.session_id
                        && reply.header.sequence == sequence
                        && reply.payload == b"ack"
                    {
                        let elapsed =
                            now_micros().saturating_sub(reply.header.send_micros) as f64 / 1_000.0;
                        replies.push(PingReply {
                            sequence,
                            rtt_ms: elapsed,
                        });
                    }
                }
            }
            Ok(Err(error)) => return Err(error.into()),
            Err(_) => {}
        }

        if sequence < u64::from(options.count) && options.interval_ms > 0 {
            time::sleep(Duration::from_millis(options.interval_ms)).await;
        }
    }

    Ok(PingResult {
        server: options.server,
        bind: socket.local_addr()?.to_string(),
        path_id: options.path_id,
        session_id: options.session_id,
        sent: options.count,
        received: replies.len() as u32,
        replies,
    })
}

async fn run_multi_ping(options: MultiPingOptions) -> Result<ProbeAggregate> {
    let config = read_config(&options.config)?;
    let server = options.server.clone().unwrap_or(config.server_addr.clone());
    let specs = select_probe_paths(&config, &options.path_ids, &options.binds)?;
    let schedule = build_schedule(
        config.mode,
        &select_path_roles(&config_health(&config), config.max_active_backups),
    );
    let selected_ids: HashSet<u16> = specs.iter().map(|path| path.path_id).collect();
    let anchor_path_id = schedule
        .anchor_path_id
        .filter(|path_id| selected_ids.contains(path_id))
        .or_else(|| specs.first().map(|path| path.path_id));
    let duplicate_path_ids = specs
        .iter()
        .map(|path| path.path_id)
        .filter(|path_id| Some(*path_id) != anchor_path_id)
        .collect::<Vec<_>>();

    let key_text = std::env::var(&options.key_env)
        .with_context(|| format!("{} environment variable is required", options.key_env))?;
    let key = XBondKey::from_passphrase(&key_text);
    let target_ip = resolve_server_ip(&server);
    let started_at = now_micros();
    let mut active_paths = Vec::with_capacity(specs.len());
    let mut inactive_stats = Vec::new();

    for spec in specs {
        let prepared = prepare_probe_path(spec, &server, target_ip).await?;
        if let Some(active) = prepared.active {
            active_paths.push(active);
        }
        if let Some(stats) = prepared.inactive_stats {
            inactive_stats.push(stats);
        }
    }

    let timeout = Duration::from_millis(options.timeout_ms);
    let mut buf = vec![0u8; 2048];
    for sequence in 1..=u64::from(options.count) {
        let send_micros = now_micros();
        for path in &mut active_paths {
            let frame = XBondFrame::new(
                XBondHeader::new(
                    PacketKind::Heartbeat,
                    options.session_id,
                    sequence,
                    send_micros,
                    path.spec.path_id,
                ),
                b"multi-ping".to_vec(),
            );
            path.socket.send(&frame.encode_sealed(&key)?).await?;
            path.stats.record_sent();
        }

        collect_multi_ping_replies(
            &mut active_paths,
            &mut buf,
            &key,
            options.session_id,
            sequence,
            timeout,
        )
        .await?;

        if sequence < u64::from(options.count) && options.interval_ms > 0 {
            time::sleep(Duration::from_millis(options.interval_ms)).await;
        }
    }

    for path in &mut active_paths {
        path.stats
            .set_duplicates_dropped(path.stats.acks.saturating_sub(path.stats.first_arrivals));
    }

    Ok(ProbeAggregate {
        mode: config.mode,
        anchor_path_id,
        duplicate_path_ids,
        started_at,
        completed_at: now_micros(),
        paths: active_paths
            .into_iter()
            .map(|path| path.stats)
            .chain(inactive_stats)
            .collect::<Vec<_>>(),
    })
}

fn handle_control_request(
    request: ControlRequest,
    active_override: &mut Option<ActiveScheduleOverride>,
) -> ControlResponse {
    prune_expired_override(active_override);

    match request {
        ControlRequest::SetOverride {
            mode,
            redundancy_policy,
            ttl_seconds,
        } => {
            let ttl_seconds = ttl_seconds.clamp(1, 3600);
            *active_override = Some(ActiveScheduleOverride {
                mode,
                redundancy_policy,
                expires_at: Instant::now() + Duration::from_secs(ttl_seconds),
            });
            ControlResponse {
                ok: true,
                message: format!(
                    "XBond override set to {:?} / {:?} for {}s.",
                    mode, redundancy_policy, ttl_seconds
                ),
                active_override: active_override.as_ref().map(ActiveScheduleOverride::status),
                ..ControlResponse::default()
            }
        }
        ControlRequest::ClearOverride => {
            *active_override = None;
            ControlResponse {
                ok: true,
                message: "XBond override cleared.".to_string(),
                active_override: None,
                ..ControlResponse::default()
            }
        }
        ControlRequest::RebindPath { .. } => ControlResponse {
            ok: false,
            message: "XBond path rebind must be handled by the tunnel supervisor.".to_string(),
            ..ControlResponse::default()
        },
        ControlRequest::Status => ControlResponse {
            ok: true,
            message: if active_override.is_some() {
                "XBond override is active.".to_string()
            } else {
                "No XBond override is active.".to_string()
            },
            active_override: active_override.as_ref().map(ActiveScheduleOverride::status),
            ..ControlResponse::default()
        },
    }
}

fn prune_expired_override(active_override: &mut Option<ActiveScheduleOverride>) {
    if active_override
        .as_ref()
        .is_some_and(|override_state| Instant::now() >= override_state.expires_at)
    {
        *active_override = None;
    }
}

fn resolve_control_rebind_path(
    specs_by_id: &HashMap<u16, ProbePathSpec>,
    path_id: Option<u16>,
    interface_name: Option<&str>,
) -> Result<(u16, Option<String>)> {
    if let Some(path_id) = path_id {
        let spec = specs_by_id
            .get(&path_id)
            .with_context(|| format!("XBond path {path_id} is not configured"))?;
        return Ok((path_id, spec.interface_name.clone()));
    }

    let interface_name = interface_name
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .context("provide --path-id or --interface for XBond path rebind")?;

    specs_by_id
        .iter()
        .find(|(_, spec)| {
            spec.interface_name
                .as_deref()
                .is_some_and(|candidate| candidate.eq_ignore_ascii_case(interface_name))
                || spec
                    .bind_device
                    .as_deref()
                    .is_some_and(|candidate| candidate.eq_ignore_ascii_case(interface_name))
        })
        .map(|(path_id, spec)| (*path_id, spec.interface_name.clone()))
        .with_context(|| format!("no configured XBond path uses interface {interface_name}"))
}

fn effective_mode_and_policy(
    config: &ClientConfig,
    active_override: &mut Option<ActiveScheduleOverride>,
) -> (ScheduleMode, RedundancyPolicy) {
    prune_expired_override(active_override);
    active_override
        .as_ref()
        .map(|override_state| (override_state.mode, override_state.redundancy_policy))
        .unwrap_or((config.mode, config.redundancy_policy))
}

fn build_effective_schedule(
    mode: ScheduleMode,
    roles: &[xbond_core::ScoredPath],
    recovery_status: &RecoveryStatus,
    recovery_config: RecoveryConfig,
) -> SchedulePlan {
    let schedule = build_schedule(mode, roles);
    if recovery_status.active {
        expand_schedule_for_recovery(&schedule, roles, recovery_config)
    } else {
        schedule
    }
}

fn effective_transmission_policy(
    effective_policy: RedundancyPolicy,
    recovery_status: &RecoveryStatus,
) -> RedundancyPolicy {
    if recovery_status.active {
        RedundancyPolicy::Reliable
    } else {
        effective_policy
    }
}

fn schedule_control_signature(
    schedule: &SchedulePlan,
    redundancy_policy: RedundancyPolicy,
    recovery_active: bool,
) -> ScheduleControlSignature {
    ScheduleControlSignature {
        schedule: schedule.clone(),
        redundancy_policy,
        recovery_active,
    }
}

fn server_needs_schedule(
    server_status: &XBondServerRecoveryStatus,
    current_schedule_generation: u64,
) -> bool {
    server_status.reported
        && (server_status.schedule_required
            || server_status.schedule_generation < current_schedule_generation)
}

#[cfg(unix)]
fn spawn_control_listener(
    socket_path: PathBuf,
    control_tx: mpsc::Sender<ControlEnvelope>,
    json_events: bool,
) -> Result<()> {
    if let Some(parent) = socket_path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }

    match std::fs::remove_file(&socket_path) {
        Ok(()) => {}
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(error) => {
            return Err(error)
                .with_context(|| format!("failed to remove old {}", socket_path.display()));
        }
    }

    let listener = std::os::unix::net::UnixListener::bind(&socket_path)
        .with_context(|| format!("failed to bind {}", socket_path.display()))?;
    listener
        .set_nonblocking(true)
        .with_context(|| format!("failed to set {} nonblocking", socket_path.display()))?;
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(&socket_path, std::fs::Permissions::from_mode(0o600))
        .with_context(|| format!("failed to set permissions on {}", socket_path.display()))?;
    let listener = UnixListener::from_std(listener)?;

    tokio::spawn(async move {
        loop {
            let (stream, _) = match listener.accept().await {
                Ok(result) => result,
                Err(error) => {
                    eprintln!("xbond control socket accept error: {error}");
                    time::sleep(Duration::from_millis(100)).await;
                    continue;
                }
            };
            let control_tx = control_tx.clone();
            tokio::spawn(async move {
                if let Err(error) = handle_control_stream(stream, control_tx).await {
                    eprintln!("xbond control socket request failed: {error}");
                }
            });
        }
    });

    if json_events {
        println!(
            "{}",
            serde_json::json!({
                "event": "control-socket-listening",
                "path": socket_path.display().to_string(),
            })
        );
    }

    Ok(())
}

#[cfg(unix)]
async fn handle_control_stream(
    stream: UnixStream,
    control_tx: mpsc::Sender<ControlEnvelope>,
) -> Result<()> {
    let mut reader = BufReader::new(stream);
    let mut request_json = String::new();
    reader
        .read_line(&mut request_json)
        .await
        .context("failed to read control request")?;
    let request = serde_json::from_str::<ControlRequest>(&request_json)
        .context("invalid XBond control request")?;
    let (response_tx, response_rx) = oneshot::channel();
    control_tx
        .send(ControlEnvelope {
            request,
            response_tx,
        })
        .await
        .map_err(|_| anyhow::anyhow!("XBond tunnel control loop is not available"))?;
    let response = response_rx
        .await
        .context("XBond tunnel dropped control response")?;
    let mut stream = reader.into_inner();
    stream
        .write_all(serde_json::to_string(&response)?.as_bytes())
        .await?;
    stream.write_all(b"\n").await?;
    Ok(())
}

#[cfg(not(unix))]
fn spawn_control_listener(
    _socket_path: PathBuf,
    _control_tx: mpsc::Sender<ControlEnvelope>,
    _json_events: bool,
) -> Result<()> {
    Ok(())
}

fn spawn_tun_reader(
    mut tun_reader: XBondTun,
    tun_name: String,
    tun_read_mtu: usize,
    tun_packet_tx: mpsc::Sender<Vec<u8>>,
    fail_after_packets: Option<u64>,
) -> mpsc::UnboundedReceiver<TunReaderExit> {
    let (exit_tx, exit_rx) = mpsc::unbounded_channel();
    let task = tokio::task::spawn_blocking(move || {
        let mut buf = vec![0u8; tun_read_mtu];
        let mut packets_read = 0u64;
        loop {
            if should_fail_tun_reader(fail_after_packets, packets_read) {
                return TunReaderExit::ReadFailed(format!(
                    "XBond TUN reader for {tun_name} stopped by lab failure injection after \
                     {packets_read} packets"
                ));
            }
            match tun_reader.read_packet(&mut buf) {
                Ok(len) => {
                    packets_read = packets_read.saturating_add(1);
                    if tun_packet_tx.blocking_send(buf[..len].to_vec()).is_err() {
                        return TunReaderExit::PacketQueueClosed;
                    }
                }
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) => {
                    return TunReaderExit::ReadFailed(format!(
                        "XBond TUN reader for {tun_name} stopped: {error}"
                    ));
                }
            }
        }
    });
    tokio::spawn(async move {
        let exit = match task.await {
            Ok(exit) => exit,
            Err(error) => TunReaderExit::TaskFailed(format!(
                "XBond TUN reader task stopped unexpectedly: {error}"
            )),
        };
        let _ = exit_tx.send(exit);
    });
    exit_rx
}

fn should_fail_tun_reader(fail_after_packets: Option<u64>, packets_read: u64) -> bool {
    fail_after_packets.is_some_and(|limit| packets_read >= limit)
}

fn tun_reader_exit_error(exit: TunReaderExit) -> anyhow::Error {
    match exit {
        TunReaderExit::PacketQueueClosed => {
            anyhow::anyhow!("XBond TUN reader stopped because its packet queue closed")
        }
        TunReaderExit::ReadFailed(message) | TunReaderExit::TaskFailed(message) => {
            anyhow::anyhow!(message)
        }
    }
}

fn spawn_tun_writer(
    mut tun_writer: XBondTun,
    tun_name: String,
    capacity: usize,
    lab_write_delay: Duration,
    payload_pool: ReceiverPayloadPool,
    metrics: Arc<TunWriterMetrics>,
) -> (TunWriterHandle, mpsc::UnboundedReceiver<TunWriterExit>) {
    let bounded_capacity = capacity.max(1);
    let (work_tx, mut work_rx) = mpsc::channel::<TunWriteBatch>(bounded_capacity);
    let queued_packets = Arc::new(AtomicU64::new(0));
    let worker_queued_packets = queued_packets.clone();
    let (exit_tx, exit_rx) = mpsc::unbounded_channel();
    let task = tokio::task::spawn_blocking(move || {
        while let Some(batch) = work_rx.blocking_recv() {
            worker_queued_packets.fetch_sub(batch.packets.len() as u64, Ordering::AcqRel);
            let queue_delay_micros = batch
                .queued_at
                .elapsed()
                .as_micros()
                .min(u128::from(u64::MAX)) as u64;
            for packet in batch.packets {
                if !lab_write_delay.is_zero() {
                    std::thread::sleep(lab_write_delay);
                }
                let write_started = Instant::now();
                let result = tun_writer.write_packet(&packet.payload);
                let write_micros = write_started
                    .elapsed()
                    .as_micros()
                    .min(u128::from(u64::MAX)) as u64;
                if result.is_ok() {
                    metrics.record_success(
                        packet.path_id,
                        packet.payload.len(),
                        queue_delay_micros,
                        write_micros,
                    );
                } else {
                    metrics.record_failure();
                }
                payload_pool.recycle(packet.payload);
                if let Err(error) = result {
                    return TunWriterExit::WriteFailed(format!(
                        "XBond TUN writer for {tun_name} stopped: {error}"
                    ));
                }
            }
        }
        TunWriterExit::QueueClosed
    });
    tokio::spawn(async move {
        let exit = match task.await {
            Ok(exit) => exit,
            Err(error) => TunWriterExit::TaskFailed(format!(
                "XBond TUN writer task stopped unexpectedly: {error}"
            )),
        };
        let _ = exit_tx.send(exit);
    });
    (
        TunWriterHandle {
            tx: work_tx,
            queued_packets,
            capacity: bounded_capacity,
        },
        exit_rx,
    )
}

fn tun_writer_exit_error(exit: TunWriterExit) -> anyhow::Error {
    match exit {
        TunWriterExit::QueueClosed => {
            anyhow::anyhow!("XBond TUN writer stopped because its packet queue closed")
        }
        TunWriterExit::WriteFailed(message) | TunWriterExit::TaskFailed(message) => {
            anyhow::anyhow!(message)
        }
    }
}

fn apply_tun_writer_metrics(
    metrics: &TunWriterMetrics,
    counters: &mut TunnelCounters,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    repair: &mut XBondRepairStatus,
) {
    let snapshot = metrics.take_snapshot();
    counters.tun_write_packets = counters.tun_write_packets.saturating_add(snapshot.packets);
    counters.tun_write_queue_micros_total = counters
        .tun_write_queue_micros_total
        .saturating_add(snapshot.queue_delay_micros);
    counters.tun_write_micros_total = counters
        .tun_write_micros_total
        .saturating_add(snapshot.write_micros);
    counters.tun_write_failures = counters
        .tun_write_failures
        .saturating_add(snapshot.failures);
    counters.data_packets_received = counters
        .data_packets_received
        .saturating_add(snapshot.packets);
    counters.data_bytes_received = counters
        .data_bytes_received
        .saturating_add(snapshot.payload_bytes);
    repair.frames_delivered = repair
        .frames_delivered
        .saturating_add(snapshot.repair_frames);
    for (path_id, bytes) in snapshot.path_bytes {
        let runtime = path_runtime.entry(path_id).or_default();
        runtime.bytes_received = runtime.bytes_received.saturating_add(bytes);
    }
}

#[derive(Debug)]
struct FreshAuthenticatedSession {
    session_id: u64,
    synchronization: ClientSynchronizationState,
}

async fn begin_fresh_authenticated_session(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    senders: &HashMap<u16, PathSenderHandle>,
    schedule_generation: u64,
    control_sequence: &mut u64,
    counters: &mut TunnelCounters,
    json_events: bool,
    trace_packets: bool,
    reason: &str,
) -> Result<FreshAuthenticatedSession> {
    let session_id = resolve_session_id(None)?;
    let request_nonce = resolve_session_handshake_nonce()?;
    let now = Instant::now();
    let mut synchronization =
        ClientSynchronizationState::new(session_id, request_nonce, schedule_generation, now);
    send_session_open(
        path_runtime,
        senders,
        session_id,
        request_nonce,
        control_sequence,
        counters,
        json_events,
        trace_packets,
    )
    .await?;
    synchronization.record_session_open_sent(Instant::now());
    if json_events {
        println!(
            "{}",
            serde_json::json!({
                "event": "fresh-session-open-sent",
                "session_id": session_id,
                "reason": reason,
            })
        );
    }
    Ok(FreshAuthenticatedSession {
        session_id,
        synchronization,
    })
}

#[allow(clippy::too_many_arguments)]
fn reset_client_session_runtime(
    config: &ClientConfig,
    inbound_receiver: &mut FrameReceiver,
    return_reorder: &mut PacketReorderBuffer,
    resend_cache: &mut ResendCache,
    repair: &mut XBondRepairStatus,
    server_recovery_status: &mut XBondServerRecoveryStatus,
    aggregate_health: &mut TunnelAggregateHealthRuntime,
    pending_fec_source: &mut Option<PendingFecSource>,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
) {
    *inbound_receiver = FrameReceiver::new(config.realtime_deadline_ms * 1_000, 8192);
    *return_reorder =
        PacketReorderBuffer::with_initial_sequence(8192, config.reorder_hold_ms * 1_000, 1);
    *resend_cache = ResendCache::new_with_byte_capacity(
        REPAIR_CACHE_CAPACITY,
        recommended_repair_cache_bytes(
            0,
            REPAIR_CACHE_TTL_MICROS,
            REPAIR_CACHE_MIN_BYTES,
            REPAIR_CACHE_MAX_BYTES,
        ),
        REPAIR_CACHE_TTL_MICROS,
    );
    *repair = XBondRepairStatus::default();
    *server_recovery_status = XBondServerRecoveryStatus::default();
    *aggregate_health = TunnelAggregateHealthRuntime::default();
    *pending_fec_source = None;
    for runtime in path_runtime.values_mut() {
        runtime.pending_heartbeats.clear();
    }
}

async fn run_tunnel(options: TunnelOptions) -> Result<()> {
    let config = read_config(&options.config)?;
    let key_text = std::env::var(&options.key_env)
        .with_context(|| format!("{} environment variable is required", options.key_env))?;
    let key = XBondKey::from_passphrase(&key_text);
    let automatic_session_rotation = options.session_id.is_none();
    let mut session_id = resolve_session_id(options.session_id)?;

    let specs = select_probe_paths(&config, &[], &[])?;
    let specs_by_id = specs
        .into_iter()
        .map(|spec| (spec.path_id, spec))
        .collect::<HashMap<_, _>>();
    let mut sockets: HashMap<u16, Arc<UdpSocket>> = HashMap::new();
    let mut senders: HashMap<u16, PathSenderHandle> = HashMap::new();
    let mut receivers: HashMap<u16, JoinHandle<()>> = HashMap::new();
    let mut path_runtime: HashMap<u16, TunnelPathRuntime> = HashMap::new();
    let (control_tx, mut control_rx) =
        mpsc::channel::<ControlEnvelope>(OPERATOR_CONTROL_QUEUE_CAPACITY);
    let send_report_capacity = config
        .tun_queue_capacity
        .max(1)
        .saturating_mul(specs_by_id.len().max(1));
    let (send_report_tx, mut send_report_rx) =
        mpsc::channel::<PathSendReport>(send_report_capacity);
    let (socket_event_tx, mut socket_event_rx) =
        mpsc::channel::<PathSocketEvent>(SOCKET_EVENT_QUEUE_CAPACITY);
    let (primary_send_completion_tx, mut primary_send_completion_rx) =
        mpsc::channel::<PrimarySendCompletion>(1);
    spawn_control_listener(
        options.control_socket.clone(),
        control_tx,
        options.json_events,
    )?;

    let tun = XBondTun::open(&options.tun_name, options.tun_mtu).with_context(|| {
        format!(
            "failed to open XBond TUN {}; run as root or grant CAP_NET_ADMIN",
            options.tun_name
        )
    })?;
    let tun_reader = tun
        .try_clone()
        .with_context(|| format!("failed to clone XBond TUN {} for packet reader", tun.name()))?;
    let tun_writer = tun
        .try_clone()
        .with_context(|| format!("failed to clone XBond TUN {} for packet writer", tun.name()))?;
    let tun_queue_capacity = config.tun_queue_capacity.max(1);
    let inbound_queue_capacity = config.inbound_queue_capacity.max(1);
    let (tun_packet_tx, mut tun_packet_rx) = mpsc::channel::<Vec<u8>>(tun_queue_capacity);
    let tun_name = tun.name().to_string();
    let tun_read_mtu = usize::from(options.tun_mtu).max(2048);
    let receiver_payload_pool = ReceiverPayloadPool::new(
        inbound_queue_capacity,
        receiver_scratch_capacity(options.tun_mtu),
    );
    let mut tun_reader_exit_rx = spawn_tun_reader(
        tun_reader,
        tun_name.clone(),
        tun_read_mtu,
        tun_packet_tx,
        options.lab_fail_tun_read_after_packets,
    );
    let tun_writer_metrics = Arc::new(TunWriterMetrics::for_paths(specs_by_id.keys().copied()));
    let (tun_write_tx, mut tun_writer_exit_rx) = spawn_tun_writer(
        tun_writer,
        tun_name,
        inbound_queue_capacity,
        Duration::from_millis(options.lab_tun_write_delay_ms),
        receiver_payload_pool.clone(),
        tun_writer_metrics.clone(),
    );

    let (inbound_control_tx, mut inbound_control_rx) =
        mpsc::channel::<InboundTunnelFrame>(INBOUND_CONTROL_QUEUE_CAPACITY);
    let (inbound_payload_tx, mut inbound_payload_rx) =
        mpsc::channel::<InboundTunnelFrame>(inbound_queue_capacity);
    let inbound_payload_drops = Arc::new(AtomicU64::new(0));
    let inbound_queues = InboundTunnelQueues {
        control_tx: inbound_control_tx,
        payload_tx: inbound_payload_tx,
        payload_drops: inbound_payload_drops.clone(),
    };
    let (silent_probe_result_tx, mut silent_probe_result_rx) =
        mpsc::channel::<SilentBlackholeProbeResult>(specs_by_id.len().max(1));
    ensure_tunnel_sockets(
        &config,
        &specs_by_id,
        &mut sockets,
        &mut senders,
        &mut receivers,
        &mut path_runtime,
        &inbound_queues,
        &send_report_tx,
        &socket_event_tx,
        &key,
        options.tun_mtu,
        &receiver_payload_pool,
        options.json_events,
    )
    .await?;
    refresh_sender_queue_metrics(&mut path_runtime, &senders);
    let policy_config = RedundancyPolicyConfig {
        interactive_packet_threshold_bytes: config.interactive_packet_threshold_bytes,
        duplicate_loss_threshold: config.duplicate_loss_threshold,
        backup_loss_disable_threshold: config.backup_loss_disable_threshold,
    };
    let mut active_override: Option<ActiveScheduleOverride> = None;
    let mut role_state = RoleSelectionState::default();
    let role_config = RoleSelectionConfig::default();
    let recovery_config = config.recovery_config();
    let mut recovery_state = RecoveryState::default();
    let mut recovery_schedule_state = RecoveryScheduleStabilityState::default();
    let mut health = tunnel_health(&config, &path_runtime, &sockets);
    let mut roles = select_path_roles_with_state(
        &health,
        config.max_active_backups,
        &mut role_state,
        role_config,
    );
    let (mut effective_mode, mut effective_policy) =
        effective_mode_and_policy(&config, &mut active_override);
    let mut recovery_status = update_recovery_state(
        &mut recovery_state,
        effective_policy,
        &health,
        recovery_config,
    );
    let mut schedule =
        build_effective_schedule(effective_mode, &roles, &recovery_status, recovery_config);
    schedule = stabilize_recovery_schedule(
        &mut recovery_schedule_state,
        &schedule,
        &roles,
        &recovery_status,
        recovery_config,
        5,
    );
    let mut transmission_policy = effective_transmission_policy(effective_policy, &recovery_status);
    let mut transmission_plans =
        precompute_transmission_plans(&schedule, transmission_policy, &health, policy_config);
    let mut current_schedule_generation = 1u64;
    let mut last_control_signature: Option<ScheduleControlSignature>;
    let mut last_control_sent_at: Instant;

    if options.json_events {
        println!(
            "{}",
            serde_json::json!({
                "event": "tunnel-started",
                "tun": tun.name(),
                "server": config.server_addr,
                "mode": effective_mode,
                "policy": effective_policy,
                "session_id": session_id,
                "schedule": schedule,
            })
        );
    } else {
        println!(
            "xbond XBond tunnel opened {} -> {} ({:?}); no routes were changed",
            tun.name(),
            config.server_addr,
            effective_mode
        );
    }

    let mut counters = TunnelCounters::default();
    let mut aggregate_health = TunnelAggregateHealthRuntime::default();
    let mut pending_fec_source: Option<PendingFecSource> = None;
    let mut inbound_receiver = FrameReceiver::new(config.realtime_deadline_ms * 1_000, 8192);
    let mut return_reorder =
        PacketReorderBuffer::with_initial_sequence(8192, config.reorder_hold_ms * 1_000, 1);
    let initial_repair_cache_bytes = recommended_repair_cache_bytes(
        0,
        REPAIR_CACHE_TTL_MICROS,
        REPAIR_CACHE_MIN_BYTES,
        REPAIR_CACHE_MAX_BYTES,
    );
    let mut resend_cache = ResendCache::new_with_byte_capacity(
        REPAIR_CACHE_CAPACITY,
        initial_repair_cache_bytes,
        REPAIR_CACHE_TTL_MICROS,
    );
    let mut repair = XBondRepairStatus::default();
    let mut server_recovery_status = XBondServerRecoveryStatus::default();
    let mut sequence = 0u64;
    let mut control_sequence = 1_000_000_000_000u64;
    let session_request_nonce = resolve_session_handshake_nonce()?;
    let synchronization_started_at = Instant::now();
    let mut synchronization = ClientSynchronizationState::new(
        session_id,
        session_request_nonce,
        current_schedule_generation,
        synchronization_started_at,
    );
    let mut scheduler_tick = time::interval(Duration::from_secs(1));
    let mut reorder_tick =
        time::interval(Duration::from_millis(config.reorder_hold_ms.clamp(5, 100)));
    let mut last_throughput_sample = Instant::now();
    let mut primary_send_pending = false;
    let mut last_primary_queue_full_reported = 0u64;
    let mut last_primary_queue_full = None::<(u16, u64)>;

    update_tunnel_throughput(
        &mut path_runtime,
        &mut counters,
        &mut last_throughput_sample,
    );
    counters.inbound_queue_drops = inbound_payload_drops.load(Ordering::Relaxed);
    repair.cache_entries = resend_cache.len();
    write_tunnel_runtime_status(
        &config,
        &tun,
        options.tun_mtu,
        &counters,
        &roles,
        &path_runtime,
        &schedule,
        effective_mode,
        effective_policy,
        &recovery_status,
        &aggregate_health,
        active_override.as_ref(),
        role_state.schedule_change_count,
        current_schedule_generation,
        &return_reorder,
        &repair,
        &server_recovery_status,
        tun_packet_rx.len(),
        inbound_queue_depth(&inbound_control_rx, &inbound_payload_rx),
    )?;
    send_session_open(
        &mut path_runtime,
        &senders,
        session_id,
        synchronization.request_nonce(),
        &mut control_sequence,
        &mut counters,
        options.json_events,
        options.trace_packets,
    )
    .await?;
    synchronization.record_session_open_sent(Instant::now());
    last_control_signature = Some(schedule_control_signature(
        &schedule,
        transmission_policy,
        recovery_status.active,
    ));
    last_control_sent_at = Instant::now();

    loop {
        tokio::select! {
            _ = scheduler_tick.tick() => {
                apply_tun_writer_metrics(
                    &tun_writer_metrics,
                    &mut counters,
                    &mut path_runtime,
                    &mut repair,
                );
                counters.inbound_queue_drops =
                    inbound_payload_drops.load(Ordering::Relaxed);
                ensure_tunnel_sockets(
                    &config,
                    &specs_by_id,
                    &mut sockets,
                    &mut senders,
                    &mut receivers,
                    &mut path_runtime,
                    &inbound_queues,
                    &send_report_tx,
                    &socket_event_tx,
                    &key,
                    options.tun_mtu,
                    &receiver_payload_pool,
                    options.json_events,
                ).await?;
                let synchronization_now = Instant::now();
                if let Some(error) = synchronization.synchronization_error(synchronization_now) {
                    if !automatic_session_rotation {
                        bail!(
                            "{error}; automatic session rotation is disabled because --session-id was supplied"
                        );
                    }
                    let previous_session_id = session_id;
                    reset_client_session_runtime(
                        &config,
                        &mut inbound_receiver,
                        &mut return_reorder,
                        &mut resend_cache,
                        &mut repair,
                        &mut server_recovery_status,
                        &mut aggregate_health,
                        &mut pending_fec_source,
                        &mut path_runtime,
                    );
                    sequence = 0;
                    primary_send_pending = false;
                    last_primary_queue_full = None;
                    let fresh = begin_fresh_authenticated_session(
                        &mut path_runtime,
                        &senders,
                        current_schedule_generation,
                        &mut control_sequence,
                        &mut counters,
                        options.json_events,
                        options.trace_packets,
                        &error,
                    )
                    .await?;
                    session_id = fresh.session_id;
                    synchronization = fresh.synchronization;
                    last_control_signature = None;
                    last_control_sent_at = synchronization_now;
                    if options.json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "session-reconnected",
                                "previous_session_id": previous_session_id,
                                "session_id": session_id,
                                "reason": error,
                            })
                        );
                    }
                    continue;
                }
                if synchronization.should_send_session_open(synchronization_now) {
                    send_session_open(
                        &mut path_runtime,
                        &senders,
                        session_id,
                        synchronization.request_nonce(),
                        &mut control_sequence,
                        &mut counters,
                        options.json_events,
                        options.trace_packets,
                    )
                    .await?;
                    synchronization.record_session_open_sent(synchronization_now);
                }
                if synchronization.session_accepted() {
                    send_tunnel_heartbeats(
                        &config,
                        &mut path_runtime,
                        &senders,
                        session_id,
                        &mut counters,
                        options.json_events,
                    )?;
                }
                drain_sender_completions(
                    &senders,
                    &mut path_runtime,
                    &mut counters,
                    &mut repair,
                );
                refresh_sender_queue_metrics(&mut path_runtime, &senders);
                schedule_silent_blackhole_probes(
                    &config,
                    &specs_by_id,
                    &sockets,
                    &mut path_runtime,
                    &silent_probe_result_tx,
                );
                update_tunnel_throughput(
                    &mut path_runtime,
                    &mut counters,
                    &mut last_throughput_sample,
                );
                update_repair_cache_budget(
                    &mut resend_cache,
                    counters.outbound_throughput_bps,
                    monotonic_micros(),
                    &mut counters,
                );
                health = tunnel_health(&config, &path_runtime, &sockets);
                roles = select_path_roles_with_state(
                    &health,
                    config.max_active_backups,
                    &mut role_state,
                    role_config,
                );
                (effective_mode, effective_policy) =
                    effective_mode_and_policy(&config, &mut active_override);
                recovery_status =
                    update_recovery_state(&mut recovery_state, effective_policy, &health, recovery_config);
                update_return_reorder_hold(
                    &mut return_reorder,
                    config.reorder_hold_ms,
                    &recovery_status,
                    &server_recovery_status,
                );
                schedule = build_effective_schedule(
                    effective_mode,
                    &roles,
                    &recovery_status,
                    recovery_config,
                );
                schedule = stabilize_recovery_schedule(
                    &mut recovery_schedule_state,
                    &schedule,
                    &roles,
                    &recovery_status,
                    recovery_config,
                    5,
                );
                transmission_policy = effective_transmission_policy(effective_policy, &recovery_status);
                transmission_plans =
                    precompute_transmission_plans(&schedule, transmission_policy, &health, policy_config);
                if synchronization.data_plane_ready() {
                    send_tunnel_aggregate_heartbeat(
                        &config,
                        &mut aggregate_health,
                        &mut path_runtime,
                        &sockets,
                        &senders,
                        &transmission_plans,
                        session_id,
                        &mut counters,
                        options.json_events,
                    )?;
                }
                repair.cache_entries = resend_cache.len();
                if options.json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "client-dataplane-internal-metrics",
                            "tun_write_queue_depth": tun_write_tx.queue_depth(),
                            "tun_write_queue_capacity": tun_write_tx.capacity,
                            "tun_write_queue_drops": counters.tun_write_queue_drops,
                            "tun_write_failures": counters.tun_write_failures,
                            "control_lane_drops": counters.control_lane_drops,
                            "control_lane_coalesced": counters.control_lane_coalesced,
                            "repair_lane_drops": counters.repair_lane_drops,
                            "sender_deadline_drops": counters.sender_deadline_drops,
                            "sender_lanes": sender_lane_metrics_json(&path_runtime),
                            "pmtu_errors": counters.pmtu_errors,
                            "repair_cache_entries": resend_cache.len(),
                            "repair_cache_bytes": resend_cache.bytes_len(),
                            "repair_cache_byte_capacity": resend_cache.byte_capacity(),
                            "repair_cache_packet_capacity": resend_cache.packet_capacity(),
                            "repair_cache_evictions": counters.repair_cache_evictions,
                            "repair_cache_evicted_bytes": counters.repair_cache_evicted_bytes,
                        })
                    );
                }
                let control_signature =
                    schedule_control_signature(&schedule, transmission_policy, recovery_status.active);
                let schedule_changed = last_control_signature.as_ref() != Some(&control_signature);
                if schedule_changed {
                    current_schedule_generation = current_schedule_generation.saturating_add(1);
                    synchronization.require_schedule(
                        current_schedule_generation,
                        synchronization_now,
                    );
                    last_control_signature = Some(control_signature.clone());
                    pending_fec_source = None;
                }
                let schedule_unacknowledged = !synchronization.data_plane_ready();
                if synchronization.session_accepted()
                    && (schedule_changed
                        || server_needs_schedule(
                            &server_recovery_status,
                            current_schedule_generation,
                        )
                        || (schedule_unacknowledged
                            && last_control_sent_at.elapsed()
                                >= SCHEDULE_CONTROL_RETRY_INTERVAL)
                        || last_control_sent_at.elapsed() >= Duration::from_secs(5))
                {
                    send_tunnel_schedule_control(
                        &config,
                        &schedule,
                        transmission_policy,
                        &mut path_runtime,
                        &sockets,
                        &senders,
                        session_id,
                        &mut control_sequence,
                        current_schedule_generation,
                        recovery_status.active,
                        &mut counters,
                        options.json_events,
                        options.trace_packets,
                    )?;
                    last_control_signature = Some(control_signature);
                    last_control_sent_at = Instant::now();
                }
                write_tunnel_runtime_status(
                    &config,
                    &tun,
                    options.tun_mtu,
                    &counters,
                    &roles,
                    &path_runtime,
                    &schedule,
                    effective_mode,
                    effective_policy,
                    &recovery_status,
                    &aggregate_health,
                    active_override.as_ref(),
                    role_state.schedule_change_count,
                    current_schedule_generation,
                    &return_reorder,
                    &repair,
                    &server_recovery_status,
                    tun_packet_rx.len(),
                    inbound_queue_depth(&inbound_control_rx, &inbound_payload_rx),
                )?;
                counters.supervisor_control_progress_ticks = counters
                    .supervisor_control_progress_ticks
                    .saturating_add(1);
                let queue_full_events_since_last = counters
                    .primary_queue_full_events
                    .saturating_sub(last_primary_queue_full_reported);
                if options.json_events && queue_full_events_since_last > 0 {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "primary-sender-queue-full",
                            "events_since_last_report": queue_full_events_since_last,
                            "queue_full_events": counters.primary_queue_full_events,
                            "primary_send_pending": primary_send_pending,
                            "path_id": last_primary_queue_full.map(|value| value.0),
                            "socket_generation": last_primary_queue_full.map(|value| value.1),
                        })
                    );
                }
                if options.json_events && (primary_send_pending || queue_full_events_since_last > 0) {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "supervisor-control-progress",
                            "progress_ticks": counters.supervisor_control_progress_ticks,
                            "primary_send_pending": primary_send_pending,
                            "primary_queue_full_events": counters.primary_queue_full_events,
                            "send_report_queue_depth": send_report_rx.len(),
                            "send_report_queue_capacity": send_report_capacity,
                        })
                    );
                    last_primary_queue_full_reported = counters.primary_queue_full_events;
                }
            }

            _ = reorder_tick.tick() => {
                let ready = return_reorder.drain_ready(monotonic_micros());
                enqueue_reordered_return_packets(
                    &tun_write_tx,
                    ready,
                    &mut counters,
                    options.json_events,
                )
                .await?;
                send_repair_requests_for_return_gaps(
                    &mut path_runtime,
                    &senders,
                    session_id,
                    &mut control_sequence,
                    &mut return_reorder,
                    &mut repair,
                    recovery_status.active,
                    aggregate_health.loss_rate,
                    &mut counters,
                    options.json_events,
                )?;
            }

            Some(envelope) = control_rx.recv() => {
                let request = envelope.request;
                let mut response = match request {
                    ControlRequest::RebindPath { path_id, interface_name } => {
                        match resolve_control_rebind_path(&specs_by_id, path_id, interface_name.as_deref()) {
                            Ok((resolved_path_id, resolved_interface)) => {
                                mark_path_socket_for_rebind(
                                    &mut path_runtime,
                                    resolved_path_id,
                                    "manual-control",
                                    None,
                                    true,
                                );
                                match ensure_tunnel_sockets(
                                    &config,
                                    &specs_by_id,
                                    &mut sockets,
                                    &mut senders,
                                    &mut receivers,
                                    &mut path_runtime,
                                    &inbound_queues,
                                    &send_report_tx,
                                    &socket_event_tx,
                                    &key,
                                    options.tun_mtu,
                                    &receiver_payload_pool,
                                    options.json_events,
                                ).await {
                                    Ok(()) if sockets.contains_key(&resolved_path_id) => {
                                        let generation = path_runtime
                                            .get(&resolved_path_id)
                                            .map(|runtime| runtime.socket_generation);
                                        ControlResponse {
                                            ok: true,
                                            message: format!(
                                                "XBond path {resolved_path_id} rebound{}.",
                                                resolved_interface
                                                    .as_deref()
                                                    .map(|iface| format!(" on {iface}"))
                                                    .unwrap_or_default()
                                            ),
                                            path_id: Some(resolved_path_id),
                                            interface_name: resolved_interface,
                                            socket_generation: generation,
                                            rebound: Some(true),
                                            ..ControlResponse::default()
                                        }
                                    }
                                    Ok(()) => ControlResponse {
                                        ok: false,
                                        message: format!(
                                            "XBond path {resolved_path_id} could not be rebound because no live socket was opened."
                                        ),
                                        path_id: Some(resolved_path_id),
                                        interface_name: resolved_interface,
                                        rebound: Some(false),
                                        ..ControlResponse::default()
                                    },
                                    Err(error) => ControlResponse {
                                        ok: false,
                                        message: format!("XBond path rebind failed: {error}"),
                                        path_id: Some(resolved_path_id),
                                        interface_name: resolved_interface,
                                        rebound: Some(false),
                                        ..ControlResponse::default()
                                    },
                                }
                            }
                            Err(error) => ControlResponse {
                                ok: false,
                                message: error.to_string(),
                                rebound: Some(false),
                                ..ControlResponse::default()
                            },
                        }
                    }
                    other => handle_control_request(other, &mut active_override),
                };
                (effective_mode, effective_policy) =
                    effective_mode_and_policy(&config, &mut active_override);
                recovery_status =
                    update_recovery_state(&mut recovery_state, effective_policy, &health, recovery_config);
                update_return_reorder_hold(
                    &mut return_reorder,
                    config.reorder_hold_ms,
                    &recovery_status,
                    &server_recovery_status,
                );
                schedule = build_effective_schedule(
                    effective_mode,
                    &roles,
                    &recovery_status,
                    recovery_config,
                );
                schedule = stabilize_recovery_schedule(
                    &mut recovery_schedule_state,
                    &schedule,
                    &roles,
                    &recovery_status,
                    recovery_config,
                    5,
                );
                transmission_policy = effective_transmission_policy(effective_policy, &recovery_status);
                transmission_plans =
                    precompute_transmission_plans(&schedule, transmission_policy, &health, policy_config);
                repair.cache_entries = resend_cache.len();
                let control_signature =
                    schedule_control_signature(&schedule, transmission_policy, recovery_status.active);
                if last_control_signature.as_ref() != Some(&control_signature) {
                    current_schedule_generation = current_schedule_generation.saturating_add(1);
                    synchronization.require_schedule(
                        current_schedule_generation,
                        Instant::now(),
                    );
                    last_control_signature = Some(control_signature.clone());
                    pending_fec_source = None;
                }
                if response.ok && synchronization.session_accepted() {
                    match send_tunnel_schedule_control(
                        &config,
                        &schedule,
                        transmission_policy,
                        &mut path_runtime,
                        &sockets,
                        &senders,
                        session_id,
                        &mut control_sequence,
                        current_schedule_generation,
                        recovery_status.active,
                        &mut counters,
                        options.json_events,
                        options.trace_packets,
                    ) {
                        Ok(()) => {
                            last_control_signature = Some(control_signature);
                            last_control_sent_at = Instant::now();
                        }
                        Err(error) => {
                            response.ok = false;
                            response.message =
                                format!("XBond override applied locally, but schedule control failed: {error}");
                        }
                    }
                }
                write_tunnel_runtime_status(
                    &config,
                    &tun,
                    options.tun_mtu,
                    &counters,
                    &roles,
                    &path_runtime,
                    &schedule,
                    effective_mode,
                    effective_policy,
                    &recovery_status,
                    &aggregate_health,
                    active_override.as_ref(),
                    role_state.schedule_change_count,
                    current_schedule_generation,
                    &return_reorder,
                    &repair,
                    &server_recovery_status,
                    tun_packet_rx.len(),
                    inbound_queue_depth(&inbound_control_rx, &inbound_payload_rx),
                )?;
                let _ = envelope.response_tx.send(response);
            }

            Some(report) = send_report_rx.recv() => {
                if !socket_generation_is_current(
                    &path_runtime,
                    &sockets,
                    report.path_id,
                    report.socket_generation,
                ) {
                    continue;
                }
                record_tunnel_send_failure(&mut path_runtime, report.path_id);
                if report.message_too_large {
                    counters.pmtu_errors = counters.pmtu_errors.saturating_add(1);
                    let runtime = path_runtime.entry(report.path_id).or_default();
                    runtime.pmtu_error_count = runtime.pmtu_error_count.saturating_add(1);
                    runtime.last_pmtu_encoded_bytes = Some(report.encoded_bytes);
                }
                if report.needs_rebind {
                    mark_path_socket_for_rebind(
                        &mut path_runtime,
                        report.path_id,
                        "udp-send-enodev",
                        report.error.clone(),
                        false,
                    );
                }
                if options.json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "path-sender-send-failed",
                            "path_id": report.path_id,
                            "packet_kind": report.packet_kind,
                            "error": report.error.unwrap_or_else(|| "unknown send failure".to_string()),
                            "raw_os_error": report.raw_os_error,
                            "needs_rebind": report.needs_rebind,
                            "message_too_large": report.message_too_large,
                            "encoded_bytes": report.encoded_bytes,
                            "configured_tun_mtu": options.tun_mtu,
                            "lane": format!("{:?}", report.lane).to_lowercase(),
                        })
                    );
                }
                if report.message_too_large && options.json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "path-pmtu-message-too-large",
                            "path_id": report.path_id,
                            "packet_kind": report.packet_kind,
                            "encoded_bytes": report.encoded_bytes,
                            "configured_tun_mtu": options.tun_mtu,
                            "raw_os_error": report.raw_os_error,
                            "automatic_mtu_change": false,
                        })
                    );
                }
            }

            Some(event) = socket_event_rx.recv() => {
                if !socket_generation_is_current(
                    &path_runtime,
                    &sockets,
                    event.path_id,
                    event.socket_generation,
                ) {
                    continue;
                }
                record_tunnel_send_failure(&mut path_runtime, event.path_id);
                mark_path_socket_for_rebind(
                    &mut path_runtime,
                    event.path_id,
                    event.reason.clone(),
                    event.error.clone(),
                    false,
                );
                if options.json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "path-socket-rebind-requested",
                            "path_id": event.path_id,
                            "reason": event.reason,
                            "error": event.error,
                        })
                    );
                }
            }

            Some(result) = silent_probe_result_rx.recv() => {
                let threshold = silent_blackhole_stale_threshold(&config);
                let (should_rebind, ineffective_rebinds, should_restart_session) =
                    if socket_generation_is_current(
                    &path_runtime,
                    &sockets,
                    result.path_id,
                    result.socket_generation,
                ) {
                    let runtime = path_runtime.entry(result.path_id).or_default();
                    runtime.direct_probe_in_flight = false;
                    let should_rebind =
                        result.reachable && path_is_silent_blackhole_candidate(runtime, threshold);
                    let should_restart_session =
                        should_rebind && record_ineffective_rebind(runtime);
                    (
                        should_rebind,
                        runtime.ineffective_rebinds,
                        should_restart_session,
                    )
                } else {
                    (false, 0, false)
                };

                if should_rebind {
                    if should_restart_session {
                        bail!(
                            "path {} remained a confirmed silent UDP blackhole after {} socket \
                             rebind attempts; restarting the authenticated XBond session instead \
                             of repeating ineffective local rebinds",
                            result.path_id,
                            ineffective_rebinds
                        );
                    }
                    mark_path_socket_for_rebind(
                        &mut path_runtime,
                        result.path_id,
                        "silent-udp-blackhole-direct-probe-ok",
                        None,
                        false,
                    );
                } else if let Some(error) = result.error.as_ref() {
                    if let Some(runtime) = path_runtime.get_mut(&result.path_id) {
                        runtime.last_rebind_error =
                            Some(format!("silent-blackhole direct probe failed: {error}"));
                    }
                }

                if options.json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "silent-blackhole-direct-probe",
                            "path_id": result.path_id,
                            "socket_generation": result.socket_generation,
                            "reachable": result.reachable,
                            "rebind_requested": should_rebind,
                            "ineffective_rebinds": ineffective_rebinds,
                            "error": result.error,
                        })
                    );
                }
            }

            Some(exit) = tun_reader_exit_rx.recv() => {
                return Err(tun_reader_exit_error(exit));
            }

            Some(exit) = tun_writer_exit_rx.recv() => {
                return Err(tun_writer_exit_error(exit));
            }

            Some(completion) = primary_send_completion_rx.recv(), if primary_send_pending => {
                primary_send_pending = false;
                if !completion.success
                    && socket_generation_is_current(
                        &path_runtime,
                        &sockets,
                        completion.path_id,
                        completion.socket_generation,
                    )
                {
                    record_tunnel_send_failure(&mut path_runtime, completion.path_id);
                    if options.json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "primary-sender-stopped",
                                "path_id": completion.path_id,
                                "socket_generation": completion.socket_generation,
                            })
                        );
                    }
                }
            }

            Some(packet) = tun_packet_rx.recv(), if synchronization.data_plane_ready() && !primary_send_pending => {
                if options
                    .packet_limit
                    .is_some_and(|packet_limit| sequence >= packet_limit)
                {
                    break;
                }

                if !is_ipv4_packet(&packet) {
                    if options.json_events && options.trace_packets {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "non-ipv4-packet-skipped",
                                "bytes": packet.len(),
                            })
                        );
                    }
                    continue;
                }

                let packet_payload = Arc::new(packet);
                let packet_transmissions = transmission_plans.for_packet_len(
                    packet_payload.len(),
                    policy_config.interactive_packet_threshold_bytes,
                );

                if packet_transmissions.is_empty() {
                    pending_fec_source = None;
                    if options.json_events && options.trace_packets {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "packet-dropped-no-live-path",
                                "bytes": packet_payload.len(),
                            })
                        );
                    }
                    continue;
                }

                sequence += 1;
                let cache_entries_before = resend_cache.len();
                let cache_bytes_before = resend_cache.bytes_len();
                resend_cache.insert(
                    session_id,
                    sequence,
                    packet_payload.clone(),
                    monotonic_micros(),
                );
                counters.repair_cache_evictions = counters
                    .repair_cache_evictions
                    .saturating_add(
                        cache_entries_before
                            .saturating_add(1)
                            .saturating_sub(resend_cache.len()) as u64,
                    );
                counters.repair_cache_evicted_bytes = counters
                    .repair_cache_evicted_bytes
                    .saturating_add(
                        cache_bytes_before
                            .saturating_add(packet_payload.len())
                            .saturating_sub(resend_cache.bytes_len()) as u64,
                    );
                counters.data_bytes_sent = counters
                    .data_bytes_sent
                    .saturating_add(packet_payload.len() as u64);
                let send_micros = now_micros();
                for transmission in packet_transmissions
                    .iter()
                    .filter(|transmission| transmission.packet_kind != PacketKind::Fec)
                {
                    let Some(sender) = senders.get(&transmission.path_id) else {
                        continue;
                    };
                    let header = XBondHeader::new(
                        transmission.packet_kind,
                        session_id,
                        sequence,
                        send_micros,
                        transmission.path_id,
                    );
                    let work = PathSendWork::data(
                        transmission.packet_kind,
                        header,
                        packet_payload.clone(),
                    );
                    let enqueue_result = if transmission.packet_kind == PacketKind::Data {
                        match enqueue_primary_work(
                            transmission.path_id,
                            sender.socket_generation,
                            sender,
                            work,
                            &primary_send_completion_tx,
                        ) {
                            Ok(PrimaryEnqueueResult::Enqueued) => Ok(()),
                            Ok(PrimaryEnqueueResult::Pending) => {
                                primary_send_pending = true;
                                counters.primary_queue_full_events = counters
                                    .primary_queue_full_events
                                    .saturating_add(1);
                                last_primary_queue_full = Some((
                                    transmission.path_id,
                                    sender.socket_generation,
                                ));
                                Ok(())
                            }
                            Err(error) => Err(error),
                        }
                    } else {
                        match try_enqueue_sender_lane(&sender.data_tx, &sender.metrics, work) {
                            Ok(()) => Ok(()),
                            Err(mpsc::error::TrySendError::Full(_)) => {
                                sender.metrics.record_enqueue_drop(PathSendLane::Data);
                                counters.duplicate_send_skips =
                                    counters.duplicate_send_skips.saturating_add(1);
                                continue;
                            }
                            Err(mpsc::error::TrySendError::Closed(_)) => Err(std::io::Error::new(
                                ErrorKind::BrokenPipe,
                                "XBond duplicate path sender stopped",
                            )),
                        }
                    };
                    match enqueue_result {
                        Ok(()) => {}
                        Err(error) => {
                            record_tunnel_send_failure(&mut path_runtime, transmission.path_id);
                            if options.json_events {
                                println!(
                                    "{}",
                                    serde_json::json!({
                                        "event": "packet-send-failed",
                                        "sequence": sequence,
                                        "path_id": transmission.path_id,
                                        "packet_kind": transmission.packet_kind,
                                        "error": error.to_string(),
                                    })
                                );
                            }
                        }
                    }
                }

                let fec_eligible = packet_transmissions
                    .iter()
                    .any(|transmission| transmission.packet_kind == PacketKind::Fec);
                if let Some((base_sequence, first_payload, second_payload)) =
                    advance_pending_fec_source(
                        &mut pending_fec_source,
                        sequence,
                        packet_payload.clone(),
                        fec_eligible,
                        current_schedule_generation,
                        effective_policy,
                        recovery_status.active,
                    )
                {
                        let fec_payload = Arc::new(XorFecBlock::encode(
                            base_sequence,
                            first_payload.as_slice(),
                            second_payload.as_slice(),
                        )?);
                        for transmission in packet_transmissions
                            .iter()
                            .filter(|transmission| transmission.packet_kind == PacketKind::Fec)
                        {
                            let Some(sender) = senders.get(&transmission.path_id) else {
                                counters.fec_packets_skipped += 1;
                                counters.fec_send_skips =
                                    counters.fec_send_skips.saturating_add(1);
                                continue;
                            };
                            let header = XBondHeader::new(
                                PacketKind::Fec,
                                session_id,
                                base_sequence,
                                send_micros,
                                transmission.path_id,
                            );
                            match try_enqueue_sender_lane(
                                &sender.data_tx,
                                &sender.metrics,
                                PathSendWork::data(
                                    PacketKind::Fec,
                                    header,
                                    fec_payload.clone(),
                                ),
                            ) {
                                Ok(()) => {}
                                Err(mpsc::error::TrySendError::Full(_)) => {
                                    sender.metrics.record_enqueue_drop(PathSendLane::Data);
                                    counters.fec_packets_skipped += 1;
                                    counters.fec_send_skips =
                                        counters.fec_send_skips.saturating_add(1);
                                }
                                Err(mpsc::error::TrySendError::Closed(_)) => {
                                    counters.fec_packets_skipped += 1;
                                    counters.fec_send_skips =
                                        counters.fec_send_skips.saturating_add(1);
                                    record_tunnel_send_failure(&mut path_runtime, transmission.path_id);
                                    if options.json_events {
                                        println!(
                                            "{}",
                                            serde_json::json!({
                                                "event": "fec-send-failed",
                                                "sequence": base_sequence,
                                                "path_id": transmission.path_id,
                                                "error": "XBond FEC path sender stopped",
                                            })
                                        );
                                    }
                                }
                            }
                        }
                }

                if options.json_events && options.trace_packets {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "packet-sent",
                            "sequence": sequence,
                            "bytes": packet_payload.len(),
                            "data_packets_sent": counters.data_packets_sent,
                            "duplicate_packets_sent": counters.duplicate_packets_sent,
                            "fec_packets_sent": counters.fec_packets_sent,
                            "fec_packets_skipped": counters.fec_packets_skipped,
                            "schedule": schedule,
                            "policy": effective_policy,
                        })
                    );
                }
            }

            Some(mut inbound) = receive_prioritized_tunnel_frame(
                &mut inbound_control_rx,
                &mut inbound_payload_rx,
            ) => {
                counters.decoded_frames = counters.decoded_frames.saturating_add(1);
                if inbound.frame.header.session_id != session_id {
                    if options.json_events && options.trace_packets {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "stale-session-frame-ignored",
                                "expected_session_id": session_id,
                                "frame_session_id": inbound.frame.header.session_id,
                                "sequence": inbound.frame.header.sequence,
                                "kind": inbound.frame.header.kind,
                                "path_id": inbound.path_id,
                            })
                        );
                    }
                    receiver_payload_pool.recycle(inbound.frame.payload);
                    continue;
                }
                match inbound_receiver.observe(&inbound.frame, now_micros()) {
                    ReceiveOutcome::Accepted => {}
                    ReceiveOutcome::Duplicate => {
                        if is_data_like(inbound.frame.header.kind) {
                            let runtime = path_runtime.entry(inbound.path_id).or_default();
                            runtime.duplicate_bytes_received = runtime
                                .duplicate_bytes_received
                                .saturating_add(inbound.frame.payload.len() as u64);
                            runtime.duplicate_late_packets =
                                runtime.duplicate_late_packets.saturating_add(1);
                        }
                        counters.duplicate_packets_dropped =
                            counters.duplicate_packets_dropped.saturating_add(1);
                        if inbound.frame.header.kind == PacketKind::Repair {
                            repair.late_frames = repair.late_frames.saturating_add(1);
                        }
                        receiver_payload_pool.recycle(inbound.frame.payload);
                        continue;
                    }
                    ReceiveOutcome::Expired => {
                        if is_data_like(inbound.frame.header.kind) {
                            let runtime = path_runtime.entry(inbound.path_id).or_default();
                            runtime.duplicate_bytes_received = runtime
                                .duplicate_bytes_received
                                .saturating_add(inbound.frame.payload.len() as u64);
                            runtime.duplicate_late_packets =
                                runtime.duplicate_late_packets.saturating_add(1);
                        }
                        counters.late_packets_dropped =
                            counters.late_packets_dropped.saturating_add(1);
                        if inbound.frame.header.kind == PacketKind::Repair {
                            repair.late_frames = repair.late_frames.saturating_add(1);
                        }
                        receiver_payload_pool.recycle(inbound.frame.payload);
                        continue;
                    }
                }
                if let Some(control) = parse_control_message(&inbound.frame) {
                    let synchronization_now = Instant::now();
                    let mut restart_reason = None;
                    match synchronization.apply_control(&control, synchronization_now) {
                        SynchronizationControlOutcome::RestartRequired(reason) => {
                            restart_reason = Some(reason);
                        }
                        SynchronizationControlOutcome::SessionChallenge {
                            request_nonce,
                            challenge,
                        } => {
                            send_session_proof(
                                &mut path_runtime,
                                &senders,
                                session_id,
                                request_nonce,
                                challenge,
                                &mut control_sequence,
                                &mut counters,
                                options.json_events,
                            )
                            .await?;
                        }
                        SynchronizationControlOutcome::SessionAccepted => {
                            synchronization.require_schedule(
                                current_schedule_generation,
                                synchronization_now,
                            );
                            send_tunnel_schedule_control(
                                &config,
                                &schedule,
                                transmission_policy,
                                &mut path_runtime,
                                &sockets,
                                &senders,
                                session_id,
                                &mut control_sequence,
                                current_schedule_generation,
                                recovery_status.active,
                                &mut counters,
                                options.json_events,
                                options.trace_packets,
                            )?;
                            last_control_sent_at = synchronization_now;
                            if options.json_events {
                                println!(
                                    "{}",
                                    serde_json::json!({
                                        "event": "session-accepted",
                                        "session_id": session_id,
                                    })
                                );
                            }
                        }
                        SynchronizationControlOutcome::ScheduleAccepted => {
                            if options.json_events {
                                println!(
                                    "{}",
                                    serde_json::json!({
                                        "event": "schedule-accepted",
                                        "session_id": session_id,
                                        "schedule_generation": current_schedule_generation,
                                    })
                                );
                            }
                        }
                        SynchronizationControlOutcome::Ignored => {}
                    }

                    if let Some(reason) = restart_reason {
                        if !automatic_session_rotation {
                            bail!(
                                "XBond server requested restart of fixed authenticated session {session_id}: {reason}"
                            );
                        }
                        let previous_session_id = session_id;
                        receiver_payload_pool.recycle(inbound.frame.payload);
                        reset_client_session_runtime(
                            &config,
                            &mut inbound_receiver,
                            &mut return_reorder,
                            &mut resend_cache,
                            &mut repair,
                            &mut server_recovery_status,
                            &mut aggregate_health,
                            &mut pending_fec_source,
                            &mut path_runtime,
                        );
                        sequence = 0;
                        primary_send_pending = false;
                        last_primary_queue_full = None;
                        let fresh = begin_fresh_authenticated_session(
                            &mut path_runtime,
                            &senders,
                            current_schedule_generation,
                            &mut control_sequence,
                            &mut counters,
                            options.json_events,
                            options.trace_packets,
                            &reason,
                        )
                        .await?;
                        session_id = fresh.session_id;
                        synchronization = fresh.synchronization;
                        last_control_signature = None;
                        last_control_sent_at = synchronization_now;
                        if options.json_events {
                            println!(
                                "{}",
                                serde_json::json!({
                                    "event": "session-reconnected",
                                    "previous_session_id": previous_session_id,
                                    "session_id": session_id,
                                    "reason": reason,
                                })
                            );
                        }
                        continue;
                    }

                    match control {
                        XBondControlMessage::ServerRecoveryStatus { status } => {
                            let mut status = *status;
                            status.reported = true;
                            server_recovery_status = status;
                            update_return_reorder_hold(
                                &mut return_reorder,
                                config.reorder_hold_ms,
                                &recovery_status,
                                &server_recovery_status,
                            );
                            if synchronization.session_accepted()
                                && server_needs_schedule(
                                    &server_recovery_status,
                                    current_schedule_generation,
                                )
                            {
                                synchronization
                                    .mark_schedule_unsynchronized(synchronization_now);
                                if options.json_events {
                                    println!(
                                        "{}",
                                        serde_json::json!({
                                            "event": "server-schedule-required",
                                            "current_schedule_generation": current_schedule_generation,
                                            "server_schedule_generation": server_recovery_status.schedule_generation,
                                            "server_schedule_required": server_recovery_status.schedule_required,
                                            "server_schedule_age_ms": server_recovery_status.schedule_age_ms,
                                        })
                                    );
                                }
                                send_tunnel_schedule_control(
                                    &config,
                                    &schedule,
                                    transmission_policy,
                                    &mut path_runtime,
                                    &sockets,
                                    &senders,
                                    session_id,
                                    &mut control_sequence,
                                    current_schedule_generation,
                                    recovery_status.active,
                                    &mut counters,
                                    options.json_events,
                                    options.trace_packets,
                                )?;
                                last_control_sent_at = synchronization_now;
                            }
                            write_tunnel_runtime_status(
                                &config,
                                &tun,
                                options.tun_mtu,
                                &counters,
                                &roles,
                                &path_runtime,
                                &schedule,
                                effective_mode,
                                effective_policy,
                                &recovery_status,
                                &aggregate_health,
                                active_override.as_ref(),
                                role_state.schedule_change_count,
                                current_schedule_generation,
                                &return_reorder,
                                &repair,
                                &server_recovery_status,
                                tun_packet_rx.len(),
                                inbound_queue_depth(&inbound_control_rx, &inbound_payload_rx),
                            )?;
                        }
                        XBondControlMessage::RepairRequest { mut sequences } => {
                            sequences.sort_unstable();
                            sequences.dedup();
                            sequences.truncate(MAX_REPAIR_REQUESTS);
                            if !sequences.is_empty() {
                                repair.requests_received = repair
                                    .requests_received
                                    .saturating_add(sequences.len() as u64);
                                send_repair_frames_from_client_cache(
                                    &config,
                                    &mut path_runtime,
                                    &senders,
                                    &transmission_plans,
                                    &mut resend_cache,
                                    session_id,
                                    &sequences,
                                    &mut repair,
                                    &mut counters,
                                )?;
                            }
                        }
                        XBondControlMessage::SessionOpen { .. }
                        | XBondControlMessage::SessionChallenge { .. }
                        | XBondControlMessage::SessionProof { .. }
                        | XBondControlMessage::SessionAccepted { .. }
                        | XBondControlMessage::SessionRestartRequired { .. }
                        | XBondControlMessage::ScheduleAccepted { .. } => {}
                    }
                    receiver_payload_pool.recycle(inbound.frame.payload);
                    continue;
                }
                if is_expected_ack(&inbound.frame, session_id, inbound.frame.header.sequence) {
                    let is_aggregate_ack = record_aggregate_tunnel_heartbeat_ack(
                        &mut aggregate_health,
                        &inbound.frame,
                    );
                    record_tunnel_heartbeat_ack(
                        &mut path_runtime,
                        inbound.path_id,
                        &inbound.frame,
                    );
                    if options.json_events && options.trace_packets {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": if is_aggregate_ack { "tunnel-health-heartbeat-ack" } else { "health-heartbeat-ack" },
                                "path_id": inbound.path_id,
                                "sequence": inbound.frame.header.sequence,
                            })
                        );
                    }
                    receiver_payload_pool.recycle(inbound.frame.payload);
                    continue;
                }

                if is_data_like(inbound.frame.header.kind) {
                    if inbound.frame.header.kind == PacketKind::Duplicate {
                        let runtime = path_runtime.entry(inbound.path_id).or_default();
                        runtime.duplicate_useful_packets =
                            runtime.duplicate_useful_packets.saturating_add(1);
                    }
                    if is_ipv4_packet(&inbound.frame.payload) {
                        let sequence = inbound.frame.header.sequence;
                        let path_id = if inbound.frame.header.kind == PacketKind::Repair {
                            u16::MAX
                        } else {
                            inbound.path_id
                        };
                        let payload_len = inbound.frame.payload.len();
                        let payload = std::mem::take(&mut inbound.frame.payload);
                        let ready = return_reorder.push(
                            sequence,
                            path_id,
                            payload,
                            monotonic_micros(),
                            0,
                        );
                        enqueue_reordered_return_packets(
                            &tun_write_tx,
                            ready,
                            &mut counters,
                            options.json_events,
                        )
                        .await?;
                        send_repair_requests_for_return_gaps(
                            &mut path_runtime,
                            &senders,
                            session_id,
                            &mut control_sequence,
                            &mut return_reorder,
                            &mut repair,
                            recovery_status.active,
                            aggregate_health.loss_rate,
                            &mut counters,
                            options.json_events,
                        )?;
                        if options.json_events && options.trace_packets {
                            println!(
                                "{}",
                                serde_json::json!({
                                    "event": "packet-received",
                                    "sequence": sequence,
                                    "bytes": payload_len,
                                    "data_packets_received": counters.data_packets_received,
                                })
                            );
                        }
                    }
                }
                receiver_payload_pool.recycle(inbound.frame.payload);
            }

            else => break,
        };
    }

    Ok(())
}

fn advance_pending_fec_source(
    pending: &mut Option<PendingFecSource>,
    sequence: u64,
    payload: Arc<Vec<u8>>,
    fec_eligible: bool,
    schedule_generation: u64,
    policy: RedundancyPolicy,
    recovery_active: bool,
) -> Option<ConsecutiveFecPair> {
    if !fec_eligible {
        *pending = None;
        return None;
    }

    let previous = pending.take();
    if let Some(previous) = previous {
        let same_context = previous.schedule_generation == schedule_generation
            && previous.policy == policy
            && previous.recovery_active == recovery_active;
        if same_context && previous.sequence.checked_add(1) == Some(sequence) {
            return Some((previous.sequence, previous.payload, payload));
        }
    }

    *pending = Some(PendingFecSource {
        sequence,
        payload,
        schedule_generation,
        policy,
        recovery_active,
    });
    None
}

async fn enqueue_reordered_return_packets(
    tun_write_tx: &TunWriterHandle,
    packets: Vec<ReorderedPacket>,
    counters: &mut TunnelCounters,
    json_events: bool,
) -> Result<()> {
    enqueue_reordered_return_packets_with_deadline(
        tun_write_tx,
        packets,
        counters,
        json_events,
        TUN_WRITE_ENQUEUE_DEADLINE,
    )
    .await
}

async fn enqueue_reordered_return_packets_with_deadline(
    tun_write_tx: &TunWriterHandle,
    packets: Vec<ReorderedPacket>,
    counters: &mut TunnelCounters,
    json_events: bool,
    enqueue_deadline: Duration,
) -> Result<()> {
    if packets.is_empty() {
        return Ok(());
    }
    let packet_count = packets.len();
    let payload_bytes = packets
        .iter()
        .map(|packet| packet.payload.len())
        .sum::<usize>();
    if !tun_write_tx.try_reserve_packets(packet_count) {
        counters.tun_write_queue_drops = counters
            .tun_write_queue_drops
            .saturating_add(packet_count as u64);
        bail!(
            "XBond TUN writer cannot atomically admit {packet_count} reordered packets within its \
             {}-packet capacity; reconnecting immediately instead of stalling the control loop \
             for up to {} ms per packet",
            tun_write_tx.capacity,
            enqueue_deadline.as_millis()
        );
    }

    let batch = TunWriteBatch {
        packets,
        queued_at: Instant::now(),
    };
    match tun_write_tx.tx.try_send(batch) {
        Ok(()) => Ok(()),
        Err(mpsc::error::TrySendError::Closed(batch)) => {
            tun_write_tx.release_packets(batch.packets.len());
            bail!("XBond TUN writer queue closed unexpectedly")
        }
        Err(mpsc::error::TrySendError::Full(batch)) => {
            tun_write_tx.release_packets(batch.packets.len());
            counters.tun_write_queue_drops = counters
                .tun_write_queue_drops
                .saturating_add(batch.packets.len() as u64);
            if json_events {
                println!(
                    "{}",
                    serde_json::json!({
                        "event": "tun-writer-primary-backpressure",
                        "packets": batch.packets.len(),
                        "bytes": payload_bytes,
                        "queue_capacity": tun_write_tx.capacity,
                        "queue_depth": tun_write_tx.queue_depth(),
                        "deadline_ms": enqueue_deadline.as_millis(),
                    })
                );
            }
            bail!(
                "XBond TUN writer batch queue is full; reconnecting immediately instead of \
                 stalling the control loop or dropping unique reordered packets"
            )
        }
    }
}

async fn ensure_tunnel_sockets(
    config: &ClientConfig,
    specs_by_id: &HashMap<u16, ProbePathSpec>,
    sockets: &mut HashMap<u16, Arc<UdpSocket>>,
    senders: &mut HashMap<u16, PathSenderHandle>,
    receivers: &mut HashMap<u16, JoinHandle<()>>,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    inbound_queues: &InboundTunnelQueues,
    send_report_tx: &mpsc::Sender<PathSendReport>,
    socket_event_tx: &mpsc::Sender<PathSocketEvent>,
    key: &XBondKey,
    tun_mtu: u16,
    receiver_payload_pool: &ReceiverPayloadPool,
    json_events: bool,
) -> Result<()> {
    for (path_id, spec) in specs_by_id {
        path_runtime.entry(*path_id).or_default();

        if !interface_is_live(spec.interface_name.as_deref()) {
            remove_tunnel_path(*path_id, sockets, senders, receivers);
            if let Some(runtime) = path_runtime.get_mut(path_id) {
                runtime.last_socket_error = Some("interface is not live".to_string());
            }
            continue;
        }

        let bind_addr = match effective_bind_addr_for_spec(&config.server_addr, spec) {
            Ok(bind_addr) => bind_addr,
            Err(error) => {
                remove_tunnel_path(*path_id, sockets, senders, receivers);
                record_tunnel_send_failure(path_runtime, *path_id);
                if let Some(runtime) = path_runtime.get_mut(path_id) {
                    runtime.last_socket_error =
                        Some(format!("bind address resolution failed: {error}"));
                }
                if json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "path-bind-resolve-failed",
                            "path_id": path_id,
                            "interface": spec.interface_name,
                            "error": error.to_string(),
                        })
                    );
                }
                continue;
            }
        };
        let bind_device = spec
            .bind_device
            .as_deref()
            .or(spec.interface_name.as_deref());
        let current_ifindex = interface_ifindex(bind_device);
        let mut recreate_reason = None::<String>;

        if let Some(socket) = sockets.get(path_id) {
            if let Some(runtime) = path_runtime.get_mut(path_id) {
                if let Some(reason) = runtime.force_rebind_reason.take() {
                    let rate_limited = !runtime.force_rebind_bypass_rate_limit
                        && runtime.last_rebind_attempt.is_some_and(|last_attempt| {
                            last_attempt.elapsed() < Duration::from_secs(15)
                        });
                    runtime.force_rebind_bypass_rate_limit = false;
                    if rate_limited {
                        runtime.force_rebind_reason = Some(reason);
                    } else {
                        recreate_reason = Some(reason);
                    }
                }

                if recreate_reason.is_none() && !socket_source_matches_bind_addr(socket, &bind_addr)
                {
                    recreate_reason = Some("bind-source-changed".to_string());
                }

                if recreate_reason.is_none()
                    && runtime.socket_ifindex.is_some()
                    && runtime.socket_ifindex != current_ifindex
                {
                    recreate_reason = Some("interface-ifindex-changed".to_string());
                }
            }

            if recreate_reason.is_none() && socket_source_matches_bind_addr(socket, &bind_addr) {
                let socket_generation = path_runtime
                    .get(path_id)
                    .map(|runtime| runtime.socket_generation)
                    .unwrap_or_default();
                if !senders.contains_key(path_id) {
                    let sender = spawn_tunnel_sender(
                        *path_id,
                        socket_generation,
                        socket.clone(),
                        key.clone(),
                        config.tun_queue_capacity.max(1),
                        send_report_tx.clone(),
                    );
                    senders.insert(*path_id, sender);
                }
                if !receivers.contains_key(path_id) {
                    let receiver = spawn_tunnel_receiver(
                        *path_id,
                        socket_generation,
                        socket.clone(),
                        inbound_queues.clone(),
                        socket_event_tx.clone(),
                        key.clone(),
                        tun_mtu,
                        receiver_payload_pool.clone(),
                    );
                    receivers.insert(*path_id, receiver);
                }
                continue;
            }

            if json_events {
                let current = socket
                    .local_addr()
                    .map(|addr| addr.to_string())
                    .unwrap_or_else(|_| "unknown".to_string());
                println!(
                    "{}",
                    serde_json::json!({
                        "event": "path-socket-recreated",
                        "path_id": path_id,
                        "old_local_addr": current,
                        "new_bind_addr": bind_addr,
                        "reason": recreate_reason.as_deref().unwrap_or("socket-mismatch"),
                    })
                );
            }
            if let Some(runtime) = path_runtime.get_mut(path_id) {
                runtime.last_rebind_reason = Some(
                    recreate_reason
                        .clone()
                        .unwrap_or_else(|| "socket-mismatch".to_string()),
                );
                runtime.last_rebind_attempt = Some(Instant::now());
                runtime.last_rebind_at_micros = Some(now_micros());
                runtime.last_socket_error = None;
            }
            remove_tunnel_path(*path_id, sockets, senders, receivers);
        } else if senders.contains_key(path_id) || receivers.contains_key(path_id) {
            remove_tunnel_path(*path_id, sockets, senders, receivers);
        }

        if let Some(socket) = sockets.get(path_id) {
            let socket_generation = path_runtime
                .get(path_id)
                .map(|runtime| runtime.socket_generation)
                .unwrap_or_default();
            if !senders.contains_key(path_id) {
                let sender = spawn_tunnel_sender(
                    *path_id,
                    socket_generation,
                    socket.clone(),
                    key.clone(),
                    config.tun_queue_capacity.max(1),
                    send_report_tx.clone(),
                );
                senders.insert(*path_id, sender);
            }
            if !receivers.contains_key(path_id) {
                let receiver = spawn_tunnel_receiver(
                    *path_id,
                    socket_generation,
                    socket.clone(),
                    inbound_queues.clone(),
                    socket_event_tx.clone(),
                    key.clone(),
                    tun_mtu,
                    receiver_payload_pool.clone(),
                );
                receivers.insert(*path_id, receiver);
            }
            continue;
        }

        let socket = match create_isolated_udp_socket(
            &bind_addr,
            bind_device,
            config.udp_socket_buffer_bytes,
        ) {
            Ok((socket, _isolation)) => socket,
            Err(error) => {
                record_tunnel_send_failure(path_runtime, *path_id);
                if let Some(runtime) = path_runtime.get_mut(path_id) {
                    runtime.last_socket_error = Some(format!("socket open failed: {error}"));
                    runtime.last_rebind_error = Some(error.to_string());
                }
                if json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "path-socket-open-failed",
                            "path_id": path_id,
                            "error": error.to_string(),
                        })
                    );
                }
                continue;
            }
        };

        if let Err(error) = socket.connect(&config.server_addr).await {
            record_tunnel_send_failure(path_runtime, *path_id);
            if let Some(runtime) = path_runtime.get_mut(path_id) {
                runtime.last_socket_error = Some(format!("socket connect failed: {error}"));
                runtime.last_rebind_error = Some(error.to_string());
            }
            if json_events {
                println!(
                    "{}",
                    serde_json::json!({
                        "event": "path-socket-connect-failed",
                        "path_id": path_id,
                        "server": config.server_addr,
                        "error": error.to_string(),
                    })
                );
            }
            continue;
        }

        let socket = Arc::new(socket);
        let socket_generation = path_runtime
            .get(path_id)
            .map(|runtime| runtime.socket_generation.saturating_add(1))
            .unwrap_or(1);
        let receiver = spawn_tunnel_receiver(
            *path_id,
            socket_generation,
            socket.clone(),
            inbound_queues.clone(),
            socket_event_tx.clone(),
            key.clone(),
            tun_mtu,
            receiver_payload_pool.clone(),
        );
        let sender = spawn_tunnel_sender(
            *path_id,
            socket_generation,
            socket.clone(),
            key.clone(),
            config.tun_queue_capacity.max(1),
            send_report_tx.clone(),
        );
        if let Some(runtime) = path_runtime.get_mut(path_id) {
            runtime.socket_generation = socket_generation;
            runtime.socket_ifindex = current_ifindex;
            runtime.socket_bind_addr = Some(bind_addr.clone());
            runtime.socket_bind_device = bind_device.map(ToString::to_string);
            runtime.socket_opened_at = Some(Instant::now());
            runtime.last_socket_error = None;
            runtime.last_rebind_error = None;
            runtime.force_rebind_reason = None;
            runtime.force_rebind_bypass_rate_limit = false;
            runtime.stale_ack_ticks = 0;
            runtime.direct_probe_in_flight = false;
            if runtime.last_rebind_reason.is_some() {
                runtime.rebind_count = runtime.rebind_count.saturating_add(1);
            }
        }
        senders.insert(*path_id, sender);
        receivers.insert(*path_id, receiver);
        sockets.insert(*path_id, socket);
    }

    Ok(())
}

fn remove_tunnel_path(
    path_id: u16,
    sockets: &mut HashMap<u16, Arc<UdpSocket>>,
    senders: &mut HashMap<u16, PathSenderHandle>,
    receivers: &mut HashMap<u16, JoinHandle<()>>,
) {
    sockets.remove(&path_id);
    if let Some(sender) = senders.remove(&path_id) {
        sender.task.abort();
    }
    if let Some(receiver) = receivers.remove(&path_id) {
        receiver.abort();
    }
}

fn try_enqueue_sender_lane(
    sender: &mpsc::Sender<PathSendWork>,
    metrics: &PathSenderMetrics,
    work: PathSendWork,
) -> std::result::Result<(), mpsc::error::TrySendError<PathSendWork>> {
    let lane = work.lane;
    let queued_at = work.queued_at;
    match sender.try_reserve() {
        Ok(permit) => {
            metrics.record_enqueued_at(lane, queued_at);
            permit.send(work);
            Ok(())
        }
        Err(mpsc::error::TrySendError::Full(())) => Err(mpsc::error::TrySendError::Full(work)),
        Err(mpsc::error::TrySendError::Closed(())) => Err(mpsc::error::TrySendError::Closed(work)),
    }
}

fn enqueue_primary_work(
    path_id: u16,
    socket_generation: u64,
    sender: &PathSenderHandle,
    work: PathSendWork,
    completion_tx: &mpsc::Sender<PrimarySendCompletion>,
) -> std::io::Result<PrimaryEnqueueResult> {
    match try_enqueue_sender_lane(&sender.data_tx, &sender.metrics, work) {
        Ok(()) => Ok(PrimaryEnqueueResult::Enqueued),
        Err(mpsc::error::TrySendError::Full(work)) => {
            let data_tx = sender.data_tx.clone();
            let metrics = sender.metrics.clone();
            let completion_tx = completion_tx.clone();
            tokio::spawn(async move {
                let lane = work.lane;
                let queued_at = work.queued_at;
                let success = match work.deadline.checked_duration_since(Instant::now()) {
                    Some(remaining) => match time::timeout(remaining, data_tx.reserve()).await {
                        Ok(Ok(permit)) => {
                            metrics.record_enqueued_at(lane, queued_at);
                            permit.send(work);
                            true
                        }
                        Ok(Err(_)) => false,
                        Err(_) => {
                            metrics.record_deadline_drop(lane);
                            false
                        }
                    },
                    None => {
                        metrics.record_deadline_drop(lane);
                        false
                    }
                };
                let _ = completion_tx
                    .send(PrimarySendCompletion {
                        path_id,
                        socket_generation,
                        success,
                    })
                    .await;
            });
            Ok(PrimaryEnqueueResult::Pending)
        }
        Err(mpsc::error::TrySendError::Closed(_)) => Err(std::io::Error::new(
            ErrorKind::BrokenPipe,
            "XBond primary path sender stopped",
        )),
    }
}

fn socket_generation_is_current(
    path_runtime: &HashMap<u16, TunnelPathRuntime>,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
    path_id: u16,
    socket_generation: u64,
) -> bool {
    sockets.contains_key(&path_id)
        && path_runtime
            .get(&path_id)
            .is_some_and(|runtime| runtime.socket_generation == socket_generation)
}

fn spawn_tunnel_sender(
    path_id: u16,
    socket_generation: u64,
    socket: Arc<UdpSocket>,
    key: XBondKey,
    capacity: usize,
    report_tx: mpsc::Sender<PathSendReport>,
) -> PathSenderHandle {
    let data_capacity = capacity.max(1);
    let (data_tx, mut data_rx) = mpsc::channel::<PathSendWork>(data_capacity);
    let (control_tx, mut control_rx) = mpsc::channel::<PathSendWork>(CONTROL_LANE_QUEUE_CAPACITY);
    let (repair_tx, mut repair_rx) = mpsc::channel::<PathSendWork>(REPAIR_LANE_QUEUE_CAPACITY);
    let metrics = Arc::new(PathSenderMetrics::new(data_capacity));
    let latest_control = Arc::new(LatestControlSlot::new(metrics.clone()));
    let task_metrics = metrics.clone();
    let task_latest_control = latest_control.clone();
    let task = tokio::spawn(async move {
        let mut encoded = Vec::with_capacity(4096);
        loop {
            let work = receive_next_path_send_work(
                &mut control_rx,
                &task_latest_control,
                &mut repair_rx,
                &mut data_rx,
            )
            .await;
            let Some(work) = work else {
                break;
            };
            task_metrics.record_dequeued(&work);
            if path_send_work_deadline_expired(&work, Instant::now()) {
                task_metrics.record_sender_deadline_drop(work.lane);
                continue;
            }
            let started = Instant::now();
            let encode_result = encode_sealed_payload_into(
                &work.header,
                work.payload.as_slice(),
                &key,
                &mut encoded,
            );
            let encode_micros = started.elapsed().as_micros().min(u128::from(u64::MAX)) as u64;
            let report = match encode_result {
                Ok(()) => {
                    let encoded_bytes = encoded.len() as u64;
                    task_metrics.record_encoded(encode_micros);
                    match send_udp_with_work_deadline(&socket, &encoded, &work).await {
                        Ok(Ok(_)) => {
                            task_metrics.record_success(work.packet_kind, encoded_bytes);
                            continue;
                        }
                        Ok(Err(error)) => {
                            let raw_os_error = error.raw_os_error();
                            PathSendReport {
                                path_id,
                                socket_generation,
                                packet_kind: work.packet_kind,
                                encoded_bytes,
                                needs_rebind: socket_error_requires_rebind(&error),
                                message_too_large: is_message_too_large_error(raw_os_error),
                                raw_os_error,
                                error: Some(error.to_string()),
                                lane: work.lane,
                            }
                        }
                        Err(()) => {
                            task_metrics.record_sender_deadline_drop(work.lane);
                            continue;
                        }
                    }
                }
                Err(error) => PathSendReport {
                    path_id,
                    socket_generation,
                    packet_kind: work.packet_kind,
                    encoded_bytes: 0,
                    error: Some(error.to_string()),
                    raw_os_error: None,
                    needs_rebind: false,
                    message_too_large: false,
                    lane: work.lane,
                },
            };
            if report_tx.send(report).await.is_err() {
                break;
            }
        }
    });
    PathSenderHandle {
        socket_generation,
        data_tx,
        control_tx,
        repair_tx,
        latest_control,
        metrics,
        task,
    }
}

async fn receive_next_path_send_work(
    control_rx: &mut mpsc::Receiver<PathSendWork>,
    latest_control: &LatestControlSlot,
    repair_rx: &mut mpsc::Receiver<PathSendWork>,
    data_rx: &mut mpsc::Receiver<PathSendWork>,
) -> Option<PathSendWork> {
    tokio::select! {
        biased;
        work = control_rx.recv() => work,
        work = latest_control.recv() => Some(work),
        work = repair_rx.recv() => work,
        work = data_rx.recv() => work,
        else => None,
    }
}

fn path_send_work_deadline_expired(work: &PathSendWork, now: Instant) -> bool {
    now >= work.deadline
}

fn udp_send_budget(work: &PathSendWork, now: Instant) -> Option<Duration> {
    work.deadline
        .checked_duration_since(now)
        .map(|remaining| remaining.min(MAX_SOCKET_SEND_BLOCK))
        .filter(|remaining| !remaining.is_zero())
}

async fn send_udp_with_work_deadline(
    socket: &UdpSocket,
    encoded: &[u8],
    work: &PathSendWork,
) -> std::result::Result<std::io::Result<usize>, ()> {
    let Some(budget) = udp_send_budget(work, Instant::now()) else {
        return Err(());
    };
    time::timeout(budget, socket.send(encoded))
        .await
        .map_err(|_| ())
}

fn spawn_tunnel_receiver(
    path_id: u16,
    socket_generation: u64,
    socket: Arc<UdpSocket>,
    inbound_queues: InboundTunnelQueues,
    socket_event_tx: mpsc::Sender<PathSocketEvent>,
    key: XBondKey,
    tun_mtu: u16,
    payload_pool: ReceiverPayloadPool,
) -> JoinHandle<()> {
    tokio::spawn(async move {
        let mut buf = vec![0u8; MAX_UDP_DATAGRAM_BYTES];
        let scratch_capacity = receiver_scratch_capacity(tun_mtu);
        let mut payload = Vec::with_capacity(scratch_capacity);
        loop {
            let len = match socket.recv(&mut buf).await {
                Ok(len) => len,
                Err(error) => {
                    if socket_error_requires_rebind(&error) {
                        let _ = socket_event_tx
                            .send(PathSocketEvent {
                                path_id,
                                socket_generation,
                                reason: "udp-recv-enodev".to_string(),
                                error: Some(error.to_string()),
                            })
                            .await;
                    }
                    eprintln!("xbond path receiver for path {path_id} hit UDP recv error: {error}");
                    time::sleep(Duration::from_millis(50)).await;
                    continue;
                }
            };
            let Ok(header) = decode_sealed_payload_into(&buf[..len], &key, &mut payload) else {
                continue;
            };
            let Some(queued_payload) =
                copy_bounded_receiver_payload(header.kind, &payload, tun_mtu, &payload_pool)
            else {
                payload.clear();
                if payload.capacity() > scratch_capacity.saturating_mul(2) {
                    payload = Vec::with_capacity(scratch_capacity);
                }
                continue;
            };
            payload.clear();
            if payload.capacity() > scratch_capacity.saturating_mul(2) {
                payload = Vec::with_capacity(scratch_capacity);
            }
            let prioritized = is_prioritized_client_inbound(header.kind);
            let inbound = InboundTunnelFrame {
                path_id,
                frame: XBondFrame::new(header, queued_payload),
            };
            if prioritized {
                if inbound_queues.control_tx.send(inbound).await.is_err() {
                    break;
                }
                continue;
            }
            match inbound_queues.payload_tx.try_send(inbound) {
                Ok(()) => {}
                Err(mpsc::error::TrySendError::Full(inbound)) => {
                    inbound_queues.payload_drops.fetch_add(1, Ordering::Relaxed);
                    payload_pool.recycle(inbound.frame.payload);
                }
                Err(mpsc::error::TrySendError::Closed(inbound)) => {
                    payload_pool.recycle(inbound.frame.payload);
                    break;
                }
            }
        }
    })
}

fn receiver_scratch_capacity(tun_mtu: u16) -> usize {
    usize::from(tun_mtu)
        .saturating_add(RECEIVER_FRAME_OVERHEAD_ALLOWANCE)
        .max(2048)
}

fn maximum_queued_payload_bytes(kind: PacketKind, tun_mtu: u16) -> usize {
    match kind {
        PacketKind::Control => MAX_CONTROL_PAYLOAD_BYTES,
        PacketKind::Heartbeat => MAX_HEARTBEAT_PAYLOAD_BYTES,
        PacketKind::Data | PacketKind::Duplicate | PacketKind::Repair | PacketKind::Fec => {
            receiver_scratch_capacity(tun_mtu)
        }
    }
}

fn copy_bounded_receiver_payload(
    kind: PacketKind,
    payload: &[u8],
    tun_mtu: u16,
    payload_pool: &ReceiverPayloadPool,
) -> Option<Vec<u8>> {
    (payload.len() <= maximum_queued_payload_bytes(kind, tun_mtu)).then(|| {
        let mut queued = payload_pool.take(payload.len());
        queued.clear();
        queued.extend_from_slice(payload);
        queued
    })
}

fn refresh_sender_queue_metrics(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    senders: &HashMap<u16, PathSenderHandle>,
) {
    for runtime in path_runtime.values_mut() {
        runtime.sender_queue_depth = 0;
        runtime.sender_queue_capacity = 0;
        runtime.sender_data_oldest_age_ms = 0;
        runtime.sender_data_enqueue_drops = 0;
        runtime.sender_data_deadline_drops = 0;
        runtime.sender_control_queue_depth = 0;
        runtime.sender_control_queue_capacity = 0;
        runtime.sender_control_oldest_age_ms = 0;
        runtime.sender_control_enqueue_drops = 0;
        runtime.sender_control_deadline_drops = 0;
        runtime.sender_repair_queue_depth = 0;
        runtime.sender_repair_queue_capacity = 0;
        runtime.sender_repair_oldest_age_ms = 0;
        runtime.sender_repair_enqueue_drops = 0;
        runtime.sender_repair_deadline_drops = 0;
    }

    for (path_id, sender) in senders {
        let now = Instant::now();
        let data = sender.metrics.snapshot(PathSendLane::Data, now);
        let control = sender.metrics.snapshot(PathSendLane::Control, now);
        let repair = sender.metrics.snapshot(PathSendLane::Repair, now);
        let runtime = path_runtime.entry(*path_id).or_default();
        runtime.sender_queue_capacity = data.capacity;
        runtime.sender_queue_depth = data.depth;
        runtime.sender_data_oldest_age_ms = data.oldest_age_ms;
        runtime.sender_data_enqueue_drops = data.enqueue_drops;
        runtime.sender_data_deadline_drops = data.deadline_drops;
        runtime.sender_control_queue_depth = control.depth;
        runtime.sender_control_queue_capacity = control.capacity;
        runtime.sender_control_oldest_age_ms = control.oldest_age_ms;
        runtime.sender_control_enqueue_drops = control.enqueue_drops;
        runtime.sender_control_deadline_drops = control.deadline_drops;
        runtime.sender_repair_queue_depth = repair.depth;
        runtime.sender_repair_queue_capacity = repair.capacity;
        runtime.sender_repair_oldest_age_ms = repair.oldest_age_ms;
        runtime.sender_repair_enqueue_drops = repair.enqueue_drops;
        runtime.sender_repair_deadline_drops = repair.deadline_drops;
    }
}

fn sender_lane_metrics_json(
    path_runtime: &HashMap<u16, TunnelPathRuntime>,
) -> Vec<serde_json::Value> {
    let mut path_ids = path_runtime.keys().copied().collect::<Vec<_>>();
    path_ids.sort_unstable();
    path_ids
        .into_iter()
        .filter_map(|path_id| {
            let runtime = path_runtime.get(&path_id)?;
            Some(serde_json::json!({
                "path_id": path_id,
                "data": {
                    "depth": runtime.sender_queue_depth,
                    "capacity": runtime.sender_queue_capacity,
                    "oldest_age_ms": runtime.sender_data_oldest_age_ms,
                    "enqueue_drops": runtime.sender_data_enqueue_drops,
                    "deadline_drops": runtime.sender_data_deadline_drops,
                },
                "control": {
                    "depth": runtime.sender_control_queue_depth,
                    "capacity": runtime.sender_control_queue_capacity,
                    "oldest_age_ms": runtime.sender_control_oldest_age_ms,
                    "enqueue_drops": runtime.sender_control_enqueue_drops,
                    "deadline_drops": runtime.sender_control_deadline_drops,
                },
                "repair": {
                    "depth": runtime.sender_repair_queue_depth,
                    "capacity": runtime.sender_repair_queue_capacity,
                    "oldest_age_ms": runtime.sender_repair_oldest_age_ms,
                    "enqueue_drops": runtime.sender_repair_enqueue_drops,
                    "deadline_drops": runtime.sender_repair_deadline_drops,
                },
            }))
        })
        .collect()
}

fn sender_queue_pressure(runtime: &TunnelPathRuntime) -> f64 {
    if runtime.sender_queue_capacity == 0 {
        0.0
    } else {
        (runtime.sender_queue_depth as f64 / runtime.sender_queue_capacity as f64).clamp(0.0, 1.0)
    }
}

fn tunnel_health(
    config: &ClientConfig,
    path_runtime: &HashMap<u16, TunnelPathRuntime>,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
) -> Vec<PathHealthSnapshot> {
    config_health(config)
        .into_iter()
        .map(|mut path| {
            if !sockets.contains_key(&path.path_id) && path.interface_up {
                path.in_cooldown = true;
                path.loss_rate = 1.0;
            }

            if let Some(runtime) = path_runtime.get(&path.path_id) {
                path.outbound_throughput_bps = runtime.outbound_throughput_bps;
                path.inbound_throughput_bps = runtime.inbound_throughput_bps;
                path.duplicate_inbound_throughput_bps = runtime.duplicate_inbound_throughput_bps;
                path.raw_inbound_throughput_bps = runtime.raw_inbound_throughput_bps;
                path.throughput_bps = runtime.throughput_bps;
                path.rtt_ms = runtime.rtt_ms;
                path.jitter_ms = runtime.jitter_ms;
                path.loss_rate = runtime.loss_rate;
                path.queue_depth = u32::try_from(runtime.sender_queue_depth).unwrap_or(u32::MAX);
                path.send_failure_streak = runtime.send_failures;
                path.stale_ack_ms =
                    runtime
                        .last_ack_at
                        .or(runtime.socket_opened_at)
                        .map(|last_ack_at| {
                            Instant::now()
                                .duration_since(last_ack_at)
                                .as_millis()
                                .min(u128::from(u64::MAX)) as u64
                        });
                path.queue_pressure = sender_queue_pressure(runtime);
                let duplicate_total = runtime
                    .duplicate_useful_packets
                    .saturating_add(runtime.duplicate_late_packets);
                path.duplicate_usefulness = if duplicate_total == 0 {
                    1.0
                } else {
                    runtime.duplicate_useful_packets as f64 / duplicate_total as f64
                };
                path.throughput_collapse_score =
                    throughput_collapse_score(runtime.peak_throughput_bps, runtime.throughput_bps);
                if runtime.send_failures >= 2 {
                    path.in_cooldown = true;
                    path.loss_rate = 1.0;
                }
                path.socket_generation = runtime.socket_generation;
                path.socket_ifindex = runtime.socket_ifindex;
                path.socket_bind_addr = runtime.socket_bind_addr.clone();
                path.last_socket_error = runtime.last_socket_error.clone();
                path.last_rebind_reason = runtime.last_rebind_reason.clone();
                path.last_rebind_error = runtime.last_rebind_error.clone();
                path.last_rebind_at_micros = runtime.last_rebind_at_micros;
                path.rebind_count = runtime.rebind_count;
            }

            path
        })
        .collect()
}

fn throughput_collapse_score(peak_bps: u64, current_bps: u64) -> f64 {
    if peak_bps < 1_000_000 {
        return 0.0;
    }

    let current_ratio = current_bps as f64 / peak_bps as f64;
    (1.0 - current_ratio).clamp(0.0, 1.0)
}

fn current_process_rss_bytes() -> Option<u64> {
    #[cfg(target_os = "linux")]
    {
        let status = std::fs::read_to_string("/proc/self/status").ok()?;
        let rss_kb = status.lines().find_map(|line| {
            let value = line.strip_prefix("VmRSS:")?.trim();
            value
                .split_whitespace()
                .next()
                .and_then(|number| number.parse::<u64>().ok())
        })?;
        return Some(rss_kb.saturating_mul(1024));
    }

    #[cfg(not(target_os = "linux"))]
    {
        None
    }
}

fn drain_sender_completions(
    senders: &HashMap<u16, PathSenderHandle>,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    counters: &mut TunnelCounters,
    repair: &mut XBondRepairStatus,
) {
    for (path_id, sender) in senders {
        let completed = sender.metrics.take_completions();
        counters.encoded_frames = counters
            .encoded_frames
            .saturating_add(completed.encoded_frames);
        counters.encode_micros_total = counters
            .encode_micros_total
            .saturating_add(completed.encode_micros);
        counters.data_packets_sent = counters
            .data_packets_sent
            .saturating_add(completed.data_packets);
        counters.duplicate_packets_sent = counters
            .duplicate_packets_sent
            .saturating_add(completed.duplicate_packets);
        counters.fec_packets_sent = counters
            .fec_packets_sent
            .saturating_add(completed.fec_packets);
        repair.frames_sent = repair.frames_sent.saturating_add(completed.repair_packets);
        counters.sender_deadline_drops = counters
            .sender_deadline_drops
            .saturating_add(completed.sender_deadline_drops);
        counters.repair_lane_drops = counters
            .repair_lane_drops
            .saturating_add(completed.repair_deadline_drops);

        let successful_packets = completed
            .data_packets
            .saturating_add(completed.duplicate_packets)
            .saturating_add(completed.fec_packets)
            .saturating_add(completed.repair_packets);
        if successful_packets == 0 {
            continue;
        }

        let runtime = path_runtime.entry(*path_id).or_default();
        runtime.send_failures = 0;
        runtime.bytes_sent = runtime
            .bytes_sent
            .saturating_add(completed.successful_bytes);
    }
}

fn record_tunnel_send_failure(path_runtime: &mut HashMap<u16, TunnelPathRuntime>, path_id: u16) {
    let runtime = path_runtime.entry(path_id).or_default();
    runtime.send_failures = runtime.send_failures.saturating_add(1);
}

fn mark_path_socket_for_rebind(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    path_id: u16,
    reason: impl Into<String>,
    error: Option<String>,
    bypass_rate_limit: bool,
) {
    let runtime = path_runtime.entry(path_id).or_default();
    runtime.force_rebind_reason = Some(reason.into());
    runtime.force_rebind_bypass_rate_limit = bypass_rate_limit;
    if let Some(error) = error {
        runtime.last_socket_error = Some(error);
    }
}

fn record_ineffective_rebind(runtime: &mut TunnelPathRuntime) -> bool {
    runtime.ineffective_rebinds = runtime.ineffective_rebinds.saturating_add(1);
    runtime.ineffective_rebinds >= SILENT_BLACKHOLE_MAX_INEFFECTIVE_REBINDS
}

fn socket_error_requires_rebind(error: &std::io::Error) -> bool {
    error.raw_os_error() == Some(19)
        || error.kind() == ErrorKind::NotFound
        || error
            .to_string()
            .to_ascii_lowercase()
            .contains("no such device")
}

fn is_message_too_large_error(raw_os_error: Option<i32>) -> bool {
    matches!(raw_os_error, Some(90 | 10040))
}

fn refresh_silent_blackhole_state(
    runtime: &mut TunnelPathRuntime,
    stale_threshold: Duration,
) -> bool {
    if path_is_silent_blackhole_candidate(runtime, stale_threshold) {
        runtime.stale_ack_ticks = runtime.stale_ack_ticks.saturating_add(1);
    } else {
        runtime.stale_ack_ticks = 0;
    }
    runtime.stale_ack_ticks >= SILENT_BLACKHOLE_CONFIRM_TICKS
}

fn path_is_silent_blackhole_candidate(
    runtime: &TunnelPathRuntime,
    stale_threshold: Duration,
) -> bool {
    let ack_reference = runtime.last_ack_at.or(runtime.socket_opened_at);
    let ack_is_stale =
        ack_reference.is_some_and(|reference| reference.elapsed() >= stale_threshold);
    let recent_heartbeats_failed = runtime.health_window.len() >= 3
        && runtime
            .health_window
            .iter()
            .rev()
            .take(3)
            .all(|delivered| !delivered);

    ack_is_stale
        && recent_heartbeats_failed
        && runtime.loss_rate >= SILENT_BLACKHOLE_MIN_LOSS_RATE
        && runtime.send_failures == 0
        && runtime.force_rebind_reason.is_none()
}

fn silent_blackhole_stale_threshold(config: &ClientConfig) -> Duration {
    tunnel_heartbeat_timeout(config)
        .saturating_mul(2)
        .max(SILENT_BLACKHOLE_MIN_STALE_ACK)
}

fn silent_blackhole_probe_allowed(runtime: &TunnelPathRuntime, confirmed: bool) -> bool {
    let probe_rate_limited = runtime
        .last_direct_probe_at
        .is_some_and(|last_probe| last_probe.elapsed() < SILENT_BLACKHOLE_PROBE_COOLDOWN);
    let rebind_rate_limited = runtime
        .last_rebind_attempt
        .is_some_and(|last_rebind| last_rebind.elapsed() < SILENT_BLACKHOLE_REBIND_COOLDOWN);

    confirmed && !runtime.direct_probe_in_flight && !probe_rate_limited && !rebind_rate_limited
}

fn schedule_silent_blackhole_probes(
    config: &ClientConfig,
    specs_by_id: &HashMap<u16, ProbePathSpec>,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    result_tx: &mpsc::Sender<SilentBlackholeProbeResult>,
) {
    let stale_threshold = silent_blackhole_stale_threshold(config);
    for path_id in sockets.keys().copied().collect::<Vec<_>>() {
        let Some(spec) = specs_by_id.get(&path_id) else {
            continue;
        };
        if !interface_is_live(spec.interface_name.as_deref()) {
            continue;
        }

        let runtime = path_runtime.entry(path_id).or_default();
        let confirmed = refresh_silent_blackhole_state(runtime, stale_threshold);
        if !silent_blackhole_probe_allowed(runtime, confirmed) {
            continue;
        }

        runtime.direct_probe_in_flight = true;
        runtime.last_direct_probe_at = Some(Instant::now());
        let socket_generation = runtime.socket_generation;
        let spec = spec.clone();
        let result_tx = result_tx.clone();
        tokio::spawn(async move {
            let probe = tokio::task::spawn_blocking(move || {
                direct_interface_tcp_probe(&spec, SILENT_BLACKHOLE_DIRECT_PROBE_TIMEOUT)
            })
            .await;
            let (reachable, error) = match probe {
                Ok(Ok(())) => (true, None),
                Ok(Err(error)) => (false, Some(error.to_string())),
                Err(error) => (false, Some(format!("direct probe task failed: {error}"))),
            };
            let _ = result_tx
                .send(SilentBlackholeProbeResult {
                    path_id,
                    socket_generation,
                    reachable,
                    error,
                })
                .await;
        });
    }
}

fn direct_interface_tcp_probe(spec: &ProbePathSpec, timeout: Duration) -> Result<()> {
    let bind_addr = effective_bind_addr_for_spec(SILENT_BLACKHOLE_DIRECT_PROBE_TARGET, spec)?;
    let local_addr = bind_addr
        .parse::<SocketAddr>()
        .with_context(|| format!("failed to parse direct-probe bind address {bind_addr}"))?;
    let target_addr = SILENT_BLACKHOLE_DIRECT_PROBE_TARGET
        .parse::<SocketAddr>()
        .context("invalid silent-blackhole direct probe target")?;
    let socket = Socket::new(
        Domain::for_address(local_addr),
        Type::STREAM,
        Some(Protocol::TCP),
    )?;
    apply_bind_device(
        &socket,
        spec.bind_device
            .as_deref()
            .or(spec.interface_name.as_deref()),
    )?;
    socket
        .bind(&local_addr.into())
        .with_context(|| format!("failed to bind direct probe to {bind_addr}"))?;
    socket
        .connect_timeout(&target_addr.into(), timeout)
        .with_context(|| {
            format!("interface-bound direct probe to {SILENT_BLACKHOLE_DIRECT_PROBE_TARGET} failed")
        })?;
    Ok(())
}

const PATH_HEALTH_WINDOW: usize = 8;
const TUNNEL_HEALTH_WINDOW: usize = 20;
const HEARTBEAT_SEQUENCE_MASK: u64 = (1u64 << 48) - 1;
const AGGREGATE_HEARTBEAT_SEQUENCE_PREFIX: u64 = 0xFFFFu64 << 48;
const REPAIR_CACHE_CAPACITY: usize = 4096;
const REPAIR_CACHE_TTL_MICROS: u64 = 3_000_000;
const REPAIR_CACHE_MIN_BYTES: usize = 1 * 1024 * 1024;
const REPAIR_CACHE_MAX_BYTES: usize = 32 * 1024 * 1024;
const REPAIR_REQUEST_INTERVAL_MICROS: u64 = 75_000;
const PRE_RECOVERY_REPAIR_REQUEST_INTERVAL_MICROS: u64 = 250_000;
const PRE_RECOVERY_MIN_TUNNEL_LOSS: f64 = 0.02;
const PRE_RECOVERY_MIN_PENDING_GAP: usize = 3;
const MAX_PRE_RECOVERY_REPAIR_REQUESTS: usize = 8;
const MAX_REPAIR_REQUESTS: usize = 64;
const MAX_UDP_DATAGRAM_BYTES: usize = 65_535;
const RECEIVER_FRAME_OVERHEAD_ALLOWANCE: usize = 512;
const MAX_CONTROL_PAYLOAD_BYTES: usize = 32 * 1024;
const MAX_HEARTBEAT_PAYLOAD_BYTES: usize = 1024;
const CONTROL_LANE_QUEUE_CAPACITY: usize = 4;
const REPAIR_LANE_QUEUE_CAPACITY: usize = 128;
const CONTROL_LANE_DEADLINE: Duration = Duration::from_secs(2);
const REPAIR_LANE_DEADLINE: Duration = Duration::from_millis(750);
const DATA_LANE_DEADLINE: Duration = Duration::from_secs(5);
const MAX_SOCKET_SEND_BLOCK: Duration = Duration::from_millis(250);
const TUN_WRITE_ENQUEUE_DEADLINE: Duration = Duration::from_millis(500);
const SILENT_BLACKHOLE_CONFIRM_TICKS: u32 = 3;
const SILENT_BLACKHOLE_MIN_LOSS_RATE: f64 = 0.75;
const SILENT_BLACKHOLE_MIN_STALE_ACK: Duration = Duration::from_secs(5);
const SILENT_BLACKHOLE_PROBE_COOLDOWN: Duration = Duration::from_secs(15);
const SILENT_BLACKHOLE_REBIND_COOLDOWN: Duration = Duration::from_secs(60);
const SILENT_BLACKHOLE_MAX_INEFFECTIVE_REBINDS: u32 = 3;
const SILENT_BLACKHOLE_DIRECT_PROBE_TIMEOUT: Duration = Duration::from_millis(750);
const SILENT_BLACKHOLE_DIRECT_PROBE_TARGET: &str = "1.1.1.1:443";
const SESSION_OPEN_RETRY_INTERVAL: Duration = Duration::from_secs(1);
const SESSION_SYNCHRONIZATION_TIMEOUT: Duration = Duration::from_secs(15);
const SCHEDULE_CONTROL_RETRY_INTERVAL: Duration = Duration::from_secs(1);
const SCHEDULE_SYNCHRONIZATION_TIMEOUT: Duration = Duration::from_secs(15);

async fn send_session_open(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    senders: &HashMap<u16, PathSenderHandle>,
    session_id: u64,
    request_nonce: SessionHandshakeNonce,
    control_sequence: &mut u64,
    counters: &mut TunnelCounters,
    json_events: bool,
    trace_packets: bool,
) -> Result<()> {
    *control_sequence = control_sequence.saturating_add(1);
    let sequence = *control_sequence;
    let payload = Arc::new(serde_json::to_vec(&XBondControlMessage::SessionOpen {
        session_id,
        request_nonce,
    })?);

    for (path_id, sender) in senders {
        let work = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(
                PacketKind::Control,
                session_id,
                sequence,
                now_micros(),
                *path_id,
            ),
            payload.clone(),
        );
        enqueue_critical_control_work(
            sender,
            work,
            path_runtime,
            *path_id,
            counters,
            "session-open",
            json_events,
        )
        .await?;
    }

    if json_events && trace_packets {
        println!(
            "{}",
            serde_json::json!({
                "event": "session-open-sent",
                "session_id": session_id,
                "sequence": sequence,
                "path_count": senders.len(),
            })
        );
    }

    Ok(())
}

async fn send_session_proof(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    senders: &HashMap<u16, PathSenderHandle>,
    session_id: u64,
    request_nonce: SessionHandshakeNonce,
    challenge: SessionHandshakeNonce,
    control_sequence: &mut u64,
    counters: &mut TunnelCounters,
    json_events: bool,
) -> Result<()> {
    *control_sequence = control_sequence.saturating_add(1);
    let sequence = *control_sequence;
    let payload = Arc::new(serde_json::to_vec(&XBondControlMessage::SessionProof {
        session_id,
        request_nonce,
        challenge,
    })?);

    for (path_id, sender) in senders {
        let work = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(
                PacketKind::Control,
                session_id,
                sequence,
                now_micros(),
                *path_id,
            ),
            payload.clone(),
        );
        enqueue_critical_control_work(
            sender,
            work,
            path_runtime,
            *path_id,
            counters,
            "session-proof",
            json_events,
        )
        .await?;
    }

    Ok(())
}

fn send_tunnel_schedule_control(
    config: &ClientConfig,
    schedule: &SchedulePlan,
    redundancy_policy: RedundancyPolicy,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
    senders: &HashMap<u16, PathSenderHandle>,
    session_id: u64,
    control_sequence: &mut u64,
    schedule_generation: u64,
    recovery_active: bool,
    counters: &mut TunnelCounters,
    json_events: bool,
    trace_packets: bool,
) -> Result<()> {
    if sockets.is_empty() {
        return Ok(());
    }

    *control_sequence = control_sequence.saturating_add(1);
    let sequence = *control_sequence;
    let payload = Arc::new(serde_json::to_vec(&ScheduleControlMessage {
        schedule_generation,
        schedule: schedule.clone(),
        redundancy_policy,
        policy_config: RedundancyPolicyConfig {
            interactive_packet_threshold_bytes: config.interactive_packet_threshold_bytes,
            duplicate_loss_threshold: config.duplicate_loss_threshold,
            backup_loss_disable_threshold: config.backup_loss_disable_threshold,
        },
        paths: tunnel_health(config, path_runtime, sockets),
        recovery_active,
    })?);

    for (path_id, sender) in senders {
        let work = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(
                PacketKind::Control,
                session_id,
                sequence,
                now_micros(),
                *path_id,
            ),
            payload.clone(),
        );
        enqueue_latest_control_work(
            sender,
            work,
            *path_id,
            counters,
            "schedule-control",
            json_events,
        );
    }

    if json_events && trace_packets {
        println!(
            "{}",
            serde_json::json!({
                "event": "schedule-control-sent",
                "sequence": sequence,
                "schedule_generation": schedule_generation,
                "schedule": schedule,
            })
        );
    }

    Ok(())
}

fn parse_control_message(frame: &XBondFrame) -> Option<XBondControlMessage> {
    if frame.header.kind != PacketKind::Control {
        return None;
    }

    serde_json::from_slice::<XBondControlMessage>(&frame.payload).ok()
}

fn update_return_reorder_hold(
    return_reorder: &mut PacketReorderBuffer,
    normal_hold_ms: u64,
    recovery_status: &RecoveryStatus,
    server_recovery_status: &XBondServerRecoveryStatus,
) {
    let normal_hold_micros = normal_hold_ms.max(1).saturating_mul(1_000);
    let desired_hold_micros = if recovery_status.active {
        let server_hold_ms = server_recovery_status
            .reported
            .then_some(server_recovery_status.ingress_reorder.current_hold_ms)
            .filter(|hold_ms| *hold_ms > 0)
            .unwrap_or(150);
        server_hold_ms.max(150).min(500).saturating_mul(1_000)
    } else {
        normal_hold_micros
    };

    if return_reorder.hold_micros() != desired_hold_micros {
        return_reorder.set_hold_micros(desired_hold_micros);
    }
}

fn send_repair_requests_for_return_gaps(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    senders: &HashMap<u16, PathSenderHandle>,
    session_id: u64,
    control_sequence: &mut u64,
    return_reorder: &mut PacketReorderBuffer,
    repair: &mut XBondRepairStatus,
    recovery_active: bool,
    tunnel_loss_rate: Option<f64>,
    counters: &mut TunnelCounters,
    json_events: bool,
) -> Result<()> {
    if senders.is_empty() {
        return Ok(());
    }

    let pre_recovery = !recovery_active
        && pre_recovery_gap_repair_allowed(tunnel_loss_rate, return_reorder.pending_len());
    if !recovery_active && !pre_recovery {
        return Ok(());
    }
    let interval = if recovery_active {
        REPAIR_REQUEST_INTERVAL_MICROS
    } else {
        PRE_RECOVERY_REPAIR_REQUEST_INTERVAL_MICROS
    };
    let maximum = if recovery_active {
        MAX_REPAIR_REQUESTS
    } else {
        MAX_PRE_RECOVERY_REPAIR_REQUESTS
    };
    let sequences = return_reorder.repair_requests(monotonic_micros(), interval, maximum);
    if sequences.is_empty() {
        return Ok(());
    }

    *control_sequence = control_sequence.saturating_add(1);
    let sequence = *control_sequence;
    let payload = Arc::new(serde_json::to_vec(&XBondControlMessage::RepairRequest {
        sequences: sequences.clone(),
    })?);
    repair.requests_sent = repair.requests_sent.saturating_add(sequences.len() as u64);

    for (path_id, sender) in senders {
        let work = PathSendWork::repair_control(
            XBondHeader::new(
                PacketKind::Control,
                session_id,
                sequence,
                now_micros(),
                *path_id,
            ),
            payload.clone(),
        );
        match try_enqueue_sender_lane(&sender.repair_tx, &sender.metrics, work) {
            Ok(()) => {}
            Err(mpsc::error::TrySendError::Full(_)) => {
                sender.metrics.record_enqueue_drop(PathSendLane::Repair);
                repair.queue_drops = repair.queue_drops.saturating_add(1);
                counters.repair_lane_drops = counters.repair_lane_drops.saturating_add(1);
                if json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "repair-request-lane-saturated",
                            "path_id": path_id,
                            "sequence": sequence,
                            "repair_sequences": sequences,
                            "pre_recovery": pre_recovery,
                        })
                    );
                }
            }
            Err(mpsc::error::TrySendError::Closed(_)) => {
                record_tunnel_send_failure(path_runtime, *path_id);
            }
        }
    }

    Ok(())
}

fn enqueue_latest_control_work(
    sender: &PathSenderHandle,
    work: PathSendWork,
    path_id: u16,
    counters: &mut TunnelCounters,
    operation: &str,
    json_events: bool,
) {
    let replaced = sender.latest_control.replace(work);
    if replaced {
        counters.control_lane_coalesced = counters.control_lane_coalesced.saturating_add(1);
        if json_events {
            println!(
                "{}",
                serde_json::json!({
                    "event": "control-lane-latest-replaced",
                    "path_id": path_id,
                    "operation": operation,
                    "replacements": counters.control_lane_coalesced,
                })
            );
        }
    }
}

async fn enqueue_critical_control_work(
    sender: &PathSenderHandle,
    work: PathSendWork,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    path_id: u16,
    counters: &mut TunnelCounters,
    operation: &str,
    json_events: bool,
) -> Result<()> {
    match try_enqueue_sender_lane(&sender.control_tx, &sender.metrics, work) {
        Ok(()) => Ok(()),
        Err(mpsc::error::TrySendError::Full(work)) => {
            let queued_at = work.queued_at;
            if let Some(remaining) = work.deadline.checked_duration_since(Instant::now()) {
                match time::timeout(remaining, sender.control_tx.reserve()).await {
                    Ok(Ok(permit)) => {
                        sender
                            .metrics
                            .record_enqueued_at(PathSendLane::Control, queued_at);
                        permit.send(work);
                        if json_events {
                            println!(
                                "{}",
                                serde_json::json!({
                                    "event": "critical-control-enqueued-after-backpressure",
                                    "path_id": path_id,
                                    "operation": operation,
                                    "waited_ms": queued_at.elapsed().as_millis(),
                                })
                            );
                        }
                        return Ok(());
                    }
                    Ok(Err(_)) => {
                        record_tunnel_send_failure(path_runtime, path_id);
                        counters.control_lane_drops = counters.control_lane_drops.saturating_add(1);
                        bail!("XBond control lane for path {path_id} stopped");
                    }
                    Err(_) => {}
                }
            }

            sender.metrics.record_deadline_drop(PathSendLane::Control);
            counters.control_lane_drops = counters.control_lane_drops.saturating_add(1);
            bail!(
                "XBond critical control lane for path {path_id} did not recover before the \
                 {operation} deadline; reconnecting rather than dropping authoritative control"
            )
        }
        Err(mpsc::error::TrySendError::Closed(_)) => {
            record_tunnel_send_failure(path_runtime, path_id);
            bail!("XBond control lane for path {path_id} stopped")
        }
    }
}

fn pre_recovery_gap_repair_allowed(tunnel_loss_rate: Option<f64>, pending_depth: usize) -> bool {
    pending_depth >= PRE_RECOVERY_MIN_PENDING_GAP
        && tunnel_loss_rate.is_some_and(|loss| loss >= PRE_RECOVERY_MIN_TUNNEL_LOSS)
}

fn update_repair_cache_budget(
    resend_cache: &mut ResendCache,
    observed_bits_per_second: u64,
    now_micros: u64,
    counters: &mut TunnelCounters,
) {
    let recommended_cache_bytes = recommended_repair_cache_bytes(
        observed_bits_per_second,
        REPAIR_CACHE_TTL_MICROS,
        REPAIR_CACHE_MIN_BYTES,
        REPAIR_CACHE_MAX_BYTES,
    );
    if recommended_cache_bytes == resend_cache.byte_capacity() {
        return;
    }

    let entries_before = resend_cache.len();
    let bytes_before = resend_cache.bytes_len();
    resend_cache.set_byte_capacity(recommended_cache_bytes, now_micros);
    counters.repair_cache_evictions = counters
        .repair_cache_evictions
        .saturating_add(entries_before.saturating_sub(resend_cache.len()) as u64);
    counters.repair_cache_evicted_bytes = counters
        .repair_cache_evicted_bytes
        .saturating_add(bytes_before.saturating_sub(resend_cache.bytes_len()) as u64);
}

fn send_repair_frames_from_client_cache(
    config: &ClientConfig,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    senders: &HashMap<u16, PathSenderHandle>,
    transmission_plans: &PacketTransmissionPlans,
    resend_cache: &mut ResendCache,
    session_id: u64,
    sequences: &[u64],
    repair: &mut XBondRepairStatus,
    counters: &mut TunnelCounters,
) -> Result<()> {
    for sequence in sequences.iter().copied().take(MAX_REPAIR_REQUESTS) {
        let now = monotonic_micros();
        let Some(payload) = resend_cache.get(session_id, sequence, now) else {
            repair.cache_misses = repair.cache_misses.saturating_add(1);
            continue;
        };

        let targets = repair_targets_from_client_plan(config, transmission_plans, payload.len());
        if targets.is_empty() {
            repair.cache_misses = repair.cache_misses.saturating_add(1);
            continue;
        }

        let send_micros = now_micros();
        for path_id in targets {
            let Some(sender) = senders.get(&path_id) else {
                repair.cache_misses = repair.cache_misses.saturating_add(1);
                continue;
            };
            let header = XBondHeader::new(
                PacketKind::Repair,
                session_id,
                sequence,
                send_micros,
                path_id,
            );
            let work = PathSendWork::repair(header, payload.clone());
            let enqueue_result =
                match try_enqueue_sender_lane(&sender.repair_tx, &sender.metrics, work) {
                    Ok(()) => Ok(()),
                    Err(mpsc::error::TrySendError::Full(_)) => {
                        sender.metrics.record_enqueue_drop(PathSendLane::Repair);
                        repair.queue_drops = repair.queue_drops.saturating_add(1);
                        counters.repair_lane_drops = counters.repair_lane_drops.saturating_add(1);
                        continue;
                    }
                    Err(mpsc::error::TrySendError::Closed(_)) => Err(std::io::Error::new(
                        ErrorKind::BrokenPipe,
                        "XBond repair path sender stopped",
                    )),
                };
            if let Err(_error) = enqueue_result {
                record_tunnel_send_failure(path_runtime, path_id);
            }
        }
    }

    repair.cache_entries = resend_cache.len();
    Ok(())
}

fn repair_targets_from_client_plan(
    config: &ClientConfig,
    transmission_plans: &PacketTransmissionPlans,
    packet_len: usize,
) -> Vec<u16> {
    let mut targets = transmission_plans
        .for_packet_len(packet_len, config.interactive_packet_threshold_bytes)
        .iter()
        .filter(|transmission| {
            matches!(
                transmission.packet_kind,
                PacketKind::Data | PacketKind::Duplicate
            )
        })
        .map(|transmission| transmission.path_id)
        .collect::<Vec<_>>();
    targets.dedup();
    targets
}

fn send_tunnel_heartbeats(
    config: &ClientConfig,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    senders: &HashMap<u16, PathSenderHandle>,
    session_id: u64,
    counters: &mut TunnelCounters,
    json_events: bool,
) -> Result<()> {
    let timeout = tunnel_heartbeat_timeout(config);
    let path_ids = senders.keys().copied().collect::<Vec<_>>();
    for path_id in path_ids {
        let Some(sender) = senders.get(&path_id) else {
            continue;
        };

        {
            let runtime = path_runtime.entry(path_id).or_default();
            expire_tunnel_heartbeats(runtime, timeout);
            if runtime.pending_heartbeats.len() >= 3 {
                continue;
            }
        }

        let sequence = {
            let runtime = path_runtime.entry(path_id).or_default();
            runtime.health_sequence =
                (runtime.health_sequence.saturating_add(1)) & HEARTBEAT_SEQUENCE_MASK;
            ((path_id as u64) << 48) | runtime.health_sequence
        };

        let work = PathSendWork::control(
            PacketKind::Heartbeat,
            XBondHeader::new(
                PacketKind::Heartbeat,
                session_id,
                sequence,
                now_micros(),
                path_id,
            ),
            Arc::new(b"health".to_vec()),
        );
        match try_enqueue_sender_lane(&sender.control_tx, &sender.metrics, work) {
            Ok(()) => {
                let runtime = path_runtime.entry(path_id).or_default();
                runtime.pending_heartbeats.insert(sequence, Instant::now());
            }
            Err(mpsc::error::TrySendError::Full(_)) => {
                sender.metrics.record_enqueue_drop(PathSendLane::Control);
                counters.control_lane_drops = counters.control_lane_drops.saturating_add(1);
            }
            Err(mpsc::error::TrySendError::Closed(_)) => {
                record_tunnel_send_failure(path_runtime, path_id);
                if let Some(runtime) = path_runtime.get_mut(&path_id) {
                    record_tunnel_health_sample(runtime, false, None);
                }
                if json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "health-heartbeat-send-failed",
                            "path_id": path_id,
                            "error": "control lane closed",
                        })
                    );
                }
            }
        }
    }

    Ok(())
}

fn send_tunnel_aggregate_heartbeat(
    config: &ClientConfig,
    aggregate_health: &mut TunnelAggregateHealthRuntime,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
    senders: &HashMap<u16, PathSenderHandle>,
    transmission_plans: &PacketTransmissionPlans,
    session_id: u64,
    counters: &mut TunnelCounters,
    json_events: bool,
) -> Result<()> {
    let timeout = tunnel_heartbeat_timeout(config);
    expire_aggregate_tunnel_heartbeats(aggregate_health, timeout);

    if aggregate_health.pending_heartbeats.len() >= 3 {
        return Ok(());
    }

    let targets = aggregate_heartbeat_targets(transmission_plans, sockets);
    if targets.is_empty() {
        return Ok(());
    }

    aggregate_health.health_sequence =
        (aggregate_health.health_sequence.saturating_add(1)) & HEARTBEAT_SEQUENCE_MASK;
    let sequence = AGGREGATE_HEARTBEAT_SEQUENCE_PREFIX | aggregate_health.health_sequence;
    let send_micros = now_micros();
    let mut sent_any = false;

    for path_id in targets {
        let Some(sender) = senders.get(&path_id) else {
            continue;
        };
        let work = PathSendWork::control(
            PacketKind::Heartbeat,
            XBondHeader::new(
                PacketKind::Heartbeat,
                session_id,
                sequence,
                send_micros,
                path_id,
            ),
            Arc::new(b"tunnel-health".to_vec()),
        );
        match try_enqueue_sender_lane(&sender.control_tx, &sender.metrics, work) {
            Ok(_) => {
                sent_any = true;
            }
            Err(mpsc::error::TrySendError::Full(_)) => {
                sender.metrics.record_enqueue_drop(PathSendLane::Control);
                counters.control_lane_drops = counters.control_lane_drops.saturating_add(1);
            }
            Err(mpsc::error::TrySendError::Closed(_)) => {
                record_tunnel_send_failure(path_runtime, path_id);
                if json_events {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "tunnel-health-heartbeat-send-failed",
                            "path_id": path_id,
                            "error": "control lane closed",
                        })
                    );
                }
            }
        }
    }

    if sent_any {
        aggregate_health
            .pending_heartbeats
            .insert(sequence, Instant::now());
    } else {
        record_aggregate_tunnel_health_sample(aggregate_health, false, None);
    }

    Ok(())
}

fn aggregate_heartbeat_targets(
    transmission_plans: &PacketTransmissionPlans,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
) -> Vec<u16> {
    let mut targets = Vec::new();
    for transmission in &transmission_plans.small {
        if !matches!(
            transmission.packet_kind,
            PacketKind::Data | PacketKind::Duplicate
        ) {
            continue;
        }

        if sockets.contains_key(&transmission.path_id) && !targets.contains(&transmission.path_id) {
            targets.push(transmission.path_id);
        }
    }

    targets
}

fn tunnel_heartbeat_timeout(config: &ClientConfig) -> Duration {
    Duration::from_millis(config.realtime_deadline_ms.saturating_mul(3).max(1_500))
}

fn expire_tunnel_heartbeats(runtime: &mut TunnelPathRuntime, timeout: Duration) {
    let now = Instant::now();
    let expired = runtime
        .pending_heartbeats
        .iter()
        .filter_map(|(sequence, sent_at)| {
            (now.duration_since(*sent_at) >= timeout).then_some(*sequence)
        })
        .collect::<Vec<_>>();

    for sequence in expired {
        if runtime.pending_heartbeats.remove(&sequence).is_some() {
            record_tunnel_health_sample(runtime, false, None);
        }
    }
}

fn expire_aggregate_tunnel_heartbeats(
    aggregate_health: &mut TunnelAggregateHealthRuntime,
    timeout: Duration,
) {
    let now = Instant::now();
    let expired = aggregate_health
        .pending_heartbeats
        .iter()
        .filter_map(|(sequence, sent_at)| {
            (now.duration_since(*sent_at) >= timeout).then_some(*sequence)
        })
        .collect::<Vec<_>>();

    for sequence in expired {
        if aggregate_health
            .pending_heartbeats
            .remove(&sequence)
            .is_some()
        {
            record_aggregate_tunnel_health_sample(aggregate_health, false, None);
        }
    }
}

fn record_aggregate_tunnel_heartbeat_ack(
    aggregate_health: &mut TunnelAggregateHealthRuntime,
    frame: &XBondFrame,
) -> bool {
    if !is_aggregate_heartbeat_sequence(frame.header.sequence) {
        return false;
    }

    let Some(sent_at) = aggregate_health
        .pending_heartbeats
        .remove(&frame.header.sequence)
    else {
        return false;
    };

    let rtt_ms = sent_at.elapsed().as_secs_f64() * 1_000.0;
    aggregate_health.last_success_at = Some(Instant::now());
    record_aggregate_tunnel_health_sample(aggregate_health, true, Some(rtt_ms));
    true
}

fn is_aggregate_heartbeat_sequence(sequence: u64) -> bool {
    sequence & !HEARTBEAT_SEQUENCE_MASK == AGGREGATE_HEARTBEAT_SEQUENCE_PREFIX
}

fn record_tunnel_heartbeat_ack(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    path_id: u16,
    frame: &XBondFrame,
) {
    let runtime = path_runtime.entry(path_id).or_default();
    let Some(sent_at) = runtime.pending_heartbeats.remove(&frame.header.sequence) else {
        return;
    };

    let rtt_ms = sent_at.elapsed().as_secs_f64() * 1_000.0;
    runtime.send_failures = 0;
    runtime.last_ack_at = Some(Instant::now());
    runtime.stale_ack_ticks = 0;
    runtime.ineffective_rebinds = 0;
    record_tunnel_health_sample(runtime, true, Some(rtt_ms));
}

fn record_aggregate_tunnel_health_sample(
    aggregate_health: &mut TunnelAggregateHealthRuntime,
    delivered: bool,
    rtt_ms: Option<f64>,
) {
    if aggregate_health.health_window.len() == TUNNEL_HEALTH_WINDOW {
        aggregate_health.health_window.pop_front();
    }
    aggregate_health.health_window.push_back(delivered);

    if let Some(rtt_ms) = rtt_ms.filter(|value| value.is_finite()) {
        if aggregate_health.rtt_samples_ms.len() == TUNNEL_HEALTH_WINDOW {
            aggregate_health.rtt_samples_ms.pop_front();
        }
        aggregate_health.rtt_samples_ms.push_back(rtt_ms);
    }

    refresh_aggregate_tunnel_health(aggregate_health);
}

fn refresh_aggregate_tunnel_health(aggregate_health: &mut TunnelAggregateHealthRuntime) {
    if aggregate_health.health_window.is_empty() {
        aggregate_health.loss_rate = None;
        aggregate_health.success_rate = None;
    } else {
        let delivered = aggregate_health
            .health_window
            .iter()
            .filter(|delivered| **delivered)
            .count();
        aggregate_health.success_rate =
            Some(delivered as f64 / aggregate_health.health_window.len() as f64);
        aggregate_health.loss_rate = Some(1.0 - aggregate_health.success_rate.unwrap_or_default());
    }

    if aggregate_health.rtt_samples_ms.is_empty() {
        aggregate_health.rtt_ms = None;
        aggregate_health.jitter_ms = None;
        return;
    }

    let average = aggregate_health.rtt_samples_ms.iter().sum::<f64>()
        / aggregate_health.rtt_samples_ms.len() as f64;
    aggregate_health.rtt_ms = Some(average);

    aggregate_health.jitter_ms = if aggregate_health.rtt_samples_ms.len() < 2 {
        Some(0.0)
    } else {
        let deltas = aggregate_health
            .rtt_samples_ms
            .iter()
            .zip(aggregate_health.rtt_samples_ms.iter().skip(1))
            .map(|(left, right)| (right - left).abs())
            .collect::<Vec<_>>();
        Some(deltas.iter().sum::<f64>() / deltas.len() as f64)
    };
}

fn classify_tunnel_health(rtt_ms: Option<f64>, loss_rate: Option<f64>) -> (String, String) {
    if rtt_ms.is_none() && loss_rate.is_none() {
        return (
            "unknown".to_string(),
            "Tunnel health has not collected enough samples yet.".to_string(),
        );
    }

    let rtt = rtt_ms.unwrap_or_default();
    let loss = loss_rate.unwrap_or_default().clamp(0.0, 1.0);
    let loss_percent = loss * 100.0;

    if loss >= 0.25 || rtt >= 300.0 {
        return (
            "critical".to_string(),
            format!("Tunnel heartbeat is critical: {loss_percent:.1}% loss, {rtt:.0} ms RTT."),
        );
    }

    if loss >= 0.10 || rtt >= 180.0 {
        return (
            "poor".to_string(),
            format!("Tunnel heartbeat is poor: {loss_percent:.1}% loss, {rtt:.0} ms RTT."),
        );
    }

    if loss >= 0.02 || rtt >= 120.0 {
        return (
            "fair".to_string(),
            format!("Tunnel heartbeat is fair: {loss_percent:.1}% loss, {rtt:.0} ms RTT."),
        );
    }

    (
        "good".to_string(),
        format!("Tunnel heartbeat is healthy: {loss_percent:.1}% loss, {rtt:.0} ms RTT."),
    )
}

fn record_tunnel_health_sample(
    runtime: &mut TunnelPathRuntime,
    delivered: bool,
    rtt_ms: Option<f64>,
) {
    if runtime.health_window.len() == PATH_HEALTH_WINDOW {
        runtime.health_window.pop_front();
    }
    runtime.health_window.push_back(delivered);

    if let Some(rtt_ms) = rtt_ms.filter(|value| value.is_finite()) {
        if runtime.rtt_samples_ms.len() == PATH_HEALTH_WINDOW {
            runtime.rtt_samples_ms.pop_front();
        }
        runtime.rtt_samples_ms.push_back(rtt_ms);
    }

    refresh_tunnel_health(runtime);
}

fn refresh_tunnel_health(runtime: &mut TunnelPathRuntime) {
    runtime.loss_rate = if runtime.health_window.is_empty() {
        0.0
    } else {
        let missed = runtime
            .health_window
            .iter()
            .filter(|delivered| !**delivered)
            .count();
        missed as f64 / runtime.health_window.len() as f64
    };

    if runtime.rtt_samples_ms.is_empty() {
        runtime.rtt_ms = None;
        runtime.jitter_ms = None;
        return;
    }

    let average = runtime.rtt_samples_ms.iter().sum::<f64>() / runtime.rtt_samples_ms.len() as f64;
    runtime.rtt_ms = Some(average);

    runtime.jitter_ms = if runtime.rtt_samples_ms.len() < 2 {
        Some(0.0)
    } else {
        let deltas = runtime
            .rtt_samples_ms
            .iter()
            .zip(runtime.rtt_samples_ms.iter().skip(1))
            .map(|(left, right)| (right - left).abs())
            .collect::<Vec<_>>();
        Some(deltas.iter().sum::<f64>() / deltas.len() as f64)
    };
}

fn update_tunnel_throughput(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    counters: &mut TunnelCounters,
    last_sample: &mut Instant,
) {
    let elapsed = last_sample.elapsed().as_secs_f64().max(0.001);
    for runtime in path_runtime.values_mut() {
        let outbound_delta = runtime.bytes_sent.saturating_sub(runtime.last_bytes_sent);
        let inbound_delta = runtime
            .bytes_received
            .saturating_sub(runtime.last_bytes_received);
        let duplicate_inbound_delta = runtime
            .duplicate_bytes_received
            .saturating_sub(runtime.last_duplicate_bytes_received);
        runtime.outbound_throughput_bps = ((outbound_delta as f64 * 8.0) / elapsed) as u64;
        runtime.inbound_throughput_bps = ((inbound_delta as f64 * 8.0) / elapsed) as u64;
        runtime.duplicate_inbound_throughput_bps =
            ((duplicate_inbound_delta as f64 * 8.0) / elapsed) as u64;
        runtime.raw_inbound_throughput_bps = runtime
            .inbound_throughput_bps
            .saturating_add(runtime.duplicate_inbound_throughput_bps);
        runtime.throughput_bps = runtime
            .outbound_throughput_bps
            .saturating_add(runtime.raw_inbound_throughput_bps);
        runtime.peak_throughput_bps = runtime.peak_throughput_bps.max(runtime.throughput_bps);
        runtime.last_bytes_sent = runtime.bytes_sent;
        runtime.last_bytes_received = runtime.bytes_received;
        runtime.last_duplicate_bytes_received = runtime.duplicate_bytes_received;
    }
    let outbound_delta = counters
        .data_bytes_sent
        .saturating_sub(counters.last_data_bytes_sent);
    let inbound_delta = counters
        .data_bytes_received
        .saturating_sub(counters.last_data_bytes_received);
    counters.outbound_throughput_bps = ((outbound_delta as f64 * 8.0) / elapsed) as u64;
    counters.inbound_throughput_bps = ((inbound_delta as f64 * 8.0) / elapsed) as u64;
    counters.last_data_bytes_sent = counters.data_bytes_sent;
    counters.last_data_bytes_received = counters.data_bytes_received;
    *last_sample = Instant::now();
}

fn write_tunnel_runtime_status(
    config: &ClientConfig,
    tun: &XBondTun,
    tun_mtu: u16,
    counters: &TunnelCounters,
    roles: &[xbond_core::ScoredPath],
    path_runtime: &HashMap<u16, TunnelPathRuntime>,
    schedule: &SchedulePlan,
    effective_mode: ScheduleMode,
    effective_policy: RedundancyPolicy,
    recovery_status: &RecoveryStatus,
    aggregate_health: &TunnelAggregateHealthRuntime,
    active_override: Option<&ActiveScheduleOverride>,
    schedule_change_count: u64,
    schedule_generation: u64,
    return_reorder: &PacketReorderBuffer,
    repair: &XBondRepairStatus,
    server_recovery_status: &XBondServerRecoveryStatus,
    tun_queue_depth: usize,
    inbound_queue_depth: usize,
) -> Result<()> {
    let paths = roles
        .iter()
        .cloned()
        .map(|scored_path| scored_path.path)
        .collect();
    let tunnel_health = aggregate_health.to_status();

    write_runtime_status_with_sender_lanes(
        config,
        XBondRuntimeStatus {
            running: true,
            mode: effective_mode,
            redundancy_policy: effective_policy,
            server_addr: config.server_addr.clone(),
            tunnel: XBondTunnelStatus {
                state: "running".to_string(),
                device_name: Some(tun.name().to_string()),
                mtu: Some(tun_mtu),
                message: "Bidirectional XBond tunnel is open.".to_string(),
                rtt_ms: tunnel_health.rtt_ms,
                jitter_ms: tunnel_health.jitter_ms,
                loss_rate: tunnel_health.loss_rate,
                success_rate: tunnel_health.success_rate,
                pending_probes: tunnel_health.pending_probes,
                last_success_age_ms: tunnel_health.last_success_age_ms,
                status: tunnel_health.status,
                reason: tunnel_health.reason,
            },
            anchor_path_id: schedule.anchor_path_id,
            schedule: Some(schedule.clone()),
            schedule_generation,
            paths,
            data_packets_sent: counters.data_packets_sent,
            duplicate_packets_sent: counters.duplicate_packets_sent,
            duplicate_packets_dropped: counters.duplicate_packets_dropped,
            data_packets_received: counters.data_packets_received,
            data_bytes_sent: counters.data_bytes_sent,
            data_bytes_received: counters.data_bytes_received,
            outbound_throughput_bps: counters.outbound_throughput_bps,
            inbound_throughput_bps: counters.inbound_throughput_bps,
            fec_packets_sent: counters.fec_packets_sent,
            fec_packets_skipped: counters.fec_packets_skipped,
            fec: fec_status_for_mode(effective_mode, XBondFecStatus::default()),
            late_packets_dropped: counters.late_packets_dropped,
            reorder: XBondReorderStatus {
                return_path: return_reorder.stats(),
            },
            repair: repair.clone(),
            server_recovery: server_recovery_status.clone(),
            server_health: server_recovery_status.server_health.clone(),
            process: XBondProcessStatus {
                process_cpu_percent: None,
                rss_bytes: current_process_rss_bytes(),
                encode_micros_total: counters.encode_micros_total,
                decode_micros_total: counters.decode_micros_total,
                encoded_frames: counters.encoded_frames,
                decoded_frames: counters.decoded_frames,
                tun_queue_capacity: config.tun_queue_capacity.max(1),
                inbound_queue_capacity: config.inbound_queue_capacity.max(1),
                tun_queue_depth,
                inbound_queue_depth,
                udp_socket_buffer_bytes: config.udp_socket_buffer_bytes,
                tun_queue_drops: counters.tun_queue_drops,
                inbound_queue_drops: counters.inbound_queue_drops,
                duplicate_send_skips: counters.duplicate_send_skips,
                fec_send_skips: counters.fec_send_skips,
                tun_write_packets: counters.tun_write_packets,
                tun_write_queue_micros_total: counters.tun_write_queue_micros_total,
                tun_write_micros_total: counters.tun_write_micros_total,
            },
            recovery: recovery_status.clone(),
            diagnostic_override: active_override.map(ActiveScheduleOverride::status),
            schedule_change_count,
            message: Some("XBond tunnel is running.".to_string()),
            ..XBondRuntimeStatus::default()
        },
        path_runtime,
    )
}

fn is_data_like(kind: PacketKind) -> bool {
    matches!(
        kind,
        PacketKind::Data | PacketKind::Duplicate | PacketKind::Repair
    )
}

async fn prepare_probe_path(
    spec: ProbePathSpec,
    server: &str,
    target_ip: Option<IpAddr>,
) -> Result<PreparedProbePath> {
    let bind_addr = match effective_bind_addr_for_spec(server, &spec) {
        Ok(bind_addr) => bind_addr,
        Err(error) => {
            let bind_label = configured_bind_label(&spec);
            let route_verification = RouteVerification::failed("bind-resolve", error.to_string());
            let stats = ProbePathStats::new(
                spec.path_id,
                Some(spec.name.clone()),
                spec.interface_name.clone(),
                bind_label,
                None,
                route_verification,
            );
            return Ok(PreparedProbePath {
                active: None,
                inactive_stats: Some(stats),
            });
        }
    };

    let socket = match create_isolated_udp_socket(
        &bind_addr,
        spec.bind_device.as_deref(),
        default_udp_socket_buffer_bytes(),
    ) {
        Ok((socket, _isolation)) => socket,
        Err(error) => {
            let route_verification = RouteVerification::failed("bind-device", error.to_string());
            let stats = ProbePathStats::new(
                spec.path_id,
                Some(spec.name),
                spec.interface_name,
                bind_addr,
                None,
                route_verification,
            );
            return Ok(PreparedProbePath {
                active: None,
                inactive_stats: Some(stats),
            });
        }
    };

    socket.connect(server).await.with_context(|| {
        format!(
            "failed to connect path {} UDP socket to {}",
            spec.path_id, server
        )
    })?;
    let local_addr = socket.local_addr()?;
    let source = source_ip(local_addr);
    let route_verification = verify_route(
        target_ip,
        local_addr,
        spec.interface_name.as_deref(),
        spec.bind_device.as_deref(),
    );
    let stats = ProbePathStats::new(
        spec.path_id,
        Some(spec.name.clone()),
        spec.interface_name.clone(),
        local_addr.to_string(),
        source.map(|ip| ip.to_string()),
        route_verification,
    );
    Ok(PreparedProbePath {
        active: Some(ActiveProbePath {
            spec,
            socket,
            stats,
        }),
        inactive_stats: None,
    })
}

async fn collect_multi_ping_replies(
    active_paths: &mut [ActiveProbePath],
    buf: &mut [u8],
    key: &XBondKey,
    session_id: u64,
    sequence: u64,
    timeout: Duration,
) -> Result<()> {
    let deadline = Instant::now() + timeout;
    let mut first_arrival_seen = false;
    let mut acked_paths = HashSet::new();

    while Instant::now() < deadline {
        for (index, path) in active_paths.iter_mut().enumerate() {
            let Some(remaining) = deadline.checked_duration_since(Instant::now()) else {
                break;
            };
            let wait = remaining.min(Duration::from_millis(10));
            if wait.is_zero() {
                break;
            }

            match time::timeout(wait, path.socket.recv(buf)).await {
                Ok(Ok(len)) => {
                    let Ok(reply) = XBondFrame::decode_sealed(&buf[..len], key) else {
                        continue;
                    };
                    if is_expected_ack(&reply, session_id, sequence) && acked_paths.insert(index) {
                        let rtt_ms =
                            now_micros().saturating_sub(reply.header.send_micros) as f64 / 1_000.0;
                        let first_arrival = !first_arrival_seen;
                        first_arrival_seen = true;
                        path.stats.record_ack(rtt_ms, first_arrival);
                    }
                }
                Ok(Err(error)) => return Err(error.into()),
                Err(_) => {}
            }
        }

        if acked_paths.len() == active_paths.len() {
            break;
        }
    }

    Ok(())
}

fn is_expected_ack(frame: &XBondFrame, session_id: u64, sequence: u64) -> bool {
    frame.header.kind == PacketKind::Heartbeat
        && frame.header.session_id == session_id
        && frame.header.sequence == sequence
        && frame.payload == b"ack"
}

fn select_probe_paths(
    config: &ClientConfig,
    path_ids: &[u16],
    binds: &[String],
) -> Result<Vec<ProbePathSpec>> {
    let mut paths = Vec::new();
    if path_ids.is_empty() {
        for path in config.paths.iter().filter(|path| path.enabled) {
            paths.push(ProbePathSpec {
                path_id: path.id,
                name: path.name.clone(),
                interface_name: path.interface_name.clone(),
                bind_addr: configured_or_default_bind_addr(path)?,
                bind_device: path.interface_name.clone(),
            });
        }
    } else {
        for path_id in path_ids {
            let Some(path) = config.paths.iter().find(|path| path.id == *path_id) else {
                bail!("path id {path_id} is not present in the client config");
            };
            paths.push(ProbePathSpec {
                path_id: path.id,
                name: path.name.clone(),
                interface_name: path.interface_name.clone(),
                bind_addr: configured_or_default_bind_addr(path)?,
                bind_device: path.interface_name.clone(),
            });
        }
    }

    let next_path_id = paths
        .iter()
        .map(|path| path.path_id)
        .max()
        .unwrap_or(999)
        .saturating_add(1);
    for (index, bind) in binds.iter().enumerate() {
        paths.push(parse_explicit_probe_bind(bind, next_path_id, index)?);
    }

    if paths.is_empty() {
        bail!("multi-ping requires at least one enabled configured path, --path-id, or --bind");
    }

    let mut seen = HashSet::new();
    for path in &paths {
        if !seen.insert(path.path_id) {
            bail!("duplicate multi-ping path id {}", path.path_id);
        }
    }

    Ok(paths)
}

fn configured_or_default_bind_addr(path: &xbond_core::PathConfig) -> Result<Option<String>> {
    if let Some(bind_addr) = &path.bind_addr {
        return Ok(Some(bind_addr.clone()));
    }

    if path.interface_name.is_some() {
        return Ok(None);
    }

    bail!(
        "path id {} ({}) must set bind_addr or interface_name for route isolation",
        path.id,
        path.name
    );
}

fn parse_explicit_probe_bind(
    value: &str,
    next_path_id: u16,
    index: usize,
) -> Result<ProbePathSpec> {
    let (path_id, bind_addr) = if let Some((left, right)) = value.split_once('=') {
        (
            left.parse::<u16>()
                .with_context(|| format!("failed to parse path id in --bind {value}"))?,
            right.to_string(),
        )
    } else {
        (
            next_path_id.saturating_add(u16::try_from(index).unwrap_or(u16::MAX)),
            value.to_string(),
        )
    };

    if bind_addr.trim().is_empty() {
        bail!("--bind value cannot be empty");
    }

    Ok(ProbePathSpec {
        path_id,
        name: format!("explicit-bind-{path_id}"),
        interface_name: None,
        bind_addr: Some(bind_addr),
        bind_device: None,
    })
}

fn configured_bind_label(spec: &ProbePathSpec) -> String {
    spec.bind_addr
        .clone()
        .or_else(|| {
            spec.interface_name
                .as_ref()
                .map(|name| format!("interface:{name}"))
        })
        .unwrap_or_else(|| "unconfigured".to_string())
}

fn effective_bind_addr_for_spec(server: &str, spec: &ProbePathSpec) -> Result<String> {
    if let Some(bind_addr) = spec
        .bind_addr
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
    {
        return Ok(bind_addr.to_string());
    }

    let Some(interface_name) = spec.interface_name.as_deref() else {
        bail!(
            "path {} ({}) has no bind_addr or interface_name",
            spec.path_id,
            spec.name
        );
    };

    resolve_interface_source_bind_addr(server, interface_name)
}

fn resolve_interface_source_bind_addr(server: &str, interface_name: &str) -> Result<String> {
    if is_tunnel_interface(interface_name) {
        bail!("refusing to resolve bind source for tunnel interface {interface_name}");
    }

    let target_ip = resolve_server_ip(server)
        .with_context(|| format!("failed to resolve XBond server address {server}"))?;
    if !target_ip.is_ipv4() {
        bail!("interface source binding is currently IPv4-only; server resolved to {target_ip}");
    }
    if !cfg!(target_os = "linux") {
        bail!("interface source binding requires Linux iproute2");
    }

    let target_ip = target_ip.to_string();
    let output = ProcessCommand::new("ip")
        .args(["-4", "route", "get", &target_ip, "oif", interface_name])
        .output()
        .with_context(|| format!("failed to run ip route get for interface {interface_name}"))?;
    if !output.status.success() {
        bail!(
            "ip route get for {target_ip} on {interface_name} failed: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    let bind_addr =
        bind_addr_from_route_output(&String::from_utf8_lossy(&output.stdout), interface_name)?;
    validate_bind_source_on_interface(&bind_addr, interface_name)?;
    Ok(bind_addr)
}

fn bind_addr_from_route_output(route_output: &str, interface_name: &str) -> Result<String> {
    let route_dev = token_after(route_output, "dev");
    if route_dev.as_deref() != Some(interface_name) {
        bail!(
            "route output dev {:?} does not match interface {interface_name}",
            route_dev
        );
    }

    let source = token_after(route_output, "src").or_else(|| token_after(route_output, "from"));
    let Some(source) = source else {
        bail!("route output did not include a source address");
    };

    Ok(format!("{source}:0"))
}

fn validate_bind_source_on_interface(bind_addr: &str, interface_name: &str) -> Result<()> {
    let source = bind_addr
        .parse::<SocketAddr>()
        .with_context(|| format!("failed to parse bind source {bind_addr}"))?
        .ip();
    if !source.is_ipv4() {
        bail!("interface source binding is currently IPv4-only; source resolved to {source}");
    }

    #[cfg(target_os = "linux")]
    {
        let output = ProcessCommand::new("ip")
            .args(["-4", "-o", "addr", "show", "dev", interface_name])
            .output()
            .with_context(|| format!("failed to read IPv4 addresses for {interface_name}"))?;
        if !output.status.success() {
            bail!(
                "failed to read IPv4 addresses for {interface_name}: {}",
                String::from_utf8_lossy(&output.stderr).trim()
            );
        }
        if !addr_show_has_ipv4_source(&String::from_utf8_lossy(&output.stdout), source) {
            bail!("route source {source} is not assigned to interface {interface_name}");
        }
    }

    #[cfg(not(target_os = "linux"))]
    {
        let _ = interface_name;
    }

    Ok(())
}

#[cfg(any(target_os = "linux", test))]
fn addr_show_has_ipv4_source(addr_output: &str, source: IpAddr) -> bool {
    addr_output
        .lines()
        .filter_map(|line| token_after(line, "inet"))
        .filter_map(|value| value.split('/').next().map(str::to_string))
        .any(|value| value.parse::<IpAddr>().ok() == Some(source))
}

fn resolve_server_ip(server: &str) -> Option<IpAddr> {
    if let Ok(addr) = server.parse::<SocketAddr>() {
        return Some(addr.ip());
    }

    server
        .to_socket_addrs()
        .ok()
        .and_then(|mut addrs| addrs.next())
        .map(|addr| addr.ip())
}

fn create_isolated_udp_socket(
    bind_addr: &str,
    bind_device: Option<&str>,
    socket_buffer_bytes: usize,
) -> Result<(UdpSocket, PathIsolationStatus)> {
    let bind_addr = bind_addr
        .parse::<SocketAddr>()
        .with_context(|| format!("failed to parse bind address {bind_addr}"))?;
    let socket = Socket::new(
        Domain::for_address(bind_addr),
        Type::DGRAM,
        Some(Protocol::UDP),
    )?;
    let isolation = apply_bind_device(&socket, bind_device)?;
    apply_udp_socket_buffers(&socket, socket_buffer_bytes);
    socket
        .bind(&bind_addr.into())
        .with_context(|| format!("failed to bind UDP socket to {bind_addr}"))?;
    socket.set_nonblocking(true)?;
    let std_socket: std::net::UdpSocket = socket.into();
    Ok((UdpSocket::from_std(std_socket)?, isolation))
}

fn socket_source_matches_bind_addr(socket: &UdpSocket, bind_addr: &str) -> bool {
    let Ok(expected) = bind_addr.parse::<SocketAddr>() else {
        return false;
    };
    if expected.ip().is_unspecified() {
        return true;
    }

    socket
        .local_addr()
        .map(|actual| actual.ip() == expected.ip())
        .unwrap_or(false)
}

fn apply_udp_socket_buffers(socket: &Socket, socket_buffer_bytes: usize) {
    if socket_buffer_bytes == 0 {
        return;
    }

    let _ = socket.set_recv_buffer_size(socket_buffer_bytes);
    let _ = socket.set_send_buffer_size(socket_buffer_bytes);
}

fn apply_bind_device(socket: &Socket, bind_device: Option<&str>) -> Result<PathIsolationStatus> {
    let Some(bind_device) = bind_device.filter(|value| !value.trim().is_empty()) else {
        return Ok(PathIsolationStatus::default());
    };

    if is_tunnel_interface(bind_device) {
        bail!("refusing to bind XBond path to tunnel interface {bind_device}");
    }

    apply_platform_bind_device(socket, bind_device)?;
    Ok(PathIsolationStatus::active(
        "so-bindtodevice",
        format!("socket is isolated to interface {bind_device}"),
    ))
}

#[cfg(target_os = "linux")]
fn apply_platform_bind_device(socket: &Socket, bind_device: &str) -> Result<()> {
    socket
        .bind_device(Some(bind_device.as_bytes()))
        .map_err(|error| {
            if matches!(error.kind(), ErrorKind::PermissionDenied) {
                anyhow::anyhow!(
                    "SO_BINDTODEVICE for {bind_device} requires root or CAP_NET_RAW/CAP_NET_ADMIN"
                )
            } else {
                anyhow::anyhow!("failed to apply SO_BINDTODEVICE for {bind_device}: {error}")
            }
        })
}

#[cfg(not(target_os = "linux"))]
fn apply_platform_bind_device(_socket: &Socket, bind_device: &str) -> Result<()> {
    let _ = ErrorKind::Unsupported;
    bail!("bind-device isolation for {bind_device} is only implemented on Linux");
}

fn source_ip(addr: SocketAddr) -> Option<IpAddr> {
    if addr.ip().is_unspecified() {
        None
    } else {
        Some(addr.ip())
    }
}

fn verify_route(
    target_ip: Option<IpAddr>,
    local_addr: SocketAddr,
    interface_name: Option<&str>,
    bind_device: Option<&str>,
) -> RouteVerification {
    let Some(target_ip) = target_ip else {
        return RouteVerification::failed(
            "ip-route-get",
            "server address did not resolve to an IP",
        );
    };
    let Some(source_ip) = source_ip(local_addr) else {
        return RouteVerification::failed(
            "ip-route-get",
            "local source address is unspecified after bind/connect",
        );
    };
    if let Some(interface_name) = interface_name {
        if is_tunnel_interface(interface_name) {
            return RouteVerification::failed(
                "ip-route-get",
                format!("configured interface {interface_name} is a tunnel interface"),
            );
        }
    }
    if let Some(bind_device) = bind_device {
        if is_tunnel_interface(bind_device) {
            return RouteVerification::failed(
                "bind-device",
                format!("configured bind device {bind_device} is a tunnel interface"),
            );
        }
    }
    if !cfg!(target_os = "linux") {
        return RouteVerification::failed(
            "ip-route-get",
            "route verification is only implemented for Linux iproute2",
        );
    }

    let target_ip = target_ip.to_string();
    let source_ip = source_ip.to_string();
    let output = ProcessCommand::new("ip")
        .args(["route", "get", &target_ip, "from", &source_ip])
        .output();
    let Ok(output) = output else {
        return RouteVerification::failed("ip-route-get", "failed to run ip route get");
    };
    if !output.status.success() {
        return RouteVerification::failed(
            "ip-route-get",
            String::from_utf8_lossy(&output.stderr).trim().to_string(),
        );
    }

    let stdout = String::from_utf8_lossy(&output.stdout);
    let route_dev = token_after(&stdout, "dev");
    let route_src = token_after(&stdout, "src").or_else(|| token_after(&stdout, "from"));
    let Some(route_dev) = route_dev else {
        return RouteVerification::failed("ip-route-get", "route output did not include dev");
    };
    if is_tunnel_interface(&route_dev) {
        if let Some(bind_device) = bind_device {
            return RouteVerification::verified(
                "bind-device+ip-route-get",
                format!(
                    "SO_BINDTODEVICE isolates socket to {bind_device}; ip route get still reports {route_dev}"
                ),
            );
        }
        return RouteVerification::failed(
            "ip-route-get",
            format!("route leaves through tunnel interface {route_dev}"),
        );
    }
    if let Some(interface_name) = interface_name {
        if route_dev != interface_name {
            if bind_device == Some(interface_name) {
                return RouteVerification::verified(
                    "bind-device+ip-route-get",
                    format!(
                        "SO_BINDTODEVICE isolates socket to {interface_name}; ip route get reports {route_dev}"
                    ),
                );
            }
            return RouteVerification::failed(
                "ip-route-get",
                format!(
                    "route dev {route_dev} does not match configured interface {interface_name}"
                ),
            );
        }
    }
    if route_src.as_deref() != Some(source_ip.as_str()) {
        return RouteVerification::failed(
            "ip-route-get",
            format!(
                "route source {:?} does not match bound source {source_ip}",
                route_src
            ),
        );
    }

    RouteVerification::verified(
        "ip-route-get",
        format!("route uses dev {route_dev} with source {source_ip}"),
    )
}

fn token_after(text: &str, token: &str) -> Option<String> {
    let mut parts = text.split_whitespace();
    while let Some(part) = parts.next() {
        if part == token {
            return parts.next().map(ToString::to_string);
        }
    }
    None
}

fn is_tunnel_interface(interface_name: &str) -> bool {
    let name = interface_name.to_ascii_lowercase();
    name == "connectify0" || name == "xbond0" || name.starts_with("xbond")
}

fn load_status(config_path: &PathBuf) -> Result<XBondStatus> {
    let config = read_config(config_path)?;
    let runtime = read_runtime_status(&config)?;
    let health = if runtime.paths.is_empty() {
        config_health(&config)
    } else {
        runtime.paths.clone()
    };
    let roles = select_path_roles(&health, config.max_active_backups);
    let schedule = runtime
        .schedule
        .clone()
        .unwrap_or_else(|| build_schedule(config.mode, &roles));
    let config_by_id = config
        .paths
        .iter()
        .map(|path| (path.id, path))
        .collect::<HashMap<_, _>>();
    let paths = roles
        .into_iter()
        .map(|role| {
            let mut status = XBondPathStatus::from(role);
            if let Some(path_config) = config_by_id.get(&status.path_id) {
                status.bind_addr = path_config.bind_addr.clone();
                status.bind_device = path_config.interface_name.clone();
                status.path_isolation = path_isolation_from_config(path_config);
            }
            status
        })
        .collect();

    Ok(XBondStatus {
        enabled: config.enabled,
        running: runtime.running,
        mode: config.mode,
        redundancy_policy: config.redundancy_policy,
        server_addr: config.server_addr,
        tunnel: runtime.tunnel,
        anchor_path_id: runtime.anchor_path_id.or(schedule.anchor_path_id),
        schedule,
        paths,
        data_packets_sent: runtime.data_packets_sent,
        duplicate_packets_sent: runtime.duplicate_packets_sent,
        duplicate_packets_dropped: runtime.duplicate_packets_dropped,
        data_packets_received: runtime.data_packets_received,
        data_bytes_sent: runtime.data_bytes_sent,
        data_bytes_received: runtime.data_bytes_received,
        outbound_throughput_bps: runtime.outbound_throughput_bps,
        inbound_throughput_bps: runtime.inbound_throughput_bps,
        fec_packets_sent: runtime.fec_packets_sent,
        fec_packets_recovered: runtime.fec_packets_recovered,
        fec_packets_skipped: runtime.fec_packets_skipped,
        fec: fec_status_for_mode(config.mode, runtime.fec),
        late_packets_dropped: runtime.late_packets_dropped,
        reorder: runtime.reorder,
        repair: runtime.repair,
        server_recovery: runtime.server_recovery,
        server_health: runtime.server_health,
        process: runtime.process,
        recovery: runtime.recovery,
        message: runtime.message.unwrap_or_else(|| {
            if config.runtime_status_path.is_some() {
                "XBond prototype status is config-derived until the runtime status file exists."
                    .to_string()
            } else {
                "XBond prototype status is config-derived; no runtime status path is configured."
                    .to_string()
            }
        }),
    })
}

fn path_isolation_from_config(path: &xbond_core::PathConfig) -> PathIsolationStatus {
    match path.interface_name.as_deref() {
        Some(interface_name) if is_tunnel_interface(interface_name) => PathIsolationStatus::failed(
            "so-bindtodevice",
            format!("refusing to isolate path to tunnel interface {interface_name}"),
        ),
        Some(interface_name) => PathIsolationStatus::requested(
            "so-bindtodevice",
            format!(
                "will request bind-device isolation on {interface_name} when the path socket opens"
            ),
        ),
        None => PathIsolationStatus::default(),
    }
}

fn fec_status_for_mode(mode: xbond_core::ScheduleMode, runtime: XBondFecStatus) -> XBondFecStatus {
    if matches!(mode, xbond_core::ScheduleMode::AnchorFec) {
        return XBondFecStatus {
            configured: true,
            production_ready: true,
            message: "AnchorFec uses XBond XOR parity blocks across packet pairs; one missing data packet can be recovered when the paired data packet and parity arrive.".to_string(),
        };
    }

    if runtime.configured || runtime.production_ready {
        runtime
    } else {
        XBondFecStatus::default()
    }
}

fn read_config(config_path: &PathBuf) -> Result<ClientConfig> {
    if !config_path.exists() {
        return Ok(ClientConfig::default());
    }

    let text = std::fs::read_to_string(config_path)
        .with_context(|| format!("failed to read {}", config_path.display()))?;
    toml::from_str(&text).with_context(|| format!("failed to parse {}", config_path.display()))
}

fn read_runtime_status(config: &ClientConfig) -> Result<XBondRuntimeStatus> {
    let Some(path) = &config.runtime_status_path else {
        return Ok(XBondRuntimeStatus::default());
    };
    let runtime_path = PathBuf::from(path);
    if !runtime_path.exists() {
        return Ok(XBondRuntimeStatus::default());
    }

    let text = std::fs::read_to_string(&runtime_path)
        .with_context(|| format!("failed to read {}", runtime_path.display()))?;
    toml_or_json_runtime_status(&text)
        .with_context(|| format!("failed to parse {}", runtime_path.display()))
}

fn write_runtime_status_with_sender_lanes(
    config: &ClientConfig,
    status: XBondRuntimeStatus,
    path_runtime: &HashMap<u16, TunnelPathRuntime>,
) -> Result<()> {
    let mut value = serde_json::to_value(status)?;
    if let Some(object) = value.as_object_mut() {
        object.insert(
            "sender_lanes".to_string(),
            serde_json::Value::Array(sender_lane_metrics_json(path_runtime)),
        );
    }
    write_runtime_status_value(config, value)
}

fn write_runtime_status_value(config: &ClientConfig, status: serde_json::Value) -> Result<()> {
    let Some(path) = &config.runtime_status_path else {
        return Ok(());
    };
    let runtime_path = PathBuf::from(path);
    if let Some(parent) = runtime_path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }
    let text = serde_json::to_string(&status)?;
    std::fs::write(&runtime_path, text)
        .with_context(|| format!("failed to write {}", runtime_path.display()))
}

fn toml_or_json_runtime_status(text: &str) -> Result<XBondRuntimeStatus> {
    if text.trim_start().starts_with('{') {
        Ok(serde_json::from_str(text)?)
    } else {
        Ok(toml::from_str(text)?)
    }
}

fn config_health(config: &ClientConfig) -> Vec<PathHealthSnapshot> {
    config
        .paths
        .iter()
        .map(|path| PathHealthSnapshot {
            path_id: path.id,
            name: path.name.clone(),
            interface_name: path.interface_name.clone(),
            rtt_ms: None,
            jitter_ms: None,
            loss_rate: 0.0,
            late_rate: 0.0,
            queue_depth: 0,
            outbound_throughput_bps: 0,
            inbound_throughput_bps: 0,
            duplicate_inbound_throughput_bps: 0,
            raw_inbound_throughput_bps: 0,
            throughput_bps: 0,
            interface_up: path.enabled && interface_is_live(path.interface_name.as_deref()),
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
        })
        .collect()
}

fn interface_is_live(interface_name: Option<&str>) -> bool {
    let Some(interface_name) = interface_name.filter(|value| !value.trim().is_empty()) else {
        return true;
    };

    read_interface_state(interface_name).is_live
}

fn interface_ifindex(interface_name: Option<&str>) -> Option<u32> {
    let interface_name = interface_name?.trim();
    if interface_name.is_empty() {
        return None;
    }

    #[cfg(target_os = "linux")]
    {
        std::fs::read_to_string(
            PathBuf::from("/sys/class/net")
                .join(interface_name)
                .join("ifindex"),
        )
        .ok()
        .and_then(|value| value.trim().parse::<u32>().ok())
    }

    #[cfg(not(target_os = "linux"))]
    {
        let _ = interface_name;
        None
    }
}

#[derive(Debug, Clone)]
struct InterfaceState {
    is_live: bool,
}

fn read_interface_state(interface_name: &str) -> InterfaceState {
    #[cfg(target_os = "linux")]
    {
        let base = PathBuf::from("/sys/class/net").join(interface_name);
        if !base.exists() {
            return InterfaceState { is_live: false };
        }

        let operstate = std::fs::read_to_string(base.join("operstate"))
            .unwrap_or_default()
            .trim()
            .to_ascii_lowercase();
        let carrier = std::fs::read_to_string(base.join("carrier"))
            .unwrap_or_default()
            .trim()
            .to_string();

        let live = matches!(operstate.as_str(), "up" | "unknown")
            && carrier != "0"
            && interface_has_ipv4_address(interface_name);
        return InterfaceState { is_live: live };
    }

    #[cfg(not(target_os = "linux"))]
    {
        let _ = interface_name;
        InterfaceState { is_live: true }
    }
}

#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
fn interface_has_ipv4_address(interface_name: &str) -> bool {
    #[cfg(target_os = "linux")]
    {
        let Ok(output) = ProcessCommand::new("ip")
            .args(["-4", "-o", "addr", "show", "dev", interface_name])
            .output()
        else {
            return false;
        };

        output.status.success()
            && String::from_utf8_lossy(&output.stdout)
                .lines()
                .any(|line| token_after(line, "inet").is_some())
    }

    #[cfg(not(target_os = "linux"))]
    {
        let _ = interface_name;
        true
    }
}

fn print_human_status(status: &XBondStatus) {
    println!("XBond enabled: {}", status.enabled);
    println!("XBond running: {}", status.running);
    println!("Mode: {:?}", status.mode);
    println!("Server: {}", status.server_addr);
    println!("Anchor path: {:?}", status.anchor_path_id);
    println!("{}", status.message);
    for path in &status.paths {
        println!(
            "- {} ({:?}) role={:?} score={:.1}",
            path.name, path.interface_name, path.role, path.score
        );
    }
}

fn print_ping_result(result: &PingResult) {
    println!("XBond ping server: {}", result.server);
    println!("Local bind: {}", result.bind);
    println!("Path: {}", result.path_id);
    println!(
        "Packets: sent={}, received={}, lost={}, loss={:.1}%",
        result.sent,
        result.received,
        result.lost(),
        result.loss_rate() * 100.0
    );
    if let (Some(min), Some(avg), Some(max)) = (
        result.min_rtt_ms(),
        result.avg_rtt_ms(),
        result.max_rtt_ms(),
    ) {
        println!("RTT: min={min:.1} ms avg={avg:.1} ms max={max:.1} ms");
    }
}

fn print_multi_ping_result(result: &ProbeAggregate) {
    println!("XBond multi-ping mode: {:?}", result.mode);
    println!("Anchor path: {:?}", result.anchor_path_id);
    println!("Duplicate paths: {:?}", result.duplicate_path_ids);
    for path in &result.paths {
        println!(
            "- path {} {:?} bind={} source={:?} sent={} acks={} first={} loss={:.1}% avg_rtt={:?} route_verified={}",
            path.path_id,
            path.interface_name,
            path.bind,
            path.source,
            path.sent,
            path.acks,
            path.first_arrivals,
            path.loss_rate * 100.0,
            path.avg_rtt_ms,
            path.route_verified
        );
        if !path.route_verified {
            println!("  route: {}", path.route_verification.reason);
        }
    }
}

fn finite_min(values: &[f64]) -> Option<f64> {
    values
        .iter()
        .copied()
        .filter(|value| value.is_finite())
        .reduce(f64::min)
}

fn finite_max(values: &[f64]) -> Option<f64> {
    values
        .iter()
        .copied()
        .filter(|value| value.is_finite())
        .reduce(f64::max)
}

fn now_micros() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_micros().min(u128::from(u64::MAX)) as u64)
        .unwrap_or_default()
}

fn resolve_session_id(override_id: Option<u64>) -> Result<u64> {
    if let Some(session_id) = override_id {
        if session_id == 0 {
            bail!("XBond session id must be nonzero");
        }
        return Ok(session_id);
    }

    for _ in 0..8 {
        let mut bytes = [0u8; std::mem::size_of::<u64>()];
        getrandom::getrandom(&mut bytes).map_err(|error| {
            anyhow::anyhow!("failed to obtain a random XBond session id: {error}")
        })?;
        let session_id = u64::from_ne_bytes(bytes);
        if session_id != 0 {
            return Ok(session_id);
        }
    }

    bail!("operating-system random source repeatedly returned a zero XBond session id")
}

fn resolve_session_handshake_nonce() -> Result<SessionHandshakeNonce> {
    for _ in 0..8 {
        let mut nonce = [0u8; 16];
        getrandom::getrandom(&mut nonce).map_err(|error| {
            anyhow::anyhow!("failed to obtain a random XBond session handshake nonce: {error}")
        })?;
        if nonce.iter().any(|byte| *byte != 0) {
            return Ok(nonce);
        }
    }

    bail!("operating-system random source repeatedly returned a zero XBond handshake nonce")
}

fn monotonic_micros() -> u64 {
    static START: OnceLock<Instant> = OnceLock::new();
    START
        .get_or_init(Instant::now)
        .elapsed()
        .as_micros()
        .min(u128::from(u64::MAX)) as u64
}

#[cfg(test)]
mod tests {
    use super::*;

    fn nonce(value: u8) -> SessionHandshakeNonce {
        [value; 16]
    }

    #[tokio::test]
    async fn inbound_control_is_serviced_before_queued_payload() {
        let (control_tx, mut control_rx) = mpsc::channel(1);
        let (payload_tx, mut payload_rx) = mpsc::channel(1);
        let frame = |kind, sequence| InboundTunnelFrame {
            path_id: 1,
            frame: XBondFrame::new(
                XBondHeader::new(kind, 7, sequence, now_micros(), 1),
                vec![0x45],
            ),
        };

        payload_tx.send(frame(PacketKind::Data, 1)).await.unwrap();
        control_tx
            .send(frame(PacketKind::Heartbeat, 2))
            .await
            .unwrap();

        let received = receive_prioritized_tunnel_frame(&mut control_rx, &mut payload_rx)
            .await
            .unwrap();
        assert_eq!(received.frame.header.kind, PacketKind::Heartbeat);
        assert_eq!(payload_rx.len(), 1);
        assert!(is_prioritized_client_inbound(PacketKind::Control));
        assert!(is_prioritized_client_inbound(PacketKind::Heartbeat));
        assert!(!is_prioritized_client_inbound(PacketKind::Data));
    }

    fn complete_session_handshake(
        state: &mut ClientSynchronizationState,
        session_id: u64,
        now: Instant,
    ) {
        let request_nonce = state.request_nonce();
        let challenge = nonce(2);
        assert!(matches!(
            state.apply_control(
                &XBondControlMessage::SessionChallenge {
                    session_id,
                    request_nonce,
                    challenge,
                },
                now,
            ),
            SynchronizationControlOutcome::SessionChallenge { .. }
        ));
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionAccepted {
                    session_id,
                    request_nonce,
                    challenge,
                },
                now,
            ),
            SynchronizationControlOutcome::SessionAccepted
        );
    }

    #[test]
    fn generated_session_ids_are_random_and_nonzero() {
        let first = resolve_session_id(None).unwrap();
        let second = resolve_session_id(None).unwrap();
        let first_nonce = resolve_session_handshake_nonce().unwrap();
        let second_nonce = resolve_session_handshake_nonce().unwrap();

        assert_ne!(first, 0);
        assert_ne!(second, 0);
        assert_ne!(first, second);
        assert!(first_nonce.iter().any(|byte| *byte != 0));
        assert!(second_nonce.iter().any(|byte| *byte != 0));
        assert_ne!(first_nonce, second_nonce);
        assert_eq!(resolve_session_id(Some(42)).unwrap(), 42);
        assert!(resolve_session_id(Some(0)).is_err());
    }

    #[test]
    fn data_plane_waits_for_matching_session_and_schedule_acceptance() {
        let now = Instant::now();
        let request_nonce = nonce(1);
        let challenge = nonce(2);
        let mut state = ClientSynchronizationState::new(42, request_nonce, 7, now);

        assert!(!state.data_plane_ready());
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionAccepted {
                    session_id: 99,
                    request_nonce,
                    challenge,
                },
                now
            ),
            SynchronizationControlOutcome::Ignored
        );
        assert!(!state.data_plane_ready());
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionChallenge {
                    session_id: 42,
                    request_nonce,
                    challenge,
                },
                now,
            ),
            SynchronizationControlOutcome::SessionChallenge {
                request_nonce,
                challenge,
            }
        );
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionAccepted {
                    session_id: 42,
                    request_nonce,
                    challenge,
                },
                now
            ),
            SynchronizationControlOutcome::SessionAccepted
        );
        assert!(!state.data_plane_ready());
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::ScheduleAccepted {
                    session_id: 42,
                    schedule_generation: 6,
                },
                now
            ),
            SynchronizationControlOutcome::Ignored
        );
        assert!(!state.data_plane_ready());
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::ScheduleAccepted {
                    session_id: 42,
                    schedule_generation: 7,
                },
                now
            ),
            SynchronizationControlOutcome::ScheduleAccepted
        );
        assert!(state.data_plane_ready());
    }

    #[test]
    fn session_handshake_ignores_stale_or_mismatched_controls() {
        let now = Instant::now();
        let request_nonce = nonce(1);
        let challenge = nonce(2);
        let mut state = ClientSynchronizationState::new(42, request_nonce, 7, now);

        for control in [
            XBondControlMessage::SessionChallenge {
                session_id: 99,
                request_nonce,
                challenge,
            },
            XBondControlMessage::SessionChallenge {
                session_id: 42,
                request_nonce: nonce(9),
                challenge,
            },
            XBondControlMessage::SessionChallenge {
                session_id: 42,
                request_nonce,
                challenge: [0; 16],
            },
            XBondControlMessage::SessionAccepted {
                session_id: 42,
                request_nonce,
                challenge,
            },
        ] {
            assert_eq!(
                state.apply_control(&control, now),
                SynchronizationControlOutcome::Ignored
            );
        }
        assert!(!state.session_accepted());

        assert!(matches!(
            state.apply_control(
                &XBondControlMessage::SessionChallenge {
                    session_id: 42,
                    request_nonce,
                    challenge,
                },
                now,
            ),
            SynchronizationControlOutcome::SessionChallenge { .. }
        ));
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionAccepted {
                    session_id: 42,
                    request_nonce,
                    challenge: nonce(3),
                },
                now,
            ),
            SynchronizationControlOutcome::Ignored
        );
        assert!(!state.session_accepted());

        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionAccepted {
                    session_id: 42,
                    request_nonce,
                    challenge,
                },
                now,
            ),
            SynchronizationControlOutcome::SessionAccepted
        );
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionChallenge {
                    session_id: 42,
                    request_nonce,
                    challenge: nonce(4),
                },
                now,
            ),
            SynchronizationControlOutcome::Ignored
        );
    }

    #[test]
    fn schedule_generation_change_requires_a_fresh_ack() {
        let now = Instant::now();
        let mut state = ClientSynchronizationState::new(42, nonce(1), 1, now);
        complete_session_handshake(&mut state, 42, now);
        state.apply_control(
            &XBondControlMessage::ScheduleAccepted {
                session_id: 42,
                schedule_generation: 1,
            },
            now,
        );
        assert!(state.data_plane_ready());

        state.require_schedule(2, now + Duration::from_secs(1));
        assert!(!state.data_plane_ready());
        state.apply_control(
            &XBondControlMessage::ScheduleAccepted {
                session_id: 42,
                schedule_generation: 2,
            },
            now + Duration::from_secs(2),
        );
        assert!(state.data_plane_ready());

        state.mark_schedule_unsynchronized(now + Duration::from_secs(3));
        assert!(!state.data_plane_ready());
    }

    #[test]
    fn matching_restart_required_ends_the_session() {
        let now = Instant::now();
        let mut state = ClientSynchronizationState::new(42, nonce(1), 1, now);
        let restart = XBondControlMessage::SessionRestartRequired {
            session_id: 42,
            reason: "server lost session state".to_string(),
        };

        assert_eq!(
            state.apply_control(&restart, now),
            SynchronizationControlOutcome::Ignored
        );
        complete_session_handshake(&mut state, 42, now);
        assert_eq!(
            state.apply_control(&restart, now),
            SynchronizationControlOutcome::RestartRequired("server lost session state".to_string())
        );
        assert_eq!(
            state.apply_control(
                &XBondControlMessage::SessionRestartRequired {
                    session_id: 99,
                    reason: "other session".to_string(),
                },
                now
            ),
            SynchronizationControlOutcome::Ignored
        );
    }

    #[test]
    fn synchronization_timeouts_force_a_clean_reconnect() {
        let now = Instant::now();
        let mut state = ClientSynchronizationState::new(42, nonce(1), 1, now);

        assert!(state
            .synchronization_error(now + SESSION_SYNCHRONIZATION_TIMEOUT - Duration::from_millis(1))
            .is_none());
        assert!(state
            .synchronization_error(now + SESSION_SYNCHRONIZATION_TIMEOUT)
            .unwrap()
            .contains("session 42"));

        complete_session_handshake(&mut state, 42, now + Duration::from_secs(1));
        assert!(state
            .synchronization_error(
                now + Duration::from_secs(1) + SCHEDULE_SYNCHRONIZATION_TIMEOUT
                    - Duration::from_millis(1)
            )
            .is_none());
        assert!(state
            .synchronization_error(now + Duration::from_secs(1) + SCHEDULE_SYNCHRONIZATION_TIMEOUT)
            .unwrap()
            .contains("schedule generation 1"));
    }

    fn primary_send_work(sequence: u64) -> PathSendWork {
        PathSendWork::data(
            PacketKind::Data,
            XBondHeader::new(PacketKind::Data, 7, sequence, now_micros(), 1),
            Arc::new(vec![sequence as u8]),
        )
    }

    fn test_sender_handle(
        data_tx: mpsc::Sender<PathSendWork>,
        control_tx: mpsc::Sender<PathSendWork>,
        repair_tx: mpsc::Sender<PathSendWork>,
        data_capacity: usize,
        task: JoinHandle<()>,
    ) -> PathSenderHandle {
        let metrics = Arc::new(PathSenderMetrics::new(data_capacity));
        let latest_control = Arc::new(LatestControlSlot::new(metrics.clone()));
        PathSenderHandle {
            socket_generation: 1,
            data_tx,
            control_tx,
            repair_tx,
            latest_control,
            metrics,
            task,
        }
    }

    #[tokio::test]
    async fn sender_successes_are_aggregated_without_using_the_report_channel() {
        let (data_tx, _data_rx) = mpsc::channel(1);
        let (control_tx, _control_rx) = mpsc::channel(1);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let task = tokio::spawn(std::future::pending::<()>());
        let sender = test_sender_handle(data_tx, control_tx, repair_tx, 1, task);
        sender.metrics.record_encoded(7);
        sender.metrics.record_encoded(11);
        sender.metrics.record_success(PacketKind::Data, 1200);
        sender.metrics.record_success(PacketKind::Duplicate, 1200);
        sender.metrics.record_success(PacketKind::Fec, 600);
        sender.metrics.record_success(PacketKind::Repair, 800);
        sender
            .metrics
            .record_sender_deadline_drop(PathSendLane::Data);
        sender
            .metrics
            .record_sender_deadline_drop(PathSendLane::Repair);

        let senders = HashMap::from([(9, sender)]);
        let mut runtimes = HashMap::new();
        let mut counters = TunnelCounters::default();
        let mut repair = XBondRepairStatus::default();
        drain_sender_completions(&senders, &mut runtimes, &mut counters, &mut repair);

        assert_eq!(counters.encoded_frames, 2);
        assert_eq!(counters.encode_micros_total, 18);
        assert_eq!(counters.data_packets_sent, 1);
        assert_eq!(counters.duplicate_packets_sent, 1);
        assert_eq!(counters.fec_packets_sent, 1);
        assert_eq!(repair.frames_sent, 1);
        assert_eq!(counters.sender_deadline_drops, 2);
        assert_eq!(counters.repair_lane_drops, 1);
        assert_eq!(runtimes[&9].bytes_sent, 3800);
        assert_eq!(
            senders[&9].metrics.take_completions(),
            PathSenderCompletionSnapshot::default()
        );
        senders[&9].task.abort();
    }

    #[test]
    fn return_reorder_uses_server_adaptive_hold_during_recovery() {
        let mut reorder = PacketReorderBuffer::new(16, 25_000);
        let recovery = RecoveryStatus {
            active: true,
            ..RecoveryStatus::default()
        };
        let mut server = XBondServerRecoveryStatus {
            reported: true,
            ..XBondServerRecoveryStatus::default()
        };
        server.ingress_reorder.current_hold_ms = 350;

        update_return_reorder_hold(&mut reorder, 25, &recovery, &server);

        assert_eq!(reorder.hold_micros(), 350_000);
    }

    #[test]
    fn return_reorder_starts_recovery_with_safe_minimum_before_server_status() {
        let mut reorder = PacketReorderBuffer::new(16, 25_000);
        let recovery = RecoveryStatus {
            active: true,
            ..RecoveryStatus::default()
        };

        update_return_reorder_hold(
            &mut reorder,
            25,
            &recovery,
            &XBondServerRecoveryStatus::default(),
        );

        assert_eq!(reorder.hold_micros(), 150_000);
    }

    #[test]
    fn return_reorder_resets_to_normal_hold_after_recovery() {
        let mut reorder = PacketReorderBuffer::new(16, 500_000);

        update_return_reorder_hold(
            &mut reorder,
            25,
            &RecoveryStatus::default(),
            &XBondServerRecoveryStatus::default(),
        );

        assert_eq!(reorder.hold_micros(), 25_000);
    }

    #[tokio::test]
    async fn saturated_primary_queue_preserves_backpressure_and_control_progress() {
        let (payload_tx, mut payload_rx) = mpsc::channel(1);
        let (sender_control_tx, sender_control_rx) = mpsc::channel(1);
        let (sender_repair_tx, sender_repair_rx) = mpsc::channel(1);
        let sender_task = tokio::spawn(async move {
            std::future::pending::<()>().await;
            drop((sender_control_rx, sender_repair_rx));
        });
        let sender = test_sender_handle(
            payload_tx,
            sender_control_tx,
            sender_repair_tx,
            1,
            sender_task,
        );
        try_enqueue_sender_lane(&sender.data_tx, &sender.metrics, primary_send_work(1)).unwrap();
        let (completion_tx, mut completion_rx) = mpsc::channel(1);

        let result =
            enqueue_primary_work(1, 4, &sender, primary_send_work(2), &completion_tx).unwrap();

        assert_eq!(result, PrimaryEnqueueResult::Pending);
        assert!(
            time::timeout(Duration::from_millis(10), completion_rx.recv())
                .await
                .is_err()
        );

        let (control_tx, mut control_rx) = mpsc::channel(3);
        for event in ["control", "heartbeat", "schedule"] {
            control_tx.send(event).await.unwrap();
        }
        for expected in ["control", "heartbeat", "schedule"] {
            assert_eq!(
                time::timeout(Duration::from_millis(100), control_rx.recv())
                    .await
                    .unwrap(),
                Some(expected)
            );
        }

        assert_eq!(payload_rx.recv().await.unwrap().header.sequence, 1);
        let completion = time::timeout(Duration::from_millis(100), completion_rx.recv())
            .await
            .unwrap()
            .unwrap();
        assert!(completion.success);
        assert_eq!(completion.path_id, 1);
        assert_eq!(completion.socket_generation, 4);
        assert_eq!(payload_rx.recv().await.unwrap().header.sequence, 2);
        sender.task.abort();
    }

    #[tokio::test]
    async fn sender_queue_metrics_use_real_bounded_channel_occupancy() {
        let (tx, rx) = mpsc::channel(4);
        let (control_tx, control_rx) = mpsc::channel(1);
        let (repair_tx, repair_rx) = mpsc::channel(1);
        let task = tokio::spawn(async move {
            std::future::pending::<()>().await;
            drop((rx, control_rx, repair_rx));
        });
        let sender = test_sender_handle(tx, control_tx, repair_tx, 4, task);
        try_enqueue_sender_lane(&sender.data_tx, &sender.metrics, primary_send_work(1)).unwrap();
        try_enqueue_sender_lane(&sender.data_tx, &sender.metrics, primary_send_work(2)).unwrap();
        let mut senders = HashMap::from([(1, sender)]);
        let mut path_runtime = HashMap::from([(1, TunnelPathRuntime::default())]);
        path_runtime
            .get_mut(&1)
            .unwrap()
            .pending_heartbeats
            .insert(9, Instant::now());

        refresh_sender_queue_metrics(&mut path_runtime, &senders);

        let runtime = path_runtime.get(&1).unwrap();
        assert_eq!(runtime.sender_queue_depth, 2);
        assert_eq!(runtime.sender_queue_capacity, 4);
        assert_eq!(sender_queue_pressure(runtime), 0.5);
        assert_eq!(runtime.pending_heartbeats.len(), 1);
        senders.remove(&1).unwrap().task.abort();
    }

    #[tokio::test]
    async fn pending_heartbeats_do_not_create_sender_queue_pressure() {
        let (tx, rx) = mpsc::channel(4);
        let (control_tx, control_rx) = mpsc::channel(1);
        let (repair_tx, repair_rx) = mpsc::channel(1);
        let task = tokio::spawn(async move {
            std::future::pending::<()>().await;
            drop((rx, control_rx, repair_rx));
        });
        let sender = test_sender_handle(tx, control_tx, repair_tx, 4, task);
        let mut senders = HashMap::from([(1, sender)]);
        let mut runtime = TunnelPathRuntime::default();
        for sequence in 1..=3 {
            runtime.pending_heartbeats.insert(sequence, Instant::now());
        }
        let mut path_runtime = HashMap::from([(1, runtime)]);

        refresh_sender_queue_metrics(&mut path_runtime, &senders);

        let runtime = path_runtime.get(&1).unwrap();
        assert_eq!(runtime.sender_queue_depth, 0);
        assert_eq!(sender_queue_pressure(runtime), 0.0);
        assert_eq!(runtime.pending_heartbeats.len(), 3);
        senders.remove(&1).unwrap().task.abort();
    }

    #[test]
    fn tun_reader_exit_is_promoted_to_tunnel_failure() {
        let read_error =
            tun_reader_exit_error(TunReaderExit::ReadFailed("read failed".to_string()));
        let task_error =
            tun_reader_exit_error(TunReaderExit::TaskFailed("task failed".to_string()));
        let queue_error = tun_reader_exit_error(TunReaderExit::PacketQueueClosed);

        assert_eq!(read_error.to_string(), "read failed");
        assert_eq!(task_error.to_string(), "task failed");
        assert!(queue_error.to_string().contains("packet queue closed"));
    }

    #[test]
    fn fec_pairs_only_consecutive_packets_in_the_same_context() {
        let mut pending = None;
        assert!(advance_pending_fec_source(
            &mut pending,
            10,
            Arc::new(vec![1]),
            true,
            3,
            RedundancyPolicy::Balanced,
            false,
        )
        .is_none());

        let pair = advance_pending_fec_source(
            &mut pending,
            11,
            Arc::new(vec![2]),
            true,
            3,
            RedundancyPolicy::Balanced,
            false,
        )
        .unwrap();

        assert_eq!(pair.0, 10);
        assert_eq!(pair.1.as_slice(), &[1]);
        assert_eq!(pair.2.as_slice(), &[2]);
        assert!(pending.is_none());
    }

    #[test]
    fn fec_pending_state_clears_on_ineligible_or_changed_context() {
        let mut pending = None;
        advance_pending_fec_source(
            &mut pending,
            20,
            Arc::new(vec![1]),
            true,
            4,
            RedundancyPolicy::Balanced,
            false,
        );
        advance_pending_fec_source(
            &mut pending,
            21,
            Arc::new(vec![2]),
            false,
            4,
            RedundancyPolicy::Balanced,
            false,
        );
        assert!(pending.is_none());

        advance_pending_fec_source(
            &mut pending,
            30,
            Arc::new(vec![3]),
            true,
            4,
            RedundancyPolicy::Balanced,
            false,
        );
        assert!(advance_pending_fec_source(
            &mut pending,
            31,
            Arc::new(vec![4]),
            true,
            5,
            RedundancyPolicy::Balanced,
            false,
        )
        .is_none());
        assert_eq!(pending.as_ref().unwrap().sequence, 31);

        assert!(advance_pending_fec_source(
            &mut pending,
            32,
            Arc::new(vec![5]),
            true,
            5,
            RedundancyPolicy::Reliable,
            true,
        )
        .is_none());
        assert_eq!(pending.as_ref().unwrap().sequence, 32);

        assert!(advance_pending_fec_source(
            &mut pending,
            34,
            Arc::new(vec![6]),
            true,
            5,
            RedundancyPolicy::Reliable,
            true,
        )
        .is_none());
        assert_eq!(pending.as_ref().unwrap().sequence, 34);
    }

    #[test]
    fn silent_blackhole_requires_hysteresis_and_resets_after_ack() {
        let mut runtime = TunnelPathRuntime {
            loss_rate: 1.0,
            last_ack_at: Some(Instant::now() - Duration::from_secs(30)),
            health_window: VecDeque::from([false, false, false]),
            ..TunnelPathRuntime::default()
        };
        let threshold = Duration::from_secs(5);

        assert!(!refresh_silent_blackhole_state(&mut runtime, threshold));
        assert!(!refresh_silent_blackhole_state(&mut runtime, threshold));
        assert!(refresh_silent_blackhole_state(&mut runtime, threshold));

        runtime.last_ack_at = Some(Instant::now());
        assert!(!refresh_silent_blackhole_state(&mut runtime, threshold));
        assert_eq!(runtime.stale_ack_ticks, 0);
    }

    #[test]
    fn silent_blackhole_probe_respects_inflight_and_cooldowns() {
        let mut runtime = TunnelPathRuntime::default();
        assert!(silent_blackhole_probe_allowed(&runtime, true));
        assert!(!silent_blackhole_probe_allowed(&runtime, false));

        runtime.direct_probe_in_flight = true;
        assert!(!silent_blackhole_probe_allowed(&runtime, true));
        runtime.direct_probe_in_flight = false;

        runtime.last_direct_probe_at = Some(Instant::now());
        assert!(!silent_blackhole_probe_allowed(&runtime, true));
        runtime.last_direct_probe_at =
            Some(Instant::now() - SILENT_BLACKHOLE_PROBE_COOLDOWN - Duration::from_secs(1));
        runtime.last_rebind_attempt = Some(Instant::now());
        assert!(!silent_blackhole_probe_allowed(&runtime, true));
        runtime.last_rebind_attempt =
            Some(Instant::now() - SILENT_BLACKHOLE_REBIND_COOLDOWN - Duration::from_secs(1));
        assert!(silent_blackhole_probe_allowed(&runtime, true));
    }

    #[test]
    fn runtime_status_json_parses_observed_counters_and_paths() {
        let runtime = toml_or_json_runtime_status(
            r#"{
                "running": true,
                "duplicate_packets_dropped": 7,
                "fec_packets_recovered": 2,
                "late_packets_dropped": 3,
                "message": "live",
                "paths": [
                    {
                        "path_id": 1,
                        "name": "fiber",
                        "interface_name": "eth0",
                        "rtt_ms": 12.0,
                        "jitter_ms": 1.0,
                        "loss_rate": 0.0,
                        "late_rate": 0.0,
                        "queue_depth": 0,
                        "throughput_bps": 1000000,
                        "interface_up": true,
                        "in_cooldown": false
                    }
                ]
            }"#,
        )
        .unwrap();

        assert!(runtime.running);
        assert_eq!(runtime.duplicate_packets_dropped, 7);
        assert_eq!(runtime.fec_packets_recovered, 2);
        assert_eq!(runtime.late_packets_dropped, 3);
        assert_eq!(runtime.paths[0].name, "fiber");
    }

    #[test]
    fn ping_result_reports_loss_and_rtt_summary() {
        let result = PingResult {
            server: "127.0.0.1:8444".to_string(),
            bind: "0.0.0.0:0".to_string(),
            path_id: 2,
            session_id: 99,
            sent: 4,
            received: 2,
            replies: vec![
                PingReply {
                    sequence: 1,
                    rtt_ms: 10.0,
                },
                PingReply {
                    sequence: 3,
                    rtt_ms: 20.0,
                },
            ],
        };

        assert_eq!(result.lost(), 2);
        assert_eq!(result.loss_rate(), 0.5);
        assert_eq!(result.min_rtt_ms(), Some(10.0));
        assert_eq!(result.avg_rtt_ms(), Some(15.0));
        assert_eq!(result.max_rtt_ms(), Some(20.0));
    }

    #[test]
    fn tunnel_health_samples_track_rtt_jitter_and_loss() {
        let mut runtime = TunnelPathRuntime::default();

        record_tunnel_health_sample(&mut runtime, true, Some(40.0));
        record_tunnel_health_sample(&mut runtime, false, None);
        record_tunnel_health_sample(&mut runtime, true, Some(70.0));

        assert_eq!(runtime.rtt_ms, Some(55.0));
        assert_eq!(runtime.jitter_ms, Some(30.0));
        assert!((runtime.loss_rate - (1.0 / 3.0)).abs() < f64::EPSILON);
    }

    #[test]
    fn path_health_window_reacts_faster_than_aggregate_tunnel_window() {
        let mut path = TunnelPathRuntime::default();
        let mut aggregate = TunnelAggregateHealthRuntime::default();

        for _ in 0..TUNNEL_HEALTH_WINDOW {
            record_tunnel_health_sample(&mut path, true, Some(40.0));
            record_aggregate_tunnel_health_sample(&mut aggregate, true, Some(40.0));
        }
        for _ in 0..PATH_HEALTH_WINDOW {
            record_tunnel_health_sample(&mut path, true, Some(240.0));
            record_aggregate_tunnel_health_sample(&mut aggregate, true, Some(240.0));
        }

        assert_eq!(path.rtt_samples_ms.len(), PATH_HEALTH_WINDOW);
        assert_eq!(path.rtt_ms, Some(240.0));
        assert_eq!(aggregate.rtt_samples_ms.len(), TUNNEL_HEALTH_WINDOW);
        assert!(aggregate.rtt_ms.is_some_and(|rtt| rtt < 240.0));
    }

    #[test]
    fn aggregate_tunnel_health_first_ack_wins_and_ignores_duplicates() {
        let mut runtime = TunnelAggregateHealthRuntime::default();
        let sequence = AGGREGATE_HEARTBEAT_SEQUENCE_PREFIX | 42;
        runtime
            .pending_heartbeats
            .insert(sequence, Instant::now() - Duration::from_millis(20));
        let frame = XBondFrame::new(
            XBondHeader::new(
                PacketKind::Heartbeat,
                7,
                sequence,
                now_micros().saturating_add(60_000_000),
                1,
            ),
            b"ack".to_vec(),
        );

        assert!(record_aggregate_tunnel_heartbeat_ack(&mut runtime, &frame));
        assert!(!record_aggregate_tunnel_heartbeat_ack(&mut runtime, &frame));
        assert_eq!(
            runtime.health_window.iter().filter(|value| **value).count(),
            1
        );
        assert_eq!(runtime.loss_rate, Some(0.0));
        assert_eq!(runtime.success_rate, Some(1.0));
        assert!(runtime.last_success_at.is_some());
        assert!(runtime
            .rtt_ms
            .is_some_and(|value| (10.0..100.0).contains(&value)));
    }

    #[test]
    fn path_rtt_uses_pending_monotonic_instant_not_wire_timestamp() {
        let sequence = 77;
        let mut runtime = TunnelPathRuntime::default();
        runtime
            .pending_heartbeats
            .insert(sequence, Instant::now() - Duration::from_millis(25));
        let mut path_runtime = HashMap::from([(1, runtime)]);
        let frame = XBondFrame::new(
            XBondHeader::new(
                PacketKind::Heartbeat,
                7,
                sequence,
                now_micros().saturating_add(60_000_000),
                1,
            ),
            b"ack".to_vec(),
        );

        record_tunnel_heartbeat_ack(&mut path_runtime, 1, &frame);

        assert!(path_runtime[&1]
            .rtt_ms
            .is_some_and(|value| (15.0..100.0).contains(&value)));
    }

    #[test]
    fn inbound_replay_window_rejects_replayed_control_frames() {
        let mut receiver = FrameReceiver::new(1_000_000, 64);
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Control, 7, 91, now_micros(), 1),
            b"authoritative-schedule".to_vec(),
        );

        assert_eq!(
            receiver.observe(&frame, now_micros()),
            ReceiveOutcome::Accepted
        );
        assert_eq!(
            receiver.observe(&frame, now_micros()),
            ReceiveOutcome::Duplicate
        );
        assert_eq!(receiver.stats().accepted_packets, 1);
        assert_eq!(receiver.stats().duplicate_packets_dropped, 1);
    }

    #[test]
    fn repeated_ineffective_rebinds_escalate_and_a_real_ack_resets_them() {
        let sequence = 92;
        let mut runtime = TunnelPathRuntime::default();

        for attempt in 1..SILENT_BLACKHOLE_MAX_INEFFECTIVE_REBINDS {
            assert!(!record_ineffective_rebind(&mut runtime));
            assert_eq!(runtime.ineffective_rebinds, attempt);
        }
        assert!(record_ineffective_rebind(&mut runtime));

        runtime
            .pending_heartbeats
            .insert(sequence, Instant::now() - Duration::from_millis(10));
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Heartbeat, 7, sequence, now_micros(), 1),
            b"ack".to_vec(),
        );
        let mut path_runtime = HashMap::from([(1, runtime)]);
        record_tunnel_heartbeat_ack(&mut path_runtime, 1, &frame);

        assert_eq!(path_runtime[&1].ineffective_rebinds, 0);
    }

    #[test]
    fn aggregate_tunnel_health_timeout_records_loss() {
        let mut runtime = TunnelAggregateHealthRuntime::default();
        let sequence = AGGREGATE_HEARTBEAT_SEQUENCE_PREFIX | 7;
        runtime
            .pending_heartbeats
            .insert(sequence, Instant::now() - Duration::from_secs(10));

        expire_aggregate_tunnel_heartbeats(&mut runtime, Duration::from_millis(1));

        assert!(runtime.pending_heartbeats.is_empty());
        assert_eq!(runtime.loss_rate, Some(1.0));
        assert_eq!(runtime.success_rate, Some(0.0));
    }

    #[test]
    fn aggregate_tunnel_health_tracks_rtt_jitter_and_status() {
        let mut runtime = TunnelAggregateHealthRuntime::default();

        record_aggregate_tunnel_health_sample(&mut runtime, true, Some(50.0));
        record_aggregate_tunnel_health_sample(&mut runtime, true, Some(80.0));
        record_aggregate_tunnel_health_sample(&mut runtime, false, None);

        assert_eq!(runtime.rtt_ms, Some(65.0));
        assert_eq!(runtime.jitter_ms, Some(30.0));
        assert!((runtime.loss_rate.unwrap() - (1.0 / 3.0)).abs() < f64::EPSILON);
        assert_eq!(runtime.to_status().status, "critical");
    }

    #[test]
    fn tunnel_health_classifier_uses_expected_thresholds() {
        assert_eq!(classify_tunnel_health(Some(80.0), Some(0.0)).0, "good");
        assert_eq!(classify_tunnel_health(Some(120.0), Some(0.0)).0, "fair");
        assert_eq!(classify_tunnel_health(Some(80.0), Some(0.02)).0, "fair");
        assert_eq!(classify_tunnel_health(Some(180.0), Some(0.0)).0, "poor");
        assert_eq!(classify_tunnel_health(Some(80.0), Some(0.10)).0, "poor");
        assert_eq!(classify_tunnel_health(Some(300.0), Some(0.0)).0, "critical");
        assert_eq!(classify_tunnel_health(None, Some(0.25)).0, "critical");
    }

    #[test]
    fn multi_ping_selects_enabled_paths_and_explicit_binds() {
        let config = ClientConfig {
            paths: vec![
                xbond_core::PathConfig {
                    id: 1,
                    name: "primary".to_string(),
                    interface_name: Some("eth0".to_string()),
                    bind_addr: Some("192.0.2.10:0".to_string()),
                    enabled: true,
                },
                xbond_core::PathConfig {
                    id: 2,
                    name: "disabled".to_string(),
                    interface_name: Some("wwan0".to_string()),
                    bind_addr: Some("192.0.2.11:0".to_string()),
                    enabled: false,
                },
            ],
            ..ClientConfig::default()
        };

        let paths = select_probe_paths(&config, &[], &["9=198.51.100.10:0".to_string()]).unwrap();

        assert_eq!(paths.len(), 2);
        assert_eq!(paths[0].path_id, 1);
        assert_eq!(paths[0].interface_name.as_deref(), Some("eth0"));
        assert_eq!(paths[1].path_id, 9);
        assert_eq!(paths[1].bind_addr.as_deref(), Some("198.51.100.10:0"));
    }

    #[test]
    fn route_parser_reads_tokens_from_ip_route_output() {
        let text = "45.77.241.247 from 192.0.2.10 dev eth0 src 192.0.2.10 uid 1000";

        assert_eq!(token_after(text, "dev").as_deref(), Some("eth0"));
        assert_eq!(token_after(text, "src").as_deref(), Some("192.0.2.10"));
        assert_eq!(token_after(text, "from").as_deref(), Some("192.0.2.10"));
        assert_eq!(token_after(text, "missing"), None);
    }

    #[test]
    fn route_output_builds_interface_source_bind_addr() {
        let text = "45.77.241.247 dev eth0 src 192.0.2.10 uid 0 cache";

        let bind_addr = bind_addr_from_route_output(text, "eth0").unwrap();

        assert_eq!(bind_addr, "192.0.2.10:0");
    }

    #[test]
    fn route_output_rejects_wrong_interface_source() {
        let text = "45.77.241.247 dev wlan0 src 192.0.2.20 uid 0 cache";

        let error = bind_addr_from_route_output(text, "eth0").unwrap_err();

        assert!(error.to_string().contains("does not match interface eth0"));
    }

    #[test]
    fn addr_show_detects_assigned_ipv4_source() {
        let text = "2: eth0    inet 192.0.2.10/24 brd 192.0.2.255 scope global eth0\n";

        assert!(addr_show_has_ipv4_source(
            text,
            "192.0.2.10".parse().unwrap()
        ));
        assert!(!addr_show_has_ipv4_source(
            text,
            "192.0.2.20".parse().unwrap()
        ));
    }

    #[test]
    fn addr_show_ignores_ipv6_when_validating_ipv4_source() {
        let text = "2: eth0    inet6 2001:db8::10/64 scope global dynamic\n";

        assert!(!addr_show_has_ipv4_source(
            text,
            "192.0.2.10".parse().unwrap()
        ));
    }

    #[test]
    fn tunnel_interface_detection_rejects_tunnel_paths() {
        assert!(is_tunnel_interface("connectify0"));
        assert!(is_tunnel_interface("xbond0"));
        assert!(is_tunnel_interface("xbond-primary"));
        assert!(!is_tunnel_interface("eth0"));
    }

    #[test]
    fn multi_ping_requires_configured_bind_addresses() {
        let config = ClientConfig {
            paths: vec![xbond_core::PathConfig {
                id: 1,
                name: "primary".to_string(),
                interface_name: None,
                bind_addr: None,
                enabled: true,
            }],
            ..ClientConfig::default()
        };

        let error = select_probe_paths(&config, &[], &[]).unwrap_err();

        assert!(error
            .to_string()
            .contains("must set bind_addr or interface_name for route isolation"));
    }

    #[test]
    fn multi_ping_can_use_interface_name_for_bind_device_isolation() {
        let config = ClientConfig {
            paths: vec![xbond_core::PathConfig {
                id: 1,
                name: "primary".to_string(),
                interface_name: Some("eth0".to_string()),
                bind_addr: None,
                enabled: true,
            }],
            ..ClientConfig::default()
        };

        let paths = select_probe_paths(&config, &[], &[]).unwrap();

        assert_eq!(paths[0].bind_addr, None);
        assert_eq!(paths[0].bind_device.as_deref(), Some("eth0"));
    }

    #[test]
    fn lab_tun_flags_are_hidden_but_parseable_and_inert_by_default() {
        let parsed = Args::try_parse_from(["xbond-client", "tunnel"]).unwrap();
        let Command::Tunnel {
            lab_fail_tun_read_after_packets,
            lab_tun_write_delay_ms,
            ..
        } = parsed.command
        else {
            panic!("expected tunnel command");
        };
        assert_eq!(lab_fail_tun_read_after_packets, None);
        assert_eq!(lab_tun_write_delay_ms, 0);

        let parsed = Args::try_parse_from([
            "xbond-client",
            "tunnel",
            "--lab-fail-tun-read-after-packets",
            "7",
            "--lab-tun-write-delay-ms",
            "25",
        ])
        .unwrap();
        let Command::Tunnel {
            lab_fail_tun_read_after_packets,
            lab_tun_write_delay_ms,
            ..
        } = parsed.command
        else {
            panic!("expected tunnel command");
        };
        assert_eq!(lab_fail_tun_read_after_packets, Some(7));
        assert_eq!(lab_tun_write_delay_ms, 25);
        assert!(!should_fail_tun_reader(Some(7), 6));
        assert!(should_fail_tun_reader(Some(7), 7));
        assert!(!should_fail_tun_reader(None, u64::MAX));
    }

    #[test]
    fn receiver_payloads_are_mtu_bounded_and_reject_oversized_data() {
        let mtu = 1400;
        let pool = ReceiverPayloadPool::new(4, receiver_scratch_capacity(mtu));
        let payload = vec![9u8; usize::from(mtu)];
        let queued = copy_bounded_receiver_payload(PacketKind::Data, &payload, mtu, &pool).unwrap();

        assert_eq!(queued, payload);
        assert!(queued.capacity() <= receiver_scratch_capacity(mtu));
        assert!(copy_bounded_receiver_payload(
            PacketKind::Data,
            &vec![1; receiver_scratch_capacity(mtu) + 1],
            mtu,
            &pool,
        )
        .is_none());
        assert!(copy_bounded_receiver_payload(
            PacketKind::Control,
            &vec![1; MAX_CONTROL_PAYLOAD_BYTES],
            mtu,
            &pool,
        )
        .is_some());
    }

    #[test]
    fn receiver_payload_pool_reuses_bounded_buffers() {
        let pool = ReceiverPayloadPool::new(2, 2048);
        let buffer = pool.take(1400);
        let pointer = buffer.as_ptr();
        pool.recycle(buffer);
        assert_eq!(pool.len(), 1);

        let reused = pool.take(1200);
        assert_eq!(reused.as_ptr(), pointer);
        assert_eq!(pool.len(), 0);
        pool.recycle(Vec::with_capacity(4096));
        assert_eq!(pool.len(), 0);
    }

    #[tokio::test]
    async fn saturated_tun_writer_queue_exits_instead_of_dropping_unique_packet() {
        let (tx, _rx) = mpsc::channel(1);
        let writer = TunWriterHandle {
            tx,
            queued_packets: Arc::new(AtomicU64::new(0)),
            capacity: 1,
        };
        let packets = |sequence| {
            vec![ReorderedPacket {
                sequence,
                path_id: 1,
                payload: vec![0; 100],
            }]
        };
        let mut counters = TunnelCounters::default();

        enqueue_reordered_return_packets_with_deadline(
            &writer,
            packets(1),
            &mut counters,
            false,
            Duration::from_millis(10),
        )
        .await
        .unwrap();
        let error = enqueue_reordered_return_packets_with_deadline(
            &writer,
            packets(2),
            &mut counters,
            false,
            Duration::from_millis(10),
        )
        .await
        .unwrap_err();

        assert_eq!(counters.tun_write_queue_drops, 1);
        assert_eq!(writer.queue_depth(), 1);
        assert!(error.to_string().contains("reconnecting"));
    }

    #[tokio::test]
    async fn tun_writer_rejects_an_oversized_batch_without_partial_admission() {
        let (tx, mut rx) = mpsc::channel(3);
        let writer = TunWriterHandle {
            tx,
            queued_packets: Arc::new(AtomicU64::new(0)),
            capacity: 3,
        };
        let mut counters = TunnelCounters::default();
        enqueue_reordered_return_packets_with_deadline(
            &writer,
            vec![
                ReorderedPacket {
                    sequence: 1,
                    path_id: 1,
                    payload: vec![1],
                },
                ReorderedPacket {
                    sequence: 2,
                    path_id: 1,
                    payload: vec![2],
                },
            ],
            &mut counters,
            false,
            Duration::from_millis(100),
        )
        .await
        .unwrap();

        let error = enqueue_reordered_return_packets_with_deadline(
            &writer,
            vec![
                ReorderedPacket {
                    sequence: 3,
                    path_id: 1,
                    payload: vec![3],
                },
                ReorderedPacket {
                    sequence: 4,
                    path_id: 1,
                    payload: vec![4],
                },
            ],
            &mut counters,
            false,
            Duration::from_millis(100),
        )
        .await
        .unwrap_err();

        assert!(error.to_string().contains("atomically admit 2"));
        assert_eq!(counters.tun_write_queue_drops, 2);
        assert_eq!(writer.queue_depth(), 2);
        let queued = rx.recv().await.unwrap();
        assert_eq!(
            queued
                .packets
                .iter()
                .map(|packet| packet.sequence)
                .collect::<Vec<_>>(),
            vec![1, 2]
        );
        assert!(rx.try_recv().is_err());
    }

    #[tokio::test]
    async fn reserved_sender_lanes_prioritize_control_then_repair_then_data() {
        let (control_tx, mut control_rx) = mpsc::channel(1);
        let (repair_tx, mut repair_rx) = mpsc::channel(1);
        let (data_tx, mut data_rx) = mpsc::channel(1);
        let metrics = Arc::new(PathSenderMetrics::new(1));
        let latest_control = LatestControlSlot::new(metrics);
        control_tx
            .send(PathSendWork::control(
                PacketKind::Control,
                XBondHeader::new(PacketKind::Control, 1, 1, 1, 1),
                Arc::new(vec![1]),
            ))
            .await
            .unwrap();
        repair_tx
            .send(PathSendWork::repair(
                XBondHeader::new(PacketKind::Repair, 1, 2, 1, 1),
                Arc::new(vec![2]),
            ))
            .await
            .unwrap();
        data_tx.send(primary_send_work(3)).await.unwrap();

        assert_eq!(
            receive_next_path_send_work(
                &mut control_rx,
                &latest_control,
                &mut repair_rx,
                &mut data_rx,
            )
            .await
            .unwrap()
            .lane,
            PathSendLane::Control
        );
        assert_eq!(
            receive_next_path_send_work(
                &mut control_rx,
                &latest_control,
                &mut repair_rx,
                &mut data_rx,
            )
            .await
            .unwrap()
            .lane,
            PathSendLane::Repair
        );
        assert_eq!(
            receive_next_path_send_work(
                &mut control_rx,
                &latest_control,
                &mut repair_rx,
                &mut data_rx,
            )
            .await
            .unwrap()
            .lane,
            PathSendLane::Data
        );
    }

    #[tokio::test]
    async fn latest_control_slot_delivers_newest_authoritative_update() {
        let metrics = Arc::new(PathSenderMetrics::new(1));
        let latest = LatestControlSlot::new(metrics.clone());
        let first = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(PacketKind::Control, 1, 10, 1, 1),
            Arc::new(vec![10]),
        );
        let newest = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(PacketKind::Control, 1, 11, 1, 1),
            Arc::new(vec![11]),
        );

        assert!(!latest.replace(first));
        assert!(latest.replace(newest));
        let received = time::timeout(Duration::from_millis(100), latest.recv())
            .await
            .unwrap();
        assert_eq!(received.header.sequence, 11);
        assert_eq!(received.payload.as_slice(), &[11]);
        metrics.record_dequeued(&received);

        let snapshot = metrics.snapshot(PathSendLane::Control, Instant::now());
        assert_eq!(snapshot.depth, 0);
        assert_eq!(snapshot.enqueue_drops, 1);
    }

    #[tokio::test]
    async fn critical_control_saturation_waits_until_deadline_then_fails_closed() {
        let (data_tx, data_rx) = mpsc::channel(1);
        let (control_tx, control_rx) = mpsc::channel(1);
        let (repair_tx, repair_rx) = mpsc::channel(1);
        let task = tokio::spawn(async move {
            std::future::pending::<()>().await;
            drop((data_rx, control_rx, repair_rx));
        });
        let sender = test_sender_handle(data_tx, control_tx, repair_tx, 1, task);
        let queued = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(PacketKind::Control, 1, 1, 1, 1),
            Arc::new(vec![1]),
        );
        try_enqueue_sender_lane(&sender.control_tx, &sender.metrics, queued).unwrap();
        let mut runtimes = HashMap::new();
        let mut counters = TunnelCounters::default();
        let mut critical = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(PacketKind::Control, 1, 2, 1, 1),
            Arc::new(vec![2]),
        );
        critical.deadline = Instant::now() + Duration::from_millis(20);

        let error = enqueue_critical_control_work(
            &sender,
            critical,
            &mut runtimes,
            1,
            &mut counters,
            "session-open",
            false,
        )
        .await
        .unwrap_err();

        assert!(error.to_string().contains("reconnecting"));
        assert_eq!(counters.control_lane_drops, 1);
        assert_eq!(
            sender
                .metrics
                .snapshot(PathSendLane::Control, Instant::now())
                .deadline_drops,
            1
        );
        sender.task.abort();
    }

    #[tokio::test]
    async fn critical_control_backpressure_delivers_when_capacity_recovers() {
        let (data_tx, _data_rx) = mpsc::channel(1);
        let (control_tx, mut control_rx) = mpsc::channel(1);
        let (repair_tx, _repair_rx) = mpsc::channel(1);
        let task = tokio::spawn(std::future::pending::<()>());
        let sender = test_sender_handle(data_tx, control_tx, repair_tx, 1, task);
        let queued = PathSendWork::control(
            PacketKind::Control,
            XBondHeader::new(PacketKind::Control, 1, 1, 1, 1),
            Arc::new(vec![1]),
        );
        try_enqueue_sender_lane(&sender.control_tx, &sender.metrics, queued).unwrap();
        let metrics = sender.metrics.clone();
        let release = tokio::spawn(async move {
            time::sleep(Duration::from_millis(10)).await;
            let dequeued = control_rx.recv().await.unwrap();
            metrics.record_dequeued(&dequeued);
            control_rx
        });
        let mut runtimes = HashMap::new();
        let mut counters = TunnelCounters::default();

        enqueue_critical_control_work(
            &sender,
            PathSendWork::control(
                PacketKind::Control,
                XBondHeader::new(PacketKind::Control, 1, 2, 1, 1),
                Arc::new(vec![2]),
            ),
            &mut runtimes,
            1,
            &mut counters,
            "session-open",
            false,
        )
        .await
        .unwrap();

        let mut control_rx = release.await.unwrap();
        let delivered = control_rx.recv().await.unwrap();
        assert_eq!(delivered.header.sequence, 2);
        sender.metrics.record_dequeued(&delivered);
        let snapshot = sender
            .metrics
            .snapshot(PathSendLane::Control, Instant::now());
        assert_eq!(snapshot.depth, 0);
        assert_eq!(snapshot.enqueue_drops, 0);
        assert_eq!(snapshot.deadline_drops, 0);
        assert_eq!(counters.control_lane_drops, 0);
        sender.task.abort();
    }

    #[tokio::test]
    async fn per_lane_metrics_report_depth_age_and_drop_classes() {
        let (data_tx, data_rx) = mpsc::channel(2);
        let (control_tx, control_rx) = mpsc::channel(1);
        let (repair_tx, repair_rx) = mpsc::channel(1);
        let task = tokio::spawn(async move {
            std::future::pending::<()>().await;
            drop((data_rx, control_rx, repair_rx));
        });
        let sender = test_sender_handle(data_tx, control_tx, repair_tx, 2, task);
        let mut data_work = primary_send_work(1);
        data_work.queued_at = Instant::now() - Duration::from_millis(25);
        try_enqueue_sender_lane(&sender.data_tx, &sender.metrics, data_work).unwrap();
        let control_work = PathSendWork::control(
            PacketKind::Heartbeat,
            XBondHeader::new(PacketKind::Heartbeat, 1, 2, 1, 1),
            Arc::new(vec![2]),
        );
        try_enqueue_sender_lane(&sender.control_tx, &sender.metrics, control_work).unwrap();
        let repair_work = PathSendWork::repair(
            XBondHeader::new(PacketKind::Repair, 1, 3, 1, 1),
            Arc::new(vec![3]),
        );
        try_enqueue_sender_lane(&sender.repair_tx, &sender.metrics, repair_work).unwrap();
        sender.metrics.record_enqueue_drop(PathSendLane::Repair);
        sender.metrics.record_deadline_drop(PathSendLane::Control);

        let mut senders = HashMap::from([(1, sender)]);
        let mut runtimes = HashMap::from([(1, TunnelPathRuntime::default())]);
        refresh_sender_queue_metrics(&mut runtimes, &senders);
        let runtime = runtimes.get(&1).unwrap();

        assert_eq!(runtime.sender_queue_depth, 1);
        assert_eq!(runtime.sender_queue_capacity, 2);
        assert!(runtime.sender_data_oldest_age_ms >= 20);
        assert_eq!(runtime.sender_control_queue_depth, 1);
        assert_eq!(runtime.sender_control_deadline_drops, 1);
        assert_eq!(runtime.sender_repair_queue_depth, 1);
        assert_eq!(runtime.sender_repair_enqueue_drops, 1);
        let json = sender_lane_metrics_json(&runtimes);
        assert_eq!(json[0]["data"]["depth"], 1);
        assert_eq!(json[0]["control"]["deadline_drops"], 1);
        assert_eq!(json[0]["repair"]["enqueue_drops"], 1);
        senders.remove(&1).unwrap().task.abort();
    }

    #[tokio::test]
    async fn send_budget_times_out_a_blocked_operation_before_lane_deadline() {
        let work = primary_send_work(1);
        let budget = udp_send_budget(&work, Instant::now()).unwrap();
        assert_eq!(budget, MAX_SOCKET_SEND_BLOCK);
        assert!(time::timeout(budget, std::future::pending::<()>())
            .await
            .is_err());
    }

    #[test]
    fn tun_writer_metrics_are_bounded_and_drain_without_per_packet_reports() {
        let metrics = TunWriterMetrics::for_paths([7, 8]);
        metrics.record_success(7, 1_200, 40, 15);
        metrics.record_success(u16::MAX, 300, 50, 20);
        metrics.record_failure();

        let snapshot = metrics.take_snapshot();
        assert_eq!(snapshot.packets, 2);
        assert_eq!(snapshot.payload_bytes, 1_500);
        assert_eq!(snapshot.queue_delay_micros, 90);
        assert_eq!(snapshot.write_micros, 35);
        assert_eq!(snapshot.failures, 1);
        assert_eq!(snapshot.repair_frames, 1);
        assert_eq!(snapshot.path_bytes.get(&7), Some(&1_200));
        assert!(!snapshot.path_bytes.contains_key(&8));
        assert_eq!(metrics.take_snapshot(), TunWriterMetricsSnapshot::default());
    }

    #[test]
    fn sender_deadlines_and_pmtu_errors_are_detected_without_mutating_mtu() {
        let mut work = primary_send_work(1);
        work.deadline = Instant::now() - Duration::from_millis(1);
        assert!(path_send_work_deadline_expired(&work, Instant::now()));
        assert!(udp_send_budget(&work, Instant::now()).is_none());
        let work = primary_send_work(2);
        assert!(udp_send_budget(&work, Instant::now()).unwrap() <= MAX_SOCKET_SEND_BLOCK);
        assert!(is_message_too_large_error(Some(90)));
        assert!(is_message_too_large_error(Some(10040)));
        assert!(!is_message_too_large_error(Some(19)));
        assert!(!is_message_too_large_error(None));
    }

    #[test]
    fn adaptive_resend_cache_uses_core_budget_and_reports_exact_eviction() {
        let mut cache = ResendCache::new_with_byte_capacity(
            10,
            REPAIR_CACHE_MAX_BYTES,
            REPAIR_CACHE_TTL_MICROS,
        );
        cache.insert(1, 1, Arc::new(vec![1; 2 * 1024 * 1024]), 1);
        cache.insert(1, 2, Arc::new(vec![2; 2 * 1024 * 1024]), 2);
        let mut counters = TunnelCounters::default();

        update_repair_cache_budget(&mut cache, 0, 3, &mut counters);

        assert_eq!(cache.byte_capacity(), REPAIR_CACHE_MIN_BYTES);
        assert_eq!(cache.bytes_len(), 0);
        assert_eq!(counters.repair_cache_evictions, 2);
        assert_eq!(counters.repair_cache_evicted_bytes, 4 * 1024 * 1024);
    }

    #[test]
    fn pre_recovery_gap_repair_is_conservative() {
        assert!(!pre_recovery_gap_repair_allowed(Some(0.5), 2));
        assert!(!pre_recovery_gap_repair_allowed(
            Some(PRE_RECOVERY_MIN_TUNNEL_LOSS - 0.001),
            PRE_RECOVERY_MIN_PENDING_GAP,
        ));
        assert!(!pre_recovery_gap_repair_allowed(
            None,
            PRE_RECOVERY_MIN_PENDING_GAP,
        ));
        assert!(pre_recovery_gap_repair_allowed(
            Some(PRE_RECOVERY_MIN_TUNNEL_LOSS),
            PRE_RECOVERY_MIN_PENDING_GAP,
        ));
    }
}
