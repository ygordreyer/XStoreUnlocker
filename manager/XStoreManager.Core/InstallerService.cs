using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace XStoreManager.Core;

public sealed class InstallerOptions
{
    public string? SystemRuntimePath { get; init; }
    public bool AdoptExistingConfiguration { get; init; }
    public Action<string>? VerifyOriginal { get; init; }
    public Action<string>? VerifyPayload { get; init; }
    public Func<GameProfile, bool>? IsGameRunning { get; init; }
    public Action<int>? AfterMutation { get; init; }
}

public sealed class InstallerService
{
    public const string ProxyName = "XGameRuntime.dll", OriginalName = "XGameRuntime_o.dll", ConfigName = "xstore_unlocker.ini";
    public const string ManifestName = ".xstore-manager.json", JournalName = ".xstore-manager.transaction.json";
    readonly string payload; readonly InstallerOptions options;
    static readonly string[] Managed = [ProxyName, OriginalName, ConfigName, ManifestName];
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    sealed class Manifest
    {
        public int Version { get; set; } = 2;
        public string Directory { get; set; } = "";
        public Dictionary<string, string?> Originals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string?> OriginalHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string RuntimeHash { get; set; } = "";
        public Dictionary<string, string> InstalledHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    sealed class Journal
    {
        public int Version { get; set; } = 1;
        public string Directory { get; set; } = "";
        public Dictionary<string, string?> Before { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string?> After { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    public InstallerService(string payloadPath, InstallerOptions? options = null) { payload = Path.GetFullPath(payloadPath); this.options = options ?? new(); }
    public InstallationStatus Status(GameProfile profile)
    {
        try
        {
            string root = Validate(profile);
            if (File.Exists(Path.Combine(root, JournalName))) return new("Interrupted", "An unfinished transaction needs recovery. Repair or remove will recover it before continuing.", false);
            var manifest = ReadManifest(root);
            if (manifest is null)
            {
                if (File.Exists(Path.Combine(root, OriginalName)) || File.Exists(Path.Combine(root, ConfigName))) return new("Unmanaged", "Existing runtime backup or INI is unmanaged. Import and explicitly adopt the INI; unknown backups must be resolved manually.", false);
                return new("Not installed", "No managed installation.", false);
            }
            VerifyInstalled(root, manifest, false);
            return new("Installed", "Managed files and original backups match the recorded SHA256 hashes.", true);
        }
        catch (Exception e) { return new("Conflict", e.Message, false); }
    }
    public void Install(GameProfile profile)
    {
        if (profile.Excluded) throw new InvalidOperationException("This game is excluded. Clear its exclusion before installing.");
        string root = Validate(profile); EnsureStopped(profile); Recover(root);
        foreach (var dlc in profile.Dlcs.Where(d => !string.IsNullOrWhiteSpace(d.MountPath))) _ = SafePath.Directory(dlc.MountPath);
        SafePath.Check(payload); VerifyX64(payload); (options.VerifyPayload ?? VerifyBundledPayload)(payload);
        var manifest = ReadManifest(root); var before = Snapshot(root);
        byte[] original;
        if (manifest is null)
        {
            if (before[OriginalName] is not null) throw new IOException("An unmanaged XGameRuntime_o.dll exists. It will not be overwritten.");
            if (before[ConfigName] is not null && !options.AdoptExistingConfiguration) throw new IOException("An unmanaged INI exists. Import it and explicitly adopt it before installation.");
            string source = before[ProxyName] is not null ? Path.Combine(root, ProxyName) : options.SystemRuntimePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), ProxyName);
            SafePath.Check(source); VerifyX64(source); (options.VerifyOriginal ?? VerifyMicrosoftRuntime)(source);
            original = File.ReadAllBytes(source);
            manifest = new()
            {
                Directory = root,
                Originals = before.Where(x => x.Key != ManifestName).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                OriginalHashes = before.Where(x => x.Key != ManifestName).ToDictionary(x => x.Key, x => x.Value is null ? null : Hash(Decode(x.Value)!), StringComparer.OrdinalIgnoreCase),
                RuntimeHash = Hash(original)
            };
        }
        else
        {
            VerifyInstalled(root, manifest, true);
            original = Decode(manifest.Originals[ProxyName]) ?? File.ReadAllBytes(Path.Combine(root, OriginalName));
        }
        var after = new Dictionary<string, string?>(before, StringComparer.OrdinalIgnoreCase)
        {
            [ProxyName] = Convert.ToBase64String(File.ReadAllBytes(payload)),
            [OriginalName] = Convert.ToBase64String(original),
            [ConfigName] = Convert.ToBase64String(Encoding.UTF8.GetBytes(IniService.Serialize(profile)))
        };
        manifest.InstalledHashes = after.Where(x => x.Key != ManifestName && x.Value is not null).ToDictionary(x => x.Key, x => Hash(Decode(x.Value)!), StringComparer.OrdinalIgnoreCase);
        after[ManifestName] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        Transact(root, before, after);
    }
    public void Uninstall(GameProfile profile)
    {
        string root = Validate(profile); EnsureStopped(profile); Recover(root);
        var manifest = ReadManifest(root) ?? throw new InvalidOperationException("No managed installation exists. Unmanaged files will not be removed.");
        VerifyInstalled(root, manifest, false);
        var before = Snapshot(root); var after = new Dictionary<string, string?>(manifest.Originals, StringComparer.OrdinalIgnoreCase) { [ManifestName] = null };
        Transact(root, before, after);
    }
    public void Recover(GameProfile profile) { string root = Validate(profile); EnsureStopped(profile); Recover(root); }
    string Validate(GameProfile profile)
    {
        string root = SafePath.Directory(profile.Directory);
        if (root.Equals(Path.GetDirectoryName(payload), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The bundled engine folder cannot be managed as a game folder.");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        if (windows.Length > 0 && (root.Equals(windows, StringComparison.OrdinalIgnoreCase) || root.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Windows system folders cannot be managed as game folders.");
        foreach (string name in Managed.Append(JournalName)) SafePath.Check(Path.Combine(root, name));
        if (!string.IsNullOrWhiteSpace(profile.ExecutablePath))
        {
            string exe = Path.GetFullPath(profile.ExecutablePath);
            if (!exe.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Executable must be inside the selected game folder.");
            SafePath.Check(exe); if (!File.Exists(exe)) throw new FileNotFoundException("Game executable is missing.", exe);
        }
        return root;
    }
    void EnsureStopped(GameProfile profile)
    {
        if ((options.IsGameRunning ?? IsRunning)(profile)) throw new IOException("Close the game before changing its files.");
    }
    static bool IsRunning(GameProfile profile)
    {
        string root = Path.GetFullPath(profile.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { string? exe = process.MainModule?.FileName; if (exe?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) return true; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    try { if (!string.IsNullOrEmpty(profile.ExecutablePath) && process.ProcessName.Equals(Path.GetFileNameWithoutExtension(profile.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return true; }
                    catch (InvalidOperationException) { }
                }
            }
        }
        return false;
    }
    static Manifest? ReadManifest(string root)
    {
        string path = Path.Combine(root, ManifestName); if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid installation manifest.");
        if (manifest.Version != 2 || !manifest.Directory.Equals(root, StringComparison.OrdinalIgnoreCase) || manifest.Originals.Count != 3 || manifest.Originals.Keys.Any(k => !Managed.Take(3).Contains(k))) throw new InvalidDataException("Installation manifest is unsupported or does not match this folder. Files were preserved.");
        ValidateOriginals(manifest);
        return manifest;
    }
    static void VerifyInstalled(string root, Manifest manifest, bool allowMissing)
    {
        ValidateOriginals(manifest);
        if (manifest.InstalledHashes.Count != 3 || manifest.InstalledHashes.Keys.Any(k => !Managed.Take(3).Contains(k))) throw new InvalidDataException("Installation hash manifest is invalid.");
        foreach (var (name, expected) in manifest.InstalledHashes)
        {
            string file = Path.Combine(root, name);
            if (!File.Exists(file)) { if (allowMissing && name != OriginalName) continue; throw new IOException("Managed file is missing: " + name); }
            if (Hash(File.ReadAllBytes(file)) != expected) throw new IOException("File changed outside the manager; refusing to overwrite: " + name);
        }
        if (!manifest.InstalledHashes[OriginalName].Equals(manifest.RuntimeHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Runtime backup hash does not match the trusted original. Files were preserved.");
    }
    static void ValidateOriginals(Manifest manifest)
    {
        if (manifest.OriginalHashes.Count != 3 || Managed.Take(3).Any(n => !manifest.OriginalHashes.ContainsKey(n)) || manifest.RuntimeHash.Length != 64) throw new InvalidDataException("Original snapshot integrity metadata is missing. Files were preserved.");
        foreach (string name in Managed.Take(3))
        {
            string? snapshot = manifest.Originals[name], expected = manifest.OriginalHashes[name];
            if (snapshot is null ? expected is not null : expected is null || !Hash(Decode(snapshot)!).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Original snapshot integrity check failed: " + name + ". Files were preserved.");
        }
        if (manifest.Originals[OriginalName] is not null) throw new InvalidDataException("Manifest contains an unsupported pre-existing runtime backup. Files were preserved.");
        if (manifest.OriginalHashes[ProxyName] is string proxyHash && !proxyHash.Equals(manifest.RuntimeHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Original runtime snapshot does not match the trusted backup hash. Files were preserved.");
    }
    static Dictionary<string, string?> Snapshot(string root) => Managed.ToDictionary(n => n, n => File.Exists(Path.Combine(root, n)) ? Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, n))) : null, StringComparer.OrdinalIgnoreCase);
    void Transact(string root, Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        var journal = new Journal { Directory = root, Before = before, After = after };
        ValidateJournal(root, journal); CheckUnlocked(root);
        string journalPath = Path.Combine(root, JournalName);
        if (File.Exists(journalPath)) throw new IOException("A transaction already exists.");
        // The durable journal exists before any managed file changes.
        var journalLock = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        journalLock.Write(JsonSerializer.SerializeToUtf8Bytes(journal, Json)); journalLock.Flush(true);
        try
        {
            int count = 0;
            foreach (string name in Managed)
            {
                string path = Path.Combine(root, name); SafePath.Check(path);
                if (ReadEncoded(path) != before[name]) throw new IOException("File changed during transaction: " + name);
                Put(path, after[name]); options.AfterMutation?.Invoke(++count);
            }
            journalLock.Dispose(); File.Delete(journalPath);
        }
        catch { journalLock.Dispose(); Recover(root); throw; }
        finally { journalLock.Dispose(); }
    }
    static void CheckUnlocked(string root)
    {
        foreach (string name in Managed) { string path = Path.Combine(root, name); if (File.Exists(path)) using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
    }
    static void ValidateJournal(string root, Journal journal)
    {
        if (journal.Version != 1 || !journal.Directory.Equals(root, StringComparison.OrdinalIgnoreCase) || journal.Before.Count != 4 || journal.After.Count != 4 || Managed.Any(n => !journal.Before.ContainsKey(n) || !journal.After.ContainsKey(n))) throw new InvalidDataException("Invalid transaction journal. Files were preserved.");
        foreach (string? data in journal.Before.Values.Concat(journal.After.Values)) if (data is not null) _ = Convert.FromBase64String(data);
    }
    static void Recover(string root)
    {
        string path = Path.Combine(root, JournalName); if (!File.Exists(path)) return;
        SafePath.Check(path);
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Unreadable transaction journal.");
        ValidateJournal(root, journal); CheckUnlocked(root);
        foreach (string name in Managed)
        {
            string file = Path.Combine(root, name); SafePath.Check(file); string? current = ReadEncoded(file);
            if (current != journal.Before[name] && current != journal.After[name]) throw new IOException("Interrupted transaction has an external file conflict: " + name + ". Files were preserved.");
        }
        foreach (string name in Managed) Put(Path.Combine(root, name), journal.Before[name]);
        File.Delete(path);
    }
    static void Put(string path, string? encoded) { if (encoded is null) { if (File.Exists(path)) File.Delete(path); } else AtomicFile.Write(path, Decode(encoded)!); }
    static string? ReadEncoded(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
    static byte[]? Decode(string? encoded) => encoded is null ? null : Convert.FromBase64String(encoded);
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static void VerifyX64(string path)
    {
        using var file = File.OpenRead(path); using var pe = new PEReader(file);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus || pe.PEHeaders.CorHeader is not null || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0) throw new InvalidDataException("An x64 native DLL is required: " + path);
    }
    static void VerifyBundledPayload(string path)
    {
        string checksumPath = path + ".sha256"; SafePath.Check(checksumPath);
        if (!File.Exists(checksumPath)) throw new InvalidDataException("Bundled payload checksum is missing. Reinstall the manager package.");
        string expected = File.ReadAllText(checksumPath).Trim().Split(' ', '\t')[0];
        if (!Hash(File.ReadAllBytes(path)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bundled payload checksum does not match. Reinstall the manager package.");
    }
    static void VerifyMicrosoftRuntime(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        if (!Path.GetFileName(path).Equals(ProxyName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Original runtime must be named XGameRuntime.dll.");
        var info = new WinTrustFileInfo(path); var data = new WinTrustData(info);
        try
        {
            Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, data) != 0) throw new InvalidDataException("Original runtime has no trusted Authenticode signature.");
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!cert.Subject.Split(',').Select(s => s.Trim()).Contains("O=Microsoft Corporation", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Original runtime is not signed by Microsoft Corporation.");
        }
        finally { data.Dispose(); info.Dispose(); }
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, [In, Out] WinTrustData data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] sealed class WinTrustFileInfo : IDisposable
    {
        public uint Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(); public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject;
        public WinTrustFileInfo(string path) => FilePath = Marshal.StringToCoTaskMemUni(path);
        public void Dispose() => Marshal.FreeCoTaskMem(FilePath);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] sealed class WinTrustData : IDisposable
    {
        public uint Size = (uint)Marshal.SizeOf<WinTrustData>(); public IntPtr PolicyCallbackData; public IntPtr SipClientData;
        public uint UIChoice = 2, RevocationChecks = 0, UnionChoice = 1; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags = 0x1000, UIContext;
        public WinTrustData(WinTrustFileInfo info) { FileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>()); Marshal.StructureToPtr(info, FileInfo, false); }
        public void Dispose() => Marshal.FreeHGlobal(FileInfo);
    }
}
