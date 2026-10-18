#include "store_hooks.h"
#include "policy.h"
#include "logger.h"
#include <array>
#include <atomic>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace {
UnlockerConfig config;
std::atomic<bool> stopping{false};
std::mutex stateMutex, installMutex;
std::unordered_set<std::string> knownDlc;
std::unordered_map<std::string, std::string> packageToStore;
std::unordered_map<void*, std::string> licenseAsync, canAcquireAsync, mountAsync, queryPackageAsync, queryPackages;
std::unordered_map<void*, std::string> fakeLicenses, realLicenses, fakeMounts;
std::atomic<uint64_t> nextToken{1};
VtableEntry_t storeOriginal[STORE_VTABLE_SIZE]{};
VtableEntry_t packageOriginal[PACKAGE_VTABLE_SIZE]{};
VtableEntry_t* storeTable = nullptr;
VtableEntry_t* packageTable = nullptr;

bool Success(int64_t result) { return static_cast<int32_t>(result) >= 0; }
bool Readable(const void* address, size_t length) {
    if (!address || !length) return false;
    auto begin = reinterpret_cast<uintptr_t>(address);
    if (begin + length < begin) return false;
    const auto end = begin + length;
    while (begin < end) {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(reinterpret_cast<void*>(begin), &info, sizeof(info)) || info.State != MEM_COMMIT ||
            (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) || !(info.Protect &
            (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
        const auto next = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
        if (next <= begin) return false;
        begin = next;
    }
    return true;
}
std::string SafeString(const char* value, size_t limit = 4096) {
    if (!value) return {};
    std::string result;
    for (size_t i = 0; i < limit; ++i) {
        if (!Readable(value + i, 1)) return {};
        if (!value[i]) return result;
        result.push_back(value[i]);
    }
    return {};
}
void Discover(const std::string& id, const std::string& package = {}) {
    const auto normalized = Policy::Id(id);
    if (normalized.empty()) return;
    std::lock_guard lock(stateMutex);
    knownDlc.insert(normalized);
    if (!package.empty()) packageToStore[Policy::Id(package)] = normalized;
}
std::string ResolvePackage(const char* package) {
    const auto value = SafeString(package);
    const auto synthetic = Policy::SyntheticStoreId(value);
    if (!synthetic.empty()) return synthetic;
    std::lock_guard lock(stateMutex);
    const auto entry = packageToStore.find(Policy::Id(value));
    return entry == packageToStore.end() ? std::string{} : entry->second;
}
bool Selected(const std::string& id) {
    std::lock_guard lock(stateMutex);
    return !stopping && Policy::Selected(config, id, knownDlc.count(Policy::Id(id)) != 0);
}
void Remember(std::unordered_map<void*, std::string>& identities, void* key, const std::string& id) {
    if (!key) return;
    std::lock_guard lock(stateMutex);
    identities.erase(key);
    if (!id.empty()) identities[key] = id;
}
std::string Take(std::unordered_map<void*, std::string>& identities, void* key) {
    std::lock_guard lock(stateMutex);
    const auto entry = identities.find(key);
    if (entry == identities.end()) return {};
    auto id = entry->second;
    identities.erase(entry);
    return id;
}
std::string Lookup(const std::unordered_map<void*, std::string>& identities, void* key) {
    std::lock_guard lock(stateMutex);
    const auto entry = identities.find(key);
    return entry == identities.end() ? std::string{} : entry->second;
}

bool ExecutableTable(VtableEntry_t* candidate, size_t count) {
    if (!Readable(candidate, count * sizeof(VtableEntry_t))) return false;
    void* image = nullptr;
    for (size_t index = 0; index < count; ++index) {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(reinterpret_cast<void*>(candidate[index]), &info, sizeof(info)) || info.State != MEM_COMMIT ||
            info.Type != MEM_IMAGE || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
            !(info.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
        if (!image) image = info.AllocationBase;
        if (image != info.AllocationBase) return false;
    }
    return true;
}
template<size_t N> bool ValidateInterface(void** output, const GUID* requestedIid, const GUID& contractIid, VtableEntry_t* (&table)) {
    if (!requestedIid || !Readable(requestedIid, sizeof(GUID)) || !Readable(output, sizeof(void*)) || !Readable(*output, sizeof(void*))) return false;
    auto* candidate = *reinterpret_cast<VtableEntry_t**>(*output);
    if (!ExecutableTable(candidate, 3)) return false;
    // Refuse a second layout: existing handles must continue using their original implementation.
    if (table) return table == candidate;
    using Query = HRESULT(__fastcall*)(void*, const GUID*, void**);
    using Release = ULONG(__fastcall*)(void*);
    void* checked = nullptr;
    const auto result = reinterpret_cast<Query>(candidate[0])(*output, &contractIid, &checked);
    if (FAILED(result) || !checked) return false;
    const bool same = Readable(checked, sizeof(void*)) && *reinterpret_cast<VtableEntry_t**>(checked) == candidate;
    bool valid = false;
    if (Readable(checked, sizeof(void*))) {
        auto* checkedTable = *reinterpret_cast<VtableEntry_t**>(checked);
        if (ExecutableTable(checkedTable, 3)) {
            valid = same && ExecutableTable(candidate, N);
            reinterpret_cast<Release>(checkedTable[2])(checked);
        }
    }
    return valid;
}

template<size_t N> bool BeginPatch(VtableEntry_t* table, VtableEntry_t (&original)[N], DWORD& previous) {
    // Patched callbacks and synthetic handles remain reachable for the process lifetime.
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&stopping), &module)) return false;
    if (!VirtualProtect(table, sizeof(original), PAGE_READWRITE, &previous)) return false;
    std::memcpy(original, table, sizeof(original));
    return true;
}
template<size_t N> void Restore(VtableEntry_t*& table, VtableEntry_t (&original)[N]) {
    if (!table || !Readable(table, sizeof(original))) return;
    DWORD previous{};
    if (VirtualProtect(table, sizeof(original), PAGE_READWRITE, &previous)) {
        std::memcpy(table, original, sizeof(original));
        VirtualProtect(table, sizeof(original), previous, &previous);
        table = nullptr;
    }
}
using Call2 = int64_t(__fastcall*)(void*, void*);
using Call3 = int64_t(__fastcall*)(void*, void*, void*);
using Call4 = int64_t(__fastcall*)(void*, void*, void*, void*);
using Result = int64_t(__fastcall*)(void*, void*, void**);

int64_t StartLicense(int index, void* self, void* context, void* input, void* async, bool package) {
    Remember(licenseAsync, async, package ? ResolvePackage(static_cast<char*>(input)) : Policy::Id(SafeString(static_cast<char*>(input))));
    const auto hr = reinterpret_cast<Call4>(storeOriginal[index])(self, context, input, async);
    if (!Success(hr)) Take(licenseAsync, async);
    return hr;
}
int64_t FinishLicense(int index, void* self, void* async, void** output) {
    const auto hr = reinterpret_cast<Result>(storeOriginal[index])(self, async, output);
    if (static_cast<uint32_t>(hr) == static_cast<uint32_t>(E_PENDING)) return hr;
    const auto id = Take(licenseAsync, async);
    if (Success(hr) && output && *output) Remember(realLicenses, *output, id);
    if (!Selected(id) || !output || !Policy::LicenseDenied(hr)) return hr;
    auto* handle = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0x30);
    if (!handle) return E_OUTOFMEMORY;
    Remember(fakeLicenses, handle, id);
    *output = handle;
    return S_OK;
}
int64_t __fastcall PackageLicenseAsync(void* self, void* context, void* package, void* async) {
    return StartLicense(StoreVtable::AcquireLicenseForPackageAsync, self, context, package, async, true);
}
int64_t __fastcall DurableLicenseAsync(void* self, void* context, void* id, void* async) {
    return StartLicense(StoreVtable::AcquireLicenseForDurablesAsync, self, context, id, async, false);
}
int64_t __fastcall PackageLicenseResult(void* self, void* async, void** output) {
    return FinishLicense(StoreVtable::AcquireLicenseForPackageResult, self, async, output);
}
int64_t __fastcall DurableLicenseResult(void* self, void* async, void** output) {
    return FinishLicense(StoreVtable::AcquireLicenseForDurablesResult, self, async, output);
}
int64_t __fastcall LicenseValid(void* self, void* handle) {
    const auto id = Lookup(fakeLicenses, handle);
    if (!id.empty()) return Selected(id) ? 1 : 0;
    if (Selected(Lookup(realLicenses, handle))) return 1;
    return reinterpret_cast<Call2>(storeOriginal[StoreVtable::LicenseIsValid])(self, handle);
}
int64_t __fastcall CloseLicense(void* self, void* handle) {
    if (!Take(fakeLicenses, handle).empty()) { HeapFree(GetProcessHeap(), 0, handle); return S_OK; }
    Take(realLicenses, handle);
    return reinterpret_cast<Call2>(storeOriginal[StoreVtable::CloseLicenseHandle])(self, handle);
}
int64_t __fastcall RegisterLicenseLost(void* self, void* license, void* queue, void* context, void* callback, void* output) {
    using Fn = int64_t(__fastcall*)(void*, void*, void*, void*, void*, void*);
    if (Lookup(fakeLicenses, license).empty()) return reinterpret_cast<Fn>(storeOriginal[StoreVtable::RegisterPackageLicenseLost])(self, license, queue, context, callback, output);
    if (!output) return E_INVALIDARG;
    *static_cast<uint64_t*>(output) = nextToken.fetch_add(1);
    return S_OK;
}
int64_t __fastcall UnregisterLicenseLost(void* self, void* license, void* token, void* wait) {
    if (!Lookup(fakeLicenses, license).empty()) return S_OK;
    return reinterpret_cast<Call4>(storeOriginal[StoreVtable::UnregisterPackageLicenseLost])(self, license, token, wait);
}
int64_t StartCanAcquire(int index, void* self, void* context, void* input, void* async, bool package) {
    Remember(canAcquireAsync, async, package ? ResolvePackage(static_cast<char*>(input)) : Policy::Id(SafeString(static_cast<char*>(input))));
    const auto hr = reinterpret_cast<Call4>(storeOriginal[index])(self, context, input, async);
    if (!Success(hr)) Take(canAcquireAsync, async);
    return hr;
}
int64_t FinishCanAcquire(int index, void* self, void* async, void* output) {
    const auto hr = reinterpret_cast<Call3>(storeOriginal[index])(self, async, output);
    if (static_cast<uint32_t>(hr) == static_cast<uint32_t>(E_PENDING)) return hr;
    const auto id = Take(canAcquireAsync, async);
    if (Success(hr) && Selected(id) && output) static_cast<XStoreCanAcquireLicenseResult*>(output)->status = 0;
    return hr;
}
int64_t __fastcall CanAcquireStoreAsync(void* self, void* context, void* id, void* async) {
    return StartCanAcquire(StoreVtable::CanAcquireLicenseForStoreIdAsync, self, context, id, async, false);
}
int64_t __fastcall CanAcquirePackageAsync(void* self, void* context, void* id, void* async) {
    return StartCanAcquire(StoreVtable::CanAcquireLicenseForPackageAsync, self, context, id, async, true);
}
int64_t __fastcall CanAcquireStoreResult(void* self, void* async, void* output) {
    return FinishCanAcquire(StoreVtable::CanAcquireLicenseForStoreIdResult, self, async, output);
}
int64_t __fastcall CanAcquirePackageResult(void* self, void* async, void* output) {
    return FinishCanAcquire(StoreVtable::CanAcquireLicenseForPackageResult, self, async, output);
}
int64_t __fastcall AddonResult(void* self, void* async, uint32_t count, void* output) {
    using Fn = int64_t(__fastcall*)(void*, void*, uint32_t, void*);
    const auto hr = reinterpret_cast<Fn>(storeOriginal[StoreVtable::QueryAddOnLicensesResult])(self, async, count, output);
    if (Success(hr) && output) {
        auto* records = static_cast<XStoreAddonLicense*>(output);
        for (uint32_t i = 0; i < count; ++i) {
            const auto id = Policy::Id(std::string(records[i].skuStoreId, strnlen_s(records[i].skuStoreId, STORE_SKU_ID_SIZE)));
            Discover(id);
            if (Selected(id)) { records[i].isActive = true; records[i].expirationDate = INT64_MAX - 1; }
        }
    }
    return hr;
}

