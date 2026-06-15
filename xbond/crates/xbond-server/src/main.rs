use anyhow::Result;
use clap::Parser;
use std::collections::{HashMap, HashSet, VecDeque};
use std::io::ErrorKind;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::net::UdpSocket;
use tokio::sync::mpsc;
use xbond_core::{
    build_transmission_plan, is_ipv4_packet, FrameReceiver, PacketKind, ReceiveOutcome,
    SchedulePlan, XBondFrame, XBondHeader, XBondKey, XBondTun, XorFecBlock,
};

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
    tun_name: Option<String>,

    #[arg(long, default_value_t = 1400)]
    tun_mtu: u16,
}

#[derive(Debug)]
struct InboundServerFrame {
    frame: XBondFrame,
    peer: SocketAddr,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct ReturnTarget {
    path_id: u16,
    packet_kind: PacketKind,
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    let key_text = std::env::var(&args.key_env)?;
    let key = XBondKey::from_passphrase(&key_text);
    let socket = Arc::new(UdpSocket::bind(&args.bind).await?);
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
    let mut peers: HashMap<u16, SocketAddr> = HashMap::new();
    let mut return_schedule: Vec<ReturnTarget> = Vec::new();
    let mut reverse_sequence = 0u64;
    let mut last_session_id = 0u64;

    let (udp_frame_tx, mut udp_frame_rx) = mpsc::unbounded_channel::<InboundServerFrame>();
    let recv_socket = socket.clone();
    let recv_key = key.clone();
    tokio::spawn(async move {
        let mut buf = vec![0u8; 4096];
        loop {
            let Ok((len, peer)) = recv_socket.recv_from(&mut buf).await else {
                break;
            };
            let Ok(frame) = XBondFrame::decode_sealed(&buf[..len], &recv_key) else {
                continue;
            };
            if udp_frame_tx
                .send(InboundServerFrame { frame, peer })
                .is_err()
            {
                break;
            }
        }
    });

    let (tun_packet_tx, mut tun_packet_rx) = mpsc::unbounded_channel::<Vec<u8>>();
    if let Some(tun_ref) = &tun {
        let mut tun_reader = tun_ref.try_clone()?;
        let tun_name = tun_ref.name().to_string();
        let tun_mtu = usize::from(args.tun_mtu).max(2048);
        tokio::task::spawn_blocking(move || {
            let mut buf = vec![0u8; tun_mtu];
            loop {
                match tun_reader.read_packet(&mut buf) {
                    Ok(len) => {
                        if tun_packet_tx.send(buf[..len].to_vec()).is_err() {
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
                    return_schedule.clear();
                }
                last_session_id = frame.header.session_id;
                if frame.header.path_id != 0 {
                    peers.insert(frame.header.path_id, peer);
                }
                if let Some(schedule) = parse_return_schedule(&frame) {
                    return_schedule = schedule;
                    if args.json_events {
                        println!(
                            "{}",
                            serde_json::json!({
                                "event": "return-schedule-updated",
                                "session_id": frame.header.session_id,
                                "paths": return_schedule
                                    .iter()
                                    .map(|target| serde_json::json!({
                                        "path_id": target.path_id,
                                        "packet_kind": target.packet_kind,
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

                if args.json_events {
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
                                tun.write_packet(&frame.payload)?;
                                data_packets_forwarded += 1;
                                forwarded_packets += 1;
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
                                tun.write_packet(&recovered.payload)?;
                            }
                            data_packets_forwarded += 1;
                            fec_packets_recovered += 1;
                            forwarded_packets += 1;
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
                                        tun.write_packet(&recovered.payload)?;
                                    }
                                    data_packets_forwarded += 1;
                                    fec_packets_recovered += 1;
                                    forwarded_packets += 1;
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

                if args.json_events && is_tunnel_payload(frame.header.kind) {
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

            Some(packet) = tun_packet_rx.recv() => {
                if !is_ipv4_packet(&packet) || peers.is_empty() || last_session_id == 0 {
                    continue;
                }

                reverse_sequence += 1;
                let send_micros = now_micros();
                let return_targets = select_return_targets(&return_schedule, &peers);
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
                    let frame = XBondFrame::new(header, packet.clone());
                    let encoded = frame.encode_sealed(&key)?;
                    match socket.send_to(&encoded, peer).await {
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

                if args.json_events {
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

fn parse_return_schedule(frame: &XBondFrame) -> Option<Vec<ReturnTarget>> {
    if frame.header.kind != PacketKind::Control {
        return None;
    }

    let schedule = serde_json::from_slice::<SchedulePlan>(&frame.payload).ok()?;
    let targets = build_transmission_plan(&schedule)
        .into_iter()
        .filter(|transmission| {
            matches!(
                transmission.packet_kind,
                PacketKind::Data | PacketKind::Duplicate
            )
        })
        .map(|transmission| ReturnTarget {
            path_id: transmission.path_id,
            packet_kind: transmission.packet_kind,
        })
        .collect::<Vec<_>>();

    (!targets.is_empty()).then_some(targets)
}

fn select_return_targets(
    schedule: &[ReturnTarget],
    peers: &HashMap<u16, SocketAddr>,
) -> Vec<(u16, SocketAddr, PacketKind)> {
    let scheduled = schedule
        .iter()
        .filter_map(|target| {
            peers
                .get(&target.path_id)
                .map(|peer| (target.path_id, *peer, target.packet_kind))
        })
        .collect::<Vec<_>>();

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

#[cfg(test)]
mod tests {
    use super::*;

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
    fn control_frame_updates_return_schedule() {
        let schedule = SchedulePlan {
            mode: xbond_core::ScheduleMode::AnchorDuplicate1,
            anchor_path_id: Some(5),
            data_path_ids: vec![5],
            duplicate_path_ids: vec![3],
            fec_path_ids: Vec::new(),
        };
        let frame = XBondFrame::new(
            XBondHeader::new(PacketKind::Control, 7, 1, 2, 5),
            serde_json::to_vec(&schedule).unwrap(),
        );

        let targets = parse_return_schedule(&frame).unwrap();

        assert_eq!(
            targets,
            vec![
                ReturnTarget {
                    path_id: 5,
                    packet_kind: PacketKind::Data,
                },
                ReturnTarget {
                    path_id: 3,
                    packet_kind: PacketKind::Duplicate,
                },
            ]
        );
    }

    #[test]
    fn scheduled_return_targets_follow_client_schedule_order() {
        let peer_3: SocketAddr = "192.0.2.3:3000".parse().unwrap();
        let peer_5: SocketAddr = "192.0.2.5:5000".parse().unwrap();
        let peers = HashMap::from([(3, peer_3), (5, peer_5)]);
        let schedule = vec![
            ReturnTarget {
                path_id: 5,
                packet_kind: PacketKind::Data,
            },
            ReturnTarget {
                path_id: 3,
                packet_kind: PacketKind::Duplicate,
            },
        ];

        assert_eq!(
            select_return_targets(&schedule, &peers),
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
            select_return_targets(&[], &peers),
            vec![
                (3, peer_3, PacketKind::Data),
                (5, peer_5, PacketKind::Duplicate),
            ]
        );
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
