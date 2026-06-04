#include "logger.h"

#include <windows.h>

#include <mutex>
#include <string>

namespace RuntimeHook
{
namespace
{
std::mutex g_logMutex;
std::wstring g_logPath;

std::wstring GetEnvironmentString(const wchar_t* name)
{
    DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0)
    {
        return L"";
    }

    std::wstring value(required, L'\0');
    DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0)
    {
        return L"";
    }

    value.resize(written);
    return value;
}

std::wstring GetModuleDirectoryFallback()
{
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring value(path);
    std::size_t slash = value.find_last_of(L"\\/");
    if (slash == std::wstring::npos)
    {
        return L".";
    }
    return value.substr(0, slash);
}

void EnsureDirectory(const std::wstring& directory)
{
    if (directory.empty())
    {
        return;
    }
    CreateDirectoryW(directory.c_str(), nullptr);
}

std::string WideToUtf8(const std::wstring& value)
{
    if (value.empty())
    {
        return {};
    }

    int required = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (required <= 0)
    {
        return {};
    }

    std::string output(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), output.data(), required, nullptr, nullptr);
    return output;
}

std::wstring Timestamp()
{
    SYSTEMTIME time = {};
    GetLocalTime(&time);

    wchar_t buffer[64] = {};
    swprintf_s(
        buffer,
        L"%04u-%02u-%02u %02u:%02u:%02u.%03u",
        time.wYear,
        time.wMonth,
        time.wDay,
        time.wHour,
        time.wMinute,
        time.wSecond,
        time.wMilliseconds);
    return buffer;
}
}

void Logger::InitializeFromEnvironment()
{
    std::lock_guard<std::mutex> lock(g_logMutex);

    std::wstring logDir = GetEnvironmentString(L"DD_RUNTIME_LOG_DIR");
    if (logDir.empty())
    {
        logDir = GetModuleDirectoryFallback();
    }

    EnsureDirectory(logDir);
    g_logPath = logDir + L"\\runtime_hook.log";
}

void Logger::Info(const std::wstring& message)
{
    Write(L"INFO", message);
}

void Logger::Warn(const std::wstring& message)
{
    Write(L"WARN", message);
}

void Logger::Error(const std::wstring& message)
{
    Write(L"ERROR", message);
}

void Logger::Write(const wchar_t* level, const std::wstring& message)
{
    std::lock_guard<std::mutex> lock(g_logMutex);

    if (g_logPath.empty())
    {
        InitializeFromEnvironment();
    }

    std::wstring line = Timestamp() + L" [" + level + L"] " + message + L"\r\n";
    std::string utf8 = WideToUtf8(line);

    HANDLE file = CreateFileW(
        g_logPath.c_str(),
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    DWORD written = 0;
    WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    CloseHandle(file);
}
}