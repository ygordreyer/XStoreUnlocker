using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using XStoreManager.Core;

namespace XStoreManager.App;

public partial class MainWindow : Window
{
    readonly ProfileStore store;
    readonly AppState state;
    readonly ObservableCollection<GameProfile> games;
    readonly InstallerService installer = new(Path.Combine(AppContext.BaseDirectory, "engine", "XGameRuntime.dll"));
    readonly DiscoveryService discovery = new();
    readonly CatalogService catalog = new();
    readonly bool smoke;
    CancellationTokenSource? scanCancellation;
    ICollectionView? dlcView;
    bool busy;
    bool restoringSelection;
    GameProfile? Current => DetailPanel.DataContext as GameProfile;

    public MainWindow(string? dataDir = null, bool smokeTest = false)
    {
        InitializeComponent();
        smoke = smokeTest;
        store = new ProfileStore(dataDir);
        state = smoke ? new AppState() : store.Load();
        if (smoke)
        {
            Title = "XStore Manager | Sample mode";
            Subtitle.Text = "SAMPLE MODE: synthetic profiles. No games installed.";
            var sample = new GameProfile { Id = "smoke-game", Name = "Forza Horizon 5", Directory = Path.Combine(dataDir!, "SyntheticGame"), StoreId = "9NNX1VVR3KNQ", EnableLogging = true };
            System.IO.Directory.CreateDirectory(sample.Directory);
            System.IO.Directory.CreateDirectory(Path.Combine(dataDir!, "SyntheticSimulator"));
            sample.Dlcs.Add(new DlcEntry { Name = "Premium Expansion Bundle", StoreId = "9NDLCDEMO001", Selected = true, MountPath = "DLC\\Premium" });
            sample.Dlcs.Add(new DlcEntry { Name = "Car Pass", StoreId = "9NCARPASS001", Blocked = true });
            sample.Dlcs.Add(new DlcEntry { Name = "Welcome Pack", StoreId = "9NWELCOME001" });
            state.Games.Add(sample);
            state.Games.Add(new GameProfile { Id = "smoke-second", Name = "Microsoft Flight Simulator", Directory = Path.Combine(dataDir!, "SyntheticSimulator"), StoreId = "9NXN8GF8N9HT", Excluded = true });
        }
        games = new ObservableCollection<GameProfile>(state.Games);
        GameList.ItemsSource = games;
        CollectionViewSource.GetDefaultView(games).Filter = o => o is GameProfile g && Match(GameSearch.Text, g.Name, g.StoreId, g.Directory);
        foreach (var game in games) Observe(game);
        GameList.SelectedItem = games.FirstOrDefault();
        UpdateCounts();
        Closing += (_, e) =>
        {
            if (busy) { e.Cancel = true; StatusText.Text = "Finish the current operation or cancel the scan before closing."; return; }
            if (smoke) return;
            try { SaveAll(); }
            catch (Exception ex) { e.Cancel = MessageBox.Show(this, "Could not save profiles: " + ex.Message + "\n\nClose and discard these changes?", "Save failed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes; }
        };
        StatusText.Text = "Ready. Profile edits are saved when you switch games or close the manager.";
    }

    void Observe(GameProfile game)
    {
        game.PropertyChanged += (_, e) =>
        {
            if (game == Current && e.PropertyName is nameof(GameProfile.Excluded) or nameof(GameProfile.Directory)) UpdateStatus();
        };
    }

    static bool Match(string text, params string[] values) => string.IsNullOrWhiteSpace(text) || values.Any(v => (v ?? "").Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
    void UpdateCounts() { LibraryCount.Text = $"{games.Count} game{(games.Count == 1 ? "" : "s")} in your library"; RootCount.Text = $"{state.LibraryRoots.Count} custom library root{(state.LibraryRoots.Count == 1 ? "" : "s")}"; }
    void UpdateDlcCount() { if (Current is {} p) DlcCount.Text = $"{p.Dlcs.Count} entries. Check Use to select; Block takes priority."; }
    void FlushEdits()
    {
        DlcGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DlcGrid.CommitEdit(DataGridEditingUnit.Row, true);
        KeyboardFocusFlush(this);
    }
    static void KeyboardFocusFlush(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox text) text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (child is CheckBox check) check.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();
            KeyboardFocusFlush(child);
        }
    }
    void SaveAll() { FlushEdits(); state.Games = games.ToList(); store.Save(state); }
    void UpdateStatus()
    {
        if (Current is not {} p) return;
        try
        {
            var status = installer.Status(p);
            InstallStatus.Text = $"{status.State}: {status.Detail}";
            if (p.Excluded) InstallStatus.Text += status.IsInstalled ? " Excluded from future installs. The runtime is STILL INSTALLED; use Uninstall / Restore to remove it." : " Excluded from future installs.";
            else if (status.IsInstalled) InstallStatus.Text += " Profile edits remain pending until Install / Apply. Clear Enable runtime unlocker and Apply to disable it.";
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "engine", "XGameRuntime.dll"))) InstallStatus.Text += " Bundled engine is missing. Place the supplied XGameRuntime.dll in the manager's engine folder before installing.";
            var managed = HasManagedFiles(p);
            DirectoryField.IsReadOnly = managed;
            GameFolderButton.IsEnabled = !managed;
            DirectoryField.ToolTip = managed ? "Uninstall / Restore before changing the runtime folder. This protects the current installation from being orphaned." : "Folder containing the game's XGameRuntime.dll";
            if (managed) InstallStatus.Text += " Uninstall / Restore before changing the runtime folder.";
        }
        catch (Exception ex) { InstallStatus.Text = "Status unavailable: " + ex.Message; }
        InstallButton.IsEnabled = RepairButton.IsEnabled = !busy && !p.Excluded;
    }
    static bool HasManagedFiles(GameProfile p) => File.Exists(Path.Combine(p.Directory, InstallerService.ManifestName)) || File.Exists(Path.Combine(p.Directory, InstallerService.JournalName));
    void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (busy || restoringSelection) return;
        try
        {
            if (DetailPanel.DataContext != null && !smoke) SaveAll();
            DetailPanel.DataContext = GameList.SelectedItem;
            DetailPanel.Visibility = Current != null ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = Current == null ? Visibility.Visible : Visibility.Collapsed;
            if (Current is {} p)
            {
                dlcView = CollectionViewSource.GetDefaultView(p.Dlcs);
                dlcView.Filter = o => o is DlcEntry d && Match(DlcSearch.Text, d.Name, d.StoreId, d.MountPath);
                DlcGrid.ItemsSource = dlcView;
                AdoptIniToggle.IsChecked = false;
                UpdateDlcCount(); UpdateStatus();
            }
        }
        catch (Exception ex) { restoringSelection = true; GameList.SelectedItem = Current; restoringSelection = false; Report(ex); }
    }
    void GameSearch_Changed(object sender, TextChangedEventArgs e) { if (GameList?.ItemsSource != null) CollectionViewSource.GetDefaultView(GameList.ItemsSource).Refresh(); }
    void DlcSearch_Changed(object sender, TextChangedEventArgs e) => dlcView?.Refresh();
    void SetBusy(bool value, string message)
    {
        busy = value;
        StatusText.Text = message;
        BusyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.IsEnabled = !value;
        GameList.IsEnabled = GameSearch.IsEnabled = AddGameButton.IsEnabled = AddRootButton.IsEnabled = ScanButton.IsEnabled = !value;
        if (!value) UpdateStatus();
    }
    async Task Run(Func<Task> action, string message)
    {
        if (busy) return;
        try { SetBusy(true, message); await action(); }
        catch (OperationCanceledException) { StatusText.Text = "Scan cancelled. Existing profiles were kept."; }
        catch (Exception ex) { Report(ex); }
        finally { var messageAfter = StatusText.Text; SetBusy(false, messageAfter); }
    }
    void Report(Exception ex) { StatusText.Text = ex.Message; if (!smoke) MessageBox.Show(this, ex.Message, "XStore Manager", MessageBoxButton.OK, MessageBoxImage.Error); }
    string? PickFolder(string title, string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
    void AddOrMerge(GameProfile found)
    {
        if (games.Any(g => string.Equals(Path.GetFullPath(g.Directory).TrimEnd('\\'), Path.GetFullPath(found.Directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        games.Add(found); Observe(found);
    }
    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await Run(async () =>
        {
            SaveAll(); scanCancellation = new CancellationTokenSource(); CancelButton.Visibility = Visibility.Visible;
            try
            {
                var found = await discovery.ScanAsync(state.LibraryRoots, scanCancellation.Token);
                foreach (var game in found) AddOrMerge(game);
                SaveAll(); UpdateCounts();
                if (GameList.SelectedItem == null) GameList.SelectedItem = games.FirstOrDefault();
                StatusText.Text = $"Scan complete. Found {found.Count} games; {games.Count} saved profiles. No game files changed.";
            }
            finally { scanCancellation.Dispose(); scanCancellation = null; CancelButton.Visibility = Visibility.Collapsed; }
        }, "Scanning libraries. This may take a moment...");
        if (Current == null && GameList.SelectedItem != null) GameList_SelectionChanged(GameList, new SelectionChangedEventArgs(SelectorSelectionEvent(), Array.Empty<object>(), new[] { GameList.SelectedItem }));
    }
    static RoutedEvent SelectorSelectionEvent() => System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
    void Cancel_Click(object sender, RoutedEventArgs e) { scanCancellation?.Cancel(); StatusText.Text = "Cancelling scan..."; }
    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        try { if (PickFolder("Choose the game folder containing XGameRuntime.dll") is {} path) { SaveAll(); var found = discovery.InspectFolder(path); AddOrMerge(found); SaveAll(); UpdateCounts(); GameList.SelectedItem = games.First(g => string.Equals(g.Directory, found.Directory, StringComparison.OrdinalIgnoreCase)); StatusText.Text = "Game profile added. No game files changed."; } }
        catch (Exception ex) { Report(ex); }
    }
    void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        try { if (PickFolder("Add a folder to search for games") is {} path) { if (!state.LibraryRoots.Contains(path, StringComparer.OrdinalIgnoreCase)) state.LibraryRoots.Add(path); SaveAll(); UpdateCounts(); StatusText.Text = "Library root saved. Click Scan libraries to discover games."; } }
        catch (Exception ex) { Report(ex); }
    }
    void GameFolder_Click(object sender, RoutedEventArgs e) { try { if (Current is not {} p) return; if (HasManagedFiles(p)) throw new InvalidOperationException("Uninstall / Restore before changing the runtime folder."); if (PickFolder("Choose game / runtime folder", p.Directory) is {} path) { p.Directory = path; UpdateStatus(); } } catch (Exception ex) { Report(ex); } }
    void DlcFolder_Click(object sender, RoutedEventArgs e) { try { if (DlcGrid.SelectedItem is not DlcEntry d) { StatusText.Text = "Select a DLC row first."; return; } if (PickFolder("Choose this DLC's mount folder", d.MountPath) is {} path) d.MountPath = path; } catch (Exception ex) { Report(ex); } }
    void AddDlc_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not {} p) return;
        var dialog = new DlcDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            if (p.Dlcs.Any(d => string.Equals(d.StoreId, dialog.StoreId, StringComparison.OrdinalIgnoreCase))) { StatusText.Text = "That store ID already exists in this profile."; return; }
            var entry = new DlcEntry { StoreId = dialog.StoreId, Name = dialog.DlcName, Selected = true };
            p.Dlcs.Add(entry); DlcSearch.Clear(); DlcGrid.SelectedItem = entry; UpdateDlcCount();
        }
    }
    void RemoveDlc_Click(object sender, RoutedEventArgs e) { if (Current is {} p && DlcGrid.SelectedItem is DlcEntry d && MessageBox.Show(this, $"Remove {d.Name} from this profile?", "Remove DLC", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { p.Dlcs.Remove(d); UpdateDlcCount(); } }
    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not {} p) return;
        await Run(async () =>
        {
            FlushEdits();
            var detached = JsonSerializer.Deserialize<GameProfile>(JsonSerializer.Serialize(p))!;
            var result = await catalog.RefreshAsync(detached);
            foreach (var entry in detached.Dlcs)
            {
                var existing = p.Dlcs.FirstOrDefault(d => string.Equals(d.StoreId, entry.StoreId, StringComparison.OrdinalIgnoreCase));
                if (existing == null) p.Dlcs.Add(entry); else existing.Name = entry.Name;
            }
            dlcView?.Refresh(); UpdateDlcCount(); SaveAll(); StatusText.Text = result.Detail;
        }, "Refreshing the Microsoft Store catalogue...");
    }
    void Import_Click(object sender, RoutedEventArgs e)
    {
        try { if (Current is not {} p) return; var dialog = new OpenFileDialog { Filter = "INI files (*.ini)|*.ini|All files (*.*)|*.*" }; if (dialog.ShowDialog(this) != true) return; FlushEdits(); IniService.Import(File.ReadAllText(dialog.FileName), p); dlcView?.Refresh(); UpdateDlcCount(); UpdateStatus(); StatusText.Text = "INI imported into this profile. Save or Apply when ready."; }
        catch (Exception ex) { Report(ex); }
    }
    void ExportIni_Click(object sender, RoutedEventArgs e) => Export(false);
    void ExportProfile_Click(object sender, RoutedEventArgs e) => Export(true);
    void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Current is not {} p) return;
            var dialog = new OpenFileDialog { Filter = "Game profile (*.json)|*.json", Title = "Import settings into the selected game profile" };
            if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > 8 * 1024 * 1024) throw new InvalidDataException("Profile is too large (maximum 8 MB).");
            FlushEdits(); ImportProfileText(File.ReadAllText(dialog.FileName), p);
            dlcView?.Refresh(); UpdateDlcCount(); UpdateStatus();
            StatusText.Text = "Profile settings imported. Current game folder, executable and profile identity were kept. Save or Apply when ready.";
        }
        catch (Exception ex) { Report(ex); }
    }
    static void ImportProfileText(string text, GameProfile target)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Name", out _) || !root.TryGetProperty("Directory", out _) || !root.TryGetProperty("Dlcs", out _)) throw new InvalidDataException("Expected an exported game profile with Name, Directory and Dlcs fields.");
        var imported = JsonSerializer.Deserialize<GameProfile>(text) ?? throw new InvalidDataException("Profile is empty.");
        if (string.IsNullOrWhiteSpace(imported.Name) || imported.Name.Length > 512 || imported.Name.Any(char.IsControl)) throw new InvalidDataException("Profile name is missing or invalid.");
        if (string.IsNullOrWhiteSpace(imported.Directory) || !Path.IsPathFullyQualified(imported.Directory) || !System.IO.Directory.Exists(imported.Directory)) throw new InvalidDataException("Imported game folder must be an existing absolute path.");
        var importedRoot = Path.GetFullPath(imported.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.IsNullOrEmpty(imported.ExecutablePath) && (!Path.IsPathFullyQualified(imported.ExecutablePath) || !Path.GetFullPath(imported.ExecutablePath).StartsWith(importedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(imported.ExecutablePath))) throw new InvalidDataException("Imported executable must exist inside its game folder.");
        if (imported.StoreId is null || (imported.StoreId.Length > 0 && !IniService.IsStoreId(imported.StoreId))) throw new InvalidDataException("Game Store ID must be empty or a valid 12-character ID.");
        if (imported.Dlcs is null || imported.Dlcs.Count > 10000) throw new InvalidDataException("Profile DLC list is missing or too large.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in imported.Dlcs)
        {
            if (entry is null || entry.StoreId is null || !IniService.IsStoreId(entry.StoreId) || !ids.Add(entry.StoreId)) throw new InvalidDataException("Every DLC must have a unique, valid 12-character Store ID.");
            if (entry.Name is null || entry.Name.Length > 512 || entry.Name.Any(char.IsControl) || entry.MountPath is null || entry.MountPath.Any(char.IsControl)) throw new InvalidDataException("DLC names and mount paths cannot contain control characters.");
            if (entry.MountPath.Length > 0) _ = Path.GetFullPath(entry.MountPath, importedRoot);
        }
        _ = IniService.Serialize(imported);
        target.Name = imported.Name; target.StoreId = imported.StoreId;
        target.Enabled = imported.Enabled; target.UnlockAll = imported.UnlockAll; target.Excluded = imported.Excluded; target.EnableLogging = imported.EnableLogging;
        target.Dlcs.Clear(); foreach (var entry in imported.Dlcs) target.Dlcs.Add(entry);
    }
    void SelectVisible_Click(object sender, RoutedEventArgs e) => SetVisibleSelection(true);
    void ClearVisible_Click(object sender, RoutedEventArgs e) => SetVisibleSelection(false);
    void SetVisibleSelection(bool selected)
    {
        try
        {
            FlushEdits();
            var entries = dlcView?.Cast<DlcEntry>().Where(d => !selected || !d.Blocked).ToArray() ?? Array.Empty<DlcEntry>();
            foreach (var entry in entries) entry.Selected = selected;
            StatusText.Text = $"{(selected ? "Selected" : "Cleared")} {entries.Length} visible DLC entries in this profile. Apply to update game files.";
        }
        catch (Exception ex) { Report(ex); }
    }
    void Export(bool json)
    {
        try { if (Current is not {} p) return; FlushEdits(); var dialog = new SaveFileDialog { Filter = json ? "Profile (*.json)|*.json" : "INI (*.ini)|*.ini", FileName = json ? "game-profile.json" : "XStoreUnlocker.ini" }; if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, json ? JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }) : IniService.Serialize(p)); StatusText.Text = "Exported " + dialog.FileName; } }
        catch (Exception ex) { Report(ex); }
    }
    void Save_Click(object sender, RoutedEventArgs e) { try { SaveAll(); UpdateStatus(); StatusText.Text = "Profiles saved. Game files were not changed."; } catch (Exception ex) { Report(ex); } }
    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not {} p || p.Excluded) return;
        var adoptIni = AdoptIniToggle.IsChecked == true;
        if (adoptIni && MessageBox.Show(this, "Apply this profile over the existing INI? The original INI will be backed up and restored on uninstall.", "Adopt existing INI", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async () => { SaveAll(); var activeInstaller = adoptIni ? new InstallerService(Path.Combine(AppContext.BaseDirectory, "engine", "XGameRuntime.dll"), new InstallerOptions { AdoptExistingConfiguration = true }) : installer; await Task.Run(() => activeInstaller.Install(p)); AdoptIniToggle.IsChecked = false; StatusText.Text = p.Enabled ? "Installation applied. Selected DLC settings are active." : "Installation applied with runtime unlocking disabled. Enable it in the profile and apply to activate."; }, "Applying runtime and configuration...");
    }
    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not {} p) return;
        if (MessageBox.Show(this, "Restore this game's original runtime and remove manager-installed files?", "Uninstall / Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async () => { SaveAll(); await Task.Run(() => installer.Uninstall(p)); StatusText.Text = "Original runtime restored. Your saved profile is kept."; }, "Restoring original game files...");
    }
    void OpenFolder_Click(object sender, RoutedEventArgs e) { try { if (Current is {} p) { if (!System.IO.Directory.Exists(p.Directory)) throw new DirectoryNotFoundException("The game folder does not exist: " + p.Directory); Process.Start(new ProcessStartInfo(p.Directory) { UseShellExecute = true }); } } catch (Exception ex) { Report(ex); } }
    void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        try { if (Current is not {} p) return; var file = Path.Combine(p.Directory, "xstore_unlocker.log"); if (!File.Exists(file)) { MessageBox.Show(this, "No runtime log exists yet. Enable logging, apply the profile, then launch the game to create xstore_unlocker.log.", "Runtime log", MessageBoxButton.OK, MessageBoxImage.Information); return; } Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { Report(ex); }
    }

    public void Capture(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path); encoder.Save(stream);
    }
    public void VerifySmoke(string dataDir)
    {
        Width = MinWidth; Height = MinHeight; UpdateLayout();
        Capture(Path.Combine(dataDir, "minimum-window.png"));
        var checks = new Dictionary<string, bool>
        {
            ["window_loaded"] = IsLoaded && ActualWidth >= 1050 && ActualHeight >= 800,
            ["library_has_two_profiles"] = GameList.Items.Count == 2,
            ["profile_binding"] = NameField.Text == "Forza Horizon 5" && DirectoryField.Text.Contains("SyntheticGame"),
            ["dlc_binding"] = DlcGrid.Items.Count == 3,
            ["safe_defaults"] = Current is { Enabled: false, UnlockAll: false },
            ["actions_visible"] = ScanButton.IsVisible && AddGameButton.IsVisible && SaveButton.IsVisible && InstallButton.IsVisible && UninstallButton.IsVisible && AddDlcButton.IsVisible
        };
        DlcSearch.Text = "Welcome"; checks["dlc_search"] = DlcGrid.Items.Count == 1; DlcSearch.Clear();
        SetVisibleSelection(false); DlcSearch.Text = "Welcome"; SetVisibleSelection(true);
        checks["bulk_selection_respects_search"] = Current!.Dlcs.Single(d => d.Name == "Welcome Pack").Selected && !Current.Dlcs.Single(d => d.Name == "Premium Expansion Bundle").Selected;
        DlcSearch.Clear(); SetVisibleSelection(true);
        checks["bulk_selection_skips_blocked"] = !Current.Dlcs.Single(d => d.Blocked).Selected;
        var originalId = Current.Id; var originalFolder = Current.Directory; var json = JsonSerializer.Serialize(Current);
        var detached = new GameProfile { Id = "retained-profile-id", Directory = Current.Directory, ExecutablePath = Current.ExecutablePath };
        ImportProfileText(json, detached);
        checks["profile_round_trip"] = detached.Id == "retained-profile-id" && detached.Directory == originalFolder && detached.Dlcs.Count == 3 && detached.Dlcs.Single(d => d.Blocked).StoreId == "9NCARPASS001";
        try { ImportProfileText(json.Replace("9NCARPASS001", "invalid"), detached); checks["invalid_profile_rejected"] = false; } catch (InvalidDataException) { checks["invalid_profile_rejected"] = detached.Dlcs.Count == 3; }
        var syntheticMarker = Path.Combine(Current.Directory, InstallerService.ManifestName);
        File.WriteAllText(syntheticMarker, "{}"); UpdateStatus();
        checks["managed_folder_edit_guard"] = DirectoryField.IsReadOnly && !GameFolderButton.IsEnabled;
        File.Delete(syntheticMarker); UpdateStatus();
        checks["sample_mode_clear"] = Subtitle.Text.Contains("SAMPLE MODE") && System.IO.Directory.Exists(Current.Directory);
        GameSearch.Text = "Flight"; checks["game_search"] = GameList.Items.Count == 1; GameSearch.Clear();
        GameList.SelectedItem = games[0];
        NameField.Text = "Edited smoke profile"; FlushEdits(); checks["editable_profile_binding"] = Current?.Name == "Edited smoke profile";
        SaveAll();
        var saved = store.Load(); checks["profile_saved"] = saved.Games.Count == 2 && saved.Games[0].Name == "Edited smoke profile";
        GameList.SelectedItem = games[1]; checks["excluded_install_disabled"] = !InstallButton.IsEnabled && !RepairButton.IsEnabled;
        GameList.SelectedItem = games[0];
        var receipt = new { passed = checks.Values.All(v => v), checks, installationInvoked = false, timestampUtc = DateTimeOffset.UtcNow };
        System.IO.Directory.CreateDirectory(dataDir); File.WriteAllText(Path.Combine(dataDir, "smoke-result.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        if (!receipt.passed) throw new InvalidOperationException("WPF smoke checks failed. See smoke-result.json.");
    }
}