constexpr size_t ProductSize = 208, SkuSize = 272;
struct FakeProduct {
    std::string id;
    std::array<uint8_t, ProductSize> product{};
    std::array<uint8_t, SkuSize> sku{};
    explicit FakeProduct(std::string value) : id(std::move(value)) {
        for (const auto offset : {0, 8}) *reinterpret_cast<const char**>(product.data() + offset) = id.c_str();
        for (const auto offset : {16, 24, 32, 40, 72}) *reinterpret_cast<const char**>(product.data() + offset) = "";
        *reinterpret_cast<uint32_t*>(product.data() + 48) = PRODUCT_KIND_DURABLE;
        product[145] = 1;
        *reinterpret_cast<uint32_t*>(product.data() + 160) = 1;
        *reinterpret_cast<uint8_t**>(product.data() + 168) = sku.data();
        *reinterpret_cast<const char**>(sku.data()) = id.c_str();
        *reinterpret_cast<const char**>(sku.data() + 8) = id.c_str();
        sku[121] = 1;
        *reinterpret_cast<int64_t*>(sku.data() + 144) = INT64_MAX - 1;
        *reinterpret_cast<uint32_t*>(sku.data() + 160) = 1;
    }
};
std::vector<std::unique_ptr<FakeProduct>> fakeProducts;
using ProductCallback = uint8_t(__fastcall*)(void*, void*);
struct ProductContext {
    ProductCallback callback;
    void* context;
    std::unordered_set<std::string> seen;
    std::string package;
    bool continued = true;
};
uint8_t __fastcall ProductVisitor(void* product, void* context) {
    auto& state = *static_cast<ProductContext*>(context);
    auto* data = static_cast<uint8_t*>(product);
    if (!Readable(data, ProductSize)) return 0;
    const auto id = Policy::Id(SafeString(*reinterpret_cast<const char**>(data)));
    state.seen.insert(id);
    const auto kind = *reinterpret_cast<uint32_t*>(data + 48);
    // Game products are never changed, including when unlock_all is enabled.
    if (kind & PRODUCT_KIND_DURABLE) {
        Discover(id, state.package);
        if (Selected(id)) {
            data[145] = 1;
            const auto count = *reinterpret_cast<uint32_t*>(data + 160);
            auto* skus = *reinterpret_cast<uint8_t**>(data + 168);
            if (count <= 64 && Readable(skus, count * SkuSize)) {
                for (uint32_t i = 0; i < count; ++i) {
                    auto* sku = skus + i * SkuSize;
                    sku[120] = 0; sku[121] = 1; sku[152] = 0;
                    *reinterpret_cast<int64_t*>(sku + 144) = INT64_MAX - 1;
                    *reinterpret_cast<uint32_t*>(sku + 156) = 0;
                    *reinterpret_cast<uint32_t*>(sku + 160) = 1;
                }
            }
        }
    }
    state.continued = !state.callback || state.callback(product, state.context);
    return state.continued ? 1 : 0;
}
int64_t __fastcall GetProducts(void* self, void* query, void* context, void* callback) {
    if (!callback || stopping) return reinterpret_cast<Call4>(storeOriginal[StoreVtable::ProductsQueryGetProducts])(self, query, context, callback);
    ProductContext state{reinterpret_cast<ProductCallback>(callback), context, {}, Lookup(queryPackages, query)};
    const auto hr = reinterpret_cast<Call4>(storeOriginal[StoreVtable::ProductsQueryGetProducts])(self, query, &state, reinterpret_cast<void*>(&ProductVisitor));
    if (Success(hr) && state.continued && state.package.empty()) {
        for (const auto& product : fakeProducts) {
            if (!state.seen.count(product->id) && Selected(product->id) && !state.callback(product->product.data(), context)) break;
        }
    }
    return hr;
}
int64_t __fastcall QueryPackageAsync(void* self, void* context, uint64_t kind, const char* package, void* async) {
    using Fn = int64_t(__fastcall*)(void*, void*, uint64_t, const char*, void*);
    Remember(queryPackageAsync, async, SafeString(package));
    const auto hr = reinterpret_cast<Fn>(storeOriginal[StoreVtable::QueryProductForPackageAsync])(self, context, kind, package, async);
    if (!Success(hr)) Take(queryPackageAsync, async);
    return hr;
}
int64_t __fastcall QueryPackageResult(void* self, void* async, void** output) {
    const auto hr = reinterpret_cast<Result>(storeOriginal[StoreVtable::QueryProductForPackageResult])(self, async, output);
    if (static_cast<uint32_t>(hr) == static_cast<uint32_t>(E_PENDING)) return hr;
    const auto package = Take(queryPackageAsync, async);
    if (Success(hr) && output && *output) Remember(queryPackages, *output, package);
    return hr;
}
int64_t __fastcall CloseQuery(void* self, void* query) {
    Take(queryPackages, query);
    return reinterpret_cast<Call2>(storeOriginal[StoreVtable::ProductsQueryClose])(self, query);
}
int64_t __fastcall QueryIdentifier(void* self, const char* storeId, uint64_t size, char* output) {
    using Fn = int64_t(__fastcall*)(void*, const char*, uint64_t, char*);
    const auto hr = reinterpret_cast<Fn>(storeOriginal[StoreVtable::QueryPackageIdentifier])(self, storeId, size, output);
    const auto id = Policy::Id(SafeString(storeId));
    if (Success(hr) && output && size) {
        const auto package = SafeString(output, static_cast<size_t>(size));
        if (!package.empty()) {
            // Mapping is useful even before a durable product query establishes its kind.
            std::lock_guard lock(stateMutex);
            packageToStore[Policy::Id(package)] = id;
        }
        return hr;
    }
    if (!Selected(id) || !Policy::MissingPackageError(hr) || !output) return hr;
    const auto package = Policy::SyntheticPackage(id);
    if (size <= package.size()) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    std::memcpy(output, package.c_str(), package.size() + 1);
    return S_OK;
}

