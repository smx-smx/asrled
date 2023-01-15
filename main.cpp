/**
  * wICPFLASH interceptor
  * (C) 2023 Stefano Moioli <smxdev4@gmail.com>
  **/

#include <cstdio>
#include <MinHook.h>

#include <ntdef.h>

#include <sstream>
#include <iomanip>
#include <fstream>
#include <chrono>
#include <iostream>
#include <vector>
#include <winternl.h>
#include <ntstatus.h>
#include "util.h"

#include <filesystem>

typedef std::basic_string<TCHAR, std::char_traits<TCHAR>, std::allocator<TCHAR>> tstring;

typedef BOOL (WINAPI *PfnDeviceIoControl)(
    HANDLE       hDevice,
    DWORD        dwIoControlCode,
    LPVOID       lpInBuffer,
    DWORD        nInBufferSize,
    LPVOID       lpOutBuffer,
    DWORD        nOutBufferSize,
    LPDWORD      lpBytesReturned,
    LPOVERLAPPED lpOverlapped
);


static PfnDeviceIoControl o_DeviceIoControl = nullptr;

static void dump_buffer(std::stringstream& ss, LPVOID pBuf, DWORD bufSize){
    uint8_t *pBytes = static_cast<uint8_t *>(pBuf);
    bool first = true;
    ss << std::hex << std::setfill('0');
    for(DWORD i=0; i<bufSize; i++){
        if(first) first = false;
        ss << std::setw(2) << (int)pBytes[i];
    }
    ss << std::cout.fill();
    ss << std::dec;
}

std::ofstream s_log;

#if 0
typedef struct _OBJECT_NAME_INFORMATION
{
    UNICODE_STRING Name;
} OBJECT_NAME_INFORMATION, *POBJECT_NAME_INFORMATION;


LPWSTR GetObjectName(HANDLE hObject)
{
    LPWSTR lpwsReturn = NULL;
    DWORD dwSize = sizeof(OBJECT_NAME_INFORMATION);
    POBJECT_NAME_INFORMATION pObjectInfo = {0};
    NTSTATUS ntReturn = NtQueryObject(hObject, ObjectNameInformation, pObjectInfo, dwSize, &dwSize);
    if(ntReturn == STATUS_BUFFER_OVERFLOW){
        delete pObjectInfo;
        pObjectInfo = (POBJECT_NAME_INFORMATION) new BYTE[dwSize];
        ntReturn = NtQueryObject(hObject, ObjectNameInformation, pObjectInfo, dwSize, &dwSize);
    }
    if((ntReturn >= STATUS_SUCCESS) && (pObjectInfo->Buffer != NULL))
    {
        lpwsReturn = (LPWSTR) new BYTE[pObjectInfo->Length + sizeof(WCHAR)];
        ZeroMemory(lpwsReturn, pObjectInfo->Length + sizeof(WCHAR));
        CopyMemory(lpwsReturn, pObjectInfo->Buffer, pObjectInfo->Length);
    }
    delete pObjectInfo;
    return lpwsReturn;
}

tstring get_handle_filename(HANDLE handle){
    DWORD bufsize = GetFinalPathNameByHandle(handle, nullptr, 0, 0);
    tstring filename(bufsize, '\x00');
    GetFinalPathNameByHandle(handle, filename.data(), bufsize, 0);
    return filename;
}
#endif

struct AsrockMemOp {
    /* 0x00 */ LARGE_INTEGER mem_addr;
    /* 0x08 */ DWORD mem_size;
    /* 0x0C */ DWORD transfer_size;
    /* 0x10 */ LPVOID buffer;
};

struct AsrockPciOp {
    /* 0x00 */ byte bus;
    /* 0x01 */ byte dev;
    /* 0x02 */ uint16_t func;
    /* 0x04 */ uint32_t offset;
    /* 0x08 */ uint32_t result;
};

enum class AsrockOp : DWORD {
    MEM_READ = 0x222808,
    MEM_WRITE = 0x22280c,
    PCI_READ1 = 0x222830,
    PCI_READ4 = 0x222840,
    PCI_WRITE4 = 0x222838,
};

extern "C" {
    __stdcall bool launch_debugger();
}

static void dump_asrock_pci_op(std::stringstream& ss, DWORD opCode, LPVOID opBuffer, DWORD opSize) {
    if(opSize < sizeof(AsrockPciOp)) return;
    AsrockPciOp *op = reinterpret_cast<AsrockPciOp *>(opBuffer);

    ss << util::ssprintf("  asrock pci op (%02X:%02X.%02X %x) -- 0x%x\n",
                         op->bus, op->dev, op->func, op->offset,
                         op->result);
}

static void dump_asrock_mem_op(std::stringstream& ss, DWORD opCode, LPVOID opBuffer, DWORD opSize){
    if(opSize < sizeof(AsrockMemOp)) return;
    AsrockMemOp *op = reinterpret_cast<AsrockMemOp *>(opBuffer);

    ss << util::ssprintf("  asrock mem op (mem_addr: 0x%08X%08X, mem_size: %d, txrx: %d @%p)\n",
                         op->mem_addr.HighPart,
                         op->mem_addr.LowPart,
                         op->mem_size,
                         op->transfer_size,
                         op->buffer);

    DWORD size = (op->transfer_size) ? op->transfer_size : op->mem_size;
    dump_buffer(ss, op->buffer, size);
    ss << "\n";
}


WINAPI BOOL DeviceIoControlHook(
    HANDLE       hDevice,
    DWORD        dwIoControlCode,
    LPVOID       lpInBuffer,
    DWORD        nInBufferSize,
    LPVOID       lpOutBuffer,
    DWORD        nOutBufferSize,
    LPDWORD      lpBytesReturned,
    LPOVERLAPPED lpOverlapped
){
    BOOL result = o_DeviceIoControl(
        hDevice,
        dwIoControlCode,
        lpInBuffer, nInBufferSize,
        lpOutBuffer, nOutBufferSize,
        lpBytesReturned, lpOverlapped);

    std::stringstream ss;
    ss << ">> ";
    ss << util::ssprintf("%p", hDevice) << " ";
    ss << std::hex << dwIoControlCode << std::dec << " ";
    ss << "[" << nInBufferSize << "] ";
    dump_buffer(ss, lpInBuffer, nInBufferSize);
    ss << "\n";

    switch((AsrockOp)dwIoControlCode){
        case AsrockOp::MEM_READ:
            ss << " mem_read\n";
            dump_asrock_mem_op(ss, dwIoControlCode, lpInBuffer, nInBufferSize);
            break;
        case AsrockOp::MEM_WRITE:
            ss << " mem_write\n";
            dump_asrock_mem_op(ss, dwIoControlCode, lpInBuffer, nInBufferSize);
            break;
        case AsrockOp::PCI_READ1:
            ss << " pci_read1\n";
            dump_asrock_pci_op(ss, dwIoControlCode, lpInBuffer, nInBufferSize);
            break;
        case AsrockOp::PCI_READ4:
            ss << " pci_read4\n";
            dump_asrock_pci_op(ss, dwIoControlCode, lpInBuffer, nInBufferSize);
            break;
        case AsrockOp::PCI_WRITE4:
            ss << " pci_write4\n";
            dump_asrock_pci_op(ss, dwIoControlCode, lpInBuffer, nInBufferSize);
            break;
    }

    if(result){
        ss << "<< OK ";
        ss << "[" << nOutBufferSize << "] ";
            dump_buffer(ss, lpOutBuffer, nOutBufferSize);
        ss << "\n";
    } else {
        ss << "<< FAIL\n";
    }
    s_log << ss.str();

    return result;
}

extern "C" {
    void post_init();
}

void at_exit(){
    puts("finished");
    s_log.close();
}

static std::string get_executable_path(){
    DWORD bufSize = GetModuleFileNameA(NULL, NULL, 0);
    tstring buf(bufSize, '\0');
    GetModuleFileNameA(NULL, buf.data(), bufSize);
    return buf;
}

typedef int (*pfnEzDotNetMain)(int argc, const char *argv[]);

WINAPI DWORD start_dotnet(LPVOID arg){
    (void)arg;

    std::filesystem::path exe_path(get_executable_path());
    auto exe_dir = exe_path.parent_path();

    auto lib_path = (exe_dir / "libezdotnet_shared.dll").string();

    HMODULE ezDotNet = LoadLibraryA(lib_path.c_str());
    if(ezDotNet == nullptr || ezDotNet == INVALID_HANDLE_VALUE){
        fprintf(stderr, "LoadLibraryA failed\n");
        return 1;
    }
    FARPROC main = GetProcAddress(ezDotNet, "main");
    if(main == nullptr) {
        fprintf(stderr, "GetProcAddress failed\n");
        return 1;
    }

    auto loader_path = (exe_dir / "libcoreclrhost.dll").string();
    auto asm_path = (exe_dir / "Release" / "net7.0" / "win-x86" / "publish" / "ManagedHook.dll").string();

    pfnEzDotNetMain pfnMain = reinterpret_cast<pfnEzDotNetMain>(main);
    const char *argv[] = {
        "ezdotnet",
        loader_path.c_str(),
        asm_path.c_str(),
        "ManagedHook.EntryPoint",
        "Entry"
    };
    pfnMain(_countof(argv), argv);
    return 0;
}


void post_init(){
#if 0
    s_log = std::ofstream("s_log.txt");

    if(MH_Initialize() != MH_OK){
        fprintf(stderr, "MH_Initialize() failed\n");
        return;
    }

    if(MH_CreateHook(
        LPVOID(&DeviceIoControl),
        LPVOID(&DeviceIoControlHook),
        reinterpret_cast<LPVOID*>(&o_DeviceIoControl)
    ) != MH_OK){
        fprintf(stderr, "MH_CreateHook() failed\n");
        return;
    }
    if(MH_EnableHook(LPVOID(&DeviceIoControl)) != MH_OK){
        fprintf(stderr, "MH_EnableHook() failed\n");
        return;
    }

    atexit(at_exit);

    puts("init done, press Enter to start main program");
    getchar();

#endif
    DWORD tid;
    CreateThread(nullptr, 0, start_dotnet, nullptr, 0, &tid);

    puts("init done, press Enter to start main program");
    getchar();
}
