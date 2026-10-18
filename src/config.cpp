#include "config.h"
#include "policy.h"
#include <fstream>

UnlockerConfig LoadConfig(const std::string& iniPath) noexcept {
    std::filesystem::path path;
    if (Paths::FromUtf8(iniPath, path)) return LoadConfig(path);
    UnlockerConfig disabled;
    disabled.enabled = false;
    return disabled;
}

UnlockerConfig LoadConfig(const std::filesystem::path& iniPath) noexcept {
    try {
    UnlockerConfig cfg;
    std::ifstream input(iniPath, std::ios::binary);
    if (!input) return cfg;
    std::string line, section;
    bool first = true;
    while (std::getline(input, line)) {
        if (first && line.starts_with("\xEF\xBB\xBF")) line.erase(0, 3);
        first = false;
        line = Policy::Trim(line);
        if (line.empty() || line[0] == ';' || line[0] == '#') continue;
        if (line.front() == '[' && line.back() == ']') {
            section = Policy::Id(std::string_view(line).substr(1, line.size() - 2));
            continue;
        }
        const auto equals = line.find('=');
        if (equals == std::string::npos) continue;
        const auto key = Policy::Id(std::string_view(line).substr(0, equals));
        const auto value = Policy::Trim(std::string_view(line).substr(equals + 1));
        if (key.empty()) continue;
        if (section == "SETTINGS") {
            if (key == "ENABLED") cfg.enabled = Policy::EnabledValue(value);
            if (key == "UNLOCK_ALL") cfg.unlockAll = Policy::EnabledValue(value);
            if (key == "LOG_ENABLED") cfg.logEnabled = Policy::EnabledValue(value);
        } else if (section == "DLCS" || section == "BLACKLIST") {
            auto& entries = section == "DLCS" ? cfg.dlcs : cfg.blacklist;
            if (Policy::EnabledValue(value)) entries.insert(key); else entries.erase(key);
        } else if (section == "MOUNTPATHS") {
            if (!value.empty()) cfg.mountPaths[key] = value; else cfg.mountPaths.erase(key);
        }
    }
    return cfg;
    } catch (...) {
        UnlockerConfig disabled;
        disabled.enabled = false;
        return disabled;
    }
}
