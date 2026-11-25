#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <iterator>
#include "MinHook.h"

static_assert(sizeof(void*) == 4, "The game uses the Win32 x86 ABI");

namespace {
constexpr uintptr_t LookupRva = 0x5E2B40;
constexpr uintptr_t ComparisonRva = 0x5E2B9B;
constexpr uintptr_t TaskListHeaderOffset = 0x274;
constexpr uintptr_t RelationshipPidOffset = 4;

// These offsets follow the game's list constructor, begin/end accessors,
// iterator increment, and the principal-ID getter.
struct ListHeader {
    uintptr_t sentinel;
    uint32_t size;
};
struct Node {
    uintptr_t next;
    uintptr_t previous;
    uint32_t valueVtable;
    uint32_t principal;
};
static_assert(offsetof(Node, principal) == 0x0c);

template <typename T>
bool Read(uintptr_t address, T& value) {
    SIZE_T copied = 0;
    return address != 0 && ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(address),
        &value, sizeof value, &copied) && copied == sizeof value;
}

bool IsBlocked(uintptr_t task, uintptr_t relationship) {
    ListHeader list{};
    uint32_t principal = 0;
    if (!task || !relationship
        || !Read(task + TaskListHeaderOffset, list) || !list.sentinel || !list.size
        || !Read(relationship + RelationshipPidOffset, principal)) {
        return false;
    }

    uintptr_t next = 0;
    if (!Read(list.sentinel, next)) {
        return false;
    }
    auto previous = list.sentinel;
    for (uint32_t remaining = list.size; remaining && next != list.sentinel; --remaining) {
        Node node{};
        if (!Read(next, node) || node.previous != previous) {
            return false;
        }
        if (node.principal == principal) {
            return true;
        }
        previous = next;
        next = node.next;
    }
    return false;
}

// The original thiscall supplies this in ECX and one argument on the stack.
bool __fastcall CheckBlocked(void* task, void*, const void* relationship) {
    const DWORD savedError = GetLastError();
    const bool blocked = IsBlocked(reinterpret_cast<uintptr_t>(task), reinterpret_cast<uintptr_t>(relationship));
    SetLastError(savedError);
    return blocked;
}

bool VerifiedExecutable() {
    wchar_t path[32768];
    const DWORD length = GetModuleFileNameW(nullptr, path, std::size(path));
    if (!length || length == std::size(path)) {
        return false;
    }
    const HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }

    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    bool valid = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0
        && BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    BYTE buffer[32768];
    BYTE digest[32];
    DWORD bytes = 0;
    while (valid) {
        valid = ReadFile(file, buffer, sizeof buffer, &bytes, nullptr) != FALSE;
        if (!valid || !bytes) {
            break;
        }
        valid = BCryptHashData(hash, buffer, bytes, 0) >= 0;
    }
    constexpr BYTE Expected[] = {
        0xc7,0x99,0x87,0xb0,0x54,0x8f,0x71,0x30,0x33,0x7c,0x3f,0x29,0x5c,0x8f,0xe2,0x69,
        0xe1,0x20,0xce,0xdd,0xad,0x55,0xc1,0x15,0x9e,0x6f,0x5c,0xbc,0x6a,0xa2,0x66,0x16
    };
    valid = valid && BCryptFinishHash(hash, digest, sizeof digest, 0) >= 0
        && std::memcmp(digest, Expected, sizeof Expected) == 0;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    CloseHandle(file);
    return valid;
}

template <size_t Size>
bool MatchesBytes(uintptr_t address, const BYTE (&expected)[Size]) {
    BYTE actual[Size];
    return Read(address, actual) && std::memcmp(actual, expected, Size) == 0;
}

const char* Install() {
    if (!VerifiedExecutable()) {
        return "Unsupported Driver.exe SHA-256.\r\n";
    }
    const auto image = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    constexpr BYTE Entry[] = {0x55,0x8b,0xec,0x83,0xec,0x18,0x56,0x89,0x4d,0xe8,0x8d,0x45,0xf8};
    constexpr BYTE Comparison[] = {0x3b,0xf0,0x75,0x04,0xb0,0x01,0xeb,0x04,0xeb,0xb7,0x32,0xc0};
    if (!MatchesBytes(image + LookupRva, Entry) || !MatchesBytes(image + ComparisonRva, Comparison)) {
        return "Unexpected block-list lookup instructions.\r\n";
    }
    if (MH_Initialize() != MH_OK) {
        return "Hook initialization failed.\r\n";
    }
    auto target = reinterpret_cast<void*>(image + LookupRva);
    if (MH_CreateHook(target, CheckBlocked, nullptr) != MH_OK || MH_EnableHook(target) != MH_OK) {
        MH_Uninitialize();
        return "Block-list hook installation failed.\r\n";
    }
    return "Block-list startup fix enabled.\r\n";
}

#ifndef BLOCK_LIST_SELF_TEST
DWORD WINAPI Initialize(void* module) {
    const char* status = Install();
    wchar_t path[32768];
    const DWORD length = GetModuleFileNameW(static_cast<HMODULE>(module), path, std::size(path));
    if (length && length < std::size(path)) {
        wchar_t* name = std::wcsrchr(path, L'\\');
        if (name && swprintf_s(name + 1, std::size(path) - static_cast<size_t>(name + 1 - path),
                L"DriverSF_BlockListFix.%lu.log", GetCurrentProcessId()) > 0) {
            const HANDLE log = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (log != INVALID_HANDLE_VALUE) {
                DWORD written = 0;
                WriteFile(log, status, static_cast<DWORD>(std::strlen(status)), &written, nullptr);
                CloseHandle(log);
            }
        }
    }
    OutputDebugStringA(status);
    return 0;
}
#endif
} // namespace

#ifndef BLOCK_LIST_SELF_TEST
BOOL WINAPI DllMain(HMODULE module, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        if (const HANDLE worker = CreateThread(nullptr, 0, Initialize, module, 0, nullptr)) {
            CloseHandle(worker);
        }
    }
    return TRUE;
}
#endif
