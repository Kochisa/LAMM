using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Backends;

public sealed record DiscoveredEngine
{
    public required string Path { get; init; }

    public required string SuggestedName { get; init; }

    public required string AdapterKind { get; init; }

    public required string ExecutableFileName { get; init; }
}

/// <summary>
/// Finds engine executables that are already present on the machine. Discovery is
/// purely additive: the user can always point at any other llama.cpp build, which
/// is what makes engines independently upgradable.
/// </summary>
public static class EngineDiscovery
{
    private static readonly string[] ExecutableNames = { "llama-server.exe" };

    public static IReadOnlyList<string> SearchRoots(string configDirectory, string? additionalRoot = null)
    {
        var roots = new List<string>
        {
            Path.Combine(configDirectory, "engines"),
            Path.Combine(AppContext.BaseDirectory, "engines"),
            AppContext.BaseDirectory,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppPaths.ProductFolderName,
                "engines"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "llama.cpp"),
        };

        if (!string.IsNullOrWhiteSpace(additionalRoot))
        {
            roots.Insert(0, additionalRoot!);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        roots.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0));

        roots.Add(Environment.CurrentDirectory);

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<DiscoveredEngine> Discover(string configDirectory, string? additionalRoot = null, int maxDepth = 3)
    {
        var found = new List<DiscoveredEngine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in SearchRoots(configDirectory, additionalRoot))
        {
            foreach (var executableName in ExecutableNames)
            {
                foreach (var file in EnumerateSafely(root, executableName, maxDepth))
                {
                    if (seen.Add(file))
                    {
                        found.Add(new DiscoveredEngine
                        {
                            Path = file,
                            SuggestedName = BuildName(root, file),
                            AdapterKind = BackendAdapterKinds.LlamaCpp,
                            ExecutableFileName = executableName,
                        });
                    }
                }
            }
        }

        return found;
    }

    public static EngineDefinition ToEngineDefinition(DiscoveredEngine discovered, string? id = null)
    {
        var engine = new EngineDefinition
        {
            Id = id ?? MakeId(discovered),
            Name = discovered.SuggestedName,
            AdapterKind = discovered.AdapterKind,
            ExecutablePath = discovered.Path,
            Tags = { "discovered" },
        };

        engine.Normalize();
        return engine;
    }

    private static IEnumerable<string> EnumerateSafely(string root, string fileName, int maxDepth)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();

            string[] entries;
            try
            {
                entries = Directory.GetFiles(directory, fileName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                yield return entry;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || string.Equals(name, "Windows", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static string BuildName(string root, string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(directory) || string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp (llama-server)";
        }

        return $"llama.cpp ({Path.GetFileName(directory)})";
    }

    public static string MakeId(DiscoveredEngine discovered)
    {
        var directory = Path.GetDirectoryName(discovered.Path);
        var suffix = string.IsNullOrEmpty(directory) ? "default" : Path.GetFileName(directory);
        var slug = new string(suffix
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray())
            .ToLowerInvariant();

        if (string.IsNullOrEmpty(slug))
        {
            slug = "default";
        }

        return $"llamacpp-{slug}";
    }
}
