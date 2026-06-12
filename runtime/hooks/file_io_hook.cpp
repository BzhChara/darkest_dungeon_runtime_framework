#include "file_io_hook.h"

#include "../logger.h"

#include <windows.h>

#include <MinHook.h>

#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cwctype>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace RuntimeHook
{
namespace
{
using CreateFileWFn = HANDLE(WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
using CreateFileAFn = HANDLE(WINAPI*)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
using ReadFileFn = BOOL(WINAPI*)(HANDLE, LPVOID, DWORD, LPDWORD, LPOVERLAPPED);
using WriteFileFn = BOOL(WINAPI*)(HANDLE, LPCVOID, DWORD, LPDWORD, LPOVERLAPPED);
using MoveFileWFn = BOOL(WINAPI*)(LPCWSTR, LPCWSTR);
using MoveFileAFn = BOOL(WINAPI*)(LPCSTR, LPCSTR);
using MoveFileExWFn = BOOL(WINAPI*)(LPCWSTR, LPCWSTR, DWORD);
using MoveFileExAFn = BOOL(WINAPI*)(LPCSTR, LPCSTR, DWORD);
using CopyFileWFn = BOOL(WINAPI*)(LPCWSTR, LPCWSTR, BOOL);
using CopyFileAFn = BOOL(WINAPI*)(LPCSTR, LPCSTR, BOOL);
using DeleteFileWFn = BOOL(WINAPI*)(LPCWSTR);
using DeleteFileAFn = BOOL(WINAPI*)(LPCSTR);
using ReplaceFileWFn = BOOL(WINAPI*)(LPCWSTR, LPCWSTR, LPCWSTR, DWORD, LPVOID, LPVOID);
using ReplaceFileAFn = BOOL(WINAPI*)(LPCSTR, LPCSTR, LPCSTR, DWORD, LPVOID, LPVOID);
using SetFileAttributesWFn = BOOL(WINAPI*)(LPCWSTR, DWORD);
using SetFileAttributesAFn = BOOL(WINAPI*)(LPCSTR, DWORD);
using CloseHandleFn = BOOL(WINAPI*)(HANDLE);
using GetFileSizeFn = DWORD(WINAPI*)(HANDLE, LPDWORD);
using GetFileSizeExFn = BOOL(WINAPI*)(HANDLE, PLARGE_INTEGER);
using SetFilePointerFn = DWORD(WINAPI*)(HANDLE, LONG, PLONG, DWORD);
using SetFilePointerExFn = BOOL(WINAPI*)(HANDLE, LARGE_INTEGER, PLARGE_INTEGER, DWORD);

CreateFileWFn g_originalKernel32CreateFileW = nullptr;
CreateFileAFn g_originalKernel32CreateFileA = nullptr;
CreateFileWFn g_originalKernelBaseCreateFileW = nullptr;
CreateFileAFn g_originalKernelBaseCreateFileA = nullptr;
ReadFileFn g_originalKernel32ReadFile = nullptr;
ReadFileFn g_originalKernelBaseReadFile = nullptr;
WriteFileFn g_originalKernel32WriteFile = nullptr;
WriteFileFn g_originalKernelBaseWriteFile = nullptr;
MoveFileWFn g_originalKernel32MoveFileW = nullptr;
MoveFileAFn g_originalKernel32MoveFileA = nullptr;
MoveFileWFn g_originalKernelBaseMoveFileW = nullptr;
MoveFileAFn g_originalKernelBaseMoveFileA = nullptr;
MoveFileExWFn g_originalKernel32MoveFileExW = nullptr;
MoveFileExAFn g_originalKernel32MoveFileExA = nullptr;
MoveFileExWFn g_originalKernelBaseMoveFileExW = nullptr;
MoveFileExAFn g_originalKernelBaseMoveFileExA = nullptr;
CopyFileWFn g_originalKernel32CopyFileW = nullptr;
CopyFileAFn g_originalKernel32CopyFileA = nullptr;
CopyFileWFn g_originalKernelBaseCopyFileW = nullptr;
CopyFileAFn g_originalKernelBaseCopyFileA = nullptr;
DeleteFileWFn g_originalKernel32DeleteFileW = nullptr;
DeleteFileAFn g_originalKernel32DeleteFileA = nullptr;
DeleteFileWFn g_originalKernelBaseDeleteFileW = nullptr;
DeleteFileAFn g_originalKernelBaseDeleteFileA = nullptr;
ReplaceFileWFn g_originalKernel32ReplaceFileW = nullptr;
ReplaceFileAFn g_originalKernel32ReplaceFileA = nullptr;
ReplaceFileWFn g_originalKernelBaseReplaceFileW = nullptr;
ReplaceFileAFn g_originalKernelBaseReplaceFileA = nullptr;
SetFileAttributesWFn g_originalKernel32SetFileAttributesW = nullptr;
SetFileAttributesAFn g_originalKernel32SetFileAttributesA = nullptr;
SetFileAttributesWFn g_originalKernelBaseSetFileAttributesW = nullptr;
SetFileAttributesAFn g_originalKernelBaseSetFileAttributesA = nullptr;
CloseHandleFn g_originalKernel32CloseHandle = nullptr;
CloseHandleFn g_originalKernelBaseCloseHandle = nullptr;
GetFileSizeFn g_originalKernel32GetFileSize = nullptr;
GetFileSizeFn g_originalKernelBaseGetFileSize = nullptr;
GetFileSizeExFn g_originalKernel32GetFileSizeEx = nullptr;
GetFileSizeExFn g_originalKernelBaseGetFileSizeEx = nullptr;
SetFilePointerFn g_originalKernel32SetFilePointer = nullptr;
SetFilePointerFn g_originalKernelBaseSetFilePointer = nullptr;
SetFilePointerExFn g_originalKernel32SetFilePointerEx = nullptr;
SetFilePointerExFn g_originalKernelBaseSetFilePointerEx = nullptr;

std::mutex g_observerMutex;
std::unordered_set<std::wstring> g_seenPaths;
std::vector<std::wstring> g_extensions;
std::atomic<unsigned long> g_loggedCount{ 0 };
unsigned long g_maxEntries = 2000;
bool g_deduplicate = true;
bool g_limitLogged = false;

bool g_eventProbeEnabled = true;
bool g_eventProbeLogFileOpen = true;
bool g_eventProbeLogFileWrite = true;
bool g_eventProbeLogSaveFiles = true;
bool g_eventProbeLogDataFiles = false;
bool g_eventProbeLogAssetFiles = false;
unsigned long g_eventProbeMaxEntries = 5000;
unsigned long g_eventProbeMaxSaveEntries = 20000;
std::atomic<unsigned long> g_eventProbeLoggedCount{ 0 };
std::atomic<unsigned long> g_eventProbeSaveLoggedCount{ 0 };
bool g_eventProbeLimitLogged = false;
bool g_eventProbeSaveLimitLogged = false;
std::vector<std::wstring> g_eventProbeIgnorePathFragments;
std::mutex g_eventProbeMutex;
std::mutex g_observedHandlesMutex;
std::unordered_map<HANDLE, std::wstring> g_observedFileHandles;

struct ReplacementRule
{
    std::string find;
    std::string replace;
};

struct VirtualRule
{
    std::wstring targetPath;
    std::wstring sourcePath;
    std::vector<ReplacementRule> replacements;
};

bool g_virtualFileEnabled = false;
std::vector<VirtualRule> g_virtualRules;
std::wstring g_managedOverlayManifestPath;
unsigned long g_managedOverlayCount = 0;
unsigned long g_managedOverlayIssueCount = 0;

struct VirtualFile
{
    std::wstring path;
    std::vector<std::uint8_t> bytes;
    std::uint64_t position = 0;
    HANDLE backingHandle = nullptr;
};

std::mutex g_virtualFilesMutex;
std::unordered_map<HANDLE, std::shared_ptr<VirtualFile>> g_virtualFiles;
thread_local bool g_insideHook = false;

std::wstring ToLower(std::wstring value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(towlower(ch));
    });
    return value;
}

std::wstring NormalizePath(std::wstring value)
{
    std::replace(value.begin(), value.end(), L'/', L'\\');
    value = ToLower(value);

    constexpr wchar_t ntPrefix[] = L"\\\\?\\";
    if (value.rfind(ntPrefix, 0) == 0)
    {
        value.erase(0, 4);
    }
    return value;
}

bool EndsWithPath(const std::wstring& path, const std::wstring& suffix)
{
    if (suffix.empty() || path.size() < suffix.size())
    {
        return false;
    }

    if (path.compare(path.size() - suffix.size(), suffix.size(), suffix) != 0)
    {
        return false;
    }

    if (path.size() == suffix.size())
    {
        return true;
    }

    wchar_t previous = path[path.size() - suffix.size() - 1];
    return previous == L'\\';
}

bool IsFullyQualifiedPath(const std::wstring& path)
{
    if (path.size() >= 2 && path[1] == L':')
    {
        return true;
    }

    return !path.empty() && path[0] == L'\\';
}

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

unsigned long GetEnvironmentUnsignedLong(const wchar_t* name, unsigned long fallback)
{
    std::wstring value = GetEnvironmentString(name);
    if (value.empty())
    {
        return fallback;
    }

    wchar_t* end = nullptr;
    unsigned long parsed = wcstoul(value.c_str(), &end, 10);
    if (end == value.c_str())
    {
        return fallback;
    }

    return parsed;
}

bool GetEnvironmentBool(const wchar_t* name, bool fallback)
{
    std::wstring value = ToLower(GetEnvironmentString(name));
    if (value == L"1" || value == L"true" || value == L"yes" || value == L"y")
    {
        return true;
    }
    if (value == L"0" || value == L"false" || value == L"no" || value == L"n")
    {
        return false;
    }
    return fallback;
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

std::wstring AnsiToWide(LPCSTR value)
{
    if (value == nullptr || value[0] == '\0')
    {
        return L"";
    }

    int required = MultiByteToWideChar(CP_ACP, 0, value, -1, nullptr, 0);
    if (required <= 0)
    {
        return L"";
    }

    std::wstring result(static_cast<std::size_t>(required), L'\0');
    int written = MultiByteToWideChar(CP_ACP, 0, value, -1, result.data(), required);
    if (written <= 0)
    {
        return L"";
    }

    if (!result.empty() && result.back() == L'\0')
    {
        result.pop_back();
    }
    return result;
}

std::wstring ToHex(DWORD value)
{
    wchar_t buffer[16] = {};
    swprintf_s(buffer, L"0x%08lX", static_cast<unsigned long>(value));
    return buffer;
}

std::vector<std::wstring> SplitExtensions(std::wstring value)
{
    std::vector<std::wstring> extensions;
    std::size_t start = 0;
    while (start < value.size())
    {
        std::size_t end = value.find_first_of(L";,", start);
        std::wstring item = value.substr(start, end == std::wstring::npos ? std::wstring::npos : end - start);
        item.erase(std::remove_if(item.begin(), item.end(), iswspace), item.end());
        if (!item.empty())
        {
            if (item[0] != L'.')
            {
                item = L"." + item;
            }
            extensions.push_back(ToLower(item));
        }

        if (end == std::wstring::npos)
        {
            break;
        }
        start = end + 1;
    }
    return extensions;
}

std::vector<std::wstring> SplitPathFragments(std::wstring value)
{
    std::vector<std::wstring> fragments;
    std::size_t start = 0;
    while (start < value.size())
    {
        std::size_t end = value.find_first_of(L";,", start);
        std::wstring item = value.substr(start, end == std::wstring::npos ? std::wstring::npos : end - start);
        item.erase(std::remove_if(item.begin(), item.end(), iswspace), item.end());
        if (!item.empty())
        {
            fragments.push_back(NormalizePath(item));
        }

        if (end == std::wstring::npos)
        {
            break;
        }
        start = end + 1;
    }
    return fragments;
}

std::wstring ExtensionOf(const std::wstring& path)
{
    std::size_t slash = path.find_last_of(L"\\/");
    std::size_t dot = path.find_last_of(L'.');
    if (dot == std::wstring::npos || (slash != std::wstring::npos && dot < slash))
    {
        return L"";
    }
    return ToLower(path.substr(dot));
}

bool ExtensionMatches(const std::wstring& path)
{
    if (g_extensions.empty())
    {
        return true;
    }

    std::wstring extension = ExtensionOf(path);
    if (extension.empty())
    {
        return false;
    }

    return std::find(g_extensions.begin(), g_extensions.end(), extension) != g_extensions.end();
}

bool IsOneOf(const std::wstring& value, const std::vector<std::wstring>& candidates)
{
    return std::find(candidates.begin(), candidates.end(), value) != candidates.end();
}

bool IsIgnoredEventPath(const std::wstring& path)
{
    if (path.empty() || g_eventProbeIgnorePathFragments.empty())
    {
        return false;
    }

    std::wstring normalized = NormalizePath(path);
    for (const std::wstring& fragment : g_eventProbeIgnorePathFragments)
    {
        if (!fragment.empty() && normalized.find(fragment) != std::wstring::npos)
        {
            return true;
        }
    }
    return false;
}

std::wstring FileNameOf(const std::wstring& path)
{
    std::size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

std::wstring ClassifyEventPath(const std::wstring& path)
{
    std::wstring normalized = NormalizePath(path);
    std::wstring fileName = FileNameOf(normalized);
    if (normalized.empty())
    {
        return L"other";
    }

    if ((normalized.find(L"\\userdata\\") != std::wstring::npos &&
         normalized.find(L"\\262060\\remote\\profile_") != std::wstring::npos) ||
        normalized.find(L"\\documents\\darkest\\profile_") != std::wstring::npos ||
        normalized.find(L"\\remote\\profile_") != std::wstring::npos ||
        fileName.rfind(L"persist.", 0) == 0 ||
        fileName.find(L".persist") != std::wstring::npos)
    {
        return L"save";
    }

    std::wstring extension = ExtensionOf(normalized);
    static const std::vector<std::wstring> dataExtensions =
    {
        L".darkest",
        L".json",
        L".xml",
        L".loc",
        L".loc2",
        L".txt",
        L".csv"
    };
    static const std::vector<std::wstring> assetExtensions =
    {
        L".png",
        L".jpg",
        L".jpeg",
        L".dds",
        L".atlas",
        L".skel",
        L".font",
        L".ttf",
        L".otf",
        L".shader",
        L".wav",
        L".bank",
        L".mp3",
        L".ogg"
    };

    if (IsOneOf(extension, dataExtensions))
    {
        return L"data";
    }
    if (IsOneOf(extension, assetExtensions))
    {
        return L"asset";
    }
    return L"other";
}

bool ShouldLogEventCategory(const std::wstring& category)
{
    if (category == L"save")
    {
        return g_eventProbeLogSaveFiles;
    }
    if (category == L"data")
    {
        return g_eventProbeLogDataFiles;
    }
    if (category == L"asset")
    {
        return g_eventProbeLogAssetFiles;
    }
    return false;
}

std::wstring EventName(const std::wstring& category, bool writeAttempt)
{
    if (category == L"save")
    {
        return writeAttempt ? L"save.file_write_attempted" : L"save.file_opened";
    }
    if (category == L"data")
    {
        return writeAttempt ? L"data.file_write_attempted" : L"data.file_opened";
    }
    if (category == L"asset")
    {
        return writeAttempt ? L"asset.file_write_attempted" : L"asset.file_opened";
    }
    return writeAttempt ? L"file.write_attempted" : L"file.opened";
}

std::wstring LifecycleEventName(const std::wstring& category, const std::wstring& operation)
{
    std::wstring prefix = category == L"other" ? L"file" : category;
    return prefix + L".file_" + operation + L"_attempted";
}

std::wstring ChooseLifecycleCategory(const std::vector<std::wstring>& paths)
{
    std::wstring fallback = L"other";
    for (const std::wstring& path : paths)
    {
        if (path.empty() || IsIgnoredEventPath(path))
        {
            continue;
        }

        std::wstring category = ClassifyEventPath(path);
        if (category == L"save")
        {
            return category;
        }
        if (fallback == L"other" && category != L"other")
        {
            fallback = category;
        }
    }
    return fallback;
}

std::wstring DispositionName(DWORD disposition);

bool ReserveEventProbeLogEntry(const std::wstring& category)
{
    std::lock_guard<std::mutex> lock(g_eventProbeMutex);
    if (category == L"save")
    {
        if (g_eventProbeMaxSaveEntries > 0 && g_eventProbeSaveLoggedCount.load() >= g_eventProbeMaxSaveEntries)
        {
            if (!g_eventProbeSaveLimitLogged)
            {
                g_eventProbeSaveLimitLogged = true;
                Logger::Warn(L"Event probe save log limit reached. Further save event entries are suppressed.");
            }
            return false;
        }

        g_eventProbeSaveLoggedCount.fetch_add(1);
        return true;
    }

    if (g_eventProbeMaxEntries > 0 && g_eventProbeLoggedCount.load() >= g_eventProbeMaxEntries)
    {
        if (!g_eventProbeLimitLogged)
        {
            g_eventProbeLimitLogged = true;
            Logger::Warn(L"Event probe log limit reached. Further event entries are suppressed.");
        }
        return false;
    }

    g_eventProbeLoggedCount.fetch_add(1);
    return true;
}

void LogEventProbeFileOpen(const std::wstring& path, DWORD desiredAccess, DWORD creationDisposition)
{
    if (!g_eventProbeEnabled || !g_eventProbeLogFileOpen || path.empty() || IsIgnoredEventPath(path))
    {
        return;
    }

    std::wstring category = ClassifyEventPath(path);
    if (!ShouldLogEventCategory(category) || !ReserveEventProbeLogEntry(category))
    {
        return;
    }

    Logger::Info(
        L"event name=" + EventName(category, false) +
        L" category=" + category +
        L" disposition=" + DispositionName(creationDisposition) +
        L" access=" + ToHex(desiredAccess) +
        L" path=" + path);
}

void LogEventProbeFileWrite(const std::wstring& path, DWORD bytesToWrite)
{
    if (!g_eventProbeEnabled || !g_eventProbeLogFileWrite || path.empty() || IsIgnoredEventPath(path))
    {
        return;
    }

    std::wstring category = ClassifyEventPath(path);
    if (!ShouldLogEventCategory(category) || !ReserveEventProbeLogEntry(category))
    {
        return;
    }

    Logger::Info(
        L"event name=" + EventName(category, true) +
        L" category=" + category +
        L" bytes=" + std::to_wstring(bytesToWrite) +
        L" path=" + path);
}

void LogEventProbePathOperation(
    const std::wstring& operation,
    const std::wstring& path,
    const std::wstring& details = L"")
{
    if (!g_eventProbeEnabled || !g_eventProbeLogFileWrite || path.empty() || IsIgnoredEventPath(path))
    {
        return;
    }

    std::wstring category = ClassifyEventPath(path);
    if (!ShouldLogEventCategory(category) || !ReserveEventProbeLogEntry(category))
    {
        return;
    }

    Logger::Info(
        L"event name=" + LifecycleEventName(category, operation) +
        L" category=" + category +
        (details.empty() ? L"" : L" " + details) +
        L" path=" + path);
}

void LogEventProbeTwoPathOperation(
    const std::wstring& operation,
    const std::wstring& sourcePath,
    const std::wstring& targetPath,
    const std::wstring& details = L"")
{
    if (!g_eventProbeEnabled || !g_eventProbeLogFileWrite)
    {
        return;
    }

    std::wstring category = ChooseLifecycleCategory({ sourcePath, targetPath });
    if (!ShouldLogEventCategory(category) || !ReserveEventProbeLogEntry(category))
    {
        return;
    }

    Logger::Info(
        L"event name=" + LifecycleEventName(category, operation) +
        L" category=" + category +
        (details.empty() ? L"" : L" " + details) +
        L" source=" + sourcePath +
        L" target=" + targetPath);
}

void LogEventProbeReplaceOperation(
    const std::wstring& replacedPath,
    const std::wstring& replacementPath,
    const std::wstring& backupPath,
    DWORD flags)
{
    if (!g_eventProbeEnabled || !g_eventProbeLogFileWrite)
    {
        return;
    }

    std::wstring category = ChooseLifecycleCategory({ replacedPath, replacementPath, backupPath });
    if (!ShouldLogEventCategory(category) || !ReserveEventProbeLogEntry(category))
    {
        return;
    }

    Logger::Info(
        L"event name=" + LifecycleEventName(category, L"replace") +
        L" category=" + category +
        L" flags=" + ToHex(flags) +
        L" replaced=" + replacedPath +
        L" replacement=" + replacementPath +
        L" backup=" + backupPath);
}

bool RequestedWriteAccess(DWORD desiredAccess)
{
    constexpr DWORD writeAccess =
        GENERIC_WRITE |
        FILE_WRITE_DATA |
        FILE_APPEND_DATA |
        FILE_WRITE_ATTRIBUTES |
        FILE_WRITE_EA;
    return (desiredAccess & writeAccess) != 0;
}

void RecordObservedFileHandle(HANDLE handle, const std::wstring& path, DWORD desiredAccess)
{
    if (!g_eventProbeEnabled ||
        !g_eventProbeLogFileWrite ||
        !RequestedWriteAccess(desiredAccess) ||
        handle == INVALID_HANDLE_VALUE ||
        handle == nullptr ||
        path.empty() ||
        IsIgnoredEventPath(path))
    {
        return;
    }

    std::lock_guard<std::mutex> lock(g_observedHandlesMutex);
    g_observedFileHandles[handle] = path;
}

std::wstring GetObservedFileHandlePath(HANDLE handle)
{
    std::lock_guard<std::mutex> lock(g_observedHandlesMutex);
    auto it = g_observedFileHandles.find(handle);
    return it == g_observedFileHandles.end() ? L"" : it->second;
}

void ForgetObservedFileHandle(HANDLE handle)
{
    std::lock_guard<std::mutex> lock(g_observedHandlesMutex);
    g_observedFileHandles.erase(handle);
}

std::wstring DispositionName(DWORD disposition)
{
    switch (disposition)
    {
    case CREATE_NEW:
        return L"CREATE_NEW";
    case CREATE_ALWAYS:
        return L"CREATE_ALWAYS";
    case OPEN_EXISTING:
        return L"OPEN_EXISTING";
    case OPEN_ALWAYS:
        return L"OPEN_ALWAYS";
    case TRUNCATE_EXISTING:
        return L"TRUNCATE_EXISTING";
    default:
        return L"DISPOSITION_" + std::to_wstring(disposition);
    }
}

std::wstring StatusToWide(MH_STATUS status)
{
    const char* message = MH_StatusToString(status);
    if (message == nullptr)
    {
        return L"status=" + std::to_wstring(static_cast<int>(status));
    }
    return AnsiToWide(message);
}

std::wstring DescribeManagedOverlayManifest()
{
    if (g_managedOverlayManifestPath.empty())
    {
        return L"none";
    }

    WIN32_FILE_ATTRIBUTE_DATA data = {};
    if (!GetFileAttributesExW(g_managedOverlayManifestPath.c_str(), GetFileExInfoStandard, &data))
    {
        return L"path=\"" + g_managedOverlayManifestPath + L"\" exists=0 overlays=" + std::to_wstring(g_managedOverlayCount);
    }

    ULARGE_INTEGER size = {};
    size.LowPart = data.nFileSizeLow;
    size.HighPart = data.nFileSizeHigh;
    return
        L"path=\"" + g_managedOverlayManifestPath +
        L"\" exists=1 bytes=" + std::to_wstring(size.QuadPart) +
        L" overlays=" + std::to_wstring(g_managedOverlayCount) +
        L" issues=" + std::to_wstring(g_managedOverlayIssueCount);
}

void LoadSettings()
{
    g_extensions = SplitExtensions(GetEnvironmentString(L"DD_RUNTIME_FILE_IO_LOG_EXTENSIONS"));
    g_maxEntries = GetEnvironmentUnsignedLong(L"DD_RUNTIME_FILE_IO_MAX_ENTRIES", 2000);
    g_deduplicate = GetEnvironmentBool(L"DD_RUNTIME_FILE_IO_DEDUPLICATE", true);

    g_eventProbeEnabled = GetEnvironmentBool(L"DD_RUNTIME_EVENT_PROBE_ENABLED", true);
    g_eventProbeLogFileOpen = GetEnvironmentBool(L"DD_RUNTIME_EVENT_PROBE_LOG_FILE_OPEN", true);
    g_eventProbeLogFileWrite = GetEnvironmentBool(L"DD_RUNTIME_EVENT_PROBE_LOG_FILE_WRITE", true);
    g_eventProbeLogSaveFiles = GetEnvironmentBool(L"DD_RUNTIME_EVENT_PROBE_LOG_SAVE_FILES", true);
    g_eventProbeLogDataFiles = GetEnvironmentBool(L"DD_RUNTIME_EVENT_PROBE_LOG_DATA_FILES", false);
    g_eventProbeLogAssetFiles = GetEnvironmentBool(L"DD_RUNTIME_EVENT_PROBE_LOG_ASSET_FILES", false);
    g_eventProbeMaxEntries = GetEnvironmentUnsignedLong(L"DD_RUNTIME_EVENT_PROBE_MAX_ENTRIES", 5000);
    g_eventProbeMaxSaveEntries = GetEnvironmentUnsignedLong(L"DD_RUNTIME_EVENT_PROBE_MAX_SAVE_ENTRIES", 20000);
    g_eventProbeIgnorePathFragments = SplitPathFragments(GetEnvironmentString(L"DD_RUNTIME_EVENT_PROBE_IGNORE_PATH_FRAGMENTS"));

    g_virtualFileEnabled = GetEnvironmentBool(L"DD_RUNTIME_VIRTUAL_FILE_ENABLED", false);
    g_virtualRules.clear();

    g_managedOverlayManifestPath = GetEnvironmentString(L"DD_RUNTIME_MANAGED_OVERLAY_MANIFEST");
    g_managedOverlayCount = GetEnvironmentUnsignedLong(L"DD_RUNTIME_MANAGED_OVERLAY_COUNT", 0);
    g_managedOverlayIssueCount = GetEnvironmentUnsignedLong(L"DD_RUNTIME_MANAGED_OVERLAY_ISSUE_COUNT", 0);

    unsigned long ruleCount = GetEnvironmentUnsignedLong(L"DD_RUNTIME_VIRTUAL_RULE_COUNT", 0);
    for (unsigned long ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
    {
        std::wstring prefix = L"DD_RUNTIME_VIRTUAL_RULE_" + std::to_wstring(ruleIndex);
        VirtualRule rule;
        rule.targetPath = NormalizePath(GetEnvironmentString((prefix + L"_TARGET").c_str()));
        rule.sourcePath = NormalizePath(GetEnvironmentString((prefix + L"_SOURCE_PATH").c_str()));
        if (rule.targetPath.empty())
        {
            continue;
        }

        unsigned long replacementCount = GetEnvironmentUnsignedLong((prefix + L"_REPLACEMENT_COUNT").c_str(), 0);
        for (unsigned long replacementIndex = 0; replacementIndex < replacementCount; replacementIndex++)
        {
            std::wstring replacementPrefix = prefix + L"_REPLACEMENT_" + std::to_wstring(replacementIndex);
            ReplacementRule replacement;
            replacement.find = WideToUtf8(GetEnvironmentString((replacementPrefix + L"_FIND").c_str()));
            replacement.replace = WideToUtf8(GetEnvironmentString((replacementPrefix + L"_REPLACE").c_str()));
            if (!replacement.find.empty())
            {
                rule.replacements.push_back(std::move(replacement));
            }
        }

        if (!rule.sourcePath.empty() || !rule.replacements.empty())
        {
            g_virtualRules.push_back(std::move(rule));
        }
    }
}

void LogFileOpen(const std::wstring& path, DWORD desiredAccess, DWORD creationDisposition)
{
    if (path.empty() || !ExtensionMatches(path))
    {
        return;
    }

    std::wstring normalized = ToLower(path);
    {
        std::lock_guard<std::mutex> lock(g_observerMutex);

        if (g_maxEntries > 0 && g_loggedCount.load() >= g_maxEntries)
        {
            if (!g_limitLogged)
            {
                g_limitLogged = true;
                Logger::Warn(L"File IO log limit reached. Further file-open events are suppressed.");
            }
            return;
        }

        if (g_deduplicate && !g_seenPaths.insert(normalized).second)
        {
            return;
        }

        g_loggedCount.fetch_add(1);
    }

    Logger::Info(
        L"file-open disposition=" + DispositionName(creationDisposition) +
        L" access=" + ToHex(desiredAccess) +
        L" path=" + path);
}

CreateFileWFn OriginalCreateFileW()
{
    return g_originalKernelBaseCreateFileW ? g_originalKernelBaseCreateFileW : g_originalKernel32CreateFileW;
}

ReadFileFn OriginalReadFile()
{
    return g_originalKernelBaseReadFile ? g_originalKernelBaseReadFile : g_originalKernel32ReadFile;
}

WriteFileFn OriginalWriteFile()
{
    return g_originalKernelBaseWriteFile ? g_originalKernelBaseWriteFile : g_originalKernel32WriteFile;
}

CloseHandleFn OriginalCloseHandle()
{
    return g_originalKernelBaseCloseHandle ? g_originalKernelBaseCloseHandle : g_originalKernel32CloseHandle;
}

GetFileSizeExFn OriginalGetFileSizeEx()
{
    return g_originalKernelBaseGetFileSizeEx ? g_originalKernelBaseGetFileSizeEx : g_originalKernel32GetFileSizeEx;
}

SetFilePointerExFn OriginalSetFilePointerEx()
{
    return g_originalKernelBaseSetFilePointerEx ? g_originalKernelBaseSetFilePointerEx : g_originalKernel32SetFilePointerEx;
}

const VirtualRule* FindVirtualRule(const std::wstring& path, DWORD desiredAccess, DWORD creationDisposition)
{
    if (!g_virtualFileEnabled || g_virtualRules.empty())
    {
        return nullptr;
    }

    if (creationDisposition != OPEN_EXISTING)
    {
        return nullptr;
    }

    if ((desiredAccess & GENERIC_WRITE) != 0)
    {
        return nullptr;
    }

    std::wstring normalizedPath = NormalizePath(path);
    for (const VirtualRule& rule : g_virtualRules)
    {
        if (normalizedPath == rule.targetPath ||
            (IsFullyQualifiedPath(normalizedPath) && EndsWithPath(normalizedPath, rule.targetPath)))
        {
            return &rule;
        }
    }

    return nullptr;
}

bool ReadOriginalFileBytes(const std::wstring& path, std::vector<std::uint8_t>& bytes)
{
    CreateFileWFn createFile = OriginalCreateFileW();
    ReadFileFn readFile = OriginalReadFile();
    CloseHandleFn closeHandle = OriginalCloseHandle();
    GetFileSizeExFn getFileSizeEx = OriginalGetFileSizeEx();
    if (createFile == nullptr || readFile == nullptr || closeHandle == nullptr || getFileSizeEx == nullptr)
    {
        return false;
    }

    HANDLE file = createFile(
        path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    LARGE_INTEGER size = {};
    if (!getFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 16 * 1024 * 1024)
    {
        closeHandle(file);
        return false;
    }

    bytes.resize(static_cast<std::size_t>(size.QuadPart));
    std::size_t offset = 0;
    while (offset < bytes.size())
    {
        DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(bytes.size() - offset, 64 * 1024));
        DWORD bytesRead = 0;
        if (!readFile(file, bytes.data() + offset, chunk, &bytesRead, nullptr))
        {
            closeHandle(file);
            return false;
        }
        if (bytesRead == 0)
        {
            break;
        }
        offset += bytesRead;
    }
    bytes.resize(offset);
    closeHandle(file);
    return true;
}

std::size_t ReplaceAll(std::vector<std::uint8_t>& bytes, const std::string& find, const std::string& replace)
{
    if (find.empty())
    {
        return 0;
    }

    std::string text(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    std::size_t replacements = 0;
    std::size_t position = 0;
    while ((position = text.find(find, position)) != std::string::npos)
    {
        text.replace(position, find.size(), replace);
        position += replace.size();
        replacements++;
    }

    bytes.assign(text.begin(), text.end());
    return replacements;
}

std::wstring BuildVirtualTempFilePath()
{
    wchar_t tempDirectory[MAX_PATH] = {};
    std::wstring directory = GetEnvironmentString(L"DD_RUNTIME_LOG_DIR");
    if (directory.empty() || directory.size() >= MAX_PATH - 64)
    {
        DWORD length = GetTempPathW(MAX_PATH, tempDirectory);
        directory = length == 0 || length >= MAX_PATH ? L"." : std::wstring(tempDirectory, length);
    }

    if (!directory.empty() && directory.back() != L'\\' && directory.back() != L'/')
    {
        directory.push_back(L'\\');
    }

    wchar_t fileName[MAX_PATH] = {};
    if (GetTempFileNameW(directory.c_str(), L"ddr", 0, fileName) == 0)
    {
        Logger::Warn(
            L"virtual-file failed to create temp path directory=" + directory +
            L" error=" + std::to_wstring(GetLastError()));
        return {};
    }

    return fileName;
}

HANDLE CreateVirtualBackingFile(const std::vector<std::uint8_t>& bytes)
{
    CreateFileWFn createFile = OriginalCreateFileW();
    WriteFileFn writeFile = OriginalWriteFile();
    SetFilePointerExFn setFilePointerEx = OriginalSetFilePointerEx();
    CloseHandleFn closeHandle = OriginalCloseHandle();
    if (createFile == nullptr || writeFile == nullptr || setFilePointerEx == nullptr || closeHandle == nullptr)
    {
        Logger::Warn(
            L"virtual-file backing file API unavailable createFile=" + std::to_wstring(createFile != nullptr) +
            L" writeFile=" + std::to_wstring(writeFile != nullptr) +
            L" setFilePointerEx=" + std::to_wstring(setFilePointerEx != nullptr) +
            L" closeHandle=" + std::to_wstring(closeHandle != nullptr));
        return nullptr;
    }

    std::wstring tempPath = BuildVirtualTempFilePath();
    if (tempPath.empty())
    {
        return nullptr;
    }

    HANDLE file = createFile(
        tempPath.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        Logger::Warn(
            L"virtual-file failed to open backing temp file path=" + tempPath +
            L" error=" + std::to_wstring(GetLastError()));
        DeleteFileW(tempPath.c_str());
        return nullptr;
    }

    std::size_t offset = 0;
    while (offset < bytes.size())
    {
        DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(bytes.size() - offset, 64 * 1024));
        DWORD bytesWritten = 0;
        if (!writeFile(file, bytes.data() + offset, chunk, &bytesWritten, nullptr) || bytesWritten != chunk)
        {
            Logger::Warn(
                L"virtual-file failed to write backing temp file path=" + tempPath +
                L" requested=" + std::to_wstring(chunk) +
                L" written=" + std::to_wstring(bytesWritten) +
                L" error=" + std::to_wstring(GetLastError()));
            closeHandle(file);
            return nullptr;
        }
        offset += bytesWritten;
    }

    LARGE_INTEGER zero = {};
    if (!setFilePointerEx(file, zero, nullptr, FILE_BEGIN))
    {
        Logger::Warn(
            L"virtual-file failed to rewind backing temp file path=" + tempPath +
            L" error=" + std::to_wstring(GetLastError()));
        closeHandle(file);
        return nullptr;
    }

    return file;
}

HANDLE CreateVirtualFileHandle(const std::wstring& path, DWORD desiredAccess, DWORD creationDisposition)
{
    const VirtualRule* rule = FindVirtualRule(path, desiredAccess, creationDisposition);
    if (rule == nullptr)
    {
        return INVALID_HANDLE_VALUE;
    }

    std::vector<std::uint8_t> bytes;
    std::wstring sourcePath = rule->sourcePath.empty() ? path : rule->sourcePath;
    if (!ReadOriginalFileBytes(sourcePath, bytes))
    {
        if (rule->sourcePath.empty())
        {
            Logger::Warn(L"virtual-file failed to read original: " + path);
        }
        else
        {
            Logger::Warn(L"virtual-file failed to read source: target=" + path + L" source=" + sourcePath);
        }
        return INVALID_HANDLE_VALUE;
    }

    std::size_t sourceSize = bytes.size();
    std::size_t replacements = 0;
    for (const ReplacementRule& replacement : rule->replacements)
    {
        replacements += ReplaceAll(bytes, replacement.find, replacement.replace);
    }

    if (!rule->replacements.empty() && replacements == 0)
    {
        Logger::Warn(L"virtual-file rule matched but no replacement text was found: " + path);
        return INVALID_HANDLE_VALUE;
    }

    HANDLE backingFile = CreateVirtualBackingFile(bytes);
    if (backingFile == nullptr || backingFile == INVALID_HANDLE_VALUE)
    {
        Logger::Warn(L"virtual-file failed to allocate backing file: " + path);
        return INVALID_HANDLE_VALUE;
    }

    auto virtualFile = std::make_shared<VirtualFile>();
    virtualFile->path = path;
    virtualFile->bytes = std::move(bytes);
    virtualFile->position = 0;
    virtualFile->backingHandle = backingFile;

    {
        std::lock_guard<std::mutex> lock(g_virtualFilesMutex);
        g_virtualFiles[backingFile] = virtualFile;
    }

    Logger::Info(
        L"virtual-file served path=" + path +
        L" mode=" + (rule->sourcePath.empty() ? L"replacement" : L"sourcePath") +
        (rule->sourcePath.empty() ? L"" : L" source=" + sourcePath) +
        L" sourceBytes=" + std::to_wstring(sourceSize) +
        L" virtualBytes=" + std::to_wstring(virtualFile->bytes.size()) +
        L" replacements=" + std::to_wstring(replacements));
    return backingFile;
}

std::shared_ptr<VirtualFile> GetVirtualFile(HANDLE handle)
{
    std::lock_guard<std::mutex> lock(g_virtualFilesMutex);
    auto it = g_virtualFiles.find(handle);
    return it == g_virtualFiles.end() ? nullptr : it->second;
}

std::shared_ptr<VirtualFile> RemoveVirtualFile(HANDLE handle)
{
    std::lock_guard<std::mutex> lock(g_virtualFilesMutex);
    auto it = g_virtualFiles.find(handle);
    if (it == g_virtualFiles.end())
    {
        return nullptr;
    }

    auto value = it->second;
    g_virtualFiles.erase(it);
    return value;
}

HANDLE CallOriginalCreateFileW(
    CreateFileWFn original,
    LPCWSTR fileName,
    DWORD desiredAccess,
    DWORD shareMode,
    LPSECURITY_ATTRIBUTES securityAttributes,
    DWORD creationDisposition,
    DWORD flagsAndAttributes,
    HANDLE templateFile)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return INVALID_HANDLE_VALUE;
    }

    if (g_insideHook)
    {
        return original(fileName, desiredAccess, shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile);
    }

    g_insideHook = true;
    std::wstring path = fileName == nullptr ? L"" : fileName;
    LogFileOpen(path, desiredAccess, creationDisposition);
    HANDLE virtualHandle = CreateVirtualFileHandle(path, desiredAccess, creationDisposition);
    HANDLE result = virtualHandle != INVALID_HANDLE_VALUE
        ? virtualHandle
        : original(fileName, desiredAccess, shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile);
    if (result != INVALID_HANDLE_VALUE && result != nullptr)
    {
        LogEventProbeFileOpen(path, desiredAccess, creationDisposition);
    }
    if (virtualHandle == INVALID_HANDLE_VALUE)
    {
        RecordObservedFileHandle(result, path, desiredAccess);
    }
    g_insideHook = false;
    return result;
}

HANDLE CallOriginalCreateFileA(
    CreateFileAFn original,
    LPCSTR fileName,
    DWORD desiredAccess,
    DWORD shareMode,
    LPSECURITY_ATTRIBUTES securityAttributes,
    DWORD creationDisposition,
    DWORD flagsAndAttributes,
    HANDLE templateFile)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return INVALID_HANDLE_VALUE;
    }

    if (g_insideHook)
    {
        return original(fileName, desiredAccess, shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile);
    }

    g_insideHook = true;
    std::wstring path = AnsiToWide(fileName);
    LogFileOpen(path, desiredAccess, creationDisposition);
    HANDLE virtualHandle = CreateVirtualFileHandle(path, desiredAccess, creationDisposition);
    HANDLE result = virtualHandle != INVALID_HANDLE_VALUE
        ? virtualHandle
        : original(fileName, desiredAccess, shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile);
    if (result != INVALID_HANDLE_VALUE && result != nullptr)
    {
        LogEventProbeFileOpen(path, desiredAccess, creationDisposition);
    }
    if (virtualHandle == INVALID_HANDLE_VALUE)
    {
        RecordObservedFileHandle(result, path, desiredAccess);
    }
    g_insideHook = false;
    return result;
}

