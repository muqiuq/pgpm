using System.Reflection;

namespace PGPM;

/// <summary>What this instance is: its name and version (Directory.Build.props), for /api/v1/info and the logs.</summary>
public static class ServerInfo
{
    public const string Name = "PGPM";

    public static string Version { get; } =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServerInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
