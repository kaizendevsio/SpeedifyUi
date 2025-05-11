using System.Text.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using XNetwork.Models;

namespace XNetwork.Services;

public record Adapter(
    string AdapterId,
    string Name,
    string Isp,
    string State,
    string Priority,
    string WorkingPriority,
    string Type);

public record Stats(string Adapter, double DownBps, double UpBps, double RttMs, double LossPct);

public record Settings(string Mode, bool AutoStart, bool Encrypted);

public class SpeedifyException(string message) : Exception(message);

public class SpeedifyService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    static string RunTerminatingCommand(string args)
    {
        var p = new Process
        {
            StartInfo = new()
            {
                FileName = "speedify_cli", Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, // Important for redirection
                CreateNoWindow = true // Good for background processes
            }
        };
        if (!p.Start()) throw new SpeedifyException("speedify_cli not found.");

        // These ReadToEnd calls are fine for commands that will close their streams
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();

        p.WaitForExit(); // Wait for the process to actually exit

        if (p.ExitCode != 0)
        {
            // Prioritize stderr if it has content, otherwise use stdout for the exception message
            throw new SpeedifyException(!string.IsNullOrWhiteSpace(err) ? err.Trim() : output.Trim());
        }

        return output;
    }

// Wrapper for StreamCommandOutputCore to handle process lifetime
    private static async IAsyncEnumerable<string> StreamCommandOutputAsync(
        string args,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "speedify_cli",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Capture errors
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = processStartInfo };
        var errorMessages = new List<string>(); // To collect error stream data

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (errorMessages) // Ensure thread-safe access if ErrorDataReceived is on another thread
                {
                    errorMessages.Add(e.Data);
                }
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new SpeedifyException($"Failed to start speedify_cli for streaming with args: {args}");
            }

            process.BeginErrorReadLine(); // Start reading stderr asynchronously

            await foreach (var line in StreamCommandOutputCoreAsync(process.StandardOutput, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return line;
            }
        }
        finally // This block ensures the process is cleaned up
        {
            try
            {
                if (!process.HasExited)
                {
                    Console.WriteLine($"SpeedifyService: Attempting to kill streaming process for '{args}'.");
                    process.Kill(true); // Kill the process and its entire tree
                    // Give a moment for the process to exit after kill signal
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                /* Process already exited */
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SpeedifyService: Exception during process cleanup for '{args}': {ex.Message}");
            }

            // Log any accumulated errors after the process has been dealt with
            if (errorMessages.Any())
            {
                Console.WriteLine(
                    $"SpeedifyService: speedify_cli (stderr) for '{args}': {Environment.NewLine}{string.Join(Environment.NewLine, errorMessages)}");
            }
        }
    }

    // Inner core method that does the yielding without try/catch/finally around yield
    private static async IAsyncEnumerable<string> StreamCommandOutputCoreAsync(
        StreamReader streamReader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StringBuilder jsonBuffer = new StringBuilder();
        string line;
        // ReadLineAsync can return null if the stream ends.
        while ((line = await streamReader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            jsonBuffer.AppendLine(line);
            string currentContent = jsonBuffer.ToString().Trim();

            if (currentContent.StartsWith("[") && currentContent.EndsWith("]"))
            {
                bool isValidJsonDoc = false;
                try
                {
                    // Minimal validation: check if it can be parsed as a JsonDocument
                    using (JsonDocument.Parse(currentContent))
                    {
                        isValidJsonDoc = true;
                    }
                }
                catch (JsonException)
                {
                    // Not a complete/valid JSON doc yet, continue buffering.
                }

                if (isValidJsonDoc)
                {
                    yield return currentContent;
                    jsonBuffer.Clear(); // Reset for the next document
                }
                else if (jsonBuffer.Length > 32768) // Safeguard for buffer size
                {
                    Console.WriteLine(
                        $"SpeedifyService: JSON buffer limit exceeded in StreamCommandOutputCoreAsync. Clearing. Partial: {jsonBuffer.ToString(0, Math.Min(jsonBuffer.Length, 200))}");
                    jsonBuffer.Clear(); // Prevent runaway buffer
                }
            }
        }
    }

    public async Task<IReadOnlyList<Adapter>> GetAdaptersAsync()
        => Json<List<Adapter>>(await Task.Run(() => RunTerminatingCommand("show adapters")));

    public async Task<IReadOnlyList<Adapter>> GetAdaptersAsync(CancellationToken cancellationToken = default)
    {
        // Task.Run to offload the synchronous RunTerminatingCommand from a potentially UI thread
        var jsonOutput = await Task.Run(() => RunTerminatingCommand("show adapters"), cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<Adapter>>(jsonOutput, _jsonOptions);
    }

    public Task SetPriorityAsync(string id, string priority, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunTerminatingCommand($"adapter priority {id} {priority}"), cancellationToken);
    }

    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunTerminatingCommand("restart"), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunTerminatingCommand("stop"), cancellationToken);
    }

    // 'start' might daemonize; ensure RunTerminatingCommand handles this or use a fire-and-forget approach
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunTerminatingCommand("start"), cancellationToken);
    }


    public Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunTerminatingCommand($"mode {mode}"), cancellationToken);
    }

    public async Task<Settings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var rawJson = await Task.Run(() => RunTerminatingCommand("status -j"), cancellationToken)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            return new Settings(
                root.GetProperty("mode").GetString() ?? "unknown",
                root.GetProperty("autoStart").GetBoolean(),
                root.GetProperty("encrypted").GetBoolean());
        }
        catch (SpeedifyException ex) // Catch specific exception from RunTerminatingCommand
        {
            Console.WriteLine(
                $"SpeedifyService: 'status -j' failed ({ex.Message}), falling back to plain text 'status'.");
            var txt = await Task.Run(() => RunTerminatingCommand("status"), cancellationToken).ConfigureAwait(false);

            string GrabValue(string key) =>
                txt.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(l => l.Contains(key, StringComparison.OrdinalIgnoreCase))?
                    .Split(':')
                    .LastOrDefault()?.Trim() ?? "";

            return new Settings(
                GrabValue("Mode").ToLowerInvariant(),
                GrabValue("AutoStart").Equals("on", StringComparison.OrdinalIgnoreCase),
                GrabValue("Encrypted").Equals("yes", StringComparison.OrdinalIgnoreCase));
        }
    }

    public async IAsyncEnumerable<Stats> GetStatsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var jsonDocString in StreamCommandOutputAsync("stats", cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(jsonDocString)) continue;

            List<JsonElement> parsedEvent = null;
            try
            {
                parsedEvent = JsonSerializer.Deserialize<List<JsonElement>>(jsonDocString, _jsonOptions);
            }
            catch (JsonException ex)
            {
                Console.WriteLine(
                    $"SpeedifyService: Error deserializing root stats JSON array: '{jsonDocString.Substring(0, Math.Min(jsonDocString.Length, 200))}'. Error: {ex.Message}");
                continue; // Skip this malformed document
            }

            if (parsedEvent == null || parsedEvent.Count != 2 || parsedEvent[0].ValueKind != JsonValueKind.String)
            {
                continue; // Not the expected structure
            }

            string eventType = parsedEvent[0].GetString();
            if (eventType != "connection_stats")
            {
                continue; // We are only interested in connection_stats for now
            }

            ConnectionStatsPayload payload = null;
            try
            {
                payload = parsedEvent[1].Deserialize<ConnectionStatsPayload>(_jsonOptions);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"SpeedifyService: Error deserializing ConnectionStatsPayload: Error: {ex.Message}");
                continue; // Skip this malformed payload
            }

            if (payload?.Connections != null)
            {
                foreach (var conn in payload.Connections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (conn.AdapterId != null &&
                        conn.AdapterId != "speedify" && // Filter out aggregate
                        conn.ConnectionId != null &&
                        !conn.ConnectionId.EndsWith("%proxy")) // Filter out proxy connections
                    {
                        // This yield is now outside a try-catch that would violate CS1626/CS1627
                        yield return new Stats(
                            Adapter: conn.AdapterId,
                            DownBps: conn.ReceiveBps,
                            UpBps: conn.SendBps,
                            RttMs: conn.LatencyMs,
                            LossPct: conn.LossSend * 100.0 // Convert fraction to percentage
                        );
                    }
                }
            }
        }
    }

    static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s, _jsonOptions)!;
}