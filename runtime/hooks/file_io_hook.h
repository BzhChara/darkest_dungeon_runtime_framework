#pragma once

namespace RuntimeHook
{
class FileIoHook
{
public:
    static void InitializeFromEnvironment();
    static void Shutdown();
};
}