BOOL CallOriginalReadFile(ReadFileFn original, HANDLE handle, LPVOID buffer, DWORD bytesToRead, LPDWORD bytesRead, LPOVERLAPPED overlapped)
{
    auto virtualFile = GetVirtualFile(handle);
    if (!virtualFile)
    {
        return original ? original(handle, buffer, bytesToRead, bytesRead, overlapped) : FALSE;
    }

    std::lock_guard<std::mutex> lock(g_virtualFilesMutex);
    std::uint64_t offset = virtualFile->position;
    if (overlapped != nullptr)
    {
        offset = (static_cast<std::uint64_t>(overlapped->OffsetHigh) << 32) | overlapped->Offset;
    }

    std::uint64_t available = offset < virtualFile->bytes.size() ? virtualFile->bytes.size() - offset : 0;
    DWORD count = static_cast<DWORD>(std::min<std::uint64_t>(bytesToRead, available));
    if (count > 0 && buffer != nullptr)
    {
        std::memcpy(buffer, virtualFile->bytes.data() + offset, count);
    }

    if (overlapped == nullptr)
    {
        virtualFile->position = offset + count;
    }
    else
    {
        overlapped->Internal = ERROR_SUCCESS;
        overlapped->InternalHigh = count;
    }

    if (bytesRead != nullptr)
    {
        *bytesRead = count;
    }
    return TRUE;
}

BOOL CallOriginalWriteFile(WriteFileFn original, HANDLE handle, LPCVOID buffer, DWORD bytesToWrite, LPDWORD bytesWritten, LPOVERLAPPED overlapped)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(handle, buffer, bytesToWrite, bytesWritten, overlapped);
    }

    g_insideHook = true;
    LogEventProbeFileWrite(GetObservedFileHandlePath(handle), bytesToWrite);
    BOOL result = original(handle, buffer, bytesToWrite, bytesWritten, overlapped);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalMoveFileW(MoveFileWFn original, LPCWSTR existingFileName, LPCWSTR newFileName)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(existingFileName, newFileName);
    }

    g_insideHook = true;
    LogEventProbeTwoPathOperation(L"move", existingFileName == nullptr ? L"" : existingFileName, newFileName == nullptr ? L"" : newFileName);
    BOOL result = original(existingFileName, newFileName);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalMoveFileA(MoveFileAFn original, LPCSTR existingFileName, LPCSTR newFileName)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(existingFileName, newFileName);
    }

    g_insideHook = true;
    LogEventProbeTwoPathOperation(L"move", AnsiToWide(existingFileName), AnsiToWide(newFileName));
    BOOL result = original(existingFileName, newFileName);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalMoveFileExW(MoveFileExWFn original, LPCWSTR existingFileName, LPCWSTR newFileName, DWORD flags)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(existingFileName, newFileName, flags);
    }

    g_insideHook = true;
    LogEventProbeTwoPathOperation(
        L"move",
        existingFileName == nullptr ? L"" : existingFileName,
        newFileName == nullptr ? L"" : newFileName,
        L"flags=" + ToHex(flags));
    BOOL result = original(existingFileName, newFileName, flags);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalMoveFileExA(MoveFileExAFn original, LPCSTR existingFileName, LPCSTR newFileName, DWORD flags)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(existingFileName, newFileName, flags);
    }

    g_insideHook = true;
    LogEventProbeTwoPathOperation(L"move", AnsiToWide(existingFileName), AnsiToWide(newFileName), L"flags=" + ToHex(flags));
    BOOL result = original(existingFileName, newFileName, flags);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalCopyFileW(CopyFileWFn original, LPCWSTR existingFileName, LPCWSTR newFileName, BOOL failIfExists)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(existingFileName, newFileName, failIfExists);
    }

    g_insideHook = true;
    LogEventProbeTwoPathOperation(
        L"copy",
        existingFileName == nullptr ? L"" : existingFileName,
        newFileName == nullptr ? L"" : newFileName,
        L"failIfExists=" + std::wstring(failIfExists ? L"true" : L"false"));
    BOOL result = original(existingFileName, newFileName, failIfExists);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalCopyFileA(CopyFileAFn original, LPCSTR existingFileName, LPCSTR newFileName, BOOL failIfExists)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(existingFileName, newFileName, failIfExists);
    }

    g_insideHook = true;
    LogEventProbeTwoPathOperation(
        L"copy",
        AnsiToWide(existingFileName),
        AnsiToWide(newFileName),
        L"failIfExists=" + std::wstring(failIfExists ? L"true" : L"false"));
    BOOL result = original(existingFileName, newFileName, failIfExists);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalDeleteFileW(DeleteFileWFn original, LPCWSTR fileName)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(fileName);
    }

    g_insideHook = true;
    LogEventProbePathOperation(L"delete", fileName == nullptr ? L"" : fileName);
    BOOL result = original(fileName);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalDeleteFileA(DeleteFileAFn original, LPCSTR fileName)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(fileName);
    }

    g_insideHook = true;
    LogEventProbePathOperation(L"delete", AnsiToWide(fileName));
    BOOL result = original(fileName);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalReplaceFileW(
    ReplaceFileWFn original,
    LPCWSTR replacedFileName,
    LPCWSTR replacementFileName,
    LPCWSTR backupFileName,
    DWORD flags,
    LPVOID exclude,
    LPVOID reserved)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(replacedFileName, replacementFileName, backupFileName, flags, exclude, reserved);
    }

    g_insideHook = true;
    LogEventProbeReplaceOperation(
        replacedFileName == nullptr ? L"" : replacedFileName,
        replacementFileName == nullptr ? L"" : replacementFileName,
        backupFileName == nullptr ? L"" : backupFileName,
        flags);
    BOOL result = original(replacedFileName, replacementFileName, backupFileName, flags, exclude, reserved);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalReplaceFileA(
    ReplaceFileAFn original,
    LPCSTR replacedFileName,
    LPCSTR replacementFileName,
    LPCSTR backupFileName,
    DWORD flags,
    LPVOID exclude,
    LPVOID reserved)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(replacedFileName, replacementFileName, backupFileName, flags, exclude, reserved);
    }

    g_insideHook = true;
    LogEventProbeReplaceOperation(AnsiToWide(replacedFileName), AnsiToWide(replacementFileName), AnsiToWide(backupFileName), flags);
    BOOL result = original(replacedFileName, replacementFileName, backupFileName, flags, exclude, reserved);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalSetFileAttributesW(SetFileAttributesWFn original, LPCWSTR fileName, DWORD fileAttributes)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(fileName, fileAttributes);
    }

    g_insideHook = true;
    LogEventProbePathOperation(
        L"set_attributes",
        fileName == nullptr ? L"" : fileName,
        L"attributes=" + ToHex(fileAttributes));
    BOOL result = original(fileName, fileAttributes);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalSetFileAttributesA(SetFileAttributesAFn original, LPCSTR fileName, DWORD fileAttributes)
{
    if (original == nullptr)
    {
        SetLastError(ERROR_INVALID_FUNCTION);
        return FALSE;
    }

    if (g_insideHook)
    {
        return original(fileName, fileAttributes);
    }

    g_insideHook = true;
    LogEventProbePathOperation(L"set_attributes", AnsiToWide(fileName), L"attributes=" + ToHex(fileAttributes));
    BOOL result = original(fileName, fileAttributes);
    g_insideHook = false;
    return result;
}

