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

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        // Execute disconnect, wait briefly, then connect
        await Task.Run(() => RunTerminatingCommand("disconnect"), cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        await Task.Run(() => RunTerminatingCommand("connect"), cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunTerminatingCommand("disconnect"), cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunTerminatingCommand("connect"), cancellationToken);
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

    public async Task<SpeedifySettings?> SetBondingModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate mode
            if (mode != "speed" && mode != "redundant" && mode != "streaming")
            {
                throw new ArgumentException($"Invalid bonding mode: {mode}. Valid modes are: speed, redundant, streaming");
            }
            
            var jsonOutput = await Task.Run(() => RunTerminatingCommand($"mode {mode}"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<SpeedifySettings>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting bonding mode: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting bonding mode: {ex.Message}");
            return null;
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

    #region Privacy Settings Methods

    /// <summary>
    /// Gets the current privacy settings including DNS configuration and leak protection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Privacy settings or null if an error occurs.</returns>
    public async Task<PrivacySettings?> GetPrivacySettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show privacy"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<PrivacySettings>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting privacy settings: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting privacy settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets custom DNS server addresses.
    /// </summary>
    /// <param name="dnsAddresses">List of DNS server IP addresses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetDnsServersAsync(List<string> dnsAddresses, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = dnsAddresses.Count > 0
                ? $"dns {string.Join(" ", dnsAddresses)}"
                : "dns";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting DNS servers: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting DNS servers: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets DNS leak protection (Windows only).
    /// </summary>
    /// <param name="enabled">True to enable, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetDnsLeakProtectionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "privacy dnsleak on" : "privacy dnsleak off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting DNS leak protection: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting DNS leak protection: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets IP leak protection (Windows only).
    /// </summary>
    /// <param name="enabled">True to enable, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetIpLeakProtectionAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "privacy ipleak on" : "privacy ipleak off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting IP leak protection: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting IP leak protection: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets kill switch (Windows only). Blocks internet if VPN disconnects.
    /// </summary>
    /// <param name="enabled">True to enable, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetKillSwitchAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "privacy killswitch on" : "privacy killswitch off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting kill switch: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting kill switch: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets whether to request browsers to disable DNS-over-HTTPS.
    /// </summary>
    /// <param name="enabled">True to enable request, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetDisableDoHRequestAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "privacy requestToDisableDoH on" : "privacy requestToDisableDoH off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting DoH request: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting DoH request: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Streaming Bypass Methods

    /// <summary>
    /// Gets the current streaming bypass settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Streaming bypass settings or null if an error occurs.</returns>
    public async Task<StreamingBypassSettings?> GetStreamingBypassSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show streamingbypass"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<StreamingBypassSettings>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting streaming bypass settings: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting streaming bypass settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Manages streaming bypass domains.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="domains">List of domain names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingBypassDomainsAsync(string action, List<string> domains, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"streamingbypass domains {action} {string.Join(" ", domains)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming bypass domains: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming bypass domains: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages streaming bypass IPv4 addresses.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ips">List of IPv4 addresses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingBypassIpv4Async(string action, List<string> ips, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"streamingbypass ipv4 {action} {string.Join(" ", ips)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming bypass IPv4: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming bypass IPv4: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages streaming bypass IPv6 addresses.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ips">List of IPv6 addresses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingBypassIpv6Async(string action, List<string> ips, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"streamingbypass ipv6 {action} {string.Join(" ", ips)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming bypass IPv6: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming bypass IPv6: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages streaming bypass ports.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ports">List of port rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingBypassPortsAsync(string action, List<PortRule> ports, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var portStrings = ports.Select(FormatPortRule);
            var args = $"streamingbypass ports {action} {string.Join(" ", portStrings)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming bypass ports: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming bypass ports: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enables or disables bypass for a specific streaming service.
    /// </summary>
    /// <param name="serviceName">Name of the service (e.g., "netflix", "youtube").</param>
    /// <param name="enabled">True to enable bypass, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingBypassServiceAsync(string serviceName, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = $"streamingbypass service {serviceName} {(enabled ? "on" : "off")}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming bypass service: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming bypass service: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enables or disables all streaming bypass functionality.
    /// </summary>
    /// <param name="enabled">True to enable, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingBypassEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "streamingbypass enable" : "streamingbypass disable";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming bypass enabled: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming bypass enabled: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Streaming Mode Methods (High-Priority Traffic)

    /// <summary>
    /// Gets the current streaming mode settings for high-priority traffic.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Streaming settings or null if an error occurs.</returns>
    public async Task<StreamingSettings?> GetStreamingSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show streaming"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<StreamingSettings>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting streaming settings: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting streaming settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Manages high-priority streaming domains.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="domains">List of domain names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingDomainsAsync(string action, List<string> domains, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"streaming domains {action} {string.Join(" ", domains)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming domains: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming domains: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages high-priority streaming IPv4 addresses.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ips">List of IPv4 addresses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingIpv4Async(string action, List<string> ips, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"streaming ipv4 {action} {string.Join(" ", ips)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming IPv4: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming IPv4: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages high-priority streaming IPv6 addresses.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ips">List of IPv6 addresses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingIpv6Async(string action, List<string> ips, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"streaming ipv6 {action} {string.Join(" ", ips)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming IPv6: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming IPv6: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages high-priority streaming ports.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ports">List of port rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStreamingPortsAsync(string action, List<PortRule> ports, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var portStrings = ports.Select(FormatPortRule);
            var args = $"streaming ports {action} {string.Join(" ", portStrings)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting streaming ports: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting streaming ports: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Transport Methods

    /// <summary>
    /// Gets the current transport settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transport settings or null if an error occurs.</returns>
    public async Task<TransportSettings?> GetTransportSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show settings"), cancellationToken)
                .ConfigureAwait(false);
            // Transport settings are part of the general settings response
            // Parse the relevant fields
            using var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;
            
            return new TransportSettings
            {
                TransportMode = root.TryGetProperty("transportMode", out var tm) ? tm.GetString() ?? "auto" : "auto",
                TransportRetrySeconds = root.TryGetProperty("transportRetrySeconds", out var tr) ? tr.GetInt32() : 30,
                ConnectRetrySeconds = root.TryGetProperty("connectRetrySeconds", out var cr) ? cr.GetInt32() : 30
            };
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting transport settings: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting transport settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets the transport mode.
    /// </summary>
    /// <param name="mode">Transport mode: "auto", "tcp", "tcp-multi", "udp", or "https".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetTransportModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        try
        {
            var validModes = new[] { "auto", "tcp", "tcp-multi", "udp", "https" };
            if (!validModes.Contains(mode.ToLowerInvariant()))
            {
                throw new ArgumentException($"Invalid transport mode: {mode}. Valid modes are: {string.Join(", ", validModes)}");
            }
            
            await Task.Run(() => RunTerminatingCommand($"transport {mode}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting transport mode: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting transport mode: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the transport retry interval.
    /// </summary>
    /// <param name="seconds">Seconds to wait before retrying transport connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetTransportRetryAsync(int seconds, CancellationToken cancellationToken = default)
    {
        try
        {
            if (seconds < 0)
            {
                throw new ArgumentException("Transport retry seconds cannot be negative");
            }
            
            await Task.Run(() => RunTerminatingCommand($"transportretry {seconds}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting transport retry: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting transport retry: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the connect retry interval.
    /// </summary>
    /// <param name="seconds">Seconds to wait before retrying server connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetConnectRetryAsync(int seconds, CancellationToken cancellationToken = default)
    {
        try
        {
            if (seconds < 0)
            {
                throw new ArgumentException("Connect retry seconds cannot be negative");
            }
            
            await Task.Run(() => RunTerminatingCommand($"connectretry {seconds}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting connect retry: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting connect retry: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Connection Methods

    /// <summary>
    /// Gets the current connect method configuration.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connect method settings or null if an error occurs.</returns>
    public async Task<ConnectMethod?> GetConnectMethodAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show connectmethod"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<ConnectMethod>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting connect method: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting connect method: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets the server connection method.
    /// </summary>
    /// <param name="method">Connection method: "closest", "public", "private", "p2p", or a country code.</param>
    /// <param name="country">Optional country code for server selection.</param>
    /// <param name="city">Optional city name for server selection.</param>
    /// <param name="num">Optional server number for specific server selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetConnectMethodAsync(string method, string? country = null, string? city = null, int? num = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = new List<string> { "connectmethod", method };
            
            if (!string.IsNullOrEmpty(country))
                args.Add(country);
            if (!string.IsNullOrEmpty(city))
                args.Add(city);
            if (num.HasValue)
                args.Add(num.Value.ToString());
            
            await Task.Run(() => RunTerminatingCommand(string.Join(" ", args)), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting connect method: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting connect method: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets whether to automatically connect on startup.
    /// </summary>
    /// <param name="enabled">True to enable startup connect, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetStartupConnectAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = enabled ? "startupconnect on" : "startupconnect off";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting startup connect: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting startup connect: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Advanced Settings Methods

    /// <summary>
    /// Sets the maximum number of redundant connections.
    /// </summary>
    /// <param name="connections">Maximum redundant connections (0 to disable).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetMaxRedundantAsync(int connections, CancellationToken cancellationToken = default)
    {
        try
        {
            if (connections < 0)
            {
                throw new ArgumentException("Max redundant connections cannot be negative");
            }
            
            await Task.Run(() => RunTerminatingCommand($"maxredundant {connections}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting max redundant: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting max redundant: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the overflow threshold in Mbps.
    /// </summary>
    /// <param name="mbps">Overflow threshold in megabits per second.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetOverflowThresholdAsync(double mbps, CancellationToken cancellationToken = default)
    {
        try
        {
            if (mbps < 0)
            {
                throw new ArgumentException("Overflow threshold cannot be negative");
            }
            
            await Task.Run(() => RunTerminatingCommand($"overflow {mbps}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting overflow threshold: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting overflow threshold: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the priority overflow threshold in Mbps.
    /// </summary>
    /// <param name="mbps">Priority overflow threshold in megabits per second.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetPriorityOverflowAsync(double mbps, CancellationToken cancellationToken = default)
    {
        try
        {
            if (mbps < 0)
            {
                throw new ArgumentException("Priority overflow cannot be negative");
            }
            
            await Task.Run(() => RunTerminatingCommand($"priorityoverflow {mbps}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting priority overflow: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting priority overflow: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the target number of connections for upload and download.
    /// </summary>
    /// <param name="uploadConnections">Target upload connections.</param>
    /// <param name="downloadConnections">Target download connections.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetTargetConnectionsAsync(int uploadConnections, int downloadConnections, CancellationToken cancellationToken = default)
    {
        try
        {
            if (uploadConnections < 0 || downloadConnections < 0)
            {
                throw new ArgumentException("Target connections cannot be negative");
            }
            
            await Task.Run(() => RunTerminatingCommand($"targetconnections {uploadConnections} {downloadConnections}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting target connections: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting target connections: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Port Forwarding Methods

    /// <summary>
    /// Sets the forwarded ports configuration.
    /// </summary>
    /// <param name="ports">List of forwarded port configurations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetForwardedPortsAsync(List<ForwardedPort> ports, CancellationToken cancellationToken = default)
    {
        try
        {
            var portStrings = ports.Select(p => $"{p.Port}/{p.Protocol}");
            var args = $"ports {string.Join(" ", portStrings)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting forwarded ports: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting forwarded ports: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears all forwarded ports.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> ClearForwardedPortsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() => RunTerminatingCommand("ports"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error clearing forwarded ports: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error clearing forwarded ports: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Subnet Methods

    /// <summary>
    /// Sets the downstream subnets for enterprise routing.
    /// </summary>
    /// <param name="subnets">List of downstream subnet configurations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetDownstreamSubnetsAsync(List<DownstreamSubnet> subnets, CancellationToken cancellationToken = default)
    {
        try
        {
            var subnetStrings = subnets.Select(s => $"{s.Address}/{s.PrefixLength}");
            var args = $"subnets {string.Join(" ", subnetStrings)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting downstream subnets: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting downstream subnets: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears all downstream subnets.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> ClearDownstreamSubnetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() => RunTerminatingCommand("subnets"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error clearing downstream subnets: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error clearing downstream subnets: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Fixed Delay Methods

    /// <summary>
    /// Gets the current fixed delay settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Fixed delay settings or null if an error occurs.</returns>
    public async Task<FixedDelaySettings?> GetFixedDelaySettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show fixeddelay"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<FixedDelaySettings>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting fixed delay settings: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting fixed delay settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets the fixed delay in milliseconds.
    /// </summary>
    /// <param name="delayMs">Delay in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetFixedDelayAsync(int delayMs, CancellationToken cancellationToken = default)
    {
        try
        {
            if (delayMs < 0)
            {
                throw new ArgumentException("Fixed delay cannot be negative");
            }
            
            await Task.Run(() => RunTerminatingCommand($"fixeddelay {delayMs}"), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting fixed delay: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting fixed delay: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages fixed delay domains.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="domains">List of domain names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetFixedDelayDomainsAsync(string action, List<string> domains, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"fixeddelay domains {action} {string.Join(" ", domains)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting fixed delay domains: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting fixed delay domains: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages fixed delay IP addresses.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ips">List of IP addresses.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetFixedDelayIpsAsync(string action, List<string> ips, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var args = $"fixeddelay ips {action} {string.Join(" ", ips)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting fixed delay IPs: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting fixed delay IPs: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Manages fixed delay ports.
    /// </summary>
    /// <param name="action">Action to perform: "add", "rem", or "set".</param>
    /// <param name="ports">List of port rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetFixedDelayPortsAsync(string action, List<PortRule> ports, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAction(action);
            var portStrings = ports.Select(FormatPortRule);
            var args = $"fixeddelay ports {action} {string.Join(" ", portStrings)}";
            await Task.Run(() => RunTerminatingCommand(args), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting fixed delay ports: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting fixed delay ports: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Extended Adapter Methods

    /// <summary>
    /// Gets extended information for all adapters.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of extended adapter information or null if an error occurs.</returns>
    public async Task<List<AdapterExtended>?> GetAdaptersExtendedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonOutput = await Task.Run(() => RunTerminatingCommand("show adapters"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<AdapterExtended>>(jsonOutput, _jsonOptions);
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error getting extended adapters: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error getting extended adapters: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sets encryption for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="enabled">True to enable encryption, false to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterEncryptionAsync(string adapterId, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = $"adapter encryption {adapterId} {(enabled ? "on" : "off")}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter encryption: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter encryption: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets whether to expose DSCP markings for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="enabled">True to expose DSCP, false to hide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterExposeDscpAsync(string adapterId, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = $"adapter expose-dscp {adapterId} {(enabled ? "on" : "off")}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter expose DSCP: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter expose DSCP: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the daily data limit for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="bytesLimit">Daily limit in bytes, or null for unlimited.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterDailyLimitAsync(string adapterId, long? bytesLimit, CancellationToken cancellationToken = default)
    {
        try
        {
            var limitArg = bytesLimit.HasValue ? bytesLimit.Value.ToString() : "unlimited";
            var command = $"adapter datalimit daily {adapterId} {limitArg}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter daily limit: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter daily limit: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the monthly data limit for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="bytesLimit">Monthly limit in bytes, or null for unlimited.</param>
    /// <param name="resetDay">Day of month (1-31) when usage resets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterMonthlyLimitAsync(string adapterId, long? bytesLimit, int resetDay, CancellationToken cancellationToken = default)
    {
        try
        {
            if (resetDay < 1 || resetDay > 31)
            {
                throw new ArgumentException("Reset day must be between 1 and 31");
            }
            
            var limitArg = bytesLimit.HasValue ? bytesLimit.Value.ToString() : "unlimited";
            var command = $"adapter datalimit monthly {adapterId} {limitArg} {resetDay}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter monthly limit: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter monthly limit: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets additional daily boost data for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="additionalBytes">Additional bytes to add to daily allowance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterDailyBoostAsync(string adapterId, long additionalBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            if (additionalBytes < 0)
            {
                throw new ArgumentException("Additional bytes cannot be negative");
            }
            
            var command = $"adapter datalimit dailyboost {adapterId} {additionalBytes}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter daily boost: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter daily boost: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the rate limit applied when adapter is over its data limit.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="bitsPerSecond">Rate limit in bits per second (0 to block entirely).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterOverlimitRateAsync(string adapterId, long bitsPerSecond, CancellationToken cancellationToken = default)
    {
        try
        {
            if (bitsPerSecond < 0)
            {
                throw new ArgumentException("Rate limit cannot be negative");
            }
            
            var command = $"adapter overlimitratelimit {adapterId} {bitsPerSecond}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter overlimit rate: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter overlimit rate: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the rate limit for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="downloadBps">Download rate limit in bits per second, or null for unlimited.</param>
    /// <param name="uploadBps">Upload rate limit in bits per second, or null for unlimited.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterRateLimitAsync(string adapterId, long? downloadBps, long? uploadBps, CancellationToken cancellationToken = default)
    {
        try
        {
            var downloadArg = downloadBps.HasValue ? downloadBps.Value.ToString() : "unlimited";
            var uploadArg = uploadBps.HasValue ? uploadBps.Value.ToString() : "unlimited";
            var command = $"adapter ratelimit {adapterId} {downloadArg} {uploadArg}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter rate limit: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter rate limit: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resets usage statistics for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> ResetAdapterUsageAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = $"adapter resetusage {adapterId}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error resetting adapter usage: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error resetting adapter usage: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the directional mode for a specific adapter.
    /// </summary>
    /// <param name="adapterId">Adapter identifier.</param>
    /// <param name="uploadMode">Upload mode: "on", "backup_off", or "strict_off".</param>
    /// <param name="downloadMode">Download mode: "on", "backup_off", or "strict_off".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> SetAdapterDirectionalModeAsync(string adapterId, string uploadMode, string downloadMode, CancellationToken cancellationToken = default)
    {
        try
        {
            var validModes = new[] { "on", "backup_off", "strict_off" };
            if (!validModes.Contains(uploadMode.ToLowerInvariant()))
            {
                throw new ArgumentException($"Invalid upload mode: {uploadMode}. Valid modes are: {string.Join(", ", validModes)}");
            }
            if (!validModes.Contains(downloadMode.ToLowerInvariant()))
            {
                throw new ArgumentException($"Invalid download mode: {downloadMode}. Valid modes are: {string.Join(", ", validModes)}");
            }
            
            var command = $"adapter directionalmode {adapterId} {uploadMode} {downloadMode}";
            await Task.Run(() => RunTerminatingCommand(command), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SpeedifyException ex)
        {
            Console.WriteLine($"SpeedifyService: Error setting adapter directional mode: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SpeedifyService: Unexpected error setting adapter directional mode: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Validates that the action is one of the allowed values.
    /// </summary>
    /// <param name="action">Action to validate.</param>
    /// <exception cref="ArgumentException">Thrown when action is invalid.</exception>
    private static void ValidateAction(string action)
    {
        var validActions = new[] { "add", "rem", "set" };
        if (!validActions.Contains(action.ToLowerInvariant()))
        {
            throw new ArgumentException($"Invalid action: {action}. Valid actions are: {string.Join(", ", validActions)}");
        }
    }

    /// <summary>
    /// Formats a port rule for CLI command.
    /// </summary>
    /// <param name="port">Port rule to format.</param>
    /// <returns>Formatted port string (e.g., "80/tcp" or "8000-8100/udp").</returns>
    private static string FormatPortRule(PortRule port)
    {
        return port.PortRangeEnd.HasValue
            ? $"{port.Port}-{port.PortRangeEnd.Value}/{port.Protocol}"
            : $"{port.Port}/{port.Protocol}";
    }

    #endregion

    static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s, _jsonOptions)!;
}