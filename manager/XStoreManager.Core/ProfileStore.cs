using System.Text.Json;

namespace XStoreManager.Core;

public sealed class ProfileStore
{
    readonly string path;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ProfileStore(string? root = null) => path = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStoreManager"), "profiles.json");
    public AppState Load() => File.Exists(path) ? JsonSerializer.Deserialize<AppState>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Profiles are empty.") : new();
    public void Save(AppState state)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(state, Json));
    }
}
internal static class AtomicFile
{
    internal static void Write(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
