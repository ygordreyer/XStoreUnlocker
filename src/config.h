// config from xstore_unlocker.ini

#pragma once

#include <string>
#include <unordered_set>
#include <unordered_map>
#include <filesystem>

struct UnlockerConfig {
    bool enabled = true;
    bool unlockAll  = false;
    bool logEnabled = true;
    std::unordered_set<std::string> blacklist;
    std::unordered_set<std::string> dlcs;
    std::unordered_map<std::string, std::string> mountPaths;
};

UnlockerConfig LoadConfig(const std::string& iniPath) noexcept;
UnlockerConfig LoadConfig(const std::filesystem::path& iniPath) noexcept;
