// XStoreAPI Unlocker v2.1.0
// xgameruntime.dll proxy. rename original to XGameRuntime_o.dll

#include "proxy.h"
#include "store_hooks.h"
#include "config.h"
#include "logger.h"
#include "paths.h"

#include <string>
#include <cstdio>
#include <Windows.h>

static HMODULE g_module = nullptr;
static UnlockerConfig g_config;

static std::string GuidToString(const GUID* g);

static std::string GuidToString(const GUID* g) {
    if (!g) return "(null)";
    char buf[64] = {};
    std::snprintf(
        buf,
        sizeof(buf),
        "%08lX-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X",
        g->Data1,
        g->Data2,
        g->Data3,
        g->Data4[0], g->Data4[1], g->Data4[2], g->Data4[3],
        g->Data4[4], g->Data4[5], g->Data4[6], g->Data4[7]);
    return buf;
}

extern "C" __declspec(dllexport) HRESULT __cdecl QueryApiImpl(
    const GUID* providerGuid,
    const GUID* interfaceGuid,
    void** ppInterface)
{
    if (!Proxy::EnsureInitialized()) return E_FAIL;

    HRESULT hr = Proxy::GetReal_QueryApiImpl()(providerGuid, interfaceGuid, ppInterface);

    if (SUCCEEDED(hr) && ppInterface && *ppInterface) {
        const bool isStoreProvider = GuidsEqual(providerGuid, &XSTORE_PROVIDER_GUID);
        const bool isPackageProvider = GuidsEqual(providerGuid, &XPACKAGE_PROVIDER_GUID);

        if (isStoreProvider) {
            static bool loggedOnce = false;
            if (!loggedOnce) {
                loggedOnce = true;
                LOG_INFO("XStore interface created (provider=%s iface=%s out=%p)",
                         GuidToString(providerGuid).c_str(),
                         GuidToString(interfaceGuid).c_str(),
                         *ppInterface);
            }
            StoreHooks::OnStoreInterfaceCreated(ppInterface, interfaceGuid);
        }

        if (isPackageProvider) {
            static bool loggedOnce = false;
            if (!loggedOnce) {
                loggedOnce = true;
                LOG_INFO("XPackage interface created (provider=%s iface=%s out=%p)",
                         GuidToString(providerGuid).c_str(),
                         GuidToString(interfaceGuid).c_str(),
                         *ppInterface);
            }
            PackageHooks::OnPackageInterfaceCreated(ppInterface, interfaceGuid);
        }
    }

    return hr;
}

extern "C" __declspec(dllexport) HRESULT __cdecl InitializeApiImpl(uint64_t a1, uint64_t a2) {
    if (!Proxy::EnsureInitialized()) return E_FAIL;
    auto fn = Proxy::GetReal_InitializeApiImpl();
    return fn ? fn(a1, a2) : E_NOTIMPL;
}

extern "C" __declspec(dllexport) HRESULT __cdecl InitializeApiImplEx(uint64_t a1, uint64_t a2, int64_t a3) {
    if (!Proxy::EnsureInitialized()) return E_FAIL;
    auto fn = Proxy::GetReal_InitializeApiImplEx();
    return fn ? fn(a1, a2, a3) : E_NOTIMPL;
}

extern "C" __declspec(dllexport) HRESULT __cdecl InitializeApiImplEx2(uint64_t a1, uint64_t a2, int64_t a3, int64_t a4) {
    if (!Proxy::EnsureInitialized()) return E_FAIL;
    auto fn = Proxy::GetReal_InitializeApiImplEx2();
    return fn ? fn(a1, a2, a3, a4) : E_NOTIMPL;
}

extern "C" __declspec(dllexport) HRESULT __cdecl UninitializeApiImpl() {
    if (!Proxy::EnsureInitialized()) return E_FAIL;
    auto fn = Proxy::GetReal_UninitializeApiImpl();
    return fn ? fn() : E_NOTIMPL;
}

extern "C" __declspec(dllexport) HRESULT __cdecl DllCanUnloadNow() {
    if (!Proxy::EnsureInitialized()) return S_FALSE;
    auto fn = Proxy::GetReal_DllCanUnloadNow();
    return fn ? fn() : S_FALSE;
}

extern "C" __declspec(dllexport) void __cdecl XErrorReport(uint64_t code, const char* message) {
    if (!Proxy::EnsureInitialized()) return;
    auto fn = Proxy::GetReal_XErrorReport();
    if (fn) fn(code, message);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpReserved) {
    try {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
    {
        g_module = hModule;
        DisableThreadLibraryCalls(hModule);
        Proxy::SetOurModule(hModule);

        const auto modulePath = Paths::ModulePath(g_module);
        const auto dir = modulePath.parent_path();

        if (modulePath.empty()) g_config.enabled = false;
        else g_config = LoadConfig(dir / L"xstore_unlocker.ini");
        Logger::Instance().SetEnabled(g_config.logEnabled);
        if (g_config.logEnabled) {
            Logger::Instance().Init(dir / L"xstore_unlocker.log");
            LOG_INFO("XStoreManager runtime v3.0.0");
            LOG_INFO("Config: enabled=%d, unlock_all=%d, log_enabled=%d, blacklist=%zu, dlcs=%zu",
                     g_config.enabled,
                     g_config.unlockAll,
                     g_config.logEnabled,
                     g_config.blacklist.size(),
                     g_config.dlcs.size());
        }

        StoreHooks::Initialize(g_config);
        PackageHooks::Initialize(g_config);
        ComServerHooks::Initialize(g_config);

        LOG_INFO("Ready. Real DLL loads on first API call.");
        break;
    }
    case DLL_PROCESS_DETACH:
        if (lpReserved) break;
        ComServerHooks::Shutdown();
        PackageHooks::Shutdown();
        StoreHooks::Shutdown();
        Proxy::Shutdown(lpReserved != nullptr);
        break;
    }
    } catch (...) {
        // A malformed configuration must leave the real runtime usable.
        g_config.enabled = false;
        try { StoreHooks::Initialize(g_config); } catch (...) {}
    }

    return TRUE;
}