using PackageCallback = bool(__fastcall*)(void*, const XPackageDetails*);
using Enumerate = int64_t(__fastcall*)(void*, uint32_t, uint32_t, void*, void*);
struct EnumerationContext { PackageCallback callback; void* context; std::unordered_set<std::string> seen; bool continued = true; };
bool __fastcall PackageVisitor(void* context, const XPackageDetails* details) {
    auto& state = *static_cast<EnumerationContext*>(context);
    if (!Readable(details, sizeof(*details))) return false;
    const auto id = Policy::Id(SafeString(details->storeId));
    state.seen.insert(id);
    if (details->kind == static_cast<uint32_t>(XPackageKind::Content)) Discover(id, SafeString(details->packageIdentifier));
    state.continued = state.callback(state.context, details);
    return state.continued;
}
int64_t EnumerateCommon(int index, void* self, uint32_t kind, uint32_t scope, void* context, void* callback) {
    if (!callback || stopping) return reinterpret_cast<Enumerate>(packageOriginal[index])(self, kind, scope, context, callback);
    EnumerationContext state{reinterpret_cast<PackageCallback>(callback), context};
    const auto hr = reinterpret_cast<Enumerate>(packageOriginal[index])(self, kind, scope, &state, reinterpret_cast<void*>(&PackageVisitor));
    if (!Success(hr) || !state.continued || kind != static_cast<uint32_t>(XPackageKind::Content)) return hr;
    // Synthetic installed content is limited to an explicit selection.
    std::vector<std::string> pending;
    for (const auto& id : config.dlcs) {
        if (!Selected(id) || state.seen.count(id)) continue;
        const auto custom = config.mountPaths.find(id);
        if (custom != config.mountPaths.end() && !Policy::ExistingAbsoluteDirectory(custom->second)) continue;
        pending.push_back(id);
    }
    uint32_t ordinal = static_cast<uint32_t>(state.seen.size());
    const auto total = ordinal + static_cast<uint32_t>(pending.size());
    for (const auto& id : pending) {
        const auto package = Policy::SyntheticPackage(id);
        XPackageDetails details{};
        details.packageIdentifier = package.c_str(); details.storeId = id.c_str();
        details.kind = kind; details.displayName = id.c_str(); details.description = ""; details.publisher = ""; details.titleID = "";
        details.version.major = 1; details.index = ordinal++; details.count = total;
        if (!state.callback(context, &details)) break;
    }
    return hr;
}
#define ENUMERATION(Name, Index) \
int64_t __fastcall Name(void* self, uint32_t kind, uint32_t scope, void* context, void* callback) { return EnumerateCommon(Index, self, kind, scope, context, callback); }
ENUMERATION(EnumPackages8, PackageVtable::EnumeratePackages_V8)
#undef ENUMERATION

