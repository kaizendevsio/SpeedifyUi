FROM rust:1.85-bookworm AS builder

WORKDIR /src
COPY Cargo.toml Cargo.lock ./
COPY crates ./crates
RUN cargo build --workspace --release

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        iperf3 \
        iproute2 \
        iptables \
        iputils-ping \
        jq \
        libfaketime \
        procps \
        python3 \
        python3-jsonschema \
        socat \
        tcpdump \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /src/target/release/xbond-client /opt/xbond/bin/xbond-client
COPY --from=builder /src/target/release/xbond-server /opt/xbond/bin/xbond-server
COPY --from=builder /src/target/release/xbond-loadgen /opt/xbond/bin/xbond-loadgen
COPY lab /opt/xbond/lab
RUN sed -i 's/\r$//' /opt/xbond/lab/entrypoint.sh \
    && chmod 0755 /opt/xbond/lab/entrypoint.sh

WORKDIR /opt/xbond/lab
ENTRYPOINT ["/opt/xbond/lab/entrypoint.sh"]
