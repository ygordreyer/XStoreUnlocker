#pragma once
#include <Windows.h>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace Paths {
inline bool FromUtf8(std::string_view value, std::filesystem::path& output) noexcept {
    try {
        if (value.empty() || value.size() > INT_MAX || value.find('\0') != std::string_view::npos) return false;
        const auto length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
        if (!length) return false;
        std::wstring wide(length, L'\0');
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), wide.data(), length)) return false;
        output = std::filesystem::path(std::move(wide));
        return true;
    } catch (...) { return false; }
}
inline std::string Utf8(const std::filesystem::path& path) noexcept {
    try {
        const auto& wide = path.native();
        if (wide.empty() || wide.size() > INT_MAX) return {};
        const auto length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
        if (!length) return {};
        std::string value(length, '\0');
        if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), value.data(), length, nullptr, nullptr)) return {};
        return value;
    } catch (...) { return {}; }
}
inline std::filesystem::path ModulePath(HMODULE module) noexcept {
    try {
        std::vector<wchar_t> buffer(32768);
        const auto length = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (!length || length >= buffer.size()) return {};
        return std::filesystem::path(std::wstring(buffer.data(), length));
    } catch (...) { return {}; }
}
}
