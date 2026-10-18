using System.Net.Http;
using System.Text.Json;

namespace XStoreManager.Core;

public sealed class CatalogService
{
    readonly HttpClient client;
    public CatalogService(HttpClient? client = null) => this.client = client ?? SharedClient;
    static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    public async Task<CatalogResult> RefreshAsync(GameProfile profile, CancellationToken cancellationToken = default)
    {
        if (!IniService.IsStoreId(profile.StoreId)) return new(false, "Set a verified 12-character game Store ID first. Import an INI or add DLC IDs manually.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            string id = Uri.EscapeDataString(profile.StoreId);
            using var response = await client.GetAsync($"https://storeedgefd.dsx.mp.microsoft.com/v9.0/recommendations/addonswithdetails/products/{id}?itemTypes=Durable&market=US&locale=en-US&deviceFamily=Windows.Desktop&itemType=apps&pageSize=200&cardsEnabled=true", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = json.RootElement;
            if (root.TryGetProperty("Payload", out var payload)) root = payload;
            if (!root.TryGetProperty("Cards", out var cards) || cards.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Catalogue response did not contain an add-on list.");
            var verified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards.EnumerateArray())
            {
                string? dlcId = Get(card, "ProductId"), parentId = Get(card, "ParentId"), title = Get(card, "Title");
                if (dlcId is not null && IniService.IsStoreId(dlcId) && parentId?.Equals(profile.StoreId, StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(title)) verified[dlcId] = title;
            }
            foreach (var (dlcId, title) in verified)
            {
                var entry = profile.Dlcs.FirstOrDefault(d => d.StoreId.Equals(dlcId, StringComparison.OrdinalIgnoreCase));
                if (entry is null) profile.Dlcs.Add(new() { StoreId = dlcId, Name = title }); else entry.Name = title;
            }
            int total = root.TryGetProperty("TotalItems", out var totalValue) && totalValue.TryGetInt32(out int count) ? count : verified.Count;
            string detail = $"Loaded {verified.Count} verified durable add-ons from Microsoft Store. New entries are unselected.";
            if (total > cards.GetArrayLength()) detail += $" Store reports {total} items but returned {cards.GetArrayLength()}; unavailable or omitted items may require manual import.";
            return new(true, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "Microsoft Store catalogue timed out. Existing entries were preserved; import an INI or add verified IDs manually."); }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException) { return new(false, "Microsoft Store catalogue unavailable: " + e.Message + " Existing entries were preserved; manual INI import remains available."); }
    }
    static string? Get(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
