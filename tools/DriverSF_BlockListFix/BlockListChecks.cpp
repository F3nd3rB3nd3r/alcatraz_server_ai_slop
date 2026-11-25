#define BLOCK_LIST_SELF_TEST
#include "DriverSF_BlockListFix.cpp"
#include <cstdio>
#include <cstdlib>

namespace {
struct Relationship {
    uint32_t valueVtable{};
    uint32_t principal{};
};
struct Task {
    BYTE padding[TaskListHeaderOffset]{};
    ListHeader blocks{};

    __declspec(noinline) bool Lookup(const Relationship* entry);
};
static_assert(offsetof(Task, blocks) == TaskListHeaderOffset);
static_assert(offsetof(Relationship, principal) == RelationshipPidOffset);

bool Task::Lookup(const Relationship* entry) {
    return entry && entry->principal == blocks.size;
}

void Require(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "%s\n", message);
        std::exit(1);
    }
}

void Link(Task& task, Node& head, Node* nodes, uint32_t size) {
    const auto sentinel = reinterpret_cast<uintptr_t>(&head);
    task.blocks = {sentinel, size};
    head.next = size ? reinterpret_cast<uintptr_t>(nodes) : sentinel;
    for (uint32_t i = 0; i < size; ++i) {
        nodes[i].previous = i ? reinterpret_cast<uintptr_t>(&nodes[i - 1]) : sentinel;
        nodes[i].next = i + 1 < size ? reinterpret_cast<uintptr_t>(&nodes[i + 1]) : sentinel;
    }
}
} // namespace

int main() {
    Require(std::strcmp(Install(), "Unsupported Driver.exe SHA-256.\r\n") == 0, "Unsupported host guard failed");

    Task task{};
    Node head{};
    Node nodes[3]{};
    nodes[0].principal = 1004;
    nodes[1].principal = 1007;
    nodes[2].principal = 1008;

    // The synthetic method uses the same x86 thiscall ABI as the game.
    auto lookup = &Task::Lookup;
    void* target = nullptr;
    static_assert(sizeof target == sizeof lookup);
    std::memcpy(&target, &lookup, sizeof target);
    using Lookup = bool (__thiscall*)(Task*, const Relationship*);
    const auto call = reinterpret_cast<Lookup>(target);
    Relationship missing{0, 1002}, first{0, 1004}, middle{0, 1007}, last{0, 1008};
    Require(!call(&task, &first), "Original method control failed");
    Require(MH_Initialize() == MH_OK && MH_CreateHook(target, CheckBlocked, nullptr) == MH_OK
        && MH_EnableHook(target) == MH_OK, "Could not attach the test detour");

    Link(task, head, nullptr, 0);
    Require(!call(&task, &missing), "An empty block list cannot match");
    Link(task, head, nodes, 1);
    Require(!call(&task, &missing) && call(&task, &first), "Single-entry mismatch or match failed");
    Link(task, head, nodes, 3);
    for (unsigned i = 0; i < 10000; ++i) {
        SetLastError(ERROR_BUSY);
        Require(!call(&task, &missing) && call(&task, &first) && call(&task, &middle) && call(&task, &last),
            "Blocked lookup did not advance or returned the wrong result");
        Require(GetLastError() == ERROR_BUSY, "The detour changed LastError");
    }

    nodes[2].next = reinterpret_cast<uintptr_t>(&nodes[1]);
    task.blocks.size = UINT32_MAX;
    Require(!call(&task, &missing), "A cyclic list must terminate");
    head.next = 1;
    Require(!call(&task, &missing) && !call(&task, nullptr), "Unreadable input must terminate safely");
    Require(MH_Uninitialize() == MH_OK && !call(&task, &first), "The hook did not detach cleanly");
    std::puts("Block-list lookup checks passed.");
}
