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

struct ReplacementRule
{
    std::string find;
    std::string replace;
};

struct VirtualRule
{
    std::wstring targetPath;
    std::vector<ReplacementRule> replacements;
};

bool g_virtualFileEnabled = false;
std::vector<VirtualRule> g_virtualRules;

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

void LoadSettings()
{
    g_extensions = SplitExtensions(GetEnvironmentString(L"DD_RUNTIME_FILE_IO_LOG_EXTENSIONS"));
    g_maxEntries = GetEnvironmentUnsignedLong(L"DD_RUNTIME_FILE_IO_MAX_ENTRIES", 2000);
    g_deduplicate = GetEnvironmentBool(L"DD_RUNTIME_FILE_IO_DEDUPLICATE", true);

    g_virtualFileEnabled = GetEnvironmentBool(L"DD_RUNTIME_VIRTUAL_FILE_ENABLED", false);
    g_virtualRules.clear();

    unsigned long ruleCount = GetEnvironmentUnsignedLong(L"DD_RUNTIME_VIRTUAL_RULE_COUNT", 0);
    for (unsigned long ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
    {
        std::wstring prefix = L"DD_RUNTIME_VIRTUAL_RULE_" + std::to_wstring(ruleIndex);
        VirtualRule rule;
        rule.targetPath = NormalizePath(GetEnvironmentString((prefix + L"_TARGET").c_str()));
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

        if (!rule.replacements.empty())
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
        L" access=0x" + std::to_wstring(desiredAccess) +
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

CloseHandleFn OriginalCloseHandle()
{
    return g_originalKernelBaseCloseHandle ? g_originalKernelBaseCloseHandle : g_originalKernel32CloseHandle;
}

GetFileSizeExFn OriginalGetFileSizeEx()
{
    return g_originalKernelBaseGetFileSizeEx ? g_originalKernelBaseGetFileSizeEx : g_originalKernel32GetFileSizeEx;
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
        if (EndsWithPath(normalizedPath, rule.targetPath))
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

HANDLE CreateVirtualFileHandle(const std::wstring& path, DWORD desiredAccess, DWORD creationDisposition)
{
    const VirtualRule* rule = FindVirtualRule(path, desiredAccess, creationDisposition);
    if (rule == nullptr)
    {
        return INVALID_HANDLE_VALUE;
    }

    std::vector<std::uint8_t> bytes;
    if (!ReadOriginalFileBytes(path, bytes))
    {
        Logger::Warn(L"virtual-file failed to read original: " + path);
        return INVALID_HANDLE_VALUE;
    }

    std::size_t originalSize = bytes.size();
    std::size_t replacements = 0;
    for (const ReplacementRule& replacement : rule->replacements)
    {
        replacements += ReplaceAll(bytes, replacement.find, replacement.replace);
    }

    if (replacements == 0)
    {
        Logger::Warn(L"virtual-file rule matched but no replacement text was found: " + path);
        return INVALID_HANDLE_VALUE;
    }

    HANDLE marker = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (marker == nullptr)
    {
        Logger::Warn(L"virtual-file failed to allocate marker handle: " + path);
        return INVALID_HANDLE_VALUE;
    }

    auto virtualFile = std::make_shared<VirtualFile>();
    virtualFile->path = path;
    virtualFile->bytes = std::move(bytes);
    virtualFile->position = 0;
    virtualFile->backingHandle = marker;

    {
        std::lock_guard<std::mutex> lock(g_virtualFilesMutex);
        g_virtualFiles[marker] = virtualFile;
    }

    Logger::Info(
        L"virtual-file served path=" + path +
        L" originalBytes=" + std::to_wstring(originalSize) +
        L" virtualBytes=" + std::to_wstring(virtualFile->bytes.size()) +
        L" replacements=" + std::to_wstring(replacements));
    return marker;
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

BOOL CallOriginalCloseHandle(CloseHandleFn original, HANDLE handle)
{
    auto virtualFile = RemoveVirtualFile(handle);
    if (!virtualFile)
    {
        return original ? original(handle) : FALSE;
    }

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
        L" virtualFile=" + (g_virtualFileEnabled ? L"enabled" : L"disabled") +
        L" virtualRules=" + std::to_wstring(g_virtualRules.size()));
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