BOOL CallOriginalCloseHandle(CloseHandleFn original, HANDLE handle)
{
    auto virtualFile = RemoveVirtualFile(handle);
    if (!virtualFile)
    {
        ForgetObservedFileHandle(handle);
        return original ? original(handle) : FALSE;
    }

    ForgetObservedFileHandle(handle);
    CloseHandleFn closeHandle = OriginalCloseHandle();
    if (closeHandle != nullptr && virtualFile->backingHandle != nullptr)
    {
        closeHandle(virtualFile->backingHandle);
    }
    Logger::Info(L"virtual-file closed path=" + virtualFile->path);
    return TRUE;
}

DWORD CallOriginalGetFileSize(GetFileSizeFn original, HANDLE handle, LPDWORD highSize)
{
    auto virtualFile = GetVirtualFile(handle);
    if (!virtualFile)
    {
        return original ? original(handle, highSize) : INVALID_FILE_SIZE;
    }

    std::uint64_t size = virtualFile->bytes.size();
    if (highSize != nullptr)
    {
        *highSize = static_cast<DWORD>(size >> 32);
    }
    SetLastError(NO_ERROR);
    return static_cast<DWORD>(size & 0xFFFFFFFF);
}

BOOL CallOriginalGetFileSizeEx(GetFileSizeExFn original, HANDLE handle, PLARGE_INTEGER size)
{
    auto virtualFile = GetVirtualFile(handle);
    if (!virtualFile)
    {
        return original ? original(handle, size) : FALSE;
    }

    if (size != nullptr)
    {
        size->QuadPart = static_cast<LONGLONG>(virtualFile->bytes.size());
    }
    return TRUE;
}

std::uint64_t SeekVirtualFile(VirtualFile& virtualFile, LARGE_INTEGER distance, DWORD moveMethod, bool& ok)
{
    LONGLONG base = 0;
    switch (moveMethod)
    {
    case FILE_BEGIN:
        base = 0;
        break;
    case FILE_CURRENT:
        base = static_cast<LONGLONG>(virtualFile.position);
        break;
    case FILE_END:
        base = static_cast<LONGLONG>(virtualFile.bytes.size());
        break;
    default:
        ok = false;
        return virtualFile.position;
    }

    LONGLONG next = base + distance.QuadPart;
    if (next < 0)
    {
        ok = false;
        return virtualFile.position;
    }

    ok = true;
    virtualFile.position = static_cast<std::uint64_t>(next);
    return virtualFile.position;
}

DWORD CallOriginalSetFilePointer(SetFilePointerFn original, HANDLE handle, LONG distanceLow, PLONG distanceHigh, DWORD moveMethod)
{
    auto virtualFile = GetVirtualFile(handle);
    if (!virtualFile)
    {
        return original ? original(handle, distanceLow, distanceHigh, moveMethod) : INVALID_SET_FILE_POINTER;
    }

    LARGE_INTEGER distance = {};
    distance.LowPart = static_cast<DWORD>(distanceLow);
    distance.HighPart = distanceHigh == nullptr ? (distanceLow < 0 ? -1 : 0) : *distanceHigh;

    bool ok = false;
    std::uint64_t position = SeekVirtualFile(*virtualFile, distance, moveMethod, ok);
    if (!ok)
    {
        SetLastError(ERROR_NEGATIVE_SEEK);
        return INVALID_SET_FILE_POINTER;
    }

    if (distanceHigh != nullptr)
    {
        *distanceHigh = static_cast<LONG>(position >> 32);
    }
    SetLastError(NO_ERROR);
    return static_cast<DWORD>(position & 0xFFFFFFFF);
}

BOOL CallOriginalSetFilePointerEx(SetFilePointerExFn original, HANDLE handle, LARGE_INTEGER distance, PLARGE_INTEGER newPosition, DWORD moveMethod)
{
    auto virtualFile = GetVirtualFile(handle);
    if (!virtualFile)
    {
        return original ? original(handle, distance, newPosition, moveMethod) : FALSE;
    }

    bool ok = false;
    std::uint64_t position = SeekVirtualFile(*virtualFile, distance, moveMethod, ok);
    if (!ok)
    {
        SetLastError(ERROR_NEGATIVE_SEEK);
        return FALSE;
    }

    if (newPosition != nullptr)
    {
        newPosition->QuadPart = static_cast<LONGLONG>(position);
    }
    return TRUE;
}

#define DEFINE_CREATEFILEW_DETOUR(name, original) \
HANDLE WINAPI name(LPCWSTR fileName, DWORD desiredAccess, DWORD shareMode, LPSECURITY_ATTRIBUTES securityAttributes, DWORD creationDisposition, DWORD flagsAndAttributes, HANDLE templateFile) \
{ \
    return CallOriginalCreateFileW(original, fileName, desiredAccess, shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile); \
}

#define DEFINE_CREATEFILEA_DETOUR(name, original) \
HANDLE WINAPI name(LPCSTR fileName, DWORD desiredAccess, DWORD shareMode, LPSECURITY_ATTRIBUTES securityAttributes, DWORD creationDisposition, DWORD flagsAndAttributes, HANDLE templateFile) \
{ \
    return CallOriginalCreateFileA(original, fileName, desiredAccess, shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile); \
}

#define DEFINE_READFILE_DETOUR(name, original) \
BOOL WINAPI name(HANDLE file, LPVOID buffer, DWORD bytesToRead, LPDWORD bytesRead, LPOVERLAPPED overlapped) \
{ \
    return CallOriginalReadFile(original, file, buffer, bytesToRead, bytesRead, overlapped); \
}

#define DEFINE_WRITEFILE_DETOUR(name, original) \
BOOL WINAPI name(HANDLE file, LPCVOID buffer, DWORD bytesToWrite, LPDWORD bytesWritten, LPOVERLAPPED overlapped) \
{ \
    return CallOriginalWriteFile(original, file, buffer, bytesToWrite, bytesWritten, overlapped); \
}

#define DEFINE_MOVEFILEW_DETOUR(name, original) \
BOOL WINAPI name(LPCWSTR existingFileName, LPCWSTR newFileName) \
{ \
    return CallOriginalMoveFileW(original, existingFileName, newFileName); \
}

#define DEFINE_MOVEFILEA_DETOUR(name, original) \
BOOL WINAPI name(LPCSTR existingFileName, LPCSTR newFileName) \
{ \
    return CallOriginalMoveFileA(original, existingFileName, newFileName); \
}

#define DEFINE_MOVEFILEEXW_DETOUR(name, original) \
BOOL WINAPI name(LPCWSTR existingFileName, LPCWSTR newFileName, DWORD flags) \
{ \
    return CallOriginalMoveFileExW(original, existingFileName, newFileName, flags); \
}

#define DEFINE_MOVEFILEEXA_DETOUR(name, original) \
BOOL WINAPI name(LPCSTR existingFileName, LPCSTR newFileName, DWORD flags) \
{ \
    return CallOriginalMoveFileExA(original, existingFileName, newFileName, flags); \
}

#define DEFINE_COPYFILEW_DETOUR(name, original) \
BOOL WINAPI name(LPCWSTR existingFileName, LPCWSTR newFileName, BOOL failIfExists) \
{ \
    return CallOriginalCopyFileW(original, existingFileName, newFileName, failIfExists); \
}

#define DEFINE_COPYFILEA_DETOUR(name, original) \
BOOL WINAPI name(LPCSTR existingFileName, LPCSTR newFileName, BOOL failIfExists) \
{ \
    return CallOriginalCopyFileA(original, existingFileName, newFileName, failIfExists); \
}

#define DEFINE_DELETEFILEW_DETOUR(name, original) \
BOOL WINAPI name(LPCWSTR fileName) \
{ \
    return CallOriginalDeleteFileW(original, fileName); \
}

#define DEFINE_DELETEFILEA_DETOUR(name, original) \
BOOL WINAPI name(LPCSTR fileName) \
{ \
    return CallOriginalDeleteFileA(original, fileName); \
}

#define DEFINE_REPLACEFILEW_DETOUR(name, original) \
BOOL WINAPI name(LPCWSTR replacedFileName, LPCWSTR replacementFileName, LPCWSTR backupFileName, DWORD flags, LPVOID exclude, LPVOID reserved) \
{ \
    return CallOriginalReplaceFileW(original, replacedFileName, replacementFileName, backupFileName, flags, exclude, reserved); \
}

#define DEFINE_REPLACEFILEA_DETOUR(name, original) \
BOOL WINAPI name(LPCSTR replacedFileName, LPCSTR replacementFileName, LPCSTR backupFileName, DWORD flags, LPVOID exclude, LPVOID reserved) \
{ \
    return CallOriginalReplaceFileA(original, replacedFileName, replacementFileName, backupFileName, flags, exclude, reserved); \
}

#define DEFINE_SETFILEATTRIBUTESW_DETOUR(name, original) \
BOOL WINAPI name(LPCWSTR fileName, DWORD fileAttributes) \
{ \
    return CallOriginalSetFileAttributesW(original, fileName, fileAttributes); \
}

#define DEFINE_SETFILEATTRIBUTESA_DETOUR(name, original) \
BOOL WINAPI name(LPCSTR fileName, DWORD fileAttributes) \
{ \
    return CallOriginalSetFileAttributesA(original, fileName, fileAttributes); \
}

#define DEFINE_CLOSEHANDLE_DETOUR(name, original) \
BOOL WINAPI name(HANDLE handle) \
{ \
    return CallOriginalCloseHandle(original, handle); \
}

#define DEFINE_GETFILESIZE_DETOUR(name, original) \
DWORD WINAPI name(HANDLE file, LPDWORD highSize) \
{ \
    return CallOriginalGetFileSize(original, file, highSize); \
}

#define DEFINE_GETFILESIZEEX_DETOUR(name, original) \
BOOL WINAPI name(HANDLE file, PLARGE_INTEGER size) \
{ \
    return CallOriginalGetFileSizeEx(original, file, size); \
}

#define DEFINE_SETFILEPOINTER_DETOUR(name, original) \
DWORD WINAPI name(HANDLE file, LONG distanceLow, PLONG distanceHigh, DWORD moveMethod) \
{ \
    return CallOriginalSetFilePointer(original, file, distanceLow, distanceHigh, moveMethod); \
}

#define DEFINE_SETFILEPOINTEREX_DETOUR(name, original) \
BOOL WINAPI name(HANDLE file, LARGE_INTEGER distance, PLARGE_INTEGER newPosition, DWORD moveMethod) \
{ \
    return CallOriginalSetFilePointerEx(original, file, distance, newPosition, moveMethod); \
}

DEFINE_CREATEFILEW_DETOUR(DetourKernel32CreateFileW, g_originalKernel32CreateFileW)
DEFINE_CREATEFILEA_DETOUR(DetourKernel32CreateFileA, g_originalKernel32CreateFileA)
DEFINE_CREATEFILEW_DETOUR(DetourKernelBaseCreateFileW, g_originalKernelBaseCreateFileW)
DEFINE_CREATEFILEA_DETOUR(DetourKernelBaseCreateFileA, g_originalKernelBaseCreateFileA)
DEFINE_READFILE_DETOUR(DetourKernel32ReadFile, g_originalKernel32ReadFile)
DEFINE_READFILE_DETOUR(DetourKernelBaseReadFile, g_originalKernelBaseReadFile)
DEFINE_WRITEFILE_DETOUR(DetourKernel32WriteFile, g_originalKernel32WriteFile)
DEFINE_WRITEFILE_DETOUR(DetourKernelBaseWriteFile, g_originalKernelBaseWriteFile)
DEFINE_MOVEFILEW_DETOUR(DetourKernel32MoveFileW, g_originalKernel32MoveFileW)
DEFINE_MOVEFILEA_DETOUR(DetourKernel32MoveFileA, g_originalKernel32MoveFileA)
DEFINE_MOVEFILEW_DETOUR(DetourKernelBaseMoveFileW, g_originalKernelBaseMoveFileW)
DEFINE_MOVEFILEA_DETOUR(DetourKernelBaseMoveFileA, g_originalKernelBaseMoveFileA)
DEFINE_MOVEFILEEXW_DETOUR(DetourKernel32MoveFileExW, g_originalKernel32MoveFileExW)
DEFINE_MOVEFILEEXA_DETOUR(DetourKernel32MoveFileExA, g_originalKernel32MoveFileExA)
DEFINE_MOVEFILEEXW_DETOUR(DetourKernelBaseMoveFileExW, g_originalKernelBaseMoveFileExW)
DEFINE_MOVEFILEEXA_DETOUR(DetourKernelBaseMoveFileExA, g_originalKernelBaseMoveFileExA)
DEFINE_COPYFILEW_DETOUR(DetourKernel32CopyFileW, g_originalKernel32CopyFileW)
DEFINE_COPYFILEA_DETOUR(DetourKernel32CopyFileA, g_originalKernel32CopyFileA)
DEFINE_COPYFILEW_DETOUR(DetourKernelBaseCopyFileW, g_originalKernelBaseCopyFileW)
DEFINE_COPYFILEA_DETOUR(DetourKernelBaseCopyFileA, g_originalKernelBaseCopyFileA)
DEFINE_DELETEFILEW_DETOUR(DetourKernel32DeleteFileW, g_originalKernel32DeleteFileW)
DEFINE_DELETEFILEA_DETOUR(DetourKernel32DeleteFileA, g_originalKernel32DeleteFileA)
DEFINE_DELETEFILEW_DETOUR(DetourKernelBaseDeleteFileW, g_originalKernelBaseDeleteFileW)
DEFINE_DELETEFILEA_DETOUR(DetourKernelBaseDeleteFileA, g_originalKernelBaseDeleteFileA)
DEFINE_REPLACEFILEW_DETOUR(DetourKernel32ReplaceFileW, g_originalKernel32ReplaceFileW)
DEFINE_REPLACEFILEA_DETOUR(DetourKernel32ReplaceFileA, g_originalKernel32ReplaceFileA)
DEFINE_REPLACEFILEW_DETOUR(DetourKernelBaseReplaceFileW, g_originalKernelBaseReplaceFileW)
DEFINE_REPLACEFILEA_DETOUR(DetourKernelBaseReplaceFileA, g_originalKernelBaseReplaceFileA)
DEFINE_SETFILEATTRIBUTESW_DETOUR(DetourKernel32SetFileAttributesW, g_originalKernel32SetFileAttributesW)
DEFINE_SETFILEATTRIBUTESA_DETOUR(DetourKernel32SetFileAttributesA, g_originalKernel32SetFileAttributesA)
DEFINE_SETFILEATTRIBUTESW_DETOUR(DetourKernelBaseSetFileAttributesW, g_originalKernelBaseSetFileAttributesW)
DEFINE_SETFILEATTRIBUTESA_DETOUR(DetourKernelBaseSetFileAttributesA, g_originalKernelBaseSetFileAttributesA)
DEFINE_CLOSEHANDLE_DETOUR(DetourKernel32CloseHandle, g_originalKernel32CloseHandle)
DEFINE_CLOSEHANDLE_DETOUR(DetourKernelBaseCloseHandle, g_originalKernelBaseCloseHandle)
DEFINE_GETFILESIZE_DETOUR(DetourKernel32GetFileSize, g_originalKernel32GetFileSize)
DEFINE_GETFILESIZE_DETOUR(DetourKernelBaseGetFileSize, g_originalKernelBaseGetFileSize)
DEFINE_GETFILESIZEEX_DETOUR(DetourKernel32GetFileSizeEx, g_originalKernel32GetFileSizeEx)
DEFINE_GETFILESIZEEX_DETOUR(DetourKernelBaseGetFileSizeEx, g_originalKernelBaseGetFileSizeEx)
DEFINE_SETFILEPOINTER_DETOUR(DetourKernel32SetFilePointer, g_originalKernel32SetFilePointer)
DEFINE_SETFILEPOINTER_DETOUR(DetourKernelBaseSetFilePointer, g_originalKernelBaseSetFilePointer)
DEFINE_SETFILEPOINTEREX_DETOUR(DetourKernel32SetFilePointerEx, g_originalKernel32SetFilePointerEx)
DEFINE_SETFILEPOINTEREX_DETOUR(DetourKernelBaseSetFilePointerEx, g_originalKernelBaseSetFilePointerEx)

bool CreateApiHook(const wchar_t* moduleName, const char* procName, LPVOID detour, LPVOID* original)
{
    MH_STATUS status = MH_CreateHookApi(moduleName, procName, detour, original);
    if (status == MH_OK)
    {
        Logger::Info(L"Created file IO hook for " + std::wstring(moduleName) + L"!" + AnsiToWide(procName));
        return true;
    }

    if (status == MH_ERROR_ALREADY_CREATED)
    {
        Logger::Warn(L"File IO hook already exists for " + std::wstring(moduleName) + L"!" + AnsiToWide(procName));
        return false;
    }

    Logger::Warn(L"Failed to create file IO hook for " + std::wstring(moduleName) + L"!" + AnsiToWide(procName) + L": " + StatusToWide(status));
    return false;
}
}

