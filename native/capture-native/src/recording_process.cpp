#include "recording_process.h"
#include <Windows.h>
#include <thread>
#include <stdexcept>
#include <memory>
#include <algorithm>

namespace clypdat {
namespace {
struct Close { void operator()(void* p) const { if (p && p != INVALID_HANDLE_VALUE) CloseHandle(p); } };
using Handle = std::unique_ptr<void, Close>;
void check(bool success, const char* action) { if (!success) throw std::runtime_error(std::string(action) + ": " + std::to_string(GetLastError())); }
void read_pipe(HANDLE pipe, std::string& destination, const std::function<void(const uint8_t*, size_t)>& sink = {}) {
    char buffer[8192]; DWORD count = 0;
    while (ReadFile(pipe, buffer, sizeof(buffer), &count, nullptr) && count) {
        if (sink) { sink(reinterpret_cast<const uint8_t*>(buffer), count); continue; }
        // Keep draining even when logging is capped, otherwise a child can deadlock.
        constexpr size_t limit = 4 * 1024 * 1024;
        if (destination.size() < limit) destination.append(buffer, (std::min)(size_t(count), limit - destination.size()));
    }
}
}
std::wstring ProcessRunner::quote(const std::wstring& arg) {
    std::wstring result = L"\""; size_t slashes = 0;
    for (wchar_t ch : arg) {
        if (ch == L'\\') { ++slashes; continue; }
        result.append(ch == L'\"' ? slashes * 2 + 1 : slashes, L'\\');
        result.push_back(ch); slashes = 0;
    }
    result.append(slashes * 2, L'\\'); result.push_back(L'\"'); return result;
}
ProcessResult ProcessRunner::run(const std::filesystem::path& executable,
    const std::vector<std::wstring>& arguments, const std::atomic_bool& cancel,
    std::chrono::milliseconds timeout, std::function<void(const uint8_t*, size_t)> stdout_sink, bool require_success,
    std::function<size_t(uint8_t*, size_t)> stdin_source) {
    if (!executable.is_absolute()) throw std::invalid_argument("Recording subprocess requires an absolute executable path");
    if (cancel.load()) throw std::runtime_error("Recording subprocess cancelled");
    SECURITY_ATTRIBUTES security{ sizeof(security), nullptr, TRUE };
    HANDLE raw_read = nullptr, raw_write = nullptr;
    check(CreatePipe(&raw_read, &raw_write, &security, 0), "Create stdout pipe");
    Handle output_read(raw_read), output_write(raw_write);
    check(SetHandleInformation(output_read.get(), HANDLE_FLAG_INHERIT, 0), "Protect stdout reader");
    check(CreatePipe(&raw_read, &raw_write, &security, 0), "Create stderr pipe");
    Handle error_read(raw_read), error_write(raw_write);
    check(SetHandleInformation(error_read.get(), HANDLE_FLAG_INHERIT, 0), "Protect stderr reader");
    Handle input(CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr));
    check(input.get() != INVALID_HANDLE_VALUE, "Open null input");
    Handle input_write;
    if(stdin_source){check(CreatePipe(&raw_read,&raw_write,&security,0),"Create stdin pipe");input.reset(raw_read);input_write.reset(raw_write);check(SetHandleInformation(input_write.get(),HANDLE_FLAG_INHERIT,0),"Protect stdin writer");}
    Handle job(CreateJobObjectW(nullptr, nullptr)); check(bool(job), "Create recording job");
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    check(SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits)), "Configure recording job");
    // The explicit inheritance list prevents capture/file handles leaking into children.
    SIZE_T attribute_bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attribute_bytes);
    std::vector<unsigned char> storage(attribute_bytes);
    auto* attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    check(InitializeProcThreadAttributeList(attributes, 1, 0, &attribute_bytes), "Initialize process attributes");
    struct AttributeScope { LPPROC_THREAD_ATTRIBUTE_LIST p; ~AttributeScope() { DeleteProcThreadAttributeList(p); } } attribute_scope{attributes};
    HANDLE inherited[] = { output_write.get(), error_write.get(), input.get() };
    check(UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr), "Set process inheritance");
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdOutput = output_write.get(); startup.StartupInfo.hStdError = error_write.get(); startup.StartupInfo.hStdInput = input.get(); startup.lpAttributeList = attributes;
    std::wstring command = quote(executable.wstring());
    for (const auto& argument : arguments) command += L" " + quote(argument);
    PROCESS_INFORMATION process{};
    check(CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, nullptr, executable.parent_path().c_str(), &startup.StartupInfo, &process), "Launch recording subprocess");
    Handle process_handle(process.hProcess), thread_handle(process.hThread);
    if (!AssignProcessToJobObject(job.get(), process.hProcess)) { TerminateProcess(process.hProcess, 1); WaitForSingleObject(process.hProcess, INFINITE); throw std::runtime_error("Cannot contain recording subprocess in job"); }
    output_write.reset(); error_write.reset(); input.reset();
    ProcessResult result;
    std::atomic_bool io_failed=false;std::exception_ptr stdout_failure,stdin_failure;
    std::jthread stdout_reader([&] {try{ read_pipe(output_read.get(), result.output, stdout_sink); }catch(...){stdout_failure=std::current_exception();io_failed=true;} });
    std::jthread stderr_reader([&] { read_pipe(error_read.get(), result.error); });
    std::jthread stdin_writer;
    if(stdin_source)stdin_writer=std::jthread([&]{try{uint8_t buffer[8192];while(!cancel.load()){size_t count=stdin_source(buffer,sizeof(buffer));if(!count)break;check(count<=sizeof(buffer),"Subprocess input callback size");size_t offset=0;while(offset<count){DWORD written=0;if(!WriteFile(input_write.get(),buffer+offset,DWORD(count-offset),&written,nullptr)||!written)return;offset+=written;}}input_write.reset();}catch(...){stdin_failure=std::current_exception();io_failed=true;}});
    ResumeThread(process.hThread);
    auto deadline = std::chrono::steady_clock::now() + timeout;
    bool cancelled = false, expired = false;
    while (WaitForSingleObject(process.hProcess, 25) == WAIT_TIMEOUT) {
        cancelled = cancel.load(); expired = timeout.count()>0 && std::chrono::steady_clock::now() >= deadline;
        if (cancelled || expired || io_failed.load()) { TerminateJobObject(job.get(), 1); WaitForSingleObject(process.hProcess, INFINITE); break; }
    }
    GetExitCodeProcess(process.hProcess, &result.exit_code);
    // Descendants may keep inherited pipes open after their parent exits.
    job.reset(); stdout_reader.join(); stderr_reader.join();if(stdin_writer.joinable())stdin_writer.join();
    if(stdout_failure)std::rethrow_exception(stdout_failure);if(stdin_failure)std::rethrow_exception(stdin_failure);
    if (cancelled) throw std::runtime_error("Recording subprocess cancelled");
    if (expired) throw std::runtime_error("Recording subprocess timed out");
    if (require_success && result.exit_code) throw std::runtime_error("Recording subprocess failed: " + result.error);
    return result;
}
}
