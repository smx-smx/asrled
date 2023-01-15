/**
  * DLL proxy
  * (C) 2023 Stefano Moioli <smxdev4@gmail.com>
  **/

#include <cstdio>
#include <cstdint>

#include <iostream>
#include <string>
#include <string_view>
#include <sstream>
#include <filesystem>
#include <map>
#include <vector>
#include <unordered_map>
#include <Windows.h>

HMODULE hModule = static_cast<HMODULE>(INVALID_HANDLE_VALUE);

using fn_map_t = std::map<std::string, void *, std::less<>>;

static std::map<std::string, std::pair<HINSTANCE, fn_map_t>, std::less<>> mod_map;
static fn_map_t fn_map;

extern "C" {
	__stdcall bool launch_debugger();
    __stdcall void *get_fptr(const char *lib_name, const char *name_ptr);
    void __attribute__((weak)) post_init(){}
}

//https://stackoverflow.com/a/20387632
__stdcall bool launch_debugger() {
	std::wstring systemDir(MAX_PATH + 1, '\0');
	UINT nChars = GetSystemDirectoryW(&systemDir[0], systemDir.length());
	if (nChars == 0)
		return false;
	systemDir.resize(nChars);

	DWORD pid = GetCurrentProcessId();
	std::wostringstream s;
	s << systemDir << L"\\vsjitdebugger.exe -p " << pid;
	std::wstring cmdLine = s.str();

	STARTUPINFOW si;
	memset(&si, 0x00, sizeof(si));
	si.cb = sizeof(si);

	PROCESS_INFORMATION pi;
	memset(&pi, 0x00, sizeof(pi));

	if (!CreateProcessW(
		nullptr, &cmdLine[0],
		nullptr, nullptr,
		false, 0, nullptr, nullptr,
		&si, &pi
	)) {
		return false;
	}

	CloseHandle(pi.hThread);
	CloseHandle(pi.hProcess);

	while (!IsDebuggerPresent())
		Sleep(100);

	DebugBreak();
	return true;
}

void null_catch(){
	for(;;){
		puts("NO FPTR RESOLVED!");
		//launch_debugger();
		getchar();
		break;
	}
}

uintptr_t forced_stub(){
	void *caller = __builtin_return_address(0);
	printf("STUB! (caller: %p)\n", caller);
	return 0;
}

HINSTANCE load_original_library(const char *lib_name){
	UINT size = GetWindowsDirectoryA(NULL, 0);
    std::wstring buf(size, '\x00');

	GetWindowsDirectoryW(buf.data(), size);
	std::wstring win_dir(buf.data(), size-1);

	std::filesystem::path path(win_dir);
    path /= "System32";
	path /= std::string(lib_name) + ".dll";

	return LoadLibraryW(path.c_str());
}

int main(){
	load_original_library("version");
	return 0;
}

__stdcall void *get_fptr(const char *lib_name, const char *name_ptr){
	std::string_view mod_name(lib_name);

	auto mod_item = ::mod_map.find(mod_name);
	if(mod_item == ::mod_map.end()){
		printf(">> module '%s' not found, loading...\n", lib_name);
		HINSTANCE handle = load_original_library(lib_name);
		if(handle != nullptr && handle != INVALID_HANDLE_VALUE){
			auto inserted = mod_map.insert(std::make_pair(mod_name, std::make_pair<HINSTANCE&, fn_map_t>(handle, fn_map_t())));
            mod_item = inserted.first;
		} else {
            fprintf(stderr, "failed to load module\n");
            return nullptr;
        }
	}

	HMODULE mod_handle = mod_item->second.first;
	auto fn_map = mod_item->second.second;

	std::string_view name(name_ptr);
	auto fn_item = fn_map.find(name);
	if(fn_item != fn_map.end()){
		return fn_item->second;
	}

	void *fn = reinterpret_cast<void *>(
		GetProcAddress(mod_handle, name_ptr)
	);
	fn_map.emplace(name, fn);

	if(fn == nullptr){
		return reinterpret_cast<void *>(&null_catch);
	}
	return fn;
}

BOOL WINAPI DllMain(
HINSTANCE hinstDLL,
DWORD fdwReason,
LPVOID lpReserved ){
	switch( fdwReason ) {
		case DLL_PROCESS_ATTACH:
			AllocConsole();
			freopen("CONIN$", "r", stdin);
			freopen("CONOUT$", "w", stdout);
			freopen("CONOUT$", "w", stderr);
			setvbuf(stdout, nullptr, _IONBF, 0);
			setvbuf(stderr, nullptr, _IONBF, 0);
			puts("== Proxy Loaded ==");
            post_init();
			break;

		case DLL_THREAD_ATTACH:
			break;

		case DLL_THREAD_DETACH:
			break;

		case DLL_PROCESS_DETACH:
			break;
	}
	return TRUE;
}
