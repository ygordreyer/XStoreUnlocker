using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XStoreManager.Core;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
}
public sealed class DlcEntry : ObservableModel
{
    string storeId = "", name = "", mountPath = ""; bool selected, blocked;
    public string StoreId { get => storeId; set => Set(ref storeId, value); }
    public string Name { get => name; set => Set(ref name, value); }
    public string MountPath { get => mountPath; set => Set(ref mountPath, value); }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public bool Blocked { get => blocked; set => Set(ref blocked, value); }
}
public sealed class GameProfile : ObservableModel
{
    string id = Guid.NewGuid().ToString("N"), name = "", directory = "", executablePath = "", storeId = "";
    bool excluded, unlockAll, enableLogging = true, enabled;
    public string Id { get => id; set => Set(ref id, value); }
    public string Name { get => name; set => Set(ref name, value); }
    public string Directory { get => directory; set => Set(ref directory, value); }
    public string ExecutablePath { get => executablePath; set => Set(ref executablePath, value); }
    public string StoreId { get => storeId; set => Set(ref storeId, value); }
    public bool Excluded { get => excluded; set => Set(ref excluded, value); }
    public bool UnlockAll { get => unlockAll; set => Set(ref unlockAll, value); }
    public bool EnableLogging { get => enableLogging; set => Set(ref enableLogging, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public ObservableCollection<DlcEntry> Dlcs { get; set; } = new();
}
public sealed class AppState
{
    public List<GameProfile> Games { get; set; } = new();
    public List<string> LibraryRoots { get; set; } = new();
}
public sealed record InstallationStatus(string State, string Detail, bool IsInstalled);
public sealed record CatalogResult(bool Available, string Detail);
