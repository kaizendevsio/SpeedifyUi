using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public class AutoServerSwitchStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ILogger<AutoServerSwitchStateStore> _logger;
    private readonly string _filePath;

    public AutoServerSwitchStateStore(ILogger<AutoServerSwitchStateStore> logger)
        : this(logger, Path.Combine(AppContext.BaseDirectory, "auto-server-switch-state.json"))
    {
    }

    public AutoServerSwitchStateStore(ILogger<AutoServerSwitchStateStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public AutoServerSwitchPersistentState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AutoServerSwitchPersistentState();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AutoServerSwitchPersistentState>(json) ?? new AutoServerSwitchPersistentState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load auto server switch state from {Path}", _filePath);
            return new AutoServerSwitchPersistentState();
        }
    }

    public void Save(AutoServerSwitchPersistentState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save auto server switch state to {Path}", _filePath);
        }
    }
}
