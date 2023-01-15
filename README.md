## asrled
This utility is designed to intercept `DeviceIoControl` calls inside `wICPFlash.exe` (ASRock/Nuvoton ICP flash tool)

It works by wrapping `version.dll` (loaded by the program)

A `DllMain` hook loads the original `version.dll`, then loads C# and runs `ManagedHook`

ManagedHook replaces `DeviceIoControl` with a hook that enqueues incoming requests for processing

The queue processor extracts GPIO reads/writes and produces a log file.

This log file can then be used in PulseView for analysis

