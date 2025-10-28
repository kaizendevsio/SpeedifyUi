using System.Text.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using XNetwork.Models;

namespace XNetwork.Services;


// In SpeedifyService.cs
public record Settings(
    string Mode,
    bool AutoStart,
    bool Encrypted,
    string State, // e.g., "connected", "disconnected"
    string? CurrentServerFriendlyName,
    string? CurrentServerCountry,
    string? CurrentServerCity
    // Add other fields from 'status -j' as needed, like public IP of the VPN
    // string? PublicIpAddress 
);

public class SpeedifyException(string message) : Exception(message);

public class SpeedifyService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    string RunTerminatingCommand(string args)
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

    public async Task<IReadOnlyList<Adapter>> GetAdaptersAsync() => Json<List<Adapter>>(RunTerminatingCommand("show adapters"));

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

    public async Task<bool> SetEncryptionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "encryption on" : "encryption off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting encryption: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting encryption: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetHeaderCompressionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "headercompression on" : "headercompression off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting header compression: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting header compression: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetPacketAggregationAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "packetaggr on" : "packetaggr off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting packet aggregation: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting packet aggregation: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetJumboPacketsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "jumbo on" : "jumbo off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting jumbo packets: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting jumbo packets: {ex.Message}");
            return false;
        }
    }

    public async Task<ServerInfo?> GetCurrentServerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show currentserver"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<ServerInfo>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting current server: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting current server: {ex.Message}");
            return null;
        }
    }

    public async Task<SpeedifySettings?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show settings"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<SpeedifySettings>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting settings: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting settings: {ex.Message}");
            return null;
        }
    }

    public async IAsyncEnumerable<ConnectionItem> GetStatsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                Console.WriteLine($"SpeedifyService: Error deserializing root stats JSON array: '{jsonDocString.Substring(0, Math.Min(jsonDocString.Length, 200))}'. Error: {ex.Message}");
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
                        yield return conn;
                    }
                }
            }
        }
    }

    public async Task<SpeedTestResult?> RunSpeedTestAsync(string? adapterId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = string.IsNullOrEmpty(adapterId) ? "speedtest" : $"speedtest {adapterId}";
            var jsonOutput = await Task.Run(() => RunTerminatingCommand(command), cancellationToken)
                .ConfigureAwait(false);
            
            var results = JsonSerializer.Deserialize<List<SpeedTestResult>>(jsonOutput, _jsonOptions);
            return results?.FirstOrDefault();
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error running speed test: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error running speed test: {ex.Message}");
            return null;
        }
    }

    public async Task<SpeedTestResult?> RunStreamTestAsync(string? adapterId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = string.IsNullOrEmpty(adapterId) ? "streamtest" : $"streamtest {adapterId}";
            var jsonOutput = await Task.Run(() => RunTerminatingCommand(command), cancellationToken)
                .ConfigureAwait(false);
            
            var results = JsonSerializer.Deserialize<List<SpeedTestResult>>(jsonOutput, _jsonOptions);
            return results?.FirstOrDefault();
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error running stream test: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error running stream test: {ex.Message}");
            return null;
        }
    }

    public async Task<List<SpeedTestResult>?> GetSpeedTestHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show speedtest"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<SpeedTestResult>>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting speed test history: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting speed test history: {ex.Message}");
            return null;
        }
    }

    static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s, _jsonOptions)!;
}