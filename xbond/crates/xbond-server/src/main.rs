use anyhow::{Context, Result};
use clap::{ArgAction, Parser};
use serde::Serialize;
use socket2::{Domain, Protocol, Socket, Type};
use std::collections::{HashMap, HashSet, VecDeque};
use std::io::ErrorKind;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::net::UdpSocket;
use tokio::sync::mpsc;
use tokio::time;
use xbond_core::{
    build_transmission_plan, decode_sealed_payload_into, encode_sealed_payload_into,
    is_ipv4_packet, precompute_transmission_plans, FrameReceiver, PacketKind, PacketReorderBuffer,
    PacketTransmissionPlans, PathHealthSnapshot, ReceiveOutcome, RedundancyPolicy,
    RedundancyPolicyConfig, ReorderStats, ReorderedPacket, ResendCache, ScheduleControlMessage,
    SchedulePlan, XBondControlMessage, XBondFrame, XBondHeader, XBondKey, XBondRepairStatus,
    XBondServerIngressReorderStatus, XBondServerRecoveryStatus, XBondTun, XorFecBlock,
};

const DEFAULT_TUN_QUEUE_CAPACITY: usize = 2048;
const DEFAULT_INBOUND_QUEUE_CAPACITY: usize = 4096;
const DEFAULT_UDP_SOCKET_BUFFER_BYTES: usize = 4 * 1024 * 1024;
const REPAIR_CACHE_CAPACITY: usize = 4096;
const REPAIR_CACHE_TTL_MICROS: u64 = 3_000_000;
const REPAIR_REQUEST_INTERVAL_MICROS: u64 = 75_000;
const MAX_REPAIR_REQUESTS: usize = 64;

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
}

#[derive(Debug)]
struct InboundServerFrame {
    frame: XBondFrame,
    peer: SocketAddr,
}

#[derive(Debug)]
struct ReturnSendWork {
    packet_kind: PacketKind,
    peer: SocketAddr,
    header: XBondHeader,
    payload: Arc<Vec<u8>>,
}

#[derive(Debug)]
struct ReturnSendReport {
    path_id: u16,
    packet_kind: PacketKind,
    encoded_bytes: u64,
    success: bool,
    peer: SocketAddr,
    error: Option<String>,
}

#[derive(Debug)]
struct ReturnSenderHandle {
    tx: mpsc::Sender<ReturnSendWork>,
}

#[derive(Debug, Clone, PartialEq)]
struct ReturnControl {
    schedule: SchedulePlan,
    policy: RedundancyPolicy,
    policy_config: RedundancyPolicyConfig,
    recovery_active: bool,
    transmission_plans: PacketTransmissionPlans,
}

#[derive(Debug, Serialize)]
struct ServerRuntimeStatus {
    running: bool,
    bind: String,
    tun: Option<String>,
    updated_at_micros: u64,
    return_schedule: Option<ServerReturnScheduleStatus>,
    ingress_reorder: ServerIngressReorderStatus,
    repair: XBondRepairStatus,
    counters: TunnelCounters,
}