std::string GameDirectory() {
    try {
        const auto modulePath = Paths::ModulePath(nullptr);
        return modulePath.empty() ? std::string{} : Paths::Utf8(modulePath.parent_path());
    } catch (...) { return {}; }
}
std::string MountPath(const std::string& id, int64_t hr) {
    if (!Selected(id)) return {};
    const auto custom = config.mountPaths.find(Policy::Id(id));
    if (custom != config.mountPaths.end()) return Policy::ExistingAbsoluteDirectory(custom->second) ? custom->second : std::string{};
    return Policy::MissingPackageError(hr) ? GameDirectory() : std::string{};
}
int64_t SyntheticMount(const std::string& path, void** output) {
    if (!output) return E_INVALIDARG;
    auto* handle = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 16);
    if (!handle) return E_OUTOFMEMORY;
    Remember(fakeMounts, handle, path);
    *output = handle;
    return S_OK;
}
using Mount = int64_t(__fastcall*)(void*, const char*, void**);
using MountStart = int64_t(__fastcall*)(void*, const char*, void*);
using MountClose = void(__fastcall*)(void*, void*);
int64_t FinishMount(void* self, const std::string& id, int64_t hr, void** output) {
    const auto path = MountPath(id, hr);
    // Custom paths intentionally override successful mounts; otherwise retain real mounts.
    if (path.empty() || (!Success(hr) && !Policy::MissingPackageError(hr))) return hr;
    if (Success(hr) && output && *output) reinterpret_cast<MountClose>(packageOriginal[PackageVtable::CloseMountHandle])(self, *output);
    return SyntheticMount(path, output);
}
int64_t __fastcall MountPackage(void* self, const char* package, void** output) {
    const auto id = ResolvePackage(package);
    // A validated custom directory needs no attempt to mount an unavailable package.
    const auto custom = config.mountPaths.find(id);
    if (Selected(id) && custom != config.mountPaths.end() && Policy::ExistingAbsoluteDirectory(custom->second)) return SyntheticMount(custom->second, output);
    if (Selected(id) && custom == config.mountPaths.end() && !Policy::SyntheticStoreId(SafeString(package)).empty()) {
        const auto directory = GameDirectory();
        if (!directory.empty()) return SyntheticMount(directory, output);
    }
    const auto hr = reinterpret_cast<Mount>(packageOriginal[PackageVtable::Mount])(self, package, output);
    return FinishMount(self, id, hr, output);
}
int64_t __fastcall MountUiAsync(void* self, const char* package, void* async) {
    Remember(mountAsync, async, ResolvePackage(package));
    const auto hr = reinterpret_cast<MountStart>(packageOriginal[PackageVtable::MountWithUiAsync])(self, package, async);
    if (!Success(hr)) Take(mountAsync, async);
    return hr;
}
int64_t __fastcall MountUiResult(void* self, void* async, void** output) {
    const auto hr = reinterpret_cast<Result>(packageOriginal[PackageVtable::MountWithUiResult])(self, async, output);
    if (static_cast<uint32_t>(hr) == static_cast<uint32_t>(E_PENDING)) return hr;
    const auto id = Take(mountAsync, async);
    return FinishMount(self, id, hr, output);
}
int64_t __fastcall MountPathSize(void* self, void* handle, uint64_t* output) {
    using Fn = int64_t(__fastcall*)(void*, void*, uint64_t*);
    const auto path = Lookup(fakeMounts, handle);
    if (path.empty()) return reinterpret_cast<Fn>(packageOriginal[PackageVtable::GetMountPathSize])(self, handle, output);
    if (!output) return E_INVALIDARG;
    *output = path.size() + 1;
    return S_OK;
}
int64_t __fastcall GetMountPath(void* self, void* handle, uint64_t size, char* output) {
    using Fn = int64_t(__fastcall*)(void*, void*, uint64_t, char*);
    const auto path = Lookup(fakeMounts, handle);
    if (path.empty()) return reinterpret_cast<Fn>(packageOriginal[PackageVtable::GetMountPath])(self, handle, size, output);
    if (!output) return E_INVALIDARG;
    if (size <= path.size()) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    std::memcpy(output, path.c_str(), path.size() + 1);
    return S_OK;
}
void __fastcall CloseMount(void* self, void* handle) {
    if (!Take(fakeMounts, handle).empty()) { HeapFree(GetProcessHeap(), 0, handle); return; }
    reinterpret_cast<MountClose>(packageOriginal[PackageVtable::CloseMountHandle])(self, handle);
}
}

void StoreHooks::Initialize(const UnlockerConfig& cfg) {
    config = cfg;
    for (const auto& id : config.dlcs) if (Policy::Selected(config, id)) fakeProducts.push_back(std::make_unique<FakeProduct>(id));
}
void StoreHooks::OnStoreInterfaceCreated(void** output, const GUID* iid) {
    if (!config.enabled || stopping) return;
    std::lock_guard lock(installMutex);
    if (storeTable && Readable(output, sizeof(void*)) && Readable(*output, sizeof(void*)) && *reinterpret_cast<VtableEntry_t**>(*output) == storeTable) return;
    if (storeTable || !ValidateInterface<STORE_VTABLE_SIZE>(output, iid, IID_IStoreImpl6, storeTable)) { LOG_WARN("Store interface rejected: IStoreImpl6 contract unavailable or table differs"); return; }
    auto* table = *reinterpret_cast<VtableEntry_t**>(*output);
    DWORD previous{};
    if (!BeginPatch(table, storeOriginal, previous)) return;
#define STORE(Index, Hook) table[StoreVtable::Index] = reinterpret_cast<VtableEntry_t>(&Hook)
    STORE(AcquireLicenseForPackageAsync, PackageLicenseAsync); STORE(AcquireLicenseForPackageResult, PackageLicenseResult);
    STORE(AcquireLicenseForDurablesAsync, DurableLicenseAsync); STORE(AcquireLicenseForDurablesResult, DurableLicenseResult);
    STORE(LicenseIsValid, LicenseValid); STORE(CloseLicenseHandle, CloseLicense);
    STORE(RegisterPackageLicenseLost, RegisterLicenseLost); STORE(UnregisterPackageLicenseLost, UnregisterLicenseLost);
    STORE(CanAcquireLicenseForStoreIdAsync, CanAcquireStoreAsync); STORE(CanAcquireLicenseForStoreIdResult, CanAcquireStoreResult);
    STORE(CanAcquireLicenseForPackageAsync, CanAcquirePackageAsync); STORE(CanAcquireLicenseForPackageResult, CanAcquirePackageResult);
    STORE(QueryAddOnLicensesResult, AddonResult); STORE(ProductsQueryGetProducts, GetProducts);
    STORE(QueryProductForPackageAsync, QueryPackageAsync); STORE(QueryProductForPackageResult, QueryPackageResult);
    STORE(ProductsQueryClose, CloseQuery); STORE(QueryPackageIdentifier, QueryIdentifier);
#undef STORE
    VirtualProtect(table, sizeof(storeOriginal), previous, &previous);
    storeTable = table;
}
void StoreHooks::Shutdown() {
    stopping = true;
    std::lock_guard lock(installMutex);
    Restore(storeTable, storeOriginal);
    // Handles can still be owned by the runtime during DLL teardown. Avoid freeing live handles here.
}
void PackageHooks::Initialize(const UnlockerConfig&) {}
void PackageHooks::OnPackageInterfaceCreated(void** output, const GUID* iid) {
    if (!config.enabled || stopping) return;
    std::lock_guard lock(installMutex);
    if (packageTable && Readable(output, sizeof(void*)) && Readable(*output, sizeof(void*)) && *reinterpret_cast<VtableEntry_t**>(*output) == packageTable) return;
    if (packageTable || !ValidateInterface<PACKAGE_VTABLE_SIZE>(output, iid, IID_IPackageImpl9, packageTable)) { LOG_WARN("Package interface rejected: IPackageImpl9 contract unavailable or table differs"); return; }
    auto* table = *reinterpret_cast<VtableEntry_t**>(*output);
    DWORD previous{};
    if (!BeginPatch(table, packageOriginal, previous)) return;
#define PACKAGE(Index, Hook) table[PackageVtable::Index] = reinterpret_cast<VtableEntry_t>(&Hook)
    PACKAGE(EnumeratePackages_V8, EnumPackages8);
    PACKAGE(Mount, MountPackage); PACKAGE(MountWithUiAsync, MountUiAsync); PACKAGE(MountWithUiResult, MountUiResult);
    PACKAGE(GetMountPathSize, MountPathSize); PACKAGE(GetMountPath, GetMountPath); PACKAGE(CloseMountHandle, CloseMount);
#undef PACKAGE
    VirtualProtect(table, sizeof(packageOriginal), previous, &previous);
    packageTable = table;
}
void PackageHooks::Shutdown() {
    stopping = true;
    std::lock_guard lock(installMutex);
    Restore(packageTable, packageOriginal);
}
// The COM methods expose no verified DLC identity and must keep their original behavior.
void ComServerHooks::Initialize(const UnlockerConfig&) {}
void ComServerHooks::TryHookContext(void*) {}
void ComServerHooks::Shutdown() {}
