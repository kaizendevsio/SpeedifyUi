using System.Reflection;
using System.Text.Json;
using XNetwork.Models;

namespace XNetwork.Services;

public class BuildInfoService(IHostEnvironment environment)
{
    private readonly Lazy<BuildInfo> _buildInfo = new(() => LoadBuildInfo(environment.ContentRootPath, environment.EnvironmentName));

    public BuildInfo GetBuildInfo() => _buildInfo.Value;

    private static BuildInfo LoadBuildInfo(string contentRootPath, string environmentName)
    {
        var buildInfoPath = Path.Combine(contentRootPath, "build-info.json");
        if (File.Exists(buildInfoPath))
        {
            try
            {
                var buildInfo = JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(buildInfoPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (buildInfo is not null)
                {
                    return buildInfo;
                }
            }
            catch
            {
            }
        }

        var assembly = typeof(BuildInfoService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var builtAtUtc = File.GetLastWriteTimeUtc(assembly.Location);

        return new BuildInfo
        {
            Version = string.IsNullOrWhiteSpace(informationalVersion) ? AppChangelog.CurrentVersion : informationalVersion,
            Commit = "local",
            Branch = environmentName,
            BuiltAtUtc = builtAtUtc == default ? null : new DateTimeOffset(builtAtUtc, TimeSpan.Zero)
        };
    }
}
