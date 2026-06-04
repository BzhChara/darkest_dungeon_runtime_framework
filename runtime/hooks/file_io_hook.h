#pragma once

namespace RuntimeHook
{
class FileIoHook
{
public:
    static void InitializeObserveOnly();
    static void Shutdown();
};
}
