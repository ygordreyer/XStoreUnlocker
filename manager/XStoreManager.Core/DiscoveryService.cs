using System.Xml.Linq;

namespace XStoreManager.Core;

internal static class SafePath
{
    internal static string Directory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Choose an absolute game folder.");
        var full = Path.GetFullPath(path);
        if (full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a game folder, not a drive root.");
        full = full.TrimEnd(Path.DirectorySeparatorChar);
        if (!System.IO.Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        Check(full); return full;
    }
    internal static void Check(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links and junctions are not supported: " + current);
    }
    internal static string Child(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest path escapes the game folder.");
        Check(path); return path;
    }
}
public sealed class DiscoveryService
{
    readonly Func<IEnumerable<string>> defaultRoots;
    public DiscoveryService(Func<IEnumerable<string>>? defaultRoots = null) => this.defaultRoots = defaultRoots ?? (() => DriveInfo.GetDrives().Where(d => d.IsReady).SelectMany(d => new[] { Path.Combine(d.RootDirectory.FullName, "XboxGames"), Path.Combine(d.RootDirectory.FullName, "Program Files", "ModifiableWindowsApps") }));
    public Task<IReadOnlyList<GameProfile>> ScanAsync(IEnumerable<string>? roots = null, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<GameProfile>>(() =>
    {
        var allRoots = defaultRoots().Concat(roots ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        var games = new List<GameProfile>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in allRoots) Walk(root, 0);
        return games;
        void Walk(string path, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (depth > 5) return;
            try
            {
                path = SafePath.Directory(path); if (!visited.Add(path)) return;
                if (File.Exists(Path.Combine(path, "MicrosoftGame.config"))) { games.Add(InspectFolder(path)); return; }
                foreach (var sub in System.IO.Directory.EnumerateDirectories(path)) Walk(sub, depth + 1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException) { }
        }
    }, cancellationToken);

    public GameProfile InspectFolder(string folder)
    {
        folder = SafePath.Directory(folder);
        string manifest = Path.Combine(folder, "MicrosoftGame.config"); SafePath.Check(manifest);
        if (!File.Exists(manifest))
        {
            string content = Path.Combine(folder, "Content");
            if (File.Exists(Path.Combine(content, "MicrosoftGame.config"))) return InspectFolder(content);
            var executablesWithoutManifest = System.IO.Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).ToArray();
            if (executablesWithoutManifest.Length != 1) throw new InvalidDataException("No manifest found. Select a folder containing exactly one game executable.");
            SafePath.Check(executablesWithoutManifest[0]);
            return new() { Name = Path.GetFileNameWithoutExtension(executablesWithoutManifest[0]), Directory = folder, ExecutablePath = executablesWithoutManifest[0] };
        }
        using var reader = System.Xml.XmlReader.Create(manifest, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        string Value(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
        var identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
        var executables = doc.Descendants().Where(e => e.Name.LocalName == "Executable").Select(e => (string?)e.Attribute("Name")).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        string? exe = executables.FirstOrDefault();
        if (exe is null) throw new InvalidDataException("Manifest has no executable Name.");
        string executablePath = SafePath.Child(folder, exe);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Manifest executable does not exist.", executablePath);
        string name = Value("ShellVisuals");
        var visuals = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
        name = (string?)visuals?.Attribute("DefaultDisplayName") ?? (string?)identity?.Attribute("Name") ?? Path.GetFileName(folder);
        return new() { Name = name, Directory = Path.GetDirectoryName(executablePath)!, ExecutablePath = executablePath, StoreId = Value("StoreId") };
    }
}
