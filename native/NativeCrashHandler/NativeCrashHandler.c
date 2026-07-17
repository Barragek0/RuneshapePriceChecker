// NativeCrashHandler.c — Pure native Vectored Exception Handler for .NET apps.
//
// The .NET runtime blocks calling managed delegates from VEH callbacks. This
// native DLL works around that limitation by registering the VEH entirely in
// native code. On crash it writes a detailed log with registers, callstack,
// and source file:line info (via dbghelp.dll).
//
// Build: cl.exe /O2 /LD NativeCrashHandler.c /Fe:NativeCrashHandler.dll
//        (run from a VS x64 Native Tools prompt)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dbghelp.h>
#include <stdio.h>

#pragma comment(lib, "dbghelp.lib")

// ---------------------------------------------------------------------------
// Exported functions
// ---------------------------------------------------------------------------

__declspec(dllexport) void WINAPI RegisterCrashHandler(const wchar_t *logDirectory);
__declspec(dllexport) void WINAPI UnregisterCrashHandler();

// ---------------------------------------------------------------------------
// Internal state
// ---------------------------------------------------------------------------

static void *g_vehHandle = NULL;
static wchar_t g_logDir[MAX_PATH] = {0};
static CRITICAL_SECTION g_cs;

// Symbol resolution state
static HANDLE g_process = NULL;
static BOOL g_symbolsReady = FALSE;

// ---------------------------------------------------------------------------
// Forward declarations
// ---------------------------------------------------------------------------

static LONG WINAPI VectoredHandler(PEXCEPTION_POINTERS ep);

// ---------------------------------------------------------------------------
// Helpers — pure Win32, no CRT dependencies beyond what we need
// ---------------------------------------------------------------------------

static void AppendStr(wchar_t *buf, size_t *pos, size_t cap, const wchar_t *s)
{
    while (*s && *pos < cap - 1)
        buf[(*pos)++] = *s++;
}

static void AppendHex64(wchar_t *buf, size_t *pos, size_t cap, ULONGLONG val)
{
    for (int i = 15; i >= 0; i--)
    {
        if (*pos >= cap - 1)
            break;
        int nibble = (int)((val >> (i * 4)) & 0xF);
        buf[(*pos)++] = nibble < 10 ? L'0' + nibble : L'A' + nibble - 10;
    }
}

static void AppendHex32(wchar_t *buf, size_t *pos, size_t cap, DWORD val)
{
    for (int i = 7; i >= 0; i--)
    {
        if (*pos >= cap - 1)
            break;
        int nibble = (int)((val >> (i * 4)) & 0xF);
        buf[(*pos)++] = nibble < 10 ? L'0' + nibble : L'A' + nibble - 10;
    }
}

static void AppendDec(wchar_t *buf, size_t *pos, size_t cap, DWORD val)
{
    wchar_t tmp[16];
    int i = 0;
    if (val == 0)
    {
        tmp[i++] = L'0';
    }
    else
    {
        while (val > 0 && i < 16)
        {
            tmp[i++] = L'0' + (val % 10);
            val /= 10;
        }
    }
    // Reverse
    while (i > 0 && *pos < cap - 1)
        buf[(*pos)++] = tmp[--i];
}

static void AppendNewline(wchar_t *buf, size_t *pos, size_t cap)
{
    if (*pos + 2 >= cap)
        return;
    buf[(*pos)++] = L'\r';
    buf[(*pos)++] = L'\n';
}

static void AppendReg(wchar_t *buf, size_t *pos, size_t cap, const wchar_t *name, DWORD64 value)
{
    // Write "NAME  0xVALUE\n"
    while (*name && *pos < cap - 1)
        buf[(*pos)++] = *name++;
    if (*pos + 2 >= cap)
        return;
    buf[(*pos)++] = L' ';
    buf[(*pos)++] = L' ';
    buf[(*pos)++] = L'0';
    buf[(*pos)++] = L'x';
    AppendHex64(buf, pos, cap, value);
    AppendNewline(buf, pos, cap);
}

// ---------------------------------------------------------------------------
// Timestamp (YYYY-MM-DD HH:MM:SS.fff)
// ---------------------------------------------------------------------------

