#include "recording_overlays.h"
#include <Windows.h>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <thread>
#include <stdexcept>

namespace clypdat {
struct RawInputCapture::State {
    std::shared_ptr<InputHistory> history;
    OverlayClock clock;
    std::mutex mutex;
    std::condition_variable changed;
    std::atomic<DWORD> thread_id{0};
    std::atomic<bool> stopping{false};
    bool ready=false, done=true, available=false;
    HHOOK keyboard=nullptr, mouse=nullptr;
    static thread_local State* active;
    void edge(PhysicalKey key, bool down) noexcept {
        try { history->add(clock(),key,down); } catch (...) { history->set_available(false); }
    }
    static LRESULT CALLBACK keyboard_hook(int code, WPARAM message, LPARAM data) {
        auto* self=active;
        if (self && code>=0) {
            auto& event=*reinterpret_cast<KBDLLHOOKSTRUCT*>(data);
            if (!(event.flags&LLKHF_INJECTED) && (message==WM_KEYDOWN || message==WM_KEYUP || message==WM_SYSKEYDOWN || message==WM_SYSKEYUP))
                self->edge({static_cast<uint16_t>(event.scanCode),(event.flags&LLKHF_EXTENDED)!=0,false,0}, message==WM_KEYDOWN || message==WM_SYSKEYDOWN);
        }
        return CallNextHookEx(nullptr,code,message,data);
    }
    static LRESULT CALLBACK mouse_hook(int code, WPARAM message, LPARAM data) {
        auto* self=active;
        if (self && code>=0) {
            auto& event=*reinterpret_cast<MSLLHOOKSTRUCT*>(data);
            uint8_t button=0; bool down=false;
            switch(message) {
            case WM_LBUTTONDOWN: down=true; [[fallthrough]]; case WM_LBUTTONUP: button=1; break;
            case WM_RBUTTONDOWN: down=true; [[fallthrough]]; case WM_RBUTTONUP: button=2; break;
            case WM_MBUTTONDOWN: down=true; [[fallthrough]]; case WM_MBUTTONUP: button=3; break;
            case WM_XBUTTONDOWN: down=true; [[fallthrough]]; case WM_XBUTTONUP: button=HIWORD(event.mouseData)==XBUTTON2?5:4; break;
            }
            if (button && !(event.flags&LLMHF_INJECTED)) self->edge({0,false,false,button},down);
        }
        return CallNextHookEx(nullptr,code,message,data);
    }
    void raw(LPARAM handle) {
        alignas(RAWINPUT) std::array<uint8_t,1024> storage{}; UINT bytes=static_cast<UINT>(storage.size());
        if (GetRawInputData(reinterpret_cast<HRAWINPUT>(handle),RID_INPUT,storage.data(),&bytes,sizeof(RAWINPUTHEADER))==static_cast<UINT>(-1)) return;
        if (bytes<sizeof(RAWINPUTHEADER)) return;
        auto& input=*reinterpret_cast<const RAWINPUT*>(storage.data());
        if (input.header.dwType==RIM_TYPEKEYBOARD && bytes>=offsetof(RAWINPUT,data)+sizeof(RAWKEYBOARD)) {
            auto& key=input.data.keyboard;
            if (key.MakeCode) edge({key.MakeCode,(key.Flags&RI_KEY_E0)!=0,(key.Flags&RI_KEY_E1)!=0,0},(key.Flags&RI_KEY_BREAK)==0);
        } else if (input.header.dwType==RIM_TYPEMOUSE && bytes>=offsetof(RAWINPUT,data)+sizeof(RAWMOUSE)) {
            const auto flags=input.data.mouse.usButtonFlags;
            constexpr USHORT downs[]={RI_MOUSE_LEFT_BUTTON_DOWN,RI_MOUSE_RIGHT_BUTTON_DOWN,RI_MOUSE_MIDDLE_BUTTON_DOWN,RI_MOUSE_BUTTON_4_DOWN,RI_MOUSE_BUTTON_5_DOWN};
            constexpr USHORT ups[]={RI_MOUSE_LEFT_BUTTON_UP,RI_MOUSE_RIGHT_BUTTON_UP,RI_MOUSE_MIDDLE_BUTTON_UP,RI_MOUSE_BUTTON_4_UP,RI_MOUSE_BUTTON_5_UP};
            for(uint8_t i=0;i<5;++i) { if(flags&downs[i]) edge({0,false,false,static_cast<uint8_t>(i+1)},true); if(flags&ups[i]) edge({0,false,false,static_cast<uint8_t>(i+1)},false); }
        }
    }
    void run() noexcept {
        active=this;
        MSG message{}; PeekMessageW(&message,nullptr,0,0,PM_NOREMOVE);
        thread_id=GetCurrentThreadId();
        HWND window=CreateWindowExW(0,L"STATIC",L"ClypDat native recording input",0,0,0,0,0,HWND_MESSAGE,nullptr,GetModuleHandleW(nullptr),nullptr);
        RAWINPUTDEVICE devices[]={{1,6,RIDEV_INPUTSINK,window},{1,2,RIDEV_INPUTSINK,window}};
        bool raw_available=window && RegisterRawInputDevices(devices,2,sizeof(RAWINPUTDEVICE));
        if (!raw_available) {
            keyboard=SetWindowsHookExW(WH_KEYBOARD_LL,keyboard_hook,GetModuleHandleW(nullptr),0);
            mouse=SetWindowsHookExW(WH_MOUSE_LL,mouse_hook,GetModuleHandleW(nullptr),0);
        }
        { std::lock_guard lock(mutex); available=raw_available || (keyboard && mouse); ready=true; history->set_available(available); }
        changed.notify_all();
        try {
            while (!stopping && GetMessageW(&message,nullptr,0,0)>0) {
                if(message.message==WM_INPUT) raw(message.lParam);
                TranslateMessage(&message); DispatchMessageW(&message);
            }
        } catch (...) { history->set_available(false); }
        if(keyboard) UnhookWindowsHookEx(keyboard); if(mouse) UnhookWindowsHookEx(mouse);
        if(raw_available) { RAWINPUTDEVICE remove[]={{1,6,RIDEV_REMOVE,nullptr},{1,2,RIDEV_REMOVE,nullptr}}; RegisterRawInputDevices(remove,2,sizeof(RAWINPUTDEVICE)); }
        if(window) DestroyWindow(window);
        active=nullptr; thread_id=0;
        { std::lock_guard lock(mutex); done=true; } changed.notify_all();
    }
};
thread_local RawInputCapture::State* RawInputCapture::State::active=nullptr;
RawInputCapture::RawInputCapture(std::shared_ptr<InputHistory> history, OverlayClock clock) : state_(std::make_shared<State>()) {
    state_->history=std::move(history); state_->clock=std::move(clock);
    if(!state_->history || !state_->clock) throw std::invalid_argument("Input history and clock are required");
}
RawInputCapture::~RawInputCapture() { stop(); }
bool RawInputCapture::start() {
    auto state=state_; std::unique_lock lock(state->mutex);
    if(!state->done) return state->ready && state->available;
    state->history->reset(false); state->done=false; state->ready=false; state->stopping=false;
    try { std::thread([state] { state->run(); }).detach(); }
    catch (...) { state->done=true; throw; }
    if(!state->changed.wait_for(lock,std::chrono::seconds(2),[&] { return state->ready; })) { state->stopping=true; return false; }
    return state->available;
}
bool RawInputCapture::stop(uint32_t timeout_ms) {
    auto state=state_; state->stopping=true;
    if(auto thread=state->thread_id.load()) PostThreadMessageW(thread,WM_QUIT,0,0);
    std::unique_lock lock(state->mutex);
    return state->changed.wait_for(lock,std::chrono::milliseconds(timeout_ms),[&] { return state->done; });
}
}
