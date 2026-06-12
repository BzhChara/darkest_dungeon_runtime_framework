#include <windows.h>

#include "hooks/file_io_hook.h"
#include "logger.h"

namespace
{
DWORD WINAPI RuntimeThread(LPVOID parameter)
{
    HMODULE module = static_cast<HMODULE>(parameter);
    wchar_t modulePath[MAX_PATH] = {};
    GetModuleFileNameW(module, modulePath, MAX_PATH);

    RuntimeHook::Logger::InitializeFromEnvironment();
    RuntimeHook::Logger::Info(L"RuntimeHook.dll loaded");
    RuntimeHook::Logger::Info(std::wstring(L"Module path: ") + modulePath);
    RuntimeHook::Logger::Info(L"Process ID: " + std::to_wstring(GetCurrentProcessId()));

    RuntimeHook::FileIoHook::InitializeFromEnvironment();

    RuntimeHook::Logger::Info(L"RuntimeHook initialization complete");
    return 0;
}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, RuntimeThread, module, 0, nullptr);
        if (thread != nullptr)
        {
            CloseHandle(thread);
        }
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        RuntimeHook::FileIoHook::Shutdown();
    }
    return TRUE;
}
