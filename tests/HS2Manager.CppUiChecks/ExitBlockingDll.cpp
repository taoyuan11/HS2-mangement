#include <windows.h>

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    // 模拟显卡/界面 DLL 在进程卸载阶段等待已被 Windows 停止的线程。
    if (reason == DLL_PROCESS_DETACH)
        Sleep(30000);
    return TRUE;
}
