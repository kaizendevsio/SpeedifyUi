use std::collections::{HashMap, HashSet, VecDeque};
use std::io::ErrorKind;
use std::net::{IpAddr, SocketAddr, ToSocketAddrs};
use std::path::PathBuf;
use std::process::Command as ProcessCommand;
use std::sync::Arc;
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
use tokio::sync::{mpsc, oneshot};
use tokio::time;
use xbond_core::{
    build_schedule, default_udp_socket_buffer_bytes, encode_sealed_payload_into,
    expand_schedule_for_recovery, is_ipv4_packet, precompute_transmission_plans, select_path_roles,
    select_path_roles_with_state, update_recovery_state, ClientConfig, FrameReceiver, PacketKind,
    PacketReorderBuffer, PathHealthSnapshot, PathIsolationStatus, ProbeAggregate, ProbePathStats,
    ReceiveOutcome, RecoveryConfig, RecoveryState, RecoveryStatus, RedundancyPolicy,
    RedundancyPolicyConfig, ReorderedPacket, RoleSelectionConfig, RoleSelectionState,
    RouteVerification, ScheduleControlMessage, ScheduleMode, SchedulePlan,
    XBondDiagnosticOverrideStatus, XBondFecStatus, XBondFrame, XBondHeader, XBondKey,
    XBondPathStatus, XBondProcessStatus, XBondReorderStatus, XBondRuntimeStatus, XBondStatus,
    XBondTun, XBondTunnelStatus, XorFecBlock,
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
    },
    Override {
        #[command(subcommand)]
        action: OverrideCommand,
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
                session_id: session_id.unwrap_or_else(now_micros),
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
                session_id: session_id.unwrap_or_else(now_micros),
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
            })
            .await?;
        }
        Command::Override { action } => {
            run_override_command(action).await?;
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
    bind_addr: String,
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

#[derive(Debug)]
struct PathSendWork {
    packet_kind: PacketKind,
    frame: Vec<u8>,
}

#[derive(Debug)]
struct PathSendReport {
    path_id: u16,
    packet_kind: PacketKind,
    encoded_bytes: u64,
    success: bool,
    error: Option<String>,
}

#[derive(Debug)]
struct PathSenderHandle {
    tx: mpsc::Sender<PathSendWork>,
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
}

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
    Status,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ControlResponse {
    ok: bool,
    message: String,
    active_override: Option<XBondDiagnosticOverrideStatus>,
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
            }
        }
        ControlRequest::ClearOverride => {
            *active_override = None;
            ControlResponse {
                ok: true,
                message: "XBond override cleared.".to_string(),
                active_override: None,
            }
        }
        ControlRequest::Status => ControlResponse {
            ok: true,
            message: if active_override.is_some() {
                "XBond override is active.".to_string()
            } else {
                "No XBond override is active.".to_string()
            },
            active_override: active_override.as_ref().map(ActiveScheduleOverride::status),
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
) -> ScheduleControlSignature {
    ScheduleControlSignature {
        schedule: schedule.clone(),
        redundancy_policy,
    }
}

#[cfg(unix)]
fn spawn_control_listener(
    socket_path: PathBuf,
    control_tx: mpsc::UnboundedSender<ControlEnvelope>,
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
    control_tx: mpsc::UnboundedSender<ControlEnvelope>,
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
    _control_tx: mpsc::UnboundedSender<ControlEnvelope>,
    _json_events: bool,
) -> Result<()> {
    Ok(())
}

async fn run_tunnel(options: TunnelOptions) -> Result<()> {
    let config = read_config(&options.config)?;
    let key_text = std::env::var(&options.key_env)
        .with_context(|| format!("{} environment variable is required", options.key_env))?;
    let key = XBondKey::from_passphrase(&key_text);
    let session_id = options.session_id.unwrap_or_else(now_micros);

    let specs = select_probe_paths(&config, &[], &[])?;
    let specs_by_id = specs
        .into_iter()
        .map(|spec| (spec.path_id, spec))
        .collect::<HashMap<_, _>>();
    let mut sockets: HashMap<u16, Arc<UdpSocket>> = HashMap::new();
    let mut senders: HashMap<u16, PathSenderHandle> = HashMap::new();
    let mut path_runtime: HashMap<u16, TunnelPathRuntime> = HashMap::new();
    let (control_tx, mut control_rx) = mpsc::unbounded_channel::<ControlEnvelope>();
    let (send_report_tx, mut send_report_rx) = mpsc::unbounded_channel::<PathSendReport>();
    spawn_control_listener(
        options.control_socket.clone(),
        control_tx,
        options.json_events,
    )?;

    let mut tun = XBondTun::open(&options.tun_name, options.tun_mtu).with_context(|| {
        format!(
            "failed to open XBond TUN {}; run as root or grant CAP_NET_ADMIN",
            options.tun_name
        )
    })?;
    let tun_reader = tun
        .try_clone()
        .with_context(|| format!("failed to clone XBond TUN {} for packet reader", tun.name()))?;
    let tun_queue_capacity = config.tun_queue_capacity.max(1);
    let inbound_queue_capacity = config.inbound_queue_capacity.max(1);
    let (tun_packet_tx, mut tun_packet_rx) = mpsc::channel::<Vec<u8>>(tun_queue_capacity);
    let tun_name = tun.name().to_string();
    let tun_read_mtu = usize::from(options.tun_mtu).max(2048);
    tokio::task::spawn_blocking(move || {
        let mut tun_reader = tun_reader;
        let mut buf = vec![0u8; tun_read_mtu];
        loop {
            match tun_reader.read_packet(&mut buf) {
                Ok(len) => {
                    if tun_packet_tx.blocking_send(buf[..len].to_vec()).is_err() {
                        break;
                    }
                }
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) => {
                    eprintln!("xbond XBond TUN reader for {tun_name} stopped: {error}");
                    break;
                }
            }
        }
    });

    let (inbound_tx, mut inbound_rx) = mpsc::channel::<InboundTunnelFrame>(inbound_queue_capacity);
    ensure_tunnel_sockets(
        &config,
        &specs_by_id,
        &mut sockets,
        &mut senders,
        &mut path_runtime,
        &inbound_tx,
        &send_report_tx,
        &key,
        options.json_events,
    )
    .await?;
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
    let mut transmission_policy = effective_transmission_policy(effective_policy, &recovery_status);
    let mut transmission_plans =
        precompute_transmission_plans(&schedule, transmission_policy, &health, policy_config);
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
    let mut pending_fec_source: Option<(u64, Vec<u8>)> = None;
    let mut inbound_receiver = FrameReceiver::new(config.realtime_deadline_ms * 1_000, 8192);
    let mut return_reorder = PacketReorderBuffer::new(8192, config.reorder_hold_ms * 1_000);
    let mut sequence = 0u64;
    let mut control_sequence = 1_000_000_000_000u64;
    let mut scheduler_tick = time::interval(Duration::from_secs(1));
    let mut reorder_tick =
        time::interval(Duration::from_millis(config.reorder_hold_ms.clamp(5, 100)));
    let mut last_throughput_sample = Instant::now();

    update_tunnel_throughput(
        &mut path_runtime,
        &mut counters,
        &mut last_throughput_sample,
    );
    write_tunnel_runtime_status(
        &config,
        &tun,
        options.tun_mtu,
        &counters,
        &roles,
        &schedule,
        effective_mode,
        effective_policy,
        &recovery_status,
        active_override.as_ref(),
        role_state.schedule_change_count,
        &return_reorder,
        tun_packet_rx.len(),
        inbound_rx.len(),
    )?;
    send_tunnel_schedule_control(
        &config,
        &schedule,
        transmission_policy,
        &mut path_runtime,
        &sockets,
        &key,
        session_id,
        &mut control_sequence,
        options.json_events,
        options.trace_packets,
    )
    .await?;
    last_control_signature = Some(schedule_control_signature(&schedule, transmission_policy));
    last_control_sent_at = Instant::now();

    loop {
        tokio::select! {
            _ = scheduler_tick.tick() => {
                ensure_tunnel_sockets(
                    &config,
                    &specs_by_id,
                    &mut sockets,
                    &mut senders,
                    &mut path_runtime,
                    &inbound_tx,
                    &send_report_tx,
                    &key,
                    options.json_events,
                ).await?;
                send_tunnel_heartbeats(
                    &config,
                    &mut path_runtime,
                    &sockets,
                    &key,
                    session_id,
                    options.json_events,
                ).await?;
                update_tunnel_throughput(
                    &mut path_runtime,
                    &mut counters,
                    &mut last_throughput_sample,
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
                schedule = build_effective_schedule(
                    effective_mode,
                    &roles,
                    &recovery_status,
                    recovery_config,
                );
                transmission_policy = effective_transmission_policy(effective_policy, &recovery_status);
                transmission_plans =
                    precompute_transmission_plans(&schedule, transmission_policy, &health, policy_config);
                let control_signature =
                    schedule_control_signature(&schedule, transmission_policy);
                if last_control_signature.as_ref() != Some(&control_signature)
                    || last_control_sent_at.elapsed() >= Duration::from_secs(5)
                {
                    send_tunnel_schedule_control(
                        &config,
                        &schedule,
                        transmission_policy,
                        &mut path_runtime,
                        &sockets,
                        &key,
                        session_id,
                        &mut control_sequence,
                        options.json_events,
                        options.trace_packets,
                    ).await?;
                    last_control_signature = Some(control_signature);
                    last_control_sent_at = Instant::now();
                }
                write_tunnel_runtime_status(
                    &config,
                    &tun,
                    options.tun_mtu,
                    &counters,
                    &roles,
                    &schedule,
                    effective_mode,
                    effective_policy,
                    &recovery_status,
                    active_override.as_ref(),
                    role_state.schedule_change_count,
                    &return_reorder,
                    tun_packet_rx.len(),
                    inbound_rx.len(),
                )?;
            }

            _ = reorder_tick.tick() => {
                let ready = return_reorder.drain_ready(now_micros());
                write_reordered_return_packets(
                    &mut tun,
                    ready,
                    &mut path_runtime,
                    &mut counters,
                )?;
            }

            Some(envelope) = control_rx.recv() => {
                let mut response =
                    handle_control_request(envelope.request, &mut active_override);
                (effective_mode, effective_policy) =
                    effective_mode_and_policy(&config, &mut active_override);
                recovery_status =
                    update_recovery_state(&mut recovery_state, effective_policy, &health, recovery_config);
                schedule = build_effective_schedule(
                    effective_mode,
                    &roles,
                    &recovery_status,
                    recovery_config,
                );
                transmission_policy = effective_transmission_policy(effective_policy, &recovery_status);
                transmission_plans =
                    precompute_transmission_plans(&schedule, transmission_policy, &health, policy_config);
                let control_signature =
                    schedule_control_signature(&schedule, transmission_policy);
                if response.ok {
                    match send_tunnel_schedule_control(
                        &config,
                        &schedule,
                        transmission_policy,
                        &mut path_runtime,
                        &sockets,
                        &key,
                        session_id,
                        &mut control_sequence,
                        options.json_events,
                        options.trace_packets,
                    ).await {
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
                    &schedule,
                    effective_mode,
                    effective_policy,
                    &recovery_status,
                    active_override.as_ref(),
                    role_state.schedule_change_count,
                    &return_reorder,
                    tun_packet_rx.len(),
                    inbound_rx.len(),
                )?;
                let _ = envelope.response_tx.send(response);
            }

            Some(report) = send_report_rx.recv() => {
                if report.success {
                    record_tunnel_send_report_success(
                        &mut path_runtime,
                        report.path_id,
                        report.packet_kind,
                        report.encoded_bytes,
                        &mut counters,
                    );
                } else {
                    record_tunnel_send_failure(&mut path_runtime, report.path_id);
                    if options.json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "path-sender-send-failed",
                                "path_id": report.path_id,
                                "packet_kind": report.packet_kind,
                                "error": report.error.unwrap_or_else(|| "unknown send failure".to_string()),
                            })
                        );
                    }
                }
            }

            Some(packet) = tun_packet_rx.recv() => {
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

                let packet_transmissions = transmission_plans.for_packet_len(
                    packet.len(),
                    policy_config.interactive_packet_threshold_bytes,
                );

                if packet_transmissions.is_empty() {
                    if options.json_events && options.trace_packets {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "packet-dropped-no-live-path",
                                "bytes": packet.len(),
                            })
                        );
                    }
                    continue;
                }

                sequence += 1;
                counters.data_bytes_sent = counters
                    .data_bytes_sent
                    .saturating_add(packet.len() as u64);
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
                    let encoded = encode_tunnel_payload(&header, &packet, &key, &mut counters)?;
                    let work = PathSendWork {
                        packet_kind: transmission.packet_kind,
                        frame: encoded,
                    };
                    let enqueue_result = if transmission.packet_kind == PacketKind::Data {
                        sender.tx.send(work).await.map_err(|_| {
                            std::io::Error::new(
                                ErrorKind::BrokenPipe,
                                "XBond primary path sender stopped",
                            )
                        })
                    } else {
                        match sender.tx.try_send(work) {
                            Ok(()) => Ok(()),
                            Err(mpsc::error::TrySendError::Full(_)) => {
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

                let fec_transmissions = packet_transmissions
                    .iter()
                    .filter(|transmission| transmission.packet_kind == PacketKind::Fec);
                if packet_transmissions
                    .iter()
                    .any(|transmission| transmission.packet_kind == PacketKind::Fec)
                {
                    if let Some((base_sequence, first_payload)) = pending_fec_source.take() {
                        let fec_payload =
                            XorFecBlock::encode(base_sequence, &first_payload, &packet)?;
                        for transmission in fec_transmissions {
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
                            let encoded =
                                encode_tunnel_payload(&header, &fec_payload, &key, &mut counters)?;
                            match sender.tx.try_send(PathSendWork {
                                packet_kind: PacketKind::Fec,
                                frame: encoded,
                            }) {
                                Ok(()) => {}
                                Err(mpsc::error::TrySendError::Full(_)) => {
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
                    } else {
                        pending_fec_source = Some((sequence, packet.clone()));
                    }
                }

                if options.json_events && options.trace_packets {
                    println!(
                        "{}",
                        serde_json::json!({
                            "event": "packet-sent",
                            "sequence": sequence,
                            "bytes": packet.len(),
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

            Some(inbound) = inbound_rx.recv() => {
                counters.decoded_frames = counters.decoded_frames.saturating_add(1);
                if is_expected_ack(&inbound.frame, session_id, inbound.frame.header.sequence) {
                    record_tunnel_heartbeat_ack(
                        &mut path_runtime,
                        inbound.path_id,
                        &inbound.frame,
                    );
                    if options.json_events && options.trace_packets {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "health-heartbeat-ack",
                                "path_id": inbound.path_id,
                                "sequence": inbound.frame.header.sequence,
                            })
                        );
                    }
                    continue;
                }

                let outcome = inbound_receiver.observe(&inbound.frame, now_micros());
                match outcome {
                    ReceiveOutcome::Accepted if is_data_like(inbound.frame.header.kind) => {
                        if inbound.frame.header.kind == PacketKind::Duplicate {
                            let runtime = path_runtime.entry(inbound.path_id).or_default();
                            runtime.duplicate_useful_packets =
                                runtime.duplicate_useful_packets.saturating_add(1);
                        }
                        if is_ipv4_packet(&inbound.frame.payload) {
                            let sequence = inbound.frame.header.sequence;
                            let path_id = inbound.path_id;
                            let payload_len = inbound.frame.payload.len();
                            let frame_deadline = inbound
                                .frame
                                .header
                                .send_micros
                                .saturating_add(config.realtime_deadline_ms * 1_000);
                            let ready = return_reorder.push(
                                sequence,
                                path_id,
                                inbound.frame.payload,
                                now_micros(),
                                frame_deadline,
                            );
                            write_reordered_return_packets(
                                &mut tun,
                                ready,
                                &mut path_runtime,
                                &mut counters,
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
                    ReceiveOutcome::Duplicate => {
                        if is_data_like(inbound.frame.header.kind) {
                            let runtime = path_runtime.entry(inbound.path_id).or_default();
                            runtime.duplicate_bytes_received = runtime
                                .duplicate_bytes_received
                                .saturating_add(inbound.frame.payload.len() as u64);
                            runtime.duplicate_late_packets =
                                runtime.duplicate_late_packets.saturating_add(1);
                        }
                        counters.duplicate_packets_dropped += 1;
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
                        counters.late_packets_dropped += 1;
                    }
                    _ => {}
                }
            }

            else => break,
        };
    }

    Ok(())
}

fn write_reordered_return_packets(
    tun: &mut XBondTun,
    packets: Vec<ReorderedPacket>,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    counters: &mut TunnelCounters,
) -> Result<()> {
    for packet in packets {
        if let Err(error) = tun.write_packet(&packet.payload) {
            eprintln!(
                "xbond client failed to write return packet to XBond TUN {}: {error}",
                tun.name()
            );
            continue;
        }
        counters.data_packets_received += 1;
        counters.data_bytes_received = counters
            .data_bytes_received
            .saturating_add(packet.payload.len() as u64);
        let runtime = path_runtime.entry(packet.path_id).or_default();
        runtime.bytes_received = runtime
            .bytes_received
            .saturating_add(packet.payload.len() as u64);
    }

    Ok(())
}

async fn ensure_tunnel_sockets(
    config: &ClientConfig,
    specs_by_id: &HashMap<u16, ProbePathSpec>,
    sockets: &mut HashMap<u16, Arc<UdpSocket>>,
    senders: &mut HashMap<u16, PathSenderHandle>,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    inbound_tx: &mpsc::Sender<InboundTunnelFrame>,
    send_report_tx: &mpsc::UnboundedSender<PathSendReport>,
    key: &XBondKey,
    json_events: bool,
) -> Result<()> {
    for (path_id, spec) in specs_by_id {
        path_runtime.entry(*path_id).or_default();

        if !interface_is_live(spec.interface_name.as_deref()) {
            sockets.remove(path_id);
            senders.remove(path_id);
            continue;
        }

        if let Some(socket) = sockets.get(path_id) {
            if !senders.contains_key(path_id) {
                let sender = spawn_tunnel_sender(
                    *path_id,
                    socket.clone(),
                    config.tun_queue_capacity.max(1),
                    send_report_tx.clone(),
                );
                senders.insert(*path_id, sender);
            }
            continue;
        }

        let socket = match create_isolated_udp_socket(
            &spec.bind_addr,
            spec.bind_device.as_deref(),
            config.udp_socket_buffer_bytes,
        ) {
            Ok((socket, _isolation)) => socket,
            Err(error) => {
                record_tunnel_send_failure(path_runtime, *path_id);
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
        spawn_tunnel_receiver(*path_id, socket.clone(), inbound_tx.clone(), key.clone());
        let sender = spawn_tunnel_sender(
            *path_id,
            socket.clone(),
            config.tun_queue_capacity.max(1),
            send_report_tx.clone(),
        );
        senders.insert(*path_id, sender);
        sockets.insert(*path_id, socket);
    }

    Ok(())
}

fn spawn_tunnel_sender(
    path_id: u16,
    socket: Arc<UdpSocket>,
    capacity: usize,
    report_tx: mpsc::UnboundedSender<PathSendReport>,
) -> PathSenderHandle {
    let (tx, mut rx) = mpsc::channel::<PathSendWork>(capacity.max(1));
    tokio::spawn(async move {
        while let Some(work) = rx.recv().await {
            let encoded_bytes = work.frame.len() as u64;
            let report = match socket.send(&work.frame).await {
                Ok(_) => PathSendReport {
                    path_id,
                    packet_kind: work.packet_kind,
                    encoded_bytes,
                    success: true,
                    error: None,
                },
                Err(error) => PathSendReport {
                    path_id,
                    packet_kind: work.packet_kind,
                    encoded_bytes,
                    success: false,
                    error: Some(error.to_string()),
                },
            };
            let _ = report_tx.send(report);
        }
    });
    PathSenderHandle { tx }
}

fn spawn_tunnel_receiver(
    path_id: u16,
    socket: Arc<UdpSocket>,
    inbound_tx: mpsc::Sender<InboundTunnelFrame>,
    key: XBondKey,
) {
    tokio::spawn(async move {
        let mut buf = vec![0u8; 4096];
        loop {
            let len = match socket.recv(&mut buf).await {
                Ok(len) => len,
                Err(error) => {
                    eprintln!("xbond path receiver for path {path_id} hit UDP recv error: {error}");
                    time::sleep(Duration::from_millis(50)).await;
                    continue;
                }
            };
            let Ok(frame) = XBondFrame::decode_sealed(&buf[..len], &key) else {
                continue;
            };
            if inbound_tx
                .send(InboundTunnelFrame { path_id, frame })
                .await
                .is_err()
            {
                break;
            }
        }
    });
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
                path.queue_depth = runtime.pending_heartbeats.len() as u32;
                path.send_failure_streak = runtime.send_failures;
                path.stale_ack_ms = runtime.last_ack_at.map(|last_ack_at| {
                    Instant::now()
                        .duration_since(last_ack_at)
                        .as_millis()
                        .min(u128::from(u64::MAX)) as u64
                });
                path.queue_pressure =
                    (runtime.pending_heartbeats.len() as f64 / 3.0).clamp(0.0, 1.0);
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

fn record_tunnel_send_report_success(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    path_id: u16,
    packet_kind: PacketKind,
    bytes: u64,
    counters: &mut TunnelCounters,
) {
    let runtime = path_runtime.entry(path_id).or_default();
    runtime.send_failures = 0;
    runtime.bytes_sent = runtime.bytes_sent.saturating_add(bytes);

    match packet_kind {
        PacketKind::Data => counters.data_packets_sent += 1,
        PacketKind::Duplicate => counters.duplicate_packets_sent += 1,
        PacketKind::Fec => counters.fec_packets_sent += 1,
        _ => {}
    }
}

fn record_tunnel_send_failure(path_runtime: &mut HashMap<u16, TunnelPathRuntime>, path_id: u16) {
    let runtime = path_runtime.entry(path_id).or_default();
    runtime.send_failures = runtime.send_failures.saturating_add(1);
}

fn encode_tunnel_payload(
    header: &XBondHeader,
    payload: &[u8],
    key: &XBondKey,
    counters: &mut TunnelCounters,
) -> Result<Vec<u8>> {
    let started = Instant::now();
    let mut encoded = Vec::with_capacity(payload.len().saturating_add(64));
    encode_sealed_payload_into(header, payload, key, &mut encoded)?;
    counters.encoded_frames = counters.encoded_frames.saturating_add(1);
    counters.encode_micros_total = counters
        .encode_micros_total
        .saturating_add(started.elapsed().as_micros().min(u128::from(u64::MAX)) as u64);
    Ok(encoded)
}

const TUNNEL_HEALTH_WINDOW: usize = 20;
const HEARTBEAT_SEQUENCE_MASK: u64 = (1u64 << 48) - 1;

async fn send_tunnel_schedule_control(
    config: &ClientConfig,
    schedule: &SchedulePlan,
    redundancy_policy: RedundancyPolicy,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
    key: &XBondKey,
    session_id: u64,
    control_sequence: &mut u64,
    json_events: bool,
    trace_packets: bool,
) -> Result<()> {
    if sockets.is_empty() {
        return Ok(());
    }

    *control_sequence = control_sequence.saturating_add(1);
    let sequence = *control_sequence;
    let payload = serde_json::to_vec(&ScheduleControlMessage {
        schedule: schedule.clone(),
        redundancy_policy,
        policy_config: RedundancyPolicyConfig {
            interactive_packet_threshold_bytes: config.interactive_packet_threshold_bytes,
            duplicate_loss_threshold: config.duplicate_loss_threshold,
            backup_loss_disable_threshold: config.backup_loss_disable_threshold,
        },
        paths: tunnel_health(config, path_runtime, sockets),
    })?;

    for (path_id, socket) in sockets {
        let frame = XBondFrame::new(
            XBondHeader::new(
                PacketKind::Control,
                session_id,
                sequence,
                now_micros(),
                *path_id,
            ),
            payload.clone(),
        );
        let encoded = frame.encode_sealed(key)?;
        if let Err(error) = socket.send(&encoded).await {
            record_tunnel_send_failure(path_runtime, *path_id);
            if json_events {
                println!(
                    "{}",
                    serde_json::json!({
                        "event": "schedule-control-send-failed",
                        "path_id": path_id,
                        "sequence": sequence,
                        "error": error.to_string(),
                    })
                );
            }
        }
    }

    if json_events && trace_packets {
        println!(
            "{}",
            serde_json::json!({
                "event": "schedule-control-sent",
                "sequence": sequence,
                "schedule": schedule,
            })
        );
    }

    Ok(())
}

async fn send_tunnel_heartbeats(
    config: &ClientConfig,
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    sockets: &HashMap<u16, Arc<UdpSocket>>,
    key: &XBondKey,
    session_id: u64,
    json_events: bool,
) -> Result<()> {
    let timeout = tunnel_heartbeat_timeout(config);
    let path_ids = sockets.keys().copied().collect::<Vec<_>>();
    for path_id in path_ids {
        let Some(socket) = sockets.get(&path_id) else {
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

        let frame = XBondFrame::new(
            XBondHeader::new(
                PacketKind::Heartbeat,
                session_id,
                sequence,
                now_micros(),
                path_id,
            ),
            b"health".to_vec(),
        );
        let encoded = frame.encode_sealed(key)?;
        match socket.send(&encoded).await {
            Ok(_) => {
                let runtime = path_runtime.entry(path_id).or_default();
                runtime.pending_heartbeats.insert(sequence, Instant::now());
            }
            Err(error) => {
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
                            "error": error.to_string(),
                        })
                    );
                }
            }
        }
    }

    Ok(())
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

fn record_tunnel_heartbeat_ack(
    path_runtime: &mut HashMap<u16, TunnelPathRuntime>,
    path_id: u16,
    frame: &XBondFrame,
) {
    let runtime = path_runtime.entry(path_id).or_default();
    if runtime
        .pending_heartbeats
        .remove(&frame.header.sequence)
        .is_none()
    {
        return;
    }

    let rtt_ms = now_micros().saturating_sub(frame.header.send_micros) as f64 / 1_000.0;
    runtime.send_failures = 0;
    runtime.last_ack_at = Some(Instant::now());
    record_tunnel_health_sample(runtime, true, Some(rtt_ms));
}

fn record_tunnel_health_sample(
    runtime: &mut TunnelPathRuntime,
    delivered: bool,
    rtt_ms: Option<f64>,
) {
    if runtime.health_window.len() == TUNNEL_HEALTH_WINDOW {
        runtime.health_window.pop_front();
    }
    runtime.health_window.push_back(delivered);

    if let Some(rtt_ms) = rtt_ms.filter(|value| value.is_finite()) {
        if runtime.rtt_samples_ms.len() == TUNNEL_HEALTH_WINDOW {
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
    schedule: &SchedulePlan,
    effective_mode: ScheduleMode,
    effective_policy: RedundancyPolicy,
    recovery_status: &RecoveryStatus,
    active_override: Option<&ActiveScheduleOverride>,
    schedule_change_count: u64,
    return_reorder: &PacketReorderBuffer,
    tun_queue_depth: usize,
    inbound_queue_depth: usize,
) -> Result<()> {
    let paths = roles
        .iter()
        .cloned()
        .map(|scored_path| scored_path.path)
        .collect();

    write_runtime_status(
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
            },
            anchor_path_id: schedule.anchor_path_id,
            schedule: Some(schedule.clone()),
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
            },
            recovery: recovery_status.clone(),
            diagnostic_override: active_override.map(ActiveScheduleOverride::status),
            schedule_change_count,
            message: Some("XBond tunnel is running.".to_string()),
            ..XBondRuntimeStatus::default()
        },
    )
}

fn is_data_like(kind: PacketKind) -> bool {
    matches!(kind, PacketKind::Data | PacketKind::Duplicate)
}

async fn prepare_probe_path(
    spec: ProbePathSpec,
    server: &str,
    target_ip: Option<IpAddr>,
) -> Result<PreparedProbePath> {
    let socket = match create_isolated_udp_socket(
        &spec.bind_addr,
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
                spec.bind_addr,
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

fn configured_or_default_bind_addr(path: &xbond_core::PathConfig) -> Result<String> {
    if let Some(bind_addr) = &path.bind_addr {
        return Ok(bind_addr.clone());
    }

    if path.interface_name.is_some() {
        return Ok("0.0.0.0:0".to_string());
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
        bind_addr,
        bind_device: None,
    })
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

fn write_runtime_status(config: &ClientConfig, status: XBondRuntimeStatus) -> Result<()> {
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
        })
        .collect()
}

fn interface_is_live(interface_name: Option<&str>) -> bool {
    let Some(interface_name) = interface_name.filter(|value| !value.trim().is_empty()) else {
        return true;
    };

    read_interface_state(interface_name).is_live
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

        let live = matches!(operstate.as_str(), "up" | "unknown") && carrier != "0";
        return InterfaceState { is_live: live };
    }

    #[cfg(not(target_os = "linux"))]
    {
        let _ = interface_name;
        InterfaceState { is_live: true }
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

#[cfg(test)]
mod tests {
    use super::*;

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
        assert_eq!(paths[1].bind_addr, "198.51.100.10:0");
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

        assert_eq!(paths[0].bind_addr, "0.0.0.0:0");
        assert_eq!(paths[0].bind_device.as_deref(), Some("eth0"));
    }
}