static void AppendTimestamp(wchar_t *buf, size_t *pos, size_t cap)
{
    SYSTEMTIME st;
    GetLocalTime(&st);

    AppendDec(buf, pos, cap, st.wYear);
    buf[(*pos)++] = L'-';
    if (st.wMonth < 10)
        buf[(*pos)++] = L'0';
    AppendDec(buf, pos, cap, st.wMonth);
    buf[(*pos)++] = L'-';
    if (st.wDay < 10)
        buf[(*pos)++] = L'0';
    AppendDec(buf, pos, cap, st.wDay);
    buf[(*pos)++] = L' ';
    if (st.wHour < 10)
        buf[(*pos)++] = L'0';
    AppendDec(buf, pos, cap, st.wHour);
    buf[(*pos)++] = L':';
    if (st.wMinute < 10)
        buf[(*pos)++] = L'0';
    AppendDec(buf, pos, cap, st.wMinute);
    buf[(*pos)++] = L':';
    if (st.wSecond < 10)
        buf[(*pos)++] = L'0';
    AppendDec(buf, pos, cap, st.wSecond);
    buf[(*pos)++] = L'.';
    AppendDec(buf, pos, cap, st.wMilliseconds);
}

// ---------------------------------------------------------------------------
// Exception code to name
// ---------------------------------------------------------------------------

static const wchar_t *CodeName(DWORD code)
{
    switch (code)
    {
    case EXCEPTION_ACCESS_VIOLATION:
        return L"ACCESS_VIOLATION";
    case EXCEPTION_STACK_OVERFLOW:
        return L"STACK_OVERFLOW";
    case 0x80131506:
        return L"COR_E_EXECUTIONENGINE";
    default:
        return L"UNKNOWN";
    }
}

// ---------------------------------------------------------------------------
// Symbol resolution
// ---------------------------------------------------------------------------

static void InitSymbols()
{
    g_process = GetCurrentProcess();

    SymSetOptions(SYMOPT_LOAD_LINES | SYMOPT_UNDNAME);

    // Try to resolve PDB from the exe directory
    wchar_t exePath[MAX_PATH];
    DWORD len = GetModuleFileNameW(NULL, exePath, MAX_PATH);
    if (len > 0)
    {
        wchar_t *slash = wcsrchr(exePath, L'\\');
        if (slash)
            *slash = L'\0';
        g_symbolsReady = SymInitializeW(g_process, exePath, TRUE);
    }
    else
    {
        g_symbolsReady = SymInitializeW(g_process, NULL, TRUE);
    }
}

static void ResolveAddress(wchar_t *buf, size_t *pos, size_t cap, DWORD64 address)
{
    if (!g_symbolsReady)
        return;

    // Try source line first (ANSI FileName from dbghelp — convert to wide)
    IMAGEHLP_LINE64 line = {0};
    line.SizeOfStruct = sizeof(line);
    DWORD displacement;
    if (SymGetLineFromAddr64(g_process, address, &displacement, &line) && line.FileName)
    {
        // Convert ANSI file name to wide
        int wideLen = MultiByteToWideChar(CP_ACP, 0, line.FileName, -1, NULL, 0);
        if (wideLen > 0)
        {
            wchar_t *wideBuf = (wchar_t *)_alloca(wideLen * sizeof(wchar_t));
            MultiByteToWideChar(CP_ACP, 0, line.FileName, -1, wideBuf, wideLen);
            AppendStr(buf, pos, cap, wideBuf);
        }
        if (*pos < cap - 1)
            buf[(*pos)++] = L':';
        AppendDec(buf, pos, cap, line.LineNumber);
        return;
    }

    // Try symbol name (use ANSI SymFromAddr with a properly sized buffer)
    BYTE symBuffer[sizeof(SYMBOL_INFO) + 256 * sizeof(CHAR)];
    PSYMBOL_INFO sym = (PSYMBOL_INFO)symBuffer;
    ZeroMemory(symBuffer, sizeof(symBuffer));
    sym->SizeOfStruct = sizeof(SYMBOL_INFO);
    sym->MaxNameLen = 256;
    DWORD64 symDisplacement;
    if (SymFromAddr(g_process, address, &symDisplacement, sym))
    {
        // Convert ANSI symbol name to wide
        int wideLen = MultiByteToWideChar(CP_ACP, 0, sym->Name, -1, NULL, 0);
        if (wideLen > 0)
        {
            wchar_t *wideBuf = (wchar_t *)_alloca(wideLen * sizeof(wchar_t));
            MultiByteToWideChar(CP_ACP, 0, sym->Name, -1, wideBuf, wideLen);
            AppendStr(buf, pos, cap, wideBuf);
        }
        return;
    }
}

// ---------------------------------------------------------------------------
// Vectored exception handler
// ---------------------------------------------------------------------------

static LONG WINAPI VectoredHandler(PEXCEPTION_POINTERS ep)
{
    // Reentrancy guard — static volatile is safe in native code
    static volatile LONG handling = 0;
    if (InterlockedCompareExchange(&handling, 1, 0) != 0)
        return EXCEPTION_CONTINUE_SEARCH;

    PEXCEPTION_RECORD rec = ep->ExceptionRecord;
    PCONTEXT ctx = ep->ContextRecord;

    // Only log actual fatal exceptions: ACCESS_VIOLATION, STACK_OVERFLOW,
    // and COR_E_EXECUTIONENGINE.  Skip managed CLR exceptions (0xE0434352)
    // and other non-fatal codes — those are handled by the CLR and produce
    // noise in the logs folder.
    DWORD code = rec->ExceptionCode;
    if (code != EXCEPTION_ACCESS_VIOLATION &&
        code != EXCEPTION_STACK_OVERFLOW &&
        code != 0x80131506) // COR_E_EXECUTIONENGINE
    {
        handling = 0;
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // Buffer for the crash file content (wide chars, ~8KB).
    // Use VirtualAlloc instead of stack allocation so we don't exhaust
    // remaining stack during a stack overflow.
    wchar_t *lineBuf = (wchar_t *)VirtualAlloc(NULL, 8192 * sizeof(wchar_t), MEM_COMMIT, PAGE_READWRITE);
    if (!lineBuf)
        return EXCEPTION_CONTINUE_SEARCH;
    size_t pos = 0;
    size_t cap = 8192;

    AppendStr(lineBuf, &pos, cap, L"========================================\r\n");
    AppendStr(lineBuf, &pos, cap, L"RuneshapePriceChecker Native Crash Report\r\n");
    AppendStr(lineBuf, &pos, cap, L"Generated: ");
    AppendTimestamp(lineBuf, &pos, cap);
    AppendNewline(lineBuf, &pos, cap);

    AppendStr(lineBuf, &pos, cap, L"PID:       ");
    AppendDec(lineBuf, &pos, cap, GetCurrentProcessId());
    AppendNewline(lineBuf, &pos, cap);

    AppendStr(lineBuf, &pos, cap, L"TID:       ");
    AppendDec(lineBuf, &pos, cap, GetCurrentThreadId());
    AppendNewline(lineBuf, &pos, cap);

    // Module base for offset calculation
    wchar_t exeName[MAX_PATH];
    DWORD exeNameLen = GetModuleFileNameW(NULL, exeName, MAX_PATH);
    ULONGLONG exeBase = (ULONGLONG)GetModuleHandleW(NULL);

    AppendStr(lineBuf, &pos, cap, L"Exe:       ");
    if (exeNameLen > 0)
        AppendStr(lineBuf, &pos, cap, exeName);
    AppendNewline(lineBuf, &pos, cap);

    AppendStr(lineBuf, &pos, cap, L"Exe base:  0x");
    AppendHex64(lineBuf, &pos, cap, exeBase);
    AppendNewline(lineBuf, &pos, cap);

    // Exception info
    AppendStr(lineBuf, &pos, cap, L"Code:      0x");
    AppendHex32(lineBuf, &pos, cap, rec->ExceptionCode);
    AppendStr(lineBuf, &pos, cap, L" (");
    AppendStr(lineBuf, &pos, cap, CodeName(rec->ExceptionCode));
    AppendStr(lineBuf, &pos, cap, L")");
    AppendNewline(lineBuf, &pos, cap);

    AppendStr(lineBuf, &pos, cap, L"Flags:     0x");
    AppendHex32(lineBuf, &pos, cap, rec->ExceptionFlags);
    AppendNewline(lineBuf, &pos, cap);

    AppendStr(lineBuf, &pos, cap, L"Address:   0x");
    AppendHex64(lineBuf, &pos, cap, (ULONGLONG)rec->ExceptionAddress);
    AppendNewline(lineBuf, &pos, cap);

    ULONGLONG offset = (ULONGLONG)rec->ExceptionAddress - exeBase;
    AppendStr(lineBuf, &pos, cap, L"Offset:    0x");
    AppendHex64(lineBuf, &pos, cap, offset);
    AppendNewline(lineBuf, &pos, cap);

    // Access violation detail
    if (rec->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && rec->NumberParameters >= 2)
    {
        AppendStr(lineBuf, &pos, cap, L"Operation: ");
        AppendStr(lineBuf, &pos, cap, rec->ExceptionInformation[0] != 0 ? L"WRITE to" : L"READ from");
        AppendNewline(lineBuf, &pos, cap);

        AppendStr(lineBuf, &pos, cap, L"Target:    0x");
        AppendHex64(lineBuf, &pos, cap, rec->ExceptionInformation[1]);
        AppendNewline(lineBuf, &pos, cap);
    }

    // Registers
    if (ctx != NULL)
    {
        AppendNewline(lineBuf, &pos, cap);
        AppendStr(lineBuf, &pos, cap, L"-- Registers --");
        AppendNewline(lineBuf, &pos, cap);

#if defined(_M_AMD64)
        AppendReg(lineBuf, &pos, cap, L"RAX", ctx->Rax);
        AppendReg(lineBuf, &pos, cap, L"RBX", ctx->Rbx);
        AppendReg(lineBuf, &pos, cap, L"RCX", ctx->Rcx);
        AppendReg(lineBuf, &pos, cap, L"RDX", ctx->Rdx);
        AppendReg(lineBuf, &pos, cap, L"RSI", ctx->Rsi);
        AppendReg(lineBuf, &pos, cap, L"RDI", ctx->Rdi);
        AppendReg(lineBuf, &pos, cap, L"RBP", ctx->Rbp);
        AppendStr(lineBuf, &pos, cap, L"RIP     0x");
        AppendHex64(lineBuf, &pos, cap, ctx->Rip);
        AppendNewline(lineBuf, &pos, cap);
        AppendReg(lineBuf, &pos, cap, L"RSP", ctx->Rsp);
        AppendReg(lineBuf, &pos, cap, L"R8", ctx->R8);
        AppendReg(lineBuf, &pos, cap, L"R9", ctx->R9);
        AppendReg(lineBuf, &pos, cap, L"R10", ctx->R10);
        AppendReg(lineBuf, &pos, cap, L"R11", ctx->R11);
        AppendReg(lineBuf, &pos, cap, L"R12", ctx->R12);
        AppendReg(lineBuf, &pos, cap, L"R13", ctx->R13);
        AppendReg(lineBuf, &pos, cap, L"R14", ctx->R14);
        AppendReg(lineBuf, &pos, cap, L"R15", ctx->R15);
#endif
    }

    // Crash location (source file:line)
    if (g_symbolsReady)
    {
        AppendNewline(lineBuf, &pos, cap);
        AppendStr(lineBuf, &pos, cap, L"-- Crash location --");
        AppendNewline(lineBuf, &pos, cap);
        ResolveAddress(lineBuf, &pos, cap, (ULONGLONG)rec->ExceptionAddress);
        AppendNewline(lineBuf, &pos, cap);

        // Native callstack via RtlCaptureStackBackTrace
        AppendNewline(lineBuf, &pos, cap);
        AppendStr(lineBuf, &pos, cap, L"-- Callstack --");
        AppendNewline(lineBuf, &pos, cap);

        void *frames[32];
        WORD frameCount = RtlCaptureStackBackTrace(0, 32, frames, NULL);
        for (WORD f = 0; f < frameCount; f++)
        {
            DWORD64 frameAddr = (DWORD64)frames[f];
            if (frameAddr == 0)
                break;

            // Frame index prefix
            lineBuf[pos++] = L' ';
            lineBuf[pos++] = L' ';
            lineBuf[pos++] = L'[';
            AppendDec(lineBuf, &pos, cap, f);
            lineBuf[pos++] = L']';
            lineBuf[pos++] = L' ';
            lineBuf[pos++] = L' ';

            ResolveAddress(lineBuf, &pos, cap, frameAddr);

            // Offset from module base
            if (!g_symbolsReady)
            {
                AppendStr(lineBuf, &pos, cap, L" +0x");
                AppendHex64(lineBuf, &pos, cap, frameAddr - exeBase);
            }

            AppendNewline(lineBuf, &pos, cap);
        }
    }

    AppendStr(lineBuf, &pos, cap, L"========================================");
    AppendNewline(lineBuf, &pos, cap);

    // Write the crash file using only Win32 APIs
    // File name: {logDir}\{timestamp}-native-crash.txt
    wchar_t filePath[MAX_PATH];
    size_t fp = 0;
    AppendStr(filePath, &fp, MAX_PATH, g_logDir);
    filePath[fp++] = L'\\';

    SYSTEMTIME st;
    GetLocalTime(&st);
    AppendDec(filePath, &fp, MAX_PATH, st.wYear);
    if (st.wMonth < 10)
        filePath[fp++] = L'0';
    AppendDec(filePath, &fp, MAX_PATH, st.wMonth);
    if (st.wDay < 10)
        filePath[fp++] = L'0';
    AppendDec(filePath, &fp, MAX_PATH, st.wDay);
    filePath[fp++] = L'-';
    if (st.wHour < 10)
        filePath[fp++] = L'0';
    AppendDec(filePath, &fp, MAX_PATH, st.wHour);
    if (st.wMinute < 10)
        filePath[fp++] = L'0';
    AppendDec(filePath, &fp, MAX_PATH, st.wMinute);
    if (st.wSecond < 10)
        filePath[fp++] = L'0';
    AppendDec(filePath, &fp, MAX_PATH, st.wSecond);
    AppendStr(filePath, &fp, MAX_PATH, L"-native-crash.txt");
    filePath[fp] = L'\0';

    // Create the log directory if it doesn't exist
    CreateDirectoryW(g_logDir, NULL);

    // Write file
    HANDLE hFile = CreateFileW(filePath, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS,
                               FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile != INVALID_HANDLE_VALUE)
    {
        // Convert wide chars to UTF-8 for output
        int utf8Len = WideCharToMultiByte(CP_UTF8, 0, lineBuf, (int)pos, NULL, 0, NULL, NULL);
        if (utf8Len > 0)
        {
            char *utf8Buf = (char *)_alloca(utf8Len);
            WideCharToMultiByte(CP_UTF8, 0, lineBuf, (int)pos, utf8Buf, utf8Len, NULL, NULL);

            DWORD written;
            WriteFile(hFile, utf8Buf, utf8Len, &written, NULL);
        }
        CloseHandle(hFile);
    }

    VirtualFree(lineBuf, 0, MEM_RELEASE);

    handling = 0;
    return EXCEPTION_CONTINUE_SEARCH;
}

// ---------------------------------------------------------------------------
// Exported API
// ---------------------------------------------------------------------------

void WINAPI RegisterCrashHandler(const wchar_t *logDirectory)
{
    if (g_vehHandle != NULL)
        return;

    InitializeCriticalSection(&g_cs);

    if (logDirectory)
    {
        wcsncpy_s(g_logDir, MAX_PATH, logDirectory, _TRUNCATE);
    }

    // Initialize symbol resolver (best-effort)
    InitSymbols();

    g_vehHandle = AddVectoredExceptionHandler(1, VectoredHandler);
}

void WINAPI UnregisterCrashHandler()
{
    if (g_vehHandle)
    {
        RemoveVectoredExceptionHandler(g_vehHandle);
        g_vehHandle = NULL;
    }

    if (g_symbolsReady)
    {
        SymCleanup(g_process);
        g_symbolsReady = FALSE;
    }

    DeleteCriticalSection(&g_cs);
}

// ---------------------------------------------------------------------------
// DllMain
// ---------------------------------------------------------------------------

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved)
{
    (void)hinstDLL;
    (void)lpvReserved;
    if (fdwReason == DLL_PROCESS_DETACH)
    {
        UnregisterCrashHandler();
    }
    return TRUE;
}