void FileIoHook::InitializeObserveOnly()
{
    if (!GetEnvironmentBool(L"DD_RUNTIME_FILE_IO_OBSERVE_ONLY", false))
    {
        Logger::Warn(L"File IO observe-only is disabled. No file API hook was installed.");
        return;
    }

    LoadSettings();

    MH_STATUS initStatus = MH_Initialize();
    if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
    {
        Logger::Error(L"MinHook initialization failed: " + StatusToWide(initStatus));
        return;
    }

    bool createdAny = false;
    createdAny |= CreateApiHook(L"kernel32.dll", "CreateFileW", reinterpret_cast<LPVOID>(&DetourKernel32CreateFileW), reinterpret_cast<LPVOID*>(&g_originalKernel32CreateFileW));
    createdAny |= CreateApiHook(L"kernel32.dll", "CreateFileA", reinterpret_cast<LPVOID>(&DetourKernel32CreateFileA), reinterpret_cast<LPVOID*>(&g_originalKernel32CreateFileA));
    createdAny |= CreateApiHook(L"KernelBase.dll", "CreateFileW", reinterpret_cast<LPVOID>(&DetourKernelBaseCreateFileW), reinterpret_cast<LPVOID*>(&g_originalKernelBaseCreateFileW));
    createdAny |= CreateApiHook(L"KernelBase.dll", "CreateFileA", reinterpret_cast<LPVOID>(&DetourKernelBaseCreateFileA), reinterpret_cast<LPVOID*>(&g_originalKernelBaseCreateFileA));
    createdAny |= CreateApiHook(L"kernel32.dll", "ReadFile", reinterpret_cast<LPVOID>(&DetourKernel32ReadFile), reinterpret_cast<LPVOID*>(&g_originalKernel32ReadFile));
    createdAny |= CreateApiHook(L"KernelBase.dll", "ReadFile", reinterpret_cast<LPVOID>(&DetourKernelBaseReadFile), reinterpret_cast<LPVOID*>(&g_originalKernelBaseReadFile));
    bool needsWriteHook = (g_eventProbeEnabled && g_eventProbeLogFileWrite) ||
        (g_virtualFileEnabled && !g_virtualRules.empty());
    if (needsWriteHook)
    {
        createdAny |= CreateApiHook(L"kernel32.dll", "WriteFile", reinterpret_cast<LPVOID>(&DetourKernel32WriteFile), reinterpret_cast<LPVOID*>(&g_originalKernel32WriteFile));
        createdAny |= CreateApiHook(L"KernelBase.dll", "WriteFile", reinterpret_cast<LPVOID>(&DetourKernelBaseWriteFile), reinterpret_cast<LPVOID*>(&g_originalKernelBaseWriteFile));
    }

    if (g_eventProbeEnabled && g_eventProbeLogFileWrite)
    {
        createdAny |= CreateApiHook(L"kernel32.dll", "MoveFileW", reinterpret_cast<LPVOID>(&DetourKernel32MoveFileW), reinterpret_cast<LPVOID*>(&g_originalKernel32MoveFileW));
        createdAny |= CreateApiHook(L"kernel32.dll", "MoveFileA", reinterpret_cast<LPVOID>(&DetourKernel32MoveFileA), reinterpret_cast<LPVOID*>(&g_originalKernel32MoveFileA));
        createdAny |= CreateApiHook(L"KernelBase.dll", "MoveFileW", reinterpret_cast<LPVOID>(&DetourKernelBaseMoveFileW), reinterpret_cast<LPVOID*>(&g_originalKernelBaseMoveFileW));
        createdAny |= CreateApiHook(L"KernelBase.dll", "MoveFileA", reinterpret_cast<LPVOID>(&DetourKernelBaseMoveFileA), reinterpret_cast<LPVOID*>(&g_originalKernelBaseMoveFileA));
        createdAny |= CreateApiHook(L"kernel32.dll", "MoveFileExW", reinterpret_cast<LPVOID>(&DetourKernel32MoveFileExW), reinterpret_cast<LPVOID*>(&g_originalKernel32MoveFileExW));
        createdAny |= CreateApiHook(L"kernel32.dll", "MoveFileExA", reinterpret_cast<LPVOID>(&DetourKernel32MoveFileExA), reinterpret_cast<LPVOID*>(&g_originalKernel32MoveFileExA));
        createdAny |= CreateApiHook(L"KernelBase.dll", "MoveFileExW", reinterpret_cast<LPVOID>(&DetourKernelBaseMoveFileExW), reinterpret_cast<LPVOID*>(&g_originalKernelBaseMoveFileExW));
        createdAny |= CreateApiHook(L"KernelBase.dll", "MoveFileExA", reinterpret_cast<LPVOID>(&DetourKernelBaseMoveFileExA), reinterpret_cast<LPVOID*>(&g_originalKernelBaseMoveFileExA));
        createdAny |= CreateApiHook(L"kernel32.dll", "CopyFileW", reinterpret_cast<LPVOID>(&DetourKernel32CopyFileW), reinterpret_cast<LPVOID*>(&g_originalKernel32CopyFileW));
        createdAny |= CreateApiHook(L"kernel32.dll", "CopyFileA", reinterpret_cast<LPVOID>(&DetourKernel32CopyFileA), reinterpret_cast<LPVOID*>(&g_originalKernel32CopyFileA));
        createdAny |= CreateApiHook(L"KernelBase.dll", "CopyFileW", reinterpret_cast<LPVOID>(&DetourKernelBaseCopyFileW), reinterpret_cast<LPVOID*>(&g_originalKernelBaseCopyFileW));
        createdAny |= CreateApiHook(L"KernelBase.dll", "CopyFileA", reinterpret_cast<LPVOID>(&DetourKernelBaseCopyFileA), reinterpret_cast<LPVOID*>(&g_originalKernelBaseCopyFileA));
        createdAny |= CreateApiHook(L"kernel32.dll", "DeleteFileW", reinterpret_cast<LPVOID>(&DetourKernel32DeleteFileW), reinterpret_cast<LPVOID*>(&g_originalKernel32DeleteFileW));
        createdAny |= CreateApiHook(L"kernel32.dll", "DeleteFileA", reinterpret_cast<LPVOID>(&DetourKernel32DeleteFileA), reinterpret_cast<LPVOID*>(&g_originalKernel32DeleteFileA));
        createdAny |= CreateApiHook(L"KernelBase.dll", "DeleteFileW", reinterpret_cast<LPVOID>(&DetourKernelBaseDeleteFileW), reinterpret_cast<LPVOID*>(&g_originalKernelBaseDeleteFileW));
        createdAny |= CreateApiHook(L"KernelBase.dll", "DeleteFileA", reinterpret_cast<LPVOID>(&DetourKernelBaseDeleteFileA), reinterpret_cast<LPVOID*>(&g_originalKernelBaseDeleteFileA));
        createdAny |= CreateApiHook(L"kernel32.dll", "ReplaceFileW", reinterpret_cast<LPVOID>(&DetourKernel32ReplaceFileW), reinterpret_cast<LPVOID*>(&g_originalKernel32ReplaceFileW));
        createdAny |= CreateApiHook(L"kernel32.dll", "ReplaceFileA", reinterpret_cast<LPVOID>(&DetourKernel32ReplaceFileA), reinterpret_cast<LPVOID*>(&g_originalKernel32ReplaceFileA));
        createdAny |= CreateApiHook(L"KernelBase.dll", "ReplaceFileW", reinterpret_cast<LPVOID>(&DetourKernelBaseReplaceFileW), reinterpret_cast<LPVOID*>(&g_originalKernelBaseReplaceFileW));
        createdAny |= CreateApiHook(L"KernelBase.dll", "ReplaceFileA", reinterpret_cast<LPVOID>(&DetourKernelBaseReplaceFileA), reinterpret_cast<LPVOID*>(&g_originalKernelBaseReplaceFileA));
        createdAny |= CreateApiHook(L"kernel32.dll", "SetFileAttributesW", reinterpret_cast<LPVOID>(&DetourKernel32SetFileAttributesW), reinterpret_cast<LPVOID*>(&g_originalKernel32SetFileAttributesW));
        createdAny |= CreateApiHook(L"kernel32.dll", "SetFileAttributesA", reinterpret_cast<LPVOID>(&DetourKernel32SetFileAttributesA), reinterpret_cast<LPVOID*>(&g_originalKernel32SetFileAttributesA));
        createdAny |= CreateApiHook(L"KernelBase.dll", "SetFileAttributesW", reinterpret_cast<LPVOID>(&DetourKernelBaseSetFileAttributesW), reinterpret_cast<LPVOID*>(&g_originalKernelBaseSetFileAttributesW));
        createdAny |= CreateApiHook(L"KernelBase.dll", "SetFileAttributesA", reinterpret_cast<LPVOID>(&DetourKernelBaseSetFileAttributesA), reinterpret_cast<LPVOID*>(&g_originalKernelBaseSetFileAttributesA));
    }
    createdAny |= CreateApiHook(L"kernel32.dll", "CloseHandle", reinterpret_cast<LPVOID>(&DetourKernel32CloseHandle), reinterpret_cast<LPVOID*>(&g_originalKernel32CloseHandle));
    createdAny |= CreateApiHook(L"KernelBase.dll", "CloseHandle", reinterpret_cast<LPVOID>(&DetourKernelBaseCloseHandle), reinterpret_cast<LPVOID*>(&g_originalKernelBaseCloseHandle));
    createdAny |= CreateApiHook(L"kernel32.dll", "GetFileSize", reinterpret_cast<LPVOID>(&DetourKernel32GetFileSize), reinterpret_cast<LPVOID*>(&g_originalKernel32GetFileSize));
    createdAny |= CreateApiHook(L"KernelBase.dll", "GetFileSize", reinterpret_cast<LPVOID>(&DetourKernelBaseGetFileSize), reinterpret_cast<LPVOID*>(&g_originalKernelBaseGetFileSize));
    createdAny |= CreateApiHook(L"kernel32.dll", "GetFileSizeEx", reinterpret_cast<LPVOID>(&DetourKernel32GetFileSizeEx), reinterpret_cast<LPVOID*>(&g_originalKernel32GetFileSizeEx));
    createdAny |= CreateApiHook(L"KernelBase.dll", "GetFileSizeEx", reinterpret_cast<LPVOID>(&DetourKernelBaseGetFileSizeEx), reinterpret_cast<LPVOID*>(&g_originalKernelBaseGetFileSizeEx));
    createdAny |= CreateApiHook(L"kernel32.dll", "SetFilePointer", reinterpret_cast<LPVOID>(&DetourKernel32SetFilePointer), reinterpret_cast<LPVOID*>(&g_originalKernel32SetFilePointer));
    createdAny |= CreateApiHook(L"KernelBase.dll", "SetFilePointer", reinterpret_cast<LPVOID>(&DetourKernelBaseSetFilePointer), reinterpret_cast<LPVOID*>(&g_originalKernelBaseSetFilePointer));
    createdAny |= CreateApiHook(L"kernel32.dll", "SetFilePointerEx", reinterpret_cast<LPVOID>(&DetourKernel32SetFilePointerEx), reinterpret_cast<LPVOID*>(&g_originalKernel32SetFilePointerEx));
    createdAny |= CreateApiHook(L"KernelBase.dll", "SetFilePointerEx", reinterpret_cast<LPVOID>(&DetourKernelBaseSetFilePointerEx), reinterpret_cast<LPVOID*>(&g_originalKernelBaseSetFilePointerEx));

    if (!createdAny)
    {
        Logger::Warn(L"File IO observe-only hook did not create any hooks.");
        return;
    }

    MH_STATUS enableStatus = MH_EnableHook(MH_ALL_HOOKS);
    if (enableStatus != MH_OK)
    {
        Logger::Error(L"Failed to enable file IO hooks: " + StatusToWide(enableStatus));
        return;
    }

    Logger::Info(
        L"File IO hooks enabled. Extensions=" +
        GetEnvironmentString(L"DD_RUNTIME_FILE_IO_LOG_EXTENSIONS") +
        L" maxEntries=" + std::to_wstring(g_maxEntries) +
        L" deduplicate=" + (g_deduplicate ? L"true" : L"false") +
        L" eventProbe=" + (g_eventProbeEnabled ? L"enabled" : L"disabled") +
        L" eventProbeMaxEntries=" + std::to_wstring(g_eventProbeMaxEntries) +
        L" eventProbeMaxSaveEntries=" + std::to_wstring(g_eventProbeMaxSaveEntries) +
        L" eventProbeIgnoredFragments=" + std::to_wstring(g_eventProbeIgnorePathFragments.size()) +
        L" virtualFile=" + (g_virtualFileEnabled ? L"enabled" : L"disabled") +
        L" virtualRules=" + std::to_wstring(g_virtualRules.size()) +
        L" managedOverlay=" + DescribeManagedOverlayManifest());
}

void FileIoHook::Shutdown()
{
    MH_DisableHook(MH_ALL_HOOKS);

    std::vector<HANDLE> handlesToClose;
    {
        std::lock_guard<std::mutex> lock(g_virtualFilesMutex);
        for (const auto& item : g_virtualFiles)
        {
            handlesToClose.push_back(item.first);
        }
        g_virtualFiles.clear();
    }

    CloseHandleFn closeHandle = OriginalCloseHandle();
    if (closeHandle != nullptr)
    {
        for (HANDLE handle : handlesToClose)
        {
            closeHandle(handle);
        }
    }

    MH_Uninitialize();
}
}
