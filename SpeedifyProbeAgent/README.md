# Speedify Probe Agent

Deploy this project on a separate Linux VPS or device with a stable internet connection and `speedify_cli` available in `PATH`.

## Run

```bash
dotnet run --project SpeedifyProbeAgent/SpeedifyProbeAgent.csproj
```

Default HTTP binding is `http://0.0.0.0:8090`.

## Configure

Edit `SpeedifyProbeAgent/appsettings.json`:

- `Probe:ApiKey`: optional shared key required by the router app.
- `Probe:Countries`: Speedify country codes to test.
- `Probe:Cities`: optional city filter; leave empty to test all cities in the selected countries.
- `Probe:MaxCandidatesPerCycle`: how many servers are tested each interval.
- `Probe:SettleSeconds`, `Probe:SampleCount`, `Probe:SampleIntervalSeconds`: probe timing.
- `Probe:ThroughputSampleBytes`, `Probe:ThroughputSampleCount`, `Probe:ThroughputParallelDownloads`: lightweight download throughput measurement size, attempts, and concurrency.
- `Probe:PriorityProbeIntervalSeconds`: how often hinted current-router servers are retested between normal cycles.

The router app should use the probe API URL in Settings, for example `http://your-vps:8090`.

## API

- `GET /health`
- `GET /scores?maxAgeMinutes=30&limit=50`
- `POST /probe/run`

If `Probe:ApiKey` is set, send it as `X-Api-Key`.
