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

public record Stats(
    string adapter,
    double downBps,
    double upBps,
    double rttMs,
    double lossPct);

public class SpeedifyException(string message) : Exception(message);

public class SpeedifyService
{
    static readonly JsonSerializerOptions opts = new() { PropertyNameCaseInsensitive = true };

    static string Run(string args)
    {
        var p = new Process
        {
            StartInfo = new()
            {
                FileName = "speedify_cli",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!p.Start()) throw new SpeedifyException("Unable to start speedify_cli.");
        string outText = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0) throw new SpeedifyException(err.Trim());
        return outText;
    }

    public async Task<IReadOnlyList<Adapter>> GetAdaptersAsync()
    {
        try
        {
            var json = await Task.Run(() => Run("show adapters"));
            return JsonSerializer.Deserialize<List<Adapter>>(json, opts)!;
        }
        catch (Exception ex) { throw new SpeedifyException($"Adapters: {ex.Message}"); }
    }

    public Task RestartAsync()   => Task.Run(() => Run("restart"));
    public Task SetPriAsync(string id, string p) => Task.Run(() => Run($"adapters prioritize {id} {p}"));

    public async Task<IReadOnlyList<Stats>> GetStatsAsync()
    {
        try
        {
            var json = await Task.Run(() => Run("stats --json"));
            return JsonSerializer.Deserialize<List<Stats>>(json, opts)!;
        }
        catch (Exception ex) { throw new SpeedifyException($"Stats: {ex.Message}"); }
    }

    // any other CLI wrapper you need can be added the same way
}
