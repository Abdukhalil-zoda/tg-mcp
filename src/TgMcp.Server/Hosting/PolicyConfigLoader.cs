using System.Collections;
using Microsoft.Extensions.Configuration;
using TgMcp.Domain;

namespace TgMcp.Server.Hosting;

/// <summary>The loaded policy plus where the file was looked for.</summary>
/// <param name="Policy">Merged, validated config.</param>
/// <param name="FilePath">Config file path that was used.</param>
/// <param name="FileFound">True if the file existed.</param>
/// <param name="Explicit">True if the path was given (TGMCP_ConfigFile or a command argument) rather than the default.</param>
public sealed record LoadedConfig(PolicyConfig Policy, string FilePath, bool FileFound, bool Explicit);

/// <summary>
/// Loads the policy: the JSON file (comments and trailing commas allowed) overridden by TGMCP_ environment
/// variables. A missing file is an error only when its path was given explicitly.
/// </summary>
public static class PolicyConfigLoader
{
    /// <summary>Default config file path inside the container.</summary>
    public const string DefaultPath = "/config/tgmcp.json";

    /// <summary>Loads from <paramref name="environment"/> and the file at <paramref name="pathArgument"/>, TGMCP_ConfigFile or the default path.</summary>
    public static LoadedConfig Load(IDictionary environment, string? pathArgument = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var env = PolicyConfig.EnvironmentEntries(environment);
        var envPath = env.FirstOrDefault(e => e.Key.Equals("ConfigFile", StringComparison.OrdinalIgnoreCase))?.Value;
        var explicitPath = !string.IsNullOrWhiteSpace(pathArgument) ? pathArgument : string.IsNullOrWhiteSpace(envPath) ? null : envPath;
        var path = Path.GetFullPath(explicitPath ?? DefaultPath);

        IEnumerable<ConfigEntry> file = [];
        string? error = null;
        var found = File.Exists(path);
        if (found)
        {
            try
            {
                var root = new ConfigurationBuilder()
                    .SetBasePath(Path.GetDirectoryName(path)!)
                    .AddJsonFile(Path.GetFileName(path), optional: false, reloadOnChange: false)
                    .Build();
                file = [.. root.AsEnumerable().Select(kv => new ConfigEntry(kv.Key, kv.Value, ConfigOrigin.File))];
            }
            catch (Exception e)
            {
                error = $"config file {path}: cannot be read or parsed ({e.GetBaseException().Message})";
            }
        }
        else if (explicitPath is not null)
        {
            error = $"config file {path}: not found";
        }
        return new LoadedConfig(PolicyConfig.Parse(file.Concat(env), error), path, found, explicitPath is not null);
    }
}
