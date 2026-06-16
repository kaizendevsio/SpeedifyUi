param(
    [string]$Branch = "feature/xband-only-runtime",
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
sudo -n install -m 0755 xbond/target/release/xbond-client /usr/local/bin/xbond-client
sudo -n systemctl restart xbond-client.service
systemctl is-active xnetwork.service
systemctl is-active xbond-client.service
systemctl is-enabled xbond-client.service
sudo -n /usr/local/bin/xbond-client override status --json
for path in / /xbond /details /settings /xrouter /wifi; do
  curl -fsS -o /dev/null "http://127.0.0.1:8080`$path"
done
ip route get 8.8.8.8 | grep 'dev xbond0'
ping -I xbond0 -c 3 -W 2 10.250.0.1
ping -c 3 -W 3 8.8.8.8
git rev-parse --short HEAD
"@

Write-Host "Deploying XBond server on $ServerHost..."
Invoke-Remote -HostName $ServerHost -UserName $ServerUser -KeyPath $ServerKey -Command $serverCommand

Write-Host "Deploying XNetwork app and XBond client on $RouterHost..."
Invoke-Remote -HostName $RouterHost -UserName $RouterUser -KeyPath $RouterKey -Command $routerCommand

Write-Host "Paired XBond deployment complete."
