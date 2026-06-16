use anyhow::{Context, Result};
use clap::Parser;
use socket2::{Domain, Protocol, Socket, Type};
use std::collections::{HashMap, HashSet, VecDeque};
use std::io::ErrorKind;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tokio::net::UdpSocket;
use tokio::sync::mpsc;
use tokio::time;
use xbond_core::{
    build_transmission_plan, encode_sealed_payload, is_ipv4_packet, precompute_transmission_plans,
    FrameReceiver, PacketKind, PacketReorderBuffer, PacketTransmissionPlans, PathHealthSnapshot,
    ReceiveOutcome, RedundancyPolicy, RedundancyPolicyConfig, ReorderedPacket,
    ScheduleControlMessage, SchedulePlan, XBondFrame, XBondHeader, XBondKey, XBondTun, XorFecBlock,
};

const DEFAULT_TUN_QUEUE_CAPACITY: usize = 2048;
const DEFAULT_INBOUND_QUEUE_CAPACITY: usize = 4096;
const DEFAULT_UDP_SOCKET_BUFFER_BYTES: usize = 4 * 1024 * 1024;

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

#[derive(Debug, Clone, PartialEq)]
struct ReturnControl {
    schedule: SchedulePlan,
    policy: RedundancyPolicy,
    policy_config: RedundancyPolicyConfig,
    transmission_plans: PacketTransmissionPlans,
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
    let mut data_packets_received = 0u64;
    let mut data_packets_forwarded = 0u64;
    let mut fec_packets_received = 0u64;
    let mut fec_packets_recovered = 0u64;
    let mut invalid_fec_packets_dropped = 0u64;
    let mut non_ipv4_packets_dropped = 0u64;
    let mut fec_recovery = FecRecovery::new(8192);
    let mut reorder = PacketReorderBuffer::new(8192, args.realtime_deadline_ms.min(50) * 1_000);
    let mut peers: HashMap<u16, SocketAddr> = HashMap::new();
    let mut return_control: Option<ReturnControl> = None;
    let mut reverse_sequence = initial_reverse_sequence();
    let mut last_session_id = 0u64;
    let mut reorder_session_id = 0u64;
    let mut reorder_tick = time::interval(Duration::from_millis(
        args.realtime_deadline_ms.clamp(5, 50),
    ));

    let (udp_frame_tx, mut udp_frame_rx) =
        mpsc::channel::<InboundServerFrame>(args.inbound_queue_capacity.max(1));
    let recv_socket = socket.clone();
    let recv_key = key.clone();
    tokio::spawn(async move {
        let mut buf = vec![0u8; 4096];
        loop {
            let (len, peer) = match recv_socket.recv_from(&mut buf).await {
                Ok(result) => result,
                Err(error) => {
                    eprintln!("xbond server UDP recv error: {error}");
                    time::sleep(Duration::from_millis(50)).await;
                    continue;
                }
            };
            let Ok(frame) = XBondFrame::decode_sealed(&buf[..len], &recv_key) else {
                continue;
            };
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
            })
        );
    } else {
        println!("xbond-server listening on {}", args.bind);
    }
    loop {
        tokio::select! {
            Some(inbound) = udp_frame_rx.recv() => {
                let frame = inbound.frame;
                let peer = inbound.peer;
                if last_session_id != 0 && frame.header.session_id != last_session_id {
                    peers.clear();
                    return_control = None;
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
                    });
                    return_control = Some(control);
                    if args.json_events && schedule_changed {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "return-schedule-updated",
                                "session_id": frame.header.session_id,
                                "policy": return_control.as_ref().map(|control| control.policy),
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

                let mut forwarded_packets = 0u64;
                let mut dropped_reason = None;
                if outcome == ReceiveOutcome::Accepted && is_data_like(frame.header.kind) {
                    data_packets_received += 1;
                    let recovered_packets = fec_recovery.observe_data(
                        frame.header.session_id,
                        frame.header.sequence,
                        frame.payload.clone(),
                    );
                    if fec_recovery.mark_delivered(frame.header.session_id, frame.header.sequence) {
                        if is_ipv4_packet(&frame.payload) {
                            if let Some(tun) = &mut tun {
                                let ready = reorder.push(
                                    frame.header.sequence,
                                    frame.header.path_id,
                                    frame.payload.clone(),
                                    now_micros(),
                                    frame.header.send_micros.saturating_add(args.realtime_deadline_ms * 1_000),
                                );
                                let delivered = write_reordered_packets(tun, ready)?;
                                data_packets_forwarded =
                                    data_packets_forwarded.saturating_add(delivered);
                                forwarded_packets = forwarded_packets.saturating_add(delivered);
                            }
                        } else {
                            non_ipv4_packets_dropped += 1;
                            dropped_reason = Some("payload is not an IPv4 packet");
                        }
                    }

                    for recovered in recovered_packets {
                        if !fec_recovery.mark_delivered(frame.header.session_id, recovered.sequence) {
                            continue;
                        }
                        if is_ipv4_packet(&recovered.payload) {
                            if let Some(tun) = &mut tun {
                                let ready = reorder.push(
                                    recovered.sequence,
                                    0,
                                    recovered.payload,
                                    now_micros(),
                                    now_micros(),
                                );
                                let delivered = write_reordered_packets(tun, ready)?;
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
                    match XorFecBlock::decode(&frame.payload) {
                        Ok(block) => {
                            for recovered in fec_recovery.observe_fec(frame.header.session_id, block) {
                                if !fec_recovery.mark_delivered(frame.header.session_id, recovered.sequence)
                                {
                                    continue;
                                }
                                if is_ipv4_packet(&recovered.payload) {
                                    if let Some(tun) = &mut tun {
                                        let ready = reorder.push(
                                            recovered.sequence,
                                            0,
                                            recovered.payload,
                                            now_micros(),
                                            now_micros(),
                                        );
                                        let delivered = write_reordered_packets(tun, ready)?;
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

                if args.json_events && args.trace_packets && is_tunnel_payload(frame.header.kind) {
                    print_data_event(
                        &frame,
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

            _ = reorder_tick.tick() => {
                if let Some(tun) = &mut tun {
                    let delivered = write_reordered_packets(tun, reorder.drain_ready(now_micros()))?;
                    data_packets_forwarded = data_packets_forwarded.saturating_add(delivered);
                }
            }

            Some(packet) = tun_packet_rx.recv() => {
                if !is_ipv4_packet(&packet) || peers.is_empty() || last_session_id == 0 {
                    continue;
                }

                reverse_sequence += 1;
                let send_micros = now_micros();
                let return_targets = select_return_targets(
                    return_control.as_ref(),
                    &peers,
                    packet.len(),
                );
                let mut sent_paths = 0usize;
                for (path_id, peer, kind) in return_targets {
                    let mut header = XBondHeader::new(
                        kind,
                        last_session_id,
                        reverse_sequence,
                        send_micros,
                        path_id,
                    );
                    header.flags = 1;
                    let encoded = encode_sealed_payload(&header, &packet, &key)?;
                    let send_result = if kind == PacketKind::Data {
                        socket.send_to(&encoded, peer).await
                    } else {
                        match socket.try_send_to(&encoded, peer) {
                            Ok(bytes) => Ok(bytes),
                            Err(error) if error.kind() == ErrorKind::WouldBlock => continue,
                            Err(error) => Err(error),
                        }
                    };
                    match send_result {
                        Ok(_) => sent_paths += 1,
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
                            "bytes": packet.len(),
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

#[derive(Debug, Clone, Copy)]
struct TunnelCounters {
    data_packets_received: u64,
    data_packets_forwarded: u64,
    fec_packets_received: u64,
    fec_packets_recovered: u64,
    invalid_fec_packets_dropped: u64,
    non_ipv4_packets_dropped: u64,
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
    matches!(kind, PacketKind::Data | PacketKind::Duplicate)
}

fn is_tunnel_payload(kind: PacketKind) -> bool {
    matches!(
        kind,
        PacketKind::Data | PacketKind::Duplicate | PacketKind::Fec
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
        ));
    }

    let schedule = serde_json::from_slice::<SchedulePlan>(&frame.payload).ok()?;
    Some(build_return_control(
        schedule,
        RedundancyPolicy::Reliable,
        RedundancyPolicyConfig::default(),
        Vec::new(),
    ))
}

fn build_return_control(
    schedule: SchedulePlan,
    policy: RedundancyPolicy,
    policy_config: RedundancyPolicyConfig,
    paths: Vec<PathHealthSnapshot>,
) -> ReturnControl {
    let transmission_plans =
        precompute_transmission_plans(&schedule, policy, &paths, policy_config);
    ReturnControl {
        schedule,
        policy,
        policy_config,
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

fn write_reordered_packets(tun: &mut XBondTun, packets: Vec<ReorderedPacket>) -> Result<u64> {
    let mut delivered = 0u64;
    for packet in packets {
        tun.write_packet(&packet.payload)?;
        delivered += 1;
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
    frame: &XBondFrame,
    forwarded_packets: u64,
    dropped_reason: Option<&str>,
    counters: TunnelCounters,
) {
    println!(
        "{}",
        serde_json::json!({
            "event": "data-decapsulated",
            "session_id": frame.header.session_id,
            "sequence": frame.header.sequence,
            "path_id": frame.header.path_id,
            "packet_kind": frame.header.kind,
            "bytes": frame.payload.len(),
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
        assert!(!is_data_like(PacketKind::Heartbeat));
        assert!(!is_data_like(PacketKind::Fec));
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
        );

        assert_eq!(
            select_return_targets(Some(&control), &peers, 1_200),
            vec![(5, peer_5, PacketKind::Data)]
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
