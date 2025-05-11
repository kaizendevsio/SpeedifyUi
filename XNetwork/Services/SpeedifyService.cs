using System.Text.Json;
using System.Diagnostics;

namespace XNetwork.Services;

public record Adapter(
    string adapterID,
    string name,
    string isp,
    string state,
    string priority,
    string workingPriority,
    string type);

public record Stats(string adapter, double downBps, double upBps, double rttMs, double lossPct);

public record Settings(string mode, bool autoStart, bool encrypted);

public class SpeedifyException(string Message) : Exception(Message);

public class SpeedifyService
{
    static JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };

    static string Run(string args)
    {
        var p = new Process
        {
            StartInfo = new()
            {
                FileName = "speedify_cli", Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        if (!p.Start()) throw new SpeedifyException("speedify_cli not found.");
        string @out = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new SpeedifyException(err.Trim().Length > 0 ? err : @out);
        return @out;
    }

    public async Task<IReadOnlyList<Adapter>> GetAdaptersAsync()
        => Json<List<Adapter>>(await Task.Run(() => Run("show adapters")));

    // stats – works on all builds: “stats current -j”
    public async Task<IReadOnlyList<Stats>> GetStatsAsync()
        => Json<List<Stats>>(await Task.Run(() => Run("stats current -j")));

    public Task SetPriorityAsync(string id, string p) => Task.Run(() => Run($"adapters prioritize {id} {p}"));
    public Task RestartAsync() => Task.Run(() => Run("restart"));
    public Task StopAsync() => Task.Run(() => Run("stop"));
    public Task StartAsync() => Task.Run(() => Run("start"));
    public Task SetModeAsync(string m) => Task.Run(() => Run($"mode {m}"));

    // show settings so UI can display mode, encryption, autostart …
    public async Task<Settings> GetSettingsAsync()
    {
        try
        {
            // Works on every Speedify build (CLI v12) and always supports –j
            var raw = await Task.Run(() => Run("status -j"));
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return new Settings(
                root.GetProperty("mode").GetString() ?? "unknown",
                root.GetProperty("autoStart").GetBoolean(),
                root.GetProperty("encrypted").GetBoolean());
        }
        catch (SpeedifyException)
        {
            /* fallback: plain-text status */
            var txt = await Task.Run(() => Run("status"));

            // fuzzy but safe enough: look for keywords
            string grab(string key)
                => txt.Split('\n').FirstOrDefault(l => l.Contains(key, StringComparison.OrdinalIgnoreCase))?
                    .Split(':').Last().Trim() ?? "";

            var mode = grab("Mode").ToLower();
            var autostart = grab("AutoStart").Equals("on", StringComparison.OrdinalIgnoreCase);
            var encrypted = grab("Encrypted").Equals("yes", StringComparison.OrdinalIgnoreCase);
            return new Settings(mode, autostart, encrypted);
        }
    }

    static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s, json)!;
}