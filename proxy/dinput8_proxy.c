#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

/* A small DirectInput 8 proxy. Keep DllMain free of work that uses the loader. */
static HMODULE system_module;
static LONG system_state;
static LONG host_launch_attempted;

static void load_system_module(void) {
    WCHAR path[MAX_PATH];
    UINT length;

    length = GetSystemDirectoryW(path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH - 14) return;
    path[length++] = L'\\';
    wcscpy(path + length, L"dinput8.dll");
    system_module = LoadLibraryW(path);
}

static FARPROC original(const char *name) {
    if (InterlockedCompareExchange(&system_state, 1, 0) == 0) {
        load_system_module();
        InterlockedExchange(&system_state, 2);
    } else {
        while (InterlockedCompareExchange(&system_state, 2, 2) != 2) Sleep(0);
    }
    return system_module ? GetProcAddress(system_module, name) : NULL;
}

static void launch_host_once(void) {
    WCHAR path[4096];
    WCHAR directory[4096];
    WCHAR command[8192];
    WCHAR digits[11];
    WCHAR *slash;
    WCHAR *cursor;
    HMODULE module;
    DWORD length;
    DWORD pid;
    int digit_count = 0;
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;

    if (InterlockedCompareExchange(&host_launch_attempted, 1, 0) != 0) return;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)(const void *)&launch_host_once, &module)) return;
    length = GetModuleFileNameW(module, path, 4096);
    if (length == 0 || length >= 4096) return;
    slash = wcsrchr(path, L'\\');
    if (!slash) return;
    *slash = L'\0';
    if (wcslen(path) + 1 + wcslen(L"CrowdPlayHost.exe") >= 4096) return;
    wcscpy(directory, path);
    *slash = L'\\';
    wcscpy(slash + 1, L"CrowdPlayHost.exe");
    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return;
    command[0] = L'"';
    wcscpy(command + 1, path);
    cursor = command + 1 + wcslen(path);
    *cursor++ = L'"';
    wcscpy(cursor, L" --game-pid ");
    cursor += wcslen(cursor);
    pid = GetCurrentProcessId();
    do {
        digits[digit_count++] = L'0' + (WCHAR)(pid % 10);
        pid /= 10;
    } while (pid != 0);
    while (digit_count > 0) *cursor++ = digits[--digit_count];
    *cursor = L'\0';

    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    if (CreateProcessW(path, command, NULL, NULL, FALSE, CREATE_NO_WINDOW,
                       NULL, directory, &startup, &process)) {
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
}

__declspec(dllexport) HRESULT WINAPI DirectInput8Create(HINSTANCE instance, DWORD version,
                                  const GUID *iid, LPVOID *output, LPVOID outer) {
    typedef HRESULT (WINAPI *Fn)(HINSTANCE, DWORD, const GUID *, LPVOID *, LPVOID);
    Fn fn = (Fn)original("DirectInput8Create");
    HRESULT result = fn ? fn(instance, version, iid, output, outer) : E_FAIL;
    launch_host_once();
    return result;
}

__declspec(dllexport) HRESULT WINAPI DllCanUnloadNow(void) {
    typedef HRESULT (WINAPI *Fn)(void);
    Fn fn = (Fn)original("DllCanUnloadNow");
    return fn ? fn() : E_FAIL;
}

__declspec(dllexport) HRESULT WINAPI DllGetClassObject(const GUID *clsid, const GUID *iid, LPVOID *output) {
    typedef HRESULT (WINAPI *Fn)(const GUID *, const GUID *, LPVOID *);
    Fn fn = (Fn)original("DllGetClassObject");
    return fn ? fn(clsid, iid, output) : E_FAIL;
}

__declspec(dllexport) HRESULT WINAPI DllRegisterServer(void) {
    typedef HRESULT (WINAPI *Fn)(void);
    Fn fn = (Fn)original("DllRegisterServer");
    return fn ? fn() : E_FAIL;
}

__declspec(dllexport) HRESULT WINAPI DllUnregisterServer(void) {
    typedef HRESULT (WINAPI *Fn)(void);
    Fn fn = (Fn)original("DllUnregisterServer");
    return fn ? fn() : E_FAIL;
}

__declspec(dllexport) const void *WINAPI GetdfDIJoystick(void) {
    typedef const void *(WINAPI *Fn)(void);
    Fn fn = (Fn)original("GetdfDIJoystick");
    return fn ? fn() : NULL;
}
