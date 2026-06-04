#pragma once

#include <string>

namespace RuntimeHook
{
class Logger
{
public:
    static void InitializeFromEnvironment();
    static void Info(const std::wstring& message);
    static void Warn(const std::wstring& message);
    static void Error(const std::wstring& message);

private:
    static void Write(const wchar_t* level, const std::wstring& message);
};
}