param(
    [string]$Branch = "main",
    [string]$RouterHost = "xeon-network",
    [string]$RouterUser = "xeon-network",
    [string]$RouterRepo = "/home/xeon-network/xnetwork",
    [string]$RouterKey = "$env:USERPROFILE\.ssh\speedifyui_cli_probe",
    [string]$ServerHost = "xeon-speedify-vultr-01",
    [string]$ServerUser = "root",
    [string]$ServerRepo = "/opt/xnetwork",
    [string]$ServerKey = "$env:USERPROFILE\.ssh\codex_proxmox_ed25519"
)

$ErrorActionPreference = "Stop"

function Invoke-Remote {
    param(
        [Parameter(Mandatory = $true)][string]$HostName,
        [Parameter(Mandatory = $true)][string]$UserName,
        [Parameter(Mandatory = $true)][string]$KeyPath,
        [Parameter(Mandatory = $true)][string]$Command
    )

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Command))
    $remoteCommand = "printf '%s' '$encodedCommand' | base64 -d | bash"

    & ssh -i $KeyPath -o IdentitiesOnly=yes -o BatchMode=yes "$UserName@$HostName" $remoteCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Remote command failed on $HostName with exit code $LASTEXITCODE"
    }
}

$serverCommand = @"
set -euo pipefail
cd '$ServerRepo'
source /root/.cargo/env 2>/dev/null || true
git fetch --all --prune
git checkout '$Branch'
git pull --ff-only
cargo build --release --manifest-path xbond/Cargo.toml -p xbond-server
install -m 0755 xbond/target/release/xbond-server /usr/local/bin/xbond-server
install -m 0644 xbond/deploy/systemd/xbond-server.service /etc/systemd/system/xbond-server.service
install -m 0644 xbond/deploy/sysctl/90-xbond.conf /etc/sysctl.d/90-xbond.conf
install -d -m 0755 /etc/systemd/journald.conf.d
install -m 0644 xbond/deploy/journald/90-ulink.conf /etc/systemd/journald.conf.d/90-ulink.conf
systemctl restart systemd-journald
journalctl --vacuum-size=512M >/dev/null
sysctl --system >/dev/null
systemctl daemon-reload
systemctl restart xbond-server.service
systemctl restart xbond-server-nat.service
systemctl is-active xbond-server.service
systemctl is-enabled xbond-server.service
systemctl is-active xbond-server-nat.service
systemctl is-enabled xbond-server-nat.service
ss -lunp | grep 8444
git rev-parse --short HEAD
"@

$routerCommand = @"
set -euo pipefail
cd '$RouterRepo'
source /home/xeon-network/.cargo/env 2>/dev/null || true
git fetch --all --prune
git checkout '$Branch'
git pull --ff-only
./deploy.sh
cargo build --release --manifest-path xbond/Cargo.toml -p xbond-client
if ! command -v nft >/dev/null 2>&1 || ! command -v conntrack >/dev/null 2>&1; then
  sudo -n apt-get update
  sudo -n DEBIAN_FRONTEND=noninteractive apt-get install -y nftables conntrack
fi
sudo -n install -m 0755 xbond/target/release/xbond-client /usr/local/bin/xbond-client
sudo -n install -m 0755 xbond/deploy/scripts/xbond-client-route-apply.sh /usr/local/sbin/xbond-client-route-apply
sudo -n install -m 0755 xbond/deploy/scripts/xbond-client-egress.py /usr/local/sbin/xbond-client-egress
sudo -n install -d -m 0755 /etc/NetworkManager/dispatcher.d
sudo -n install -m 0755 xbond/deploy/scripts/90-ulink-egress /etc/NetworkManager/dispatcher.d/90-ulink-egress
sudo -n install -m 0755 xbond/deploy/scripts/xbond-client-rollback.sh /usr/local/sbin/xbond-client-rollback
sudo -n install -m 0644 xbond/deploy/systemd/xbond-client.service /etc/systemd/system/xbond-client.service
sudo -n install -m 0644 xbond/deploy/sysctl/90-xbond.conf /etc/sysctl.d/90-xbond.conf
sudo -n install -d -m 0755 /etc/systemd/journald.conf.d
sudo -n install -m 0644 xbond/deploy/journald/90-ulink.conf /etc/systemd/journald.conf.d/90-ulink.conf
sudo -n systemctl restart systemd-journald
sudo -n journalctl --vacuum-size=512M >/dev/null
sudo -n sysctl --system >/dev/null
if grep -q '^udp_socket_buffer_bytes[[:space:]]*=' /etc/xbond/client.toml; then
  sudo -n sed -i 's/^udp_socket_buffer_bytes[[:space:]]*=.*/udp_socket_buffer_bytes = 8388608/' /etc/xbond/client.toml
else
  sudo -n sed -i '0,/^\[\[paths\]\]/{s//udp_socket_buffer_bytes = 8388608\n\n[[paths]]/}' /etc/xbond/client.toml
fi
if grep -q '^udp_receive_batch_size[[:space:]]*=' /etc/xbond/client.toml; then
  sudo -n sed -i 's/^udp_receive_batch_size[[:space:]]*=.*/udp_receive_batch_size = 32/' /etc/xbond/client.toml
else
  sudo -n sed -i '0,/^\[\[paths\]\]/{s//udp_receive_batch_size = 32\n\n[[paths]]/}' /etc/xbond/client.toml
fi
sudo -n systemctl daemon-reload
sudo -n systemctl restart xbond-client.service
systemctl is-active xnetwork.service
systemctl is-active xbond-client.service
systemctl is-enabled xbond-client.service
sudo -n /usr/local/bin/xbond-client override status --json
for path in / /xbond /details /settings /xrouter /wifi; do
  curl -fsS -o /dev/null "http://127.0.0.1:8080`$path"
done
active_egress="`$(python3 -c 'import json; print(json.load(open("/run/xbond/client-status.json")).get("egress", {}).get("active_egress", "tunnel"))')"
case "`$active_egress" in
  tunnel)
    ip route get 8.8.8.8 | grep 'dev xbond0'
    ;;
  direct)
    direct_if="`$(python3 -c 'import json; print(json.load(open("/run/xbond/client-status.json"))["egress"]["direct_interface_name"])')"
    ip route get 8.8.8.8 | grep "dev `$direct_if"
    ;;
  *)
    echo "uLink deployed without a usable egress route" >&2
    exit 1
    ;;
esac
cudy_host="`$(sed -n 's/.*"ManagementBaseUrl"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' /home/xeon-network/.config/XNetwork/cudy-ap-automation-settings.json 2>/dev/null | head -n 1 | sed 's#^[a-zA-Z][a-zA-Z0-9+.-]*://##; s#/.*##; s#:.*##')"
if [ -n "`$cudy_host" ]; then
  ip route get "`$cudy_host" | grep -v 'dev xbond0'
fi
ping -I xbond0 -c 3 -W 2 10.250.0.1
ping -c 3 -W 3 8.8.8.8
git rev-parse --short HEAD
"@

Write-Host "Deploying XBond server on $ServerHost..."
Invoke-Remote -HostName $ServerHost -UserName $ServerUser -KeyPath $ServerKey -Command $serverCommand

Write-Host "Deploying XNetwork app and XBond client on $RouterHost..."
Invoke-Remote -HostName $RouterHost -UserName $RouterUser -KeyPath $RouterKey -Command $routerCommand

Write-Host "Paired XBond deployment complete."
