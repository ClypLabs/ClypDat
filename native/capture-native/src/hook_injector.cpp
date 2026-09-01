#include <windows.h>
#include <tlhelp32.h>
#include <sddl.h>
#include <string>
#include <vector>

namespace {
int fail(const wchar_t* message) { fwprintf(stderr, L"%ls\n", message); return 1; }

bool same_session(DWORD target_pid) {
    DWORD own_session = 0, target_session = 0;
    return ProcessIdToSessionId(GetCurrentProcessId(), &own_session) &&
        ProcessIdToSessionId(target_pid, &target_session) && own_session == target_session;
}

bool is_x64(HANDLE process) {
    USHORT process_machine = 0, native_machine = 0;
    if (!IsWow64Process2(process, &process_machine, &native_machine)) return false;
    return native_machine == IMAGE_FILE_MACHINE_AMD64 && process_machine == IMAGE_FILE_MACHINE_UNKNOWN;
}

DWORD integrity_rid(HANDLE process) {
    HANDLE token = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return 0;
    DWORD bytes = 0;
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &bytes);
    std::vector<BYTE> data(bytes);
    const bool ok = GetTokenInformation(token, TokenIntegrityLevel, data.data(), bytes, &bytes) != FALSE;
    CloseHandle(token);
    if (!ok) return 0;
    const auto* label = reinterpret_cast<const TOKEN_MANDATORY_LABEL*>(data.data());
    return *GetSidSubAuthority(label->Label.Sid, *GetSidSubAuthorityCount(label->Label.Sid) - 1);
}

bool loaded_hook(DWORD process_id) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, process_id);
    if (snapshot == INVALID_HANDLE_VALUE) return false;
    MODULEENTRY32W module{ sizeof(module) };
    bool found = false;
    if (Module32FirstW(snapshot, &module)) do {
        if (wcsstr(module.szModule, L"ClypDat.GraphicsHook") != nullptr) { found = true; break; }
    } while (Module32NextW(snapshot, &module));
    CloseHandle(snapshot);
    return found;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 5 || wcscmp(argv[1], L"--pid") != 0 || wcscmp(argv[3], L"--dll") != 0) return fail(L"Usage: ClypDat.Hook.Injector.exe --pid <pid> --dll <path>");
    const DWORD pid = wcstoul(argv[2], nullptr, 10);
    if (pid == 0 || !same_session(pid)) return fail(L"Target must be in this Windows session.");
    const DWORD rights = PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_QUERY_LIMITED_INFORMATION;
    HANDLE target = OpenProcess(rights, FALSE, pid);
    if (target == nullptr) return fail(L"Target process cannot be opened with injection rights.");
    if (!is_x64(GetCurrentProcess()) || !is_x64(target)) { CloseHandle(target); return fail(L"ClypDat graphics hook supports x64 targets only."); }
    if (integrity_rid(target) > integrity_rid(GetCurrentProcess())) { CloseHandle(target); return fail(L"Target integrity level is higher than ClypDat."); }
    PROCESS_MITIGATION_DYNAMIC_CODE_POLICY policy{};
    if (GetProcessMitigationPolicy(target, ProcessDynamicCodePolicy, &policy, sizeof(policy)) && policy.ProhibitDynamicCode) {
        CloseHandle(target); return fail(L"Target dynamic-code policy prohibits the graphics hook.");
    }
    if (loaded_hook(pid)) { CloseHandle(target); return fail(L"A ClypDat graphics hook is already resident; restart the game after updating ClypDat."); }
    const std::wstring dll = argv[4];
    const SIZE_T bytes = (dll.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(target, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remote == nullptr || !WriteProcessMemory(target, remote, dll.c_str(), bytes, nullptr)) { if (remote) VirtualFreeEx(target, remote, 0, MEM_RELEASE); CloseHandle(target); return fail(L"Could not write hook path to target."); }
    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    auto load_library = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(kernel32, "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(target, nullptr, 0, load_library, remote, 0, nullptr);
    if (thread == nullptr) { VirtualFreeEx(target, remote, 0, MEM_RELEASE); CloseHandle(target); return fail(L"Target rejected hook load."); }
    WaitForSingleObject(thread, 5000);
    DWORD module = 0; GetExitCodeThread(thread, &module);
    CloseHandle(thread); VirtualFreeEx(target, remote, 0, MEM_RELEASE); CloseHandle(target);
    return module == 0 ? fail(L"Target failed to load graphics hook.") : 0;
}
