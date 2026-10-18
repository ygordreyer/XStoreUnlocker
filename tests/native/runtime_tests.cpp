#include "../../src/store_hooks.cpp"
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace {
int checks = 0, nativeMounts = 0, nativeCloses = 0, nativeValidCalls = 0, callbacks = 0;
int64_t resultCode = S_OK;
void* realHandle = reinterpret_cast<void*>(0x12345);
bool acceptContract = false;
int contractQueries = 0, contractReleases = 0;
GUID requestedFixtureIid{1, 2, 3, {4}};
HRESULT __fastcall ContractQuery(void* self, const GUID* iid, void** output) {
    if (memcmp(iid, &IID_IStoreImpl6, sizeof(GUID)) == 0) {
        ++contractQueries;
        if (!acceptContract) return E_NOINTERFACE;
    } else if (memcmp(iid, &requestedFixtureIid, sizeof(GUID)) != 0) return E_NOINTERFACE;
    *output = self;
    return S_OK;
}
ULONG __fastcall ContractRelease(void*) { ++contractReleases; return 1; }
int64_t __fastcall NativeAddon(void*, void*, uint32_t count, void* output) {
    auto* records = static_cast<XStoreAddonLicense*>(output);
    if (count >= 2) {
        strcpy_s(records[0].skuStoreId, "9SELECTED");
        strcpy_s(records[1].skuStoreId, "UNKNOWN");
    }
    return S_OK;
}
void Check(bool condition, const char* message) {
    ++checks;
    if (!condition) throw std::runtime_error(message);
}
int64_t __fastcall NativeStart(void*, void*, void*, void*) { return S_OK; }
int64_t __fastcall NativeResult(void*, void*, void** output) {
    if (output) *output = Success(resultCode) ? realHandle : nullptr;
    return resultCode;
}
int64_t __fastcall NativeValid(void*, void*) { ++nativeValidCalls; return 0; }
int64_t __fastcall NativeCloseLicense(void*, void*) { ++nativeCloses; return 0; }
int64_t __fastcall NativeMount(void*, const char*, void** output) {
    ++nativeMounts;
    if (output) *output = Success(resultCode) ? realHandle : nullptr;
    return resultCode;
}
int64_t __fastcall NativeMountStart(void*, const char*, void*) { return S_OK; }
void __fastcall NativeClose(void*, void*) { ++nativeCloses; }
int64_t __fastcall NativePathSize(void*, void*, uint64_t*) { return E_UNEXPECTED; }
int64_t __fastcall NativePath(void*, void*, uint64_t, char*) { return E_UNEXPECTED; }
bool __fastcall EnumCallback(void*, const XPackageDetails*) { ++callbacks; return true; }
int64_t __fastcall NativeEnumeration(void*, uint32_t kind, uint32_t, void* context, void* callback) {
    XPackageDetails detail{};
    detail.kind = kind; detail.storeId = "9REAL"; detail.packageIdentifier = "Installed.Package";
    reinterpret_cast<PackageCallback>(callback)(context, &detail);
    return S_OK;
}
void Setup() {
    config = {};
    config.dlcs = {"9SELECTED"};
    config.blacklist = {"9DENIED"};
    knownDlc.clear(); packageToStore.clear();
    for (const int index : {StoreVtable::AcquireLicenseForPackageAsync, StoreVtable::AcquireLicenseForDurablesAsync}) storeOriginal[index] = reinterpret_cast<VtableEntry_t>(&NativeStart);
    for (const int index : {StoreVtable::AcquireLicenseForPackageResult, StoreVtable::AcquireLicenseForDurablesResult}) storeOriginal[index] = reinterpret_cast<VtableEntry_t>(&NativeResult);
    storeOriginal[StoreVtable::LicenseIsValid] = reinterpret_cast<VtableEntry_t>(&NativeValid);
    storeOriginal[StoreVtable::CloseLicenseHandle] = reinterpret_cast<VtableEntry_t>(&NativeCloseLicense);
    packageOriginal[PackageVtable::Mount] = reinterpret_cast<VtableEntry_t>(&NativeMount);
    packageOriginal[PackageVtable::MountWithUiAsync] = reinterpret_cast<VtableEntry_t>(&NativeMountStart);
    packageOriginal[PackageVtable::MountWithUiResult] = reinterpret_cast<VtableEntry_t>(&NativeResult);
    packageOriginal[PackageVtable::CloseMountHandle] = reinterpret_cast<VtableEntry_t>(&NativeClose);
    packageOriginal[PackageVtable::GetMountPathSize] = reinterpret_cast<VtableEntry_t>(&NativePathSize);
    packageOriginal[PackageVtable::GetMountPath] = reinterpret_cast<VtableEntry_t>(&NativePath);
    packageOriginal[PackageVtable::EnumeratePackages_V8] = reinterpret_cast<VtableEntry_t>(&NativeEnumeration);
}
void TestConfig(const std::filesystem::path& folder) {
    const auto path = folder / "test.ini";
    { std::ofstream file(path);
      file << "[Settings]\nenabled=1\nunlock_all=0\nlog_enabled=0\n[DLCs]\n9selected=1\n9disabled=0\n9repeat=1\n9repeat=0\n[Blacklist]\n9denied=1\n9selected=0\n[MountPaths]\n9selected=" << folder.string() << "\n"; }
    const auto cfg = LoadConfig(path.string());
    Check(cfg.enabled && !cfg.unlockAll && !cfg.logEnabled, "settings parsing");
    Check(cfg.dlcs.size() == 1 && cfg.dlcs.count("9SELECTED"), "disabled DLC entries must be ignored");
    Check(cfg.blacklist.size() == 1 && cfg.blacklist.count("9DENIED"), "disabled blacklist entry must be ignored");
    Check(cfg.mountPaths.at("9SELECTED") == folder.string(), "mount path values retain case and spaces");
    Check(!LoadConfig((folder / "missing.ini").string()).unlockAll, "missing config defaults to explicit selection");
}
void TestPolicy(const std::filesystem::path& folder) {
    auto cfg = config;
    Check(Policy::Selected(cfg, "9selected"), "case normalized explicit selection");
    cfg.dlcs.insert("9DENIED");
    Check(!Policy::Selected(cfg, "9DENIED", true), "blacklist overrides explicit selection");
    cfg.unlockAll = true;
    Check(!Policy::Selected(cfg, "UNKNOWN"), "unlock all must pass unknown IDs through");
    Check(Policy::Selected(cfg, "KNOWN", true), "unlock all applies to known DLC");
    cfg.enabled = false;
    Check(!Policy::Selected(cfg, "9SELECTED", true), "enabled false overrides selection");
    Check(Policy::SyntheticStoreId(Policy::SyntheticPackage("9selected")) == "9SELECTED", "synthetic identity round trip");
    Check(Policy::SyntheticStoreId("FakeDLC.9SELECTED_bad").empty(), "reject unrecognized synthetic package");
    Check(Policy::ExistingAbsoluteDirectory(folder.string()), "existing absolute mount directory");
    Check(!Policy::ExistingAbsoluteDirectory("relative"), "relative mount directory rejected");
    Check(!Policy::ExistingAbsoluteDirectory((folder / "missing").string()), "missing mount directory rejected");
    Check(!Policy::MissingPackageError(E_FAIL) && !Policy::LicenseDenied(E_FAIL), "unrelated failure must not be translated");
}
void TestLicense() {
    int asyncBlock{};
    void* output = nullptr;
    resultCode = E_ACCESSDENIED;
    DurableLicenseAsync(nullptr, nullptr, const_cast<char*>("UNKNOWN"), &asyncBlock);
    Check(DurableLicenseResult(nullptr, &asyncBlock, &output) == E_ACCESSDENIED && !output, "unknown denied license pass through");
    DurableLicenseAsync(nullptr, nullptr, const_cast<char*>("9SELECTED"), &asyncBlock);
    resultCode = E_PENDING;
    Check(DurableLicenseResult(nullptr, &asyncBlock, &output) == E_PENDING, "pending license preserved");
    resultCode = E_ACCESSDENIED;
    Check(DurableLicenseResult(nullptr, &asyncBlock, &output) == S_OK && output, "selected denied license creates tracked handle");
    Check(LicenseValid(nullptr, output) == 1, "selected fake license valid");
    const auto before = nativeCloses;
    CloseLicense(nullptr, output);
    Check(nativeCloses == before && fakeLicenses.empty(), "fake license closed without original API");
    Check(LicenseValid(nullptr, realHandle) == 0 && nativeValidCalls == 1, "unknown real license validity pass through");
    resultCode = E_FAIL;
    DurableLicenseAsync(nullptr, nullptr, const_cast<char*>("9SELECTED"), &asyncBlock);
    Check(DurableLicenseResult(nullptr, &asyncBlock, &output) == E_FAIL, "selected unrelated license failure retained");
    config.dlcs.insert("9DENIED");
    resultCode = E_ACCESSDENIED;
    DurableLicenseAsync(nullptr, nullptr, const_cast<char*>("9DENIED"), &asyncBlock);
    Check(DurableLicenseResult(nullptr, &asyncBlock, &output) == E_ACCESSDENIED, "blacklisted license untouched");
}
void CheckPath(void* handle, const std::string& expected) {
    uint64_t required{};
    Check(MountPathSize(nullptr, handle, &required) == S_OK && required == expected.size() + 1, "mount size includes terminator");
    std::vector<char> buffer(required);
    Check(GetMountPath(nullptr, handle, required - 1, buffer.data()) == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), "short mount buffer returns error");
    Check(GetMountPath(nullptr, handle, required, buffer.data()) == S_OK && std::string(buffer.data()) == expected, "mount handle retains exact path");
    CloseMount(nullptr, handle);
}
void TestMounts(const std::filesystem::path& folder) {
    Discover("9SELECTED", "Real.Selected.Package");
    Check(ResolvePackage("Real.Selected.Package") == "9SELECTED", "package enumeration mapping resolves store ID");
    config.mountPaths["9SELECTED"] = folder.string();
    void* output = nullptr;
    resultCode = E_FAIL;
    const auto before = nativeMounts;
    Check(MountPackage(nullptr, "Real.Selected.Package", &output) == S_OK && nativeMounts == before, "custom directory mapped by Store ID");
    CheckPath(output, folder.string());
    resultCode = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    Check(MountPackage(nullptr, "Unknown.Package", &output) == resultCode && !output, "unknown mount failure retained");
    Check(MountPackage(nullptr, "FakeDLC.9DENIED_unlocker", &output) == resultCode && !output, "blacklisted synthetic mount failure retained");
    config.mountPaths.clear();
    resultCode = S_OK;
    Check(MountPackage(nullptr, "Real.Selected.Package", &output) == S_OK && output == realHandle, "successful installed mount retained");
    resultCode = E_FAIL;
    Check(MountPackage(nullptr, "Real.Selected.Package", &output) == E_FAIL, "unrelated selected mount failure retained");
    config.mountPaths["9SELECTED"] = (folder / "missing").string();
    resultCode = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    Check(MountPackage(nullptr, "Real.Selected.Package", &output) == resultCode, "invalid custom path must not fall back");
    config.mountPaths["9SELECTED"] = folder.string();
    int asyncBlock{};
    MountUiAsync(nullptr, "Real.Selected.Package", &asyncBlock);
    resultCode = E_PENDING;
    Check(MountUiResult(nullptr, &asyncBlock, &output) == E_PENDING, "pending async mount preserved");
    resultCode = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    Check(MountUiResult(nullptr, &asyncBlock, &output) == S_OK && output, "async mount result retains package identity");
    CheckPath(output, folder.string());
    MountUiAsync(nullptr, "Unknown.Package", &asyncBlock);
    Check(MountUiResult(nullptr, &asyncBlock, &output) == resultCode && !output, "unknown async mount failure retained");
    resultCode = E_FAIL;
    MountUiAsync(nullptr, "Real.Selected.Package", &asyncBlock);
    Check(MountUiResult(nullptr, &asyncBlock, &output) == E_FAIL, "unrelated async failure retained");
    Check(MountPathSize(nullptr, realHandle, nullptr) == E_UNEXPECTED, "real mount handle pass through");
}
void TestEnumeration() {
    config.dlcs.erase("9DENIED");
    config.mountPaths.clear();
    config.unlockAll = true;
    Discover("9DISCOVERED");
    callbacks = 0;
    EnumPackages8(nullptr, static_cast<uint32_t>(XPackageKind::Content), 0, nullptr, reinterpret_cast<void*>(&EnumCallback));
    Check(callbacks == 2, "only explicitly selected synthetic package injected");
    Check(ResolvePackage("Installed.Package") == "9REAL", "real enumeration records package/store identity");
    config.enabled = false;
    callbacks = 0;
    EnumPackages8(nullptr, static_cast<uint32_t>(XPackageKind::Content), 0, nullptr, reinterpret_cast<void*>(&EnumCallback));
    Check(callbacks == 1, "disabled runtime does not inject packages");
}
void TestProducts() {
    config.enabled = true; config.unlockAll = false;
    FakeProduct selected("9SELECTED");
    selected.product[145] = 0;
    ProductContext context{};
    ProductVisitor(selected.product.data(), &context);
    Check(selected.product[145] == 1, "selected durable product marked owned");
    FakeProduct game("9SELECTED");
    game.product[145] = 0;
    *reinterpret_cast<uint32_t*>(game.product.data() + 48) = 4;
    ProductVisitor(game.product.data(), &context);
    Check(game.product[145] == 0, "base game product preserved even with matching explicit ID");
    FakeProduct consumable("9SELECTED"); consumable.product[145] = 0;
    *reinterpret_cast<uint32_t*>(consumable.product.data() + 48) = 1;
    ProductVisitor(consumable.product.data(), &context);
    Check(consumable.product[145] == 0, "consumable product preserved even with matching explicit ID");
    FakeProduct unknown("UNKNOWN"); unknown.product[145] = 0;
    ProductVisitor(unknown.product.data(), &context);
    Check(unknown.product[145] == 0, "unselected product preserved");
    config.dlcs.insert("9DENIED");
    FakeProduct denied("9DENIED"); denied.product[145] = 0;
    ProductVisitor(denied.product.data(), &context);
    Check(denied.product[145] == 0, "blacklisted product preserved");
}
void TestInterfaceValidation() {
    config.enabled = true;
    GUID iid{};
    void* invalid = nullptr;
    StoreHooks::OnStoreInterfaceCreated(&invalid, &iid);
    Check(!storeTable, "null store interface rejected");
    std::array<VtableEntry_t, STORE_VTABLE_SIZE> slots{};
    for (auto& slot : slots) slot = reinterpret_cast<VtableEntry_t>(&NativeValid);
    slots.back() = reinterpret_cast<VtableEntry_t>(1);
    auto* slotPointer = slots.data();
    invalid = &slotPointer;
    StoreHooks::OnStoreInterfaceCreated(&invalid, &iid);
    Check(!storeTable && slots.front() == reinterpret_cast<VtableEntry_t>(&NativeValid), "nonexecutable vtable slot rejected without writes");
    slots.back() = reinterpret_cast<VtableEntry_t>(&NativeValid);
    StoreHooks::OnStoreInterfaceCreated(&invalid, nullptr);
    Check(!storeTable, "missing requested interface identity rejected");
    slots[0] = reinterpret_cast<VtableEntry_t>(&ContractQuery);
    slots[2] = reinterpret_cast<VtableEntry_t>(&ContractRelease);
    acceptContract = false;
    StoreHooks::OnStoreInterfaceCreated(&invalid, &requestedFixtureIid);
    Check(!storeTable && contractQueries == 1, "arbitrary IID with executable full table cannot authorize unsupported Store ABI");
    acceptContract = true;
    StoreHooks::OnStoreInterfaceCreated(&invalid, &requestedFixtureIid);
    Check(storeTable == slots.data() && contractReleases == 1, "lower requested IID sharing verified highest contract table accepted and QI reference released");
    Check(slots[StoreVtable::AcquireLicenseForPackageAsync] == reinterpret_cast<VtableEntry_t>(&PackageLicenseAsync), "verified Store contract hooks correct slot");
    Restore(storeTable, storeOriginal);
    Check(slots[StoreVtable::AcquireLicenseForPackageAsync] == reinterpret_cast<VtableEntry_t>(&NativeValid), "verified table restores original slot");
}
void TestAddonCount() {
    config.enabled = true; config.unlockAll = false;
    storeOriginal[StoreVtable::QueryAddOnLicensesResult] = reinterpret_cast<VtableEntry_t>(&NativeAddon);
    std::array<XStoreAddonLicense, 3> records{};
    records[2].expirationDate = 1234;
    Check(AddonResult(nullptr, nullptr, 2, records.data()) == S_OK, "addon result accepts record count");
    Check(records[0].isActive && !records[1].isActive, "addon record count two patches selected record and preserves unknown");
    Check(records[2].expirationDate == 1234, "addon result stays inside supplied record count");
}
void TestUnicodeAndMalformedPaths(const std::filesystem::path& folder) {
    const auto unicode = folder / std::filesystem::path(L"S\u00e3o \u65e5\u672c\u8a9e");
    std::filesystem::create_directories(unicode);
    const auto file = unicode / L"xstore_unlocker.ini";
    { std::ofstream output(file, std::ios::binary);
      output << "\xEF\xBB\xBF[Settings]\nenabled=0\n[DLCs]\n9SELECTED=1\n[MountPaths]\n9SELECTED=" << Paths::Utf8(unicode) << "\n"; }
    const auto cfg = LoadConfig(Paths::Utf8(file));
    Check(!cfg.enabled && cfg.dlcs.count("9SELECTED"), "Unicode config file and UTF8 BOM enabled=0 are honored");
    Check(cfg.mountPaths.at("9SELECTED") == Paths::Utf8(unicode), "Unicode mount path parsed without ACP conversion");
    Check(Policy::ExistingAbsoluteDirectory(Paths::Utf8(unicode)), "Unicode directory validation");
    std::filesystem::path roundTrip;
    Check(Paths::FromUtf8(Paths::Utf8(unicode), roundTrip) && roundTrip == unicode, "Unicode native path round trip");
    const std::string malformed("\xC3\x28");
    Check(!Policy::ExistingAbsoluteDirectory(malformed), "malformed UTF8 mount path returns false without throwing");
    Check(!LoadConfig(malformed).enabled, "malformed UTF8 config filename disables overrides without throwing");
    config.enabled = true;
    config.dlcs.insert("9SELECTED");
    config.mountPaths["9SELECTED"] = malformed;
    resultCode = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    void* output = nullptr;
    Check(MountPackage(nullptr, "FakeDLC.9SELECTED_unlocker", &output) == resultCode && !output, "malformed configured mount path preserves original failure");
    const auto binaries = Paths::ModulePath(nullptr).parent_path();
    std::filesystem::copy_file(binaries / L"XGameRuntime.dll", unicode / L"XGameRuntime.dll");
    std::filesystem::copy_file(binaries / L"runtime_test_original.dll", unicode / L"XGameRuntime_o.dll");
    const auto module = LoadLibraryW((unicode / L"XGameRuntime.dll").c_str());
    Check(module != nullptr, "runtime DLL loads from Unicode folder");
    const auto query = reinterpret_cast<QueryApiImpl_t>(GetProcAddress(module, "QueryApiImpl"));
    Check(query && query(nullptr, nullptr, nullptr) == HRESULT_FROM_WIN32(ERROR_CANCELLED), "proxy loads original DLL with Unicode path");
    Check(std::filesystem::exists(unicode / L"xstore_unlocker.log"), "logger opens Unicode path");
    Check(FreeLibrary(module), "Unicode runtime smoke test unloads cleanly");
}
}
int main() {
    const auto folder = std::filesystem::temp_directory_path() / ("XStoreManager native tests " + std::to_string(GetCurrentProcessId()));
    std::filesystem::create_directories(folder);
    try {
        Setup(); TestConfig(folder); TestPolicy(folder); TestLicense(); TestMounts(folder); TestEnumeration(); TestProducts(); TestAddonCount(); TestInterfaceValidation(); TestUnicodeAndMalformedPaths(folder);
        std::filesystem::remove_all(folder);
        std::cout << checks << " runtime checks passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::filesystem::remove_all(folder);
        std::cerr << "FAILED: " << error.what() << '\n';
        return 1;
    }
}
