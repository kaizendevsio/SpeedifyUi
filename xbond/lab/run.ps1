param(
    [Parameter(Position = 0)]
    [ValidateSet(
        "topology-smoke",
        "four-wan-smoke",
        "four-wan-resilience",
        "app-smoke",
        "dev-stack",
        "healthy-single",
        "anchor-bad-backup",
        "all-intermittent",
        "heavy-bidirectional",
        "throughput-stress",
        "multi-session-scale",
        "silent-blackhole",
        "usb-reenumeration",
        "heartbeat-integrity",
        "tun-read-failure",
        "server-process-restart",
        "tun-write-backpressure",
        "server-tun-write-backpressure",
        "clock-rollback",
        "clock-skew",
        "stale-return-schedule",
        "queue-saturation",
        "mtu-sweep",
        "soak",
        "matrix"
    )]
    [string]$Scenario = "topology-smoke",

    [int]$DurationSeconds = 1800,

    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$dockerContext = "xeon-dev"
$labRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$xbondRoot = Split-Path -Parent $labRoot
$repoRoot = Split-Path -Parent $xbondRoot
$results = Join-Path $labRoot "results"
$appScenarios = @("app-smoke", "dev-stack")
$needsApp = $appScenarios -contains $Scenario
$appPublish = $null
New-Item -ItemType Directory -Force -Path $results | Out-Null

if (($Scenario -eq "soak" -or $Scenario -eq "matrix") -and $DurationSeconds -lt 1800) {
    throw "Scenario '$Scenario' requires at least 1800 seconds (30 minutes)."
}

if ($Scenario -eq "matrix" -and $NoBuild) {
    throw "The release validation matrix requires a fresh xeon-dev image build; -NoBuild is not allowed."
}

& docker --context $dockerContext version *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Required Docker context '$dockerContext' is unavailable. Start or reconnect xeon-dev before running the XBond lab."
}

$image = "xbond-lab:local"
$container = "xbond-lab-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
$lockNetwork = "xbond-lab-exclusive-lock"
$lockAcquired = $false
$exitCode = 1
$existingResults = @{}
Get-ChildItem -LiteralPath $results -Filter "*.json" -File | ForEach-Object {
    $existingResults[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}

$gitCommit = (& git -C $xbondRoot rev-parse HEAD).Trim()
$gitBranch = (& git -C $xbondRoot branch --show-current).Trim()
$gitDirty = if ((& git -C $xbondRoot status --porcelain --untracked-files=all -- .).Count -gt 0) { "true" } else { "false" }
if ($Scenario -eq "matrix" -and $gitDirty -eq "true") {
    throw "The release validation matrix requires committed XBond sources. Commit tracked changes under '$xbondRoot' before running it."
}
$labOrchestratorHash = (Get-FileHash -LiteralPath (Join-Path $labRoot "lab.py") -Algorithm SHA256).Hash.ToLowerInvariant()
$resultSchemaHash = (Get-FileHash -LiteralPath (Join-Path $labRoot "result.schema.json") -Algorithm SHA256).Hash.ToLowerInvariant()
$dockerHost = (& docker context inspect $dockerContext --format "{{.Endpoints.docker.Host}}").Trim()
$dockerEngineVersion = (& docker --context $dockerContext version --format "{{.Server.Version}}").Trim()
$dockerKernel = (& docker --context $dockerContext version --format "{{.Server.KernelVersion}}").Trim()
$dockerOs = (& docker --context $dockerContext version --format "{{.Server.Os}}/{{.Server.Arch}}").Trim()

try {
    & docker --context $dockerContext network create `
        --driver bridge `
        --label "xbond.lab.lock=true" `
        $lockNetwork *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Another XBond lab scenario is already running on '$dockerContext'. Scenarios share network namespace names and must run serially. Remove stale network '$lockNetwork' only after confirming no lab is active."
    }
    $lockAcquired = $true

    if (-not $NoBuild) {
        & docker --context $dockerContext build `
            --file (Join-Path $labRoot "Dockerfile") `
            --tag $image `
            $xbondRoot
        if ($LASTEXITCODE -ne 0) {
            throw "XBond lab image build failed."
        }
    }

    $imageId = (& docker --context $dockerContext image inspect $image --format "{{.Id}}").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageId)) {
        throw "XBond lab image '$image' is unavailable on '$dockerContext'. Run without -NoBuild first."
    }
    $imageDigestJson = (& docker --context $dockerContext image inspect $image --format "{{json .RepoDigests}}").Trim()
    $imageDigests = if ($imageDigestJson -and $imageDigestJson -ne "null") {
        @($imageDigestJson | ConvertFrom-Json)
    } else {
        @()
    }
    $imageDigestEnv = $imageDigests -join ","
    $provenance = [ordered]@{
        git_commit            = $gitCommit
        git_branch            = $gitBranch
        git_dirty             = [bool]::Parse($gitDirty)
        docker_context        = $dockerContext
        docker_host           = $dockerHost
        docker_engine_version = $dockerEngineVersion
        docker_kernel         = $dockerKernel
        docker_os             = $dockerOs
        image_id              = $imageId
        image_digests         = $imageDigests
        lab_orchestrator_sha256 = $labOrchestratorHash
        result_schema_sha256    = $resultSchemaHash
        lab_orchestrator_source_sha256 = $labOrchestratorHash
        result_schema_source_sha256    = $resultSchemaHash
        lab_orchestrator_hash_matches_source = $true
        result_schema_hash_matches_source    = $true
    }

    if ($needsApp) {
        $appPublish = Join-Path ([System.IO.Path]::GetTempPath()) "ulink-lab-$([Guid]::NewGuid().ToString('N'))"
        & dotnet publish (Join-Path $repoRoot "XNetwork\XNetwork.csproj") `
            -c Release `
            -o $appPublish `
            --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "uLink app publish failed."
        }
    }

    $createArgs = @(
        "--context", $dockerContext,
        "create",
        "--name", $container,
        "--privileged",
        "--cap-add", "NET_ADMIN",
        "--cap-add", "NET_RAW",
        "--cap-add", "SYS_ADMIN",
        "--security-opt", "seccomp=unconfined",
        "--env", "XBOND_PSK=xbond-local-lab-key",
        "--env", "LAB_RESULTS_DIR=/results",
        "--env", "LAB_GIT_COMMIT=$gitCommit",
        "--env", "LAB_GIT_BRANCH=$gitBranch",
        "--env", "LAB_GIT_DIRTY=$gitDirty",
        "--env", "LAB_DOCKER_CONTEXT=$dockerContext",
        "--env", "LAB_DOCKER_HOST=$dockerHost",
        "--env", "LAB_DOCKER_ENGINE_VERSION=$dockerEngineVersion",
        "--env", "LAB_DOCKER_KERNEL=$dockerKernel",
        "--env", "LAB_DOCKER_OS=$dockerOs",
        "--env", "LAB_IMAGE_ID=$imageId",
        "--env", "LAB_IMAGE_DIGESTS=$imageDigestEnv",
        "--env", "LAB_ORCHESTRATOR_SHA256=$labOrchestratorHash",
        "--env", "LAB_SCHEMA_SHA256=$resultSchemaHash"
    )
    if ($needsApp) {
        $createArgs += @("--publish", "100.75.11.49:18080:8080")
    }
    $createArgs += @($image, $Scenario, "--duration-seconds", $DurationSeconds)

    & docker @createArgs | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "XBond lab container creation failed."
    }

    & docker --context $dockerContext cp (Join-Path $labRoot "lab.py") "${container}:/opt/xbond/lab/lab.py"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not copy the current lab orchestrator into the container."
    }
    & docker --context $dockerContext cp (Join-Path $labRoot "result.schema.json") "${container}:/opt/xbond/lab/result.schema.json"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not copy the current result schema into the container."
    }
    & docker --context $dockerContext cp (Join-Path $labRoot "modem_fixture.py") "${container}:/opt/xbond/lab/modem_fixture.py"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not copy the current modem fixtures into the container."
    }
    if ($needsApp) {
        & docker --context $dockerContext cp $appPublish "${container}:/opt/ulink"
        if ($LASTEXITCODE -ne 0) {
            throw "Could not copy the published uLink app into the container."
        }
    }

    & docker --context $dockerContext start --attach $container
    $exitCode = [int]((& docker --context $dockerContext inspect $container --format "{{.State.ExitCode}}").Trim())

    & docker --context $dockerContext cp "${container}:/results/." $results
    if ($LASTEXITCODE -ne 0) {
        throw "XBond lab completed but result copy-out failed."
    }

    Get-ChildItem -LiteralPath $results -Filter "*.json" -File | ForEach-Object {
        $currentHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        if (-not $existingResults.ContainsKey($_.FullName) -or $existingResults[$_.FullName] -ne $currentHash) {
            $payload = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
            if ($null -eq $payload.provenance) {
                $payload | Add-Member -NotePropertyName provenance -NotePropertyValue $provenance
            } else {
                foreach ($entry in $provenance.GetEnumerator()) {
                    if ($null -eq $payload.provenance.PSObject.Properties[$entry.Key]) {
                        $payload.provenance | Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value
                    }
                }
            }
            $payload | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $_.FullName -Encoding utf8

            if ($payload.scenario -ne "matrix") {
                if ($payload.provenance.client_binary_sha256 -notmatch "^[0-9a-f]{64}$") {
                    throw "Result '$($_.Name)' is missing a valid client binary SHA-256 provenance value."
                }
                if ($payload.provenance.server_binary_sha256 -notmatch "^[0-9a-f]{64}$") {
                    throw "Result '$($_.Name)' is missing a valid server binary SHA-256 provenance value."
                }
                if (
                    $payload.cleanup.namespaces_remaining.Count -gt 0 -or
                    $payload.cleanup.processes_remaining.Count -gt 0 -or
                    $payload.cleanup.process_groups_remaining.Count -gt 0 -or
                    $payload.cleanup.namespace_pids_remaining.PSObject.Properties.Count -gt 0 -or
                    -not $payload.cleanup.qdisc_state_removed
                ) {
                    throw "Result '$($_.Name)' reports incomplete namespace, descendant, process-group, or qdisc cleanup."
                }
            }
        }
    }
}
finally {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & docker --context $dockerContext inspect $container *> $null
    if ($LASTEXITCODE -eq 0) {
        & docker --context $dockerContext rm --force $container *> $null
    }
    if ($lockAcquired) {
        & docker --context $dockerContext network rm $lockNetwork *> $null
    }
    if ($appPublish -and (Test-Path -LiteralPath $appPublish)) {
        Remove-Item -LiteralPath $appPublish -Recurse -Force
    }
    $ErrorActionPreference = $previousPreference
}

if ($exitCode -ne 0) {
    throw "XBond lab scenario '$Scenario' failed with exit code $exitCode. Inspect the JSON result under '$results'."
}
