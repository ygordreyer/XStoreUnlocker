#pragma once
#include "config.h"
#include "paths.h"
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <string_view>

namespace Policy {
inline std::string Trim(std::string_view value) {
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string_view::npos) return {};
    return std::string(value.substr(first, value.find_last_not_of(" \t\r\n") - first + 1));
}
inline std::string Id(std::string_view value) {
    auto result = Trim(value);
    const auto slash = result.find('/');
    if (slash != std::string::npos) result.resize(slash);
    std::transform(result.begin(), result.end(), result.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    return result;
}
inline bool EnabledValue(std::string_view value) { return Trim(value) == "1"; }
inline bool Selected(const UnlockerConfig& cfg, const std::string& id, bool knownDlc = false) {
    const auto normalized = Id(id);
    return cfg.enabled && !normalized.empty() && !cfg.blacklist.count(normalized) &&
        (cfg.dlcs.count(normalized) || (cfg.unlockAll && knownDlc));
}
inline std::string SyntheticPackage(const std::string& id) { return "FakeDLC." + Id(id) + "_unlocker"; }
inline std::string SyntheticStoreId(std::string_view package) {
    constexpr std::string_view prefix = "FakeDLC.", suffix = "_unlocker";
    if (!package.starts_with(prefix) || !package.ends_with(suffix) || package.size() <= prefix.size() + suffix.size()) return {};
    return Id(package.substr(prefix.size(), package.size() - prefix.size() - suffix.size()));
}
inline bool ExistingAbsoluteDirectory(const std::string& value) noexcept {
    try {
        std::filesystem::path path;
        if (!Paths::FromUtf8(value, path)) return false;
        std::error_code error;
        return path.is_absolute() && std::filesystem::is_directory(path, error) && !error;
    } catch (...) { return false; }
}
inline bool MissingPackageError(int64_t result) {
    const auto hr = static_cast<uint32_t>(result);
    return hr == 0x80070002u || hr == 0x80070003u || hr == 0x80070490u;
}
inline bool LicenseDenied(int64_t result) { return static_cast<uint32_t>(result) == 0x80070005u; }
inline bool OverrideMount(const UnlockerConfig& cfg, const std::string& id, bool knownDlc,
                          int64_t result, bool validCustomPath, bool hasCustomPath) {
    return Selected(cfg, id, knownDlc) && (hasCustomPath ? validCustomPath : MissingPackageError(result));
}
}