#[derive(Debug, Serialize)]
struct ServerReturnScheduleStatus {
    policy: RedundancyPolicy,
    recovery_active: bool,
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

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let key_text = std::env::var(&args.key_env)?;
    let key = XBondKey::from_passphrase(&key_text);
    let socket = Arc::new(bind_udp_socket(&args.bind, args.udp_socket_buffer_bytes).await?);
    let mut receiver = FrameReceiver::new(args.realtime_deadline_ms * 1_000, 8192);
    let mut tun = match args.tun_name.as_deref() {
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
    let mut reorder =
        PacketReorderBuffer::new(args.ingress_reorder_capacity, current_ingress_hold_micros);
    let mut peers: HashMap<u16, SocketAddr> = HashMap::new();
    let mut return_senders: HashMap<u16, ReturnSenderHandle> = HashMap::new();
    let mut return_control: Option<ReturnControl> = None;
    let mut resend_cache = ResendCache::new(REPAIR_CACHE_CAPACITY, REPAIR_CACHE_TTL_MICROS);
    let mut repair = XBondRepairStatus::default();
    let mut reverse_sequence = initial_reverse_sequence();
    let mut control_sequence = 2_000_000_000_000u64;
    let mut last_server_recovery_status_sent = Instant::now();
    let mut last_session_id = 0u64;
    let mut reorder_session_id = 0u64;
    let mut reorder_tick = time::interval(Duration::from_millis(
        args.realtime_deadline_ms.clamp(5, 50),
    ));
    let mut status_tick = time::interval(Duration::from_secs(1));
    let mut json_status_event_counter = 0u32;

    let (udp_frame_tx, mut udp_frame_rx) =
        mpsc::channel::<InboundServerFrame>(args.inbound_queue_capacity.max(1));
    let (send_report_tx, mut send_report_rx) = mpsc::unbounded_channel::<ReturnSendReport>();
    let recv_socket = socket.clone();
    let recv_key = key.clone();
    tokio::spawn(async move {
        let mut buf = vec![0u8; 4096];
        let mut payload = Vec::with_capacity(4096);
        loop {
            let (len, peer) = match recv_socket.recv_from(&mut buf).await {
                Ok(result) => result,
                Err(error) => {
                    eprintln!("xbond server UDP recv error: {error}");
                    time::sleep(Duration::from_millis(50)).await;
                    continue;
                }
            };
            let Ok(header) = decode_sealed_payload_into(&buf[..len], &recv_key, &mut payload)
            else {
                continue;
            };
            let frame = XBondFrame::new(
                header,
                std::mem::replace(&mut payload, Vec::with_capacity(4096)),
            );
            if udp_frame_tx
                .send(InboundServerFrame { frame, peer })
                .await
                .is_err()
            {
                break;
            }
        }
    });

    let (tun_packet_tx, mut tun_packet_rx) =
        mpsc::channel::<Vec<u8>>(args.tun_queue_capacity.max(1));
    if let Some(tun_ref) = &tun {
        let mut tun_reader = tun_ref.try_clone()?;
        let tun_name = tun_ref.name().to_string();
        let tun_mtu = usize::from(args.tun_mtu).max(2048);
        tokio::task::spawn_blocking(move || {
            let mut buf = vec![0u8; tun_mtu];
            loop {
                match tun_reader.read_packet(&mut buf) {
                    Ok(len) => {
                        if tun_packet_tx.blocking_send(buf[..len].to_vec()).is_err() {
                            break;
                        }
                    }
                    Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                    Err(error) => {
                        eprintln!("xbond server TUN reader for {tun_name} stopped: {error}");
                        break;
                    }
                }
            }
        });
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
            })
        );
    } else {
        println!("xbond-server listening on {}", args.bind);
    }
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
        &hold_controller,
    )?;
    loop {
        tokio::select! {
            Some(inbound) = udp_frame_rx.recv() => {
                let frame = inbound.frame;
                let peer = inbound.peer;
                if last_session_id != 0 && frame.header.session_id != last_session_id {
                    peers.clear();
                    return_senders.clear();
                    return_control = None;
                    resend_cache = ResendCache::new(REPAIR_CACHE_CAPACITY, REPAIR_CACHE_TTL_MICROS);
                    if hold_controller.reset_to_normal(reorder.stats(), &repair) {
                        current_ingress_hold_micros = hold_controller.current_hold_micros();
                        reorder.set_hold_micros(current_ingress_hold_micros);
                    }
                }
                last_session_id = frame.header.session_id;
                if is_tunnel_payload(frame.header.kind)
                    && reorder_session_id != frame.header.session_id
                {
                    reorder.reset();
                    fec_recovery = FecRecovery::new(8192);
                    reorder_session_id = frame.header.session_id;
                }
                if frame.header.path_id != 0 {
                    peers.insert(frame.header.path_id, peer);
                }
                if let Some(control) = parse_return_control(&frame) {
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
                                    "session_id": frame.header.session_id,
                                    "recovery_active": control.recovery_active,
                                    "adaptive": adaptive,
                                })
                            );
                        }
                    }
                    return_control = Some(control);
                    if args.json_events && schedule_changed {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "return-schedule-updated",
                                "session_id": frame.header.session_id,
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
                    let status = build_server_recovery_status(
                        return_control
                            .as_ref()
                            .is_some_and(|control| control.recovery_active),
                        &reorder,
                        &repair,
                        &hold_controller,
                        args.ingress_reorder_capacity,
                    );
                    send_server_recovery_status(
                        &socket,
                        &key,
                        &peers,
                        last_session_id,
                        &mut control_sequence,
                        status,
                        args.json_events,
                    )
                    .await?;
                    last_server_recovery_status_sent = Instant::now();
                }

                let should_ack = matches!(
                    frame.header.kind,
                    PacketKind::Heartbeat | PacketKind::Control
                );
                let outcome = receiver.observe(&frame, now_micros());
                let ack_sent = should_ack
                    && matches!(
                        outcome,
                        ReceiveOutcome::Accepted | ReceiveOutcome::Duplicate
                    );
                if ack_sent {
                    let reply = build_ack_frame(&frame);
                    let encoded = reply.encode_sealed(&key)?;
                    if let Err(error) = socket.send_to(&encoded, peer).await {
                        if args.json_events {
                            println!(
                                "{}",
                                serde_json::json!({
                                    "event": "ack-send-failed",
                                    "peer": peer.to_string(),
                                    "path_id": frame.header.path_id,
                                    "sequence": frame.header.sequence,
                                    "error": error.to_string(),
                                })
                            );
                        }
                    }
                }

                if args.json_events && args.trace_packets {
                    print_packet_event(
                        event_name(outcome),
                        outcome,
                        &frame,
                        &peer.to_string(),
                        ack_sent,
                        &receiver,
                    );
                }

                if outcome == ReceiveOutcome::Accepted {
                    if let Some(sequences) = parse_repair_request(&frame) {
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
                        )
                        .await?;
                        continue;
                    }
                }

                let mut forwarded_packets = 0u64;
                let mut dropped_reason = None;
                let trace_tunnel_payload =
                    (args.json_events && args.trace_packets && is_tunnel_payload(frame.header.kind))
                        .then(|| (frame.header.clone(), frame.payload.len()));
                if outcome == ReceiveOutcome::Accepted && is_data_like(frame.header.kind) {
                    data_packets_received += 1;
                    let header = frame.header.clone();
                    let payload = frame.payload;
                    let fec_is_active = return_control
                        .as_ref()
                        .is_some_and(|control| !control.schedule.fec_path_ids.is_empty());
                    let recovered_packets = if fec_is_active {
                        fec_recovery.observe_data(
                            header.session_id,
                            header.sequence,
                            payload.clone(),
                        )
                    } else {
                        Vec::new()
                    };
                    if fec_recovery.mark_delivered(header.session_id, header.sequence) {
                        if is_ipv4_packet(&payload) {
                            if let Some(tun) = &mut tun {
                                let ready = reorder.push(
                                    header.sequence,
                                    if header.kind == PacketKind::Repair {
                                        u16::MAX
                                    } else {
                                        header.path_id
                                    },
                                    payload,
                                    monotonic_micros(),
                                    0,
                                );
                                let delivered = write_reordered_packets(tun, ready, &mut repair)?;
                                data_packets_forwarded =
                                    data_packets_forwarded.saturating_add(delivered);
                                forwarded_packets = forwarded_packets.saturating_add(delivered);
                                send_repair_requests_for_ingress_gaps(
                                    &socket,
                                    &key,
                                    &peers,
                                    last_session_id,
                                    &mut control_sequence,
                                    &mut reorder,
                                    &mut repair,
                                    return_control
                                        .as_ref()
                                        .is_some_and(|control| control.recovery_active),
                                    args.json_events,
                                )
                                .await?;
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
                            if let Some(tun) = &mut tun {
                                let ready = reorder.push(
                                    recovered.sequence,
                                    0,
                                    recovered.payload,
                                    monotonic_micros(),
                                    0,
                                );
                                let delivered = write_reordered_packets(tun, ready, &mut repair)?;
                                data_packets_forwarded =
                                    data_packets_forwarded.saturating_add(delivered);
                                forwarded_packets = forwarded_packets.saturating_add(delivered);
                            }
                            fec_packets_recovered += 1;
                        } else {
                            non_ipv4_packets_dropped += 1;
                        }
                    }
                } else if outcome == ReceiveOutcome::Accepted && frame.header.kind == PacketKind::Fec {
                    fec_packets_received += 1;
                    let session_id = frame.header.session_id;
                    match XorFecBlock::decode(&frame.payload) {
                        Ok(block) => {
                            for recovered in fec_recovery.observe_fec(session_id, block) {
                                if !fec_recovery.mark_delivered(session_id, recovered.sequence)
                                {
                                    continue;
                                }
                                if is_ipv4_packet(&recovered.payload) {
                                    if let Some(tun) = &mut tun {
                                        let ready = reorder.push(
                                            recovered.sequence,
                                            0,
                                            recovered.payload,
                                            monotonic_micros(),
                                            0,
                                        );
                                        let delivered = write_reordered_packets(tun, ready, &mut repair)?;
                                        data_packets_forwarded =
                                            data_packets_forwarded.saturating_add(delivered);
                                        forwarded_packets = forwarded_packets.saturating_add(delivered);
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

                if frame.header.kind == PacketKind::Repair
                    && matches!(outcome, ReceiveOutcome::Duplicate | ReceiveOutcome::Expired)
                {
                    repair.late_frames = repair.late_frames.saturating_add(1);
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
                let _ = report.encoded_bytes;
                if report.success && report.packet_kind == PacketKind::Repair {
                    repair.frames_sent = repair.frames_sent.saturating_add(1);
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
            }

            _ = reorder_tick.tick() => {
                if let Some(tun) = &mut tun {
                    let delivered = write_reordered_packets(
                        tun,
                        reorder.drain_ready(monotonic_micros()),
                        &mut repair,
                    )?;
                    data_packets_forwarded = data_packets_forwarded.saturating_add(delivered);
                    send_repair_requests_for_ingress_gaps(
                        &socket,
                        &key,
                        &peers,
                        last_session_id,
                        &mut control_sequence,
                        &mut reorder,
                        &mut repair,
                        return_control
                            .as_ref()
                            .is_some_and(|control| control.recovery_active),
                        args.json_events,
                    )
                    .await?;
                }
            }

            _ = status_tick.tick() => {
                repair.cache_entries = resend_cache.len();
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
                write_server_status(
                    &args,
                    tun.as_ref(),
                    return_control.as_ref(),
                    &reorder,
                    counters,
                    &repair,
                    &hold_controller,
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
                            })
                        );
                    }
                }
                if last_server_recovery_status_sent.elapsed() >= Duration::from_secs(1) {
                    let status = build_server_recovery_status(
                        recovery_active,
                        &reorder,
                        &repair,
                        &hold_controller,
                        args.ingress_reorder_capacity,
                    );
                    send_server_recovery_status(
                        &socket,
                        &key,
                        &peers,
                        last_session_id,
                        &mut control_sequence,
                        status,
                        args.json_events && args.trace_packets,
                    )
                    .await?;
                    last_server_recovery_status_sent = Instant::now();
                }
            }

            Some(packet) = tun_packet_rx.recv() => {
                if !is_ipv4_packet(&packet) || peers.is_empty() || last_session_id == 0 {
                    continue;
                }

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
                let return_targets = select_return_targets(
                    return_control.as_ref(),
                    &peers,
                    packet_payload.len(),
                );
                let mut sent_paths = 0usize;
                for (path_id, peer, kind) in return_targets {
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
                    let mut header = XBondHeader::new(
                        kind,
                        last_session_id,
                        reverse_sequence,
                        send_micros,
                        path_id,
                    );
                    header.flags = 1;
                    let work = ReturnSendWork {
                        packet_kind: kind,
                        peer,
                        header,
                        payload: packet_payload.clone(),
                    };
                    let enqueue_result = if kind == PacketKind::Data {
                        sender.tx.send(work).await.map_err(|_| {
                            std::io::Error::new(
                                ErrorKind::BrokenPipe,
                                "XBond return path sender stopped",
                            )
                        })
                    } else {
                        match sender.tx.try_send(work) {
                            Ok(()) => Ok(()),
                            Err(mpsc::error::TrySendError::Full(_)) => continue,
                            Err(mpsc::error::TrySendError::Closed(_)) => Err(std::io::Error::new(
                                ErrorKind::BrokenPipe,
                                "XBond duplicate return path sender stopped",
                            )),
                        }
                    };
                    match enqueue_result {
                        Ok(()) => sent_paths += 1,
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
    apply_udp_socket_buffers(&socket, socket_buffer_bytes);
    socket
        .bind(&bind_addr.into())
        .with_context(|| format!("failed to bind UDP socket to {bind_addr}"))?;
    socket.set_nonblocking(true)?;
    let std_socket: std::net::UdpSocket = socket.into();
    Ok(UdpSocket::from_std(std_socket)?)
}

fn spawn_return_sender(
    path_id: u16,
    socket: Arc<UdpSocket>,
    key: XBondKey,
    capacity: usize,
    report_tx: mpsc::UnboundedSender<ReturnSendReport>,
) -> ReturnSenderHandle {
    let (tx, mut rx) = mpsc::channel::<ReturnSendWork>(capacity.max(1));
    tokio::spawn(async move {
        let mut encoded = Vec::with_capacity(4096);
        while let Some(work) = rx.recv().await {
            let report = match encode_sealed_payload_into(
                &work.header,
                work.payload.as_slice(),
                &key,
                &mut encoded,
            ) {
                Ok(()) => {
                    let encoded_bytes = encoded.len() as u64;
                    match socket.send_to(&encoded, work.peer).await {
                        Ok(_) => ReturnSendReport {
                            path_id,
                            packet_kind: work.packet_kind,
                            encoded_bytes,
                            success: true,
                            peer: work.peer,
                            error: None,
                        },
                        Err(error) => ReturnSendReport {
                            path_id,
                            packet_kind: work.packet_kind,
                            encoded_bytes,
                            success: false,
                            peer: work.peer,
                            error: Some(error.to_string()),
                        },
                    }
                }
                Err(error) => ReturnSendReport {
                    path_id,
                    packet_kind: work.packet_kind,
                    encoded_bytes: 0,
                    success: false,
                    peer: work.peer,
                    error: Some(error.to_string()),
                },
            };
            let _ = report_tx.send(report);
        }
    });
    ReturnSenderHandle { tx }
}

fn apply_udp_socket_buffers(socket: &Socket, socket_buffer_bytes: usize) {
    if socket_buffer_bytes == 0 {
        return;
    }

    let _ = socket.set_recv_buffer_size(socket_buffer_bytes);
    let _ = socket.set_send_buffer_size(socket_buffer_bytes);
}

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

fn write_server_status(
    args: &Args,
    tun: Option<&XBondTun>,
    return_control: Option<&ReturnControl>,
    reorder: &PacketReorderBuffer,
    counters: TunnelCounters,
    repair: &XBondRepairStatus,
    hold_controller: &IngressReorderHoldController,
) -> Result<()> {
    if let Some(parent) = args.status_path.parent() {
        std::fs::create_dir_all(parent)
            .with_context(|| format!("failed to create {}", parent.display()))?;
    }

    let status = ServerRuntimeStatus {
        running: true,
        bind: args.bind.clone(),
        tun: tun.map(|tun| tun.name().to_string()),
        updated_at_micros: now_micros(),
        return_schedule: return_control.map(|control| ServerReturnScheduleStatus {
            policy: control.policy,
            recovery_active: control.recovery_active,
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
        counters,
    };
    let json = serde_json::to_vec(&status)?;
    std::fs::write(&args.status_path, json)
        .with_context(|| format!("failed to write {}", args.status_path.display()))
}

fn build_ack_frame(frame: &XBondFrame) -> XBondFrame {
    XBondFrame::new(
        XBondHeader::new(
            frame.header.kind,
            frame.header.session_id,
            frame.header.sequence,
            frame.header.send_micros,
            0,
        ),
        b"ack".to_vec(),
    )
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

fn parse_return_control(frame: &XBondFrame) -> Option<ReturnControl> {
    if frame.header.kind != PacketKind::Control {
        return None;
    }

    if let Ok(control) = serde_json::from_slice::<ScheduleControlMessage>(&frame.payload) {
        return Some(build_return_control(
            control.schedule,
            control.redundancy_policy,
            control.policy_config,
            control.paths,
            control.recovery_active,
        ));
    }

    let schedule = serde_json::from_slice::<SchedulePlan>(&frame.payload).ok()?;
    Some(build_return_control(
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
        XBondControlMessage::ServerRecoveryStatus { .. } => return None,
    };
    sequences.sort_unstable();
    sequences.dedup();
    sequences.truncate(MAX_REPAIR_REQUESTS);
    (!sequences.is_empty()).then_some(sequences)
}

fn build_server_recovery_status(
    recovery_active: bool,
    reorder: &PacketReorderBuffer,
    repair: &XBondRepairStatus,
    hold_controller: &IngressReorderHoldController,
    capacity: usize,
) -> XBondServerRecoveryStatus {
    let adaptive = hold_controller.status();
    XBondServerRecoveryStatus {
        reported: true,
        recovery_active,
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
        updated_at_micros: now_micros(),
    }
}

async fn send_server_recovery_status(
    socket: &UdpSocket,
    key: &XBondKey,
    peers: &HashMap<u16, SocketAddr>,
    session_id: u64,
    control_sequence: &mut u64,
    status: XBondServerRecoveryStatus,
    json_events: bool,
) -> Result<()> {
    if peers.is_empty() || session_id == 0 {
        return Ok(());
    }

    *control_sequence = control_sequence.saturating_add(1);
    let sequence = *control_sequence;
    let payload = serde_json::to_vec(&XBondControlMessage::ServerRecoveryStatus { status })?;

    for (path_id, peer) in peers {
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
        if let Err(error) = socket.send_to(&encoded, *peer).await {
            if json_events {
                println!(
                    "{}",
                    serde_json::json!({
                        "event": "server-recovery-status-send-failed",
                        "path_id": path_id,
                        "peer": peer.to_string(),
                        "sequence": sequence,
                        "error": error.to_string(),
                    })
                );
            }
        }
    }

    if json_events {
        println!(
            "{}",
            serde_json::json!({
                "event": "server-recovery-status-sent",
                "sequence": sequence,
                "paths": peers.keys().copied().collect::<Vec<_>>(),
            })
        );
    }

    Ok(())
}

async fn send_repair_requests_for_ingress_gaps(
    socket: &UdpSocket,
    key: &XBondKey,
    peers: &HashMap<u16, SocketAddr>,
    session_id: u64,
    control_sequence: &mut u64,
    reorder: &mut PacketReorderBuffer,
    repair: &mut XBondRepairStatus,
    recovery_active: bool,
    json_events: bool,
) -> Result<()> {
    if !recovery_active || peers.is_empty() || session_id == 0 {
        return Ok(());
    }

    let sequences = reorder.repair_requests(
        monotonic_micros(),
        REPAIR_REQUEST_INTERVAL_MICROS,
        MAX_REPAIR_REQUESTS,
    );
    if sequences.is_empty() {
        return Ok(());
    }

    *control_sequence = control_sequence.saturating_add(1);
    let control_id = *control_sequence;
    let payload = serde_json::to_vec(&XBondControlMessage::RepairRequest {
        sequences: sequences.clone(),
    })?;
    repair.requests_sent = repair.requests_sent.saturating_add(sequences.len() as u64);

    for (path_id, peer) in peers {
        let frame = XBondFrame::new(
            XBondHeader::new(
                PacketKind::Control,
                session_id,
                control_id,
                now_micros(),
                *path_id,
            ),
            payload.clone(),
        );
        let encoded = frame.encode_sealed(key)?;
        if let Err(error) = socket.send_to(&encoded, *peer).await {
            if json_events {
                println!(
                    "{}",
                    serde_json::json!({
                        "event": "ingress-repair-request-send-failed",
                        "path_id": path_id,
                        "peer": peer.to_string(),
                        "sequence": control_id,
                        "repair_sequences": sequences,
                        "error": error.to_string(),
                    })
                );
            }
        }
    }

    Ok(())
}

async fn send_repair_frames_from_server_cache(
    args: &Args,
    socket: &Arc<UdpSocket>,
    key: &XBondKey,
    return_senders: &mut HashMap<u16, ReturnSenderHandle>,
    return_control: Option<&ReturnControl>,
    peers: &HashMap<u16, SocketAddr>,
    resend_cache: &mut ResendCache,
    session_id: u64,
    sequences: &[u64],
    send_report_tx: &mpsc::UnboundedSender<ReturnSendReport>,
    repair: &mut XBondRepairStatus,
) -> Result<()> {
    for sequence in sequences.iter().copied().take(MAX_REPAIR_REQUESTS) {
        let now = monotonic_micros();
        let Some(payload) = resend_cache.get(session_id, sequence, now) else {
            repair.cache_misses = repair.cache_misses.saturating_add(1);
            continue;
        };

        let targets = select_return_targets(return_control, peers, payload.len());
        if targets.is_empty() {
            repair.cache_misses = repair.cache_misses.saturating_add(1);
            continue;
        }

        let send_micros = now_micros();
        for (index, (path_id, peer, _kind)) in targets.into_iter().enumerate() {
            let tx = return_senders
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
                .tx
                .clone();
            let mut header = XBondHeader::new(
                PacketKind::Repair,
                session_id,
                sequence,
                send_micros,
                path_id,
            );
            header.flags = 1;
            let work = ReturnSendWork {
                packet_kind: PacketKind::Repair,
                peer,
                header,
                payload: payload.clone(),
            };
            if index == 0 {
                if tx.send(work).await.is_err() {
                    repair.queue_drops = repair.queue_drops.saturating_add(1);
                }
            } else {
                match tx.try_send(work) {
                    Ok(()) => {}
                    Err(mpsc::error::TrySendError::Full(_)) => {
                        repair.queue_drops = repair.queue_drops.saturating_add(1);
                    }
                    Err(mpsc::error::TrySendError::Closed(_)) => {
                        repair.queue_drops = repair.queue_drops.saturating_add(1);
                    }
                }
            }
        }
    }

    repair.cache_entries = resend_cache.len();
    Ok(())
}

fn build_return_control(
    schedule: SchedulePlan,
    policy: RedundancyPolicy,
    policy_config: RedundancyPolicyConfig,
    paths: Vec<PathHealthSnapshot>,
    recovery_active: bool,
) -> ReturnControl {
    let transmission_plans =
        precompute_transmission_plans(&schedule, policy, &paths, policy_config);
    ReturnControl {
        schedule,
        policy,
        policy_config,
        recovery_active,
        transmission_plans,
    }
}

fn select_return_targets(
    control: Option<&ReturnControl>,
    peers: &HashMap<u16, SocketAddr>,
    packet_len: usize,
) -> Vec<(u16, SocketAddr, PacketKind)> {
    let scheduled = control
        .map(|control| {
            control
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
                .filter_map(|transmission| {
                    peers
                        .get(&transmission.path_id)
                        .map(|peer| (transmission.path_id, *peer, transmission.packet_kind))
                })
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    if !scheduled.is_empty() {
        return scheduled;
    }

    let mut fallback = peers.iter().collect::<Vec<_>>();
    fallback.sort_by_key(|(path_id, _)| **path_id);
    fallback
        .into_iter()
        .enumerate()
        .map(|(index, (path_id, peer))| {
            let kind = if index == 0 {
                PacketKind::Data
            } else {
                PacketKind::Duplicate
            };
            (*path_id, *peer, kind)
        })
        .collect()
}

fn write_reordered_packets(
    tun: &mut XBondTun,
    packets: Vec<ReorderedPacket>,
    repair: &mut XBondRepairStatus,
) -> Result<u64> {
    let mut delivered = 0u64;
    for packet in packets {
        if let Err(error) = tun.write_packet(&packet.payload) {
            eprintln!(
                "xbond server failed to write packet to XBond TUN {}: {error}",
                tun.name()
            );
            continue;
        }
        delivered += 1;
        if packet.path_id == u16::MAX {
            repair.frames_delivered = repair.frames_delivered.saturating_add(1);
        }
    }

    Ok(delivered)
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

fn initial_reverse_sequence() -> u64 {
    now_micros()
}

#[cfg(test)]
mod tests {
    use super::*;
    use xbond_core::ScheduleMode;

    #[test]
    fn ack_preserves_heartbeat_or_control_kind() {
        for kind in [PacketKind::Heartbeat, PacketKind::Control] {
            let frame = XBondFrame::new(XBondHeader::new(kind, 1, 2, 3, 4), b"hello".to_vec());
            let ack = build_ack_frame(&frame);

            assert_eq!(ack.header.kind, kind);
            assert_eq!(ack.header.sequence, frame.header.sequence);
            assert_eq!(ack.payload, b"ack");
        }
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
    fn data_and_duplicate_are_tunnel_payload_kinds() {
        assert!(is_data_like(PacketKind::Data));
        assert!(is_data_like(PacketKind::Duplicate));
        assert!(is_data_like(PacketKind::Repair));
        assert!(!is_data_like(PacketKind::Heartbeat));
        assert!(!is_data_like(PacketKind::Fec));
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
    fn reverse_sequence_starts_from_current_time_to_avoid_restart_rewinds() {
        let before = now_micros();
        let sequence = initial_reverse_sequence();
        let after = now_micros();

        assert!(sequence >= before);
        assert!(sequence <= after);
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

        let control = parse_return_control(&frame).unwrap();

        assert_eq!(control.schedule, schedule);
        assert_eq!(control.policy, RedundancyPolicy::Reliable);
        assert!(!control.recovery_active);
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

        let control = parse_return_control(&frame).unwrap();

        assert_eq!(control.schedule, schedule);
        assert!(control.recovery_active);
    }

    #[test]
    fn scheduled_return_targets_follow_client_schedule_order() {
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let peers = HashMap::from([(3, peer_3), (5, peer_5)]);
        let control = build_return_control(
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
            select_return_targets(Some(&control), &peers, 1_200),
            vec![
                (5, peer_5, PacketKind::Data),
                (3, peer_3, PacketKind::Duplicate),
            ]
        );
    }

    #[test]
    fn return_targets_fallback_to_sorted_peers_without_schedule() {
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let peers = HashMap::from([(5, peer_5), (3, peer_3)]);

        assert_eq!(
            select_return_targets(None, &peers, 1_200),
            vec![
                (3, peer_3, PacketKind::Data),
                (5, peer_5, PacketKind::Duplicate),
            ]
        );
    }

    #[test]
    fn balanced_return_targets_do_not_duplicate_healthy_bulk_packets() {
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let peers = HashMap::from([(3, peer_3), (5, peer_5)]);
        let control = build_return_control(
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
            select_return_targets(Some(&control), &peers, 1_200),
            vec![(5, peer_5, PacketKind::Data)]
        );
    }

    #[test]
    fn recovery_return_targets_duplicate_bulk_to_all_scheduled_paths() {
        let peer_2: SocketAddr = "192.0.2.2:2000".parse().unwrap();
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let peers = HashMap::from([(2, peer_2), (3, peer_3), (5, peer_5)]);
        let control = build_return_control(
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
            select_return_targets(Some(&control), &peers, 1_200),
            vec![
                (5, peer_5, PacketKind::Data),
                (2, peer_2, PacketKind::Duplicate),
                (3, peer_3, PacketKind::Duplicate),
            ]
        );
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
