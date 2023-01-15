/**
  * Copyright (C) 2023 Stefano Moioli <smxdev4@gmail.com>
  */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Schema;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ManagedHook
{
    public class Program : IDisposable
    {
        [DllImport("MinHook.x86", CallingConvention = CallingConvention.Winapi)]
        private static extern int MH_Initialize();
        [DllImport("MinHook.x86", CallingConvention = CallingConvention.Winapi)]
        private static extern int MH_CreateHook(nint pTarget, nint pDetour, nint ppOriginal);
        [DllImport("MinHook.x86", CallingConvention = CallingConvention.Winapi)]
        private static extern int MH_EnableHook(nint pTarget);
        [DllImport("MinHook.x86", CallingConvention = CallingConvention.Winapi)]
        private static extern int MH_DisableHook(nint pTarget);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public delegate bool pfnDeviceIoControl(
            nint hDevice,
            [MarshalAs(UnmanagedType.U4)]
            uint dwIoControlCode,
            nint lpInBuffer,
            [MarshalAs(UnmanagedType.U4)]
            uint nInBufferSize,
            nint lpOutBuffer,
            [MarshalAs(UnmanagedType.U4)]
            uint nOutBufferSize,
            nint lpBytesReturned,
            nint lpOverlapped
        );

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void pfnExitProcess(uint exitCode);

        public static nint ExitProcess_Addr;
        public static nint ExitProcess_Hook;
        public static nint ExitProcess_PointerStorage;
        public static pfnExitProcess ExitProcess_Fn;

        public static nint DeviceIoControl_Addr;
        public static nint DeviceIoControl_Hook;
        public static nint DeviceIoControl_PointerStorage;
        public static pfnDeviceIoControl DeviceIoControl_Fn;

        public static AsrockDeviceIoControlHandler handler = new AsrockDeviceIoControlHandler("log.txt");

        private nint kernel32;

        public Program()
        {
            DeviceIoControl_Hook = Marshal.GetFunctionPointerForDelegate<pfnDeviceIoControl>(DeviceIoControlHook);
            ExitProcess_Hook = Marshal.GetFunctionPointerForDelegate<pfnExitProcess>(ExitProcessHook);

            if (!NativeLibrary.TryLoad("kernel32.dll", out kernel32))
            {
                throw new Exception("Cannot acquire kernel32.dll");               
            }
            if (!NativeLibrary.TryGetExport(kernel32, "DeviceIoControl", out DeviceIoControl_Addr)
                || !NativeLibrary.TryGetExport(kernel32, "ExitProcess", out ExitProcess_Addr)
            )
            {
                throw new Exception("Cannot resolve symbols");
            }

            if (MH_Initialize() != 0)
            {
                throw new Exception("MH_Initialize failed");
            }

            // create storage for the original function pointer
            DeviceIoControl_PointerStorage = Marshal.AllocHGlobal(IntPtr.Size);
            ExitProcess_PointerStorage = Marshal.AllocHGlobal(IntPtr.Size);

            if (MH_CreateHook(DeviceIoControl_Addr, DeviceIoControl_Hook, DeviceIoControl_PointerStorage) != 0
                || MH_CreateHook(ExitProcess_Addr, ExitProcess_Hook, ExitProcess_PointerStorage) != 0
            ){
                throw new Exception("MH_CreateHook failed");
            }

            DeviceIoControl_Fn = Marshal.GetDelegateForFunctionPointer<pfnDeviceIoControl>(
                // retrieve the function pointer
                Marshal.ReadIntPtr(DeviceIoControl_PointerStorage)
            );

            ExitProcess_Fn = Marshal.GetDelegateForFunctionPointer<pfnExitProcess>(
                Marshal.ReadIntPtr(ExitProcess_PointerStorage)
            );

            if (MH_EnableHook(DeviceIoControl_Addr) != 0
                || MH_EnableHook(ExitProcess_Addr) != 0
            ) {
                throw new Exception("MH_EnableHook failed");
            }
        }

        private static Program? programInstance;

        public void Dispose()
        {
            if (MH_DisableHook(DeviceIoControl_Addr) != 0)
            {
                Console.Error.WriteLine("MH_DisableHook failed");
            }
            else
            {
                Marshal.FreeHGlobal(DeviceIoControl_PointerStorage);
            }
            handler.Stop();
            handler.Dispose();
        }

        private static byte[] ReadMemory(nint addr, nint size)
        {
            if (addr == 0 || size < 1) return new byte[0];
            byte[] data = new byte[size];
            try
            {
                Marshal.Copy(addr, data, 0, data.Length);
            }
            catch (Exception)
            {
                return new byte[0];
            }
            return data;
        }

        public static void ExitProcessHook(uint exitCode)
        {
            Console.WriteLine("ManagedHook is exiting");
            if (programInstance == null) return;
            programInstance.Dispose();

            Console.WriteLine("bye");
            ExitProcess_Fn(exitCode);
        }

        public static bool DeviceIoControlHook(
            nint hDevice,
            uint dwIoControlCode,
            nint lpInBuffer,
            uint nInBufferSize,
            nint lpOutBuffer,
            uint nOutBufferSize,
            nint lpBytesReturned,
            nint lpOverlapped
        )
        {
            bool result = DeviceIoControl_Fn(hDevice, dwIoControlCode,
                    lpInBuffer, nInBufferSize,
                    lpOutBuffer, nOutBufferSize,
                    lpBytesReturned, lpOverlapped);

            byte[] inData = ReadMemory(lpInBuffer, (nint)nInBufferSize);
            byte[] outData = ReadMemory(lpOutBuffer, (nint)nOutBufferSize);

            uint bytesReturned = 0;
            if (lpBytesReturned != 0)
            {
                bytesReturned = (uint)Marshal.ReadInt32(lpBytesReturned, 0);
            }

            handler.EnqueueItem(new DeviceIoControlRequest()
            {
                ticks = Stopwatch.GetTimestamp(),
                hDevice = hDevice,
                dwIoControlCode = dwIoControlCode,
                inData = inData,
                outData = outData,
                bytesReturned = bytesReturned,
                result = result
            });
            return result;
        }

        private static void LaunchDebugger()
        {
            if (!Debugger.IsAttached)
            {
                if (!Debugger.Launch()) return;
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(200);
                }
            }
            Debugger.Break();
        }

        public static void Main(string[] args)
        {
            //LaunchDebugger();
            Console.WriteLine("ManagedHook is initializing");
            try
            {
                programInstance = new Program();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Initialization failed: " + ex);
            }

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            handler.Start();
            Console.WriteLine("ManagedHook is ready!");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine();
            Console.WriteLine("ManagedHook: Unhandled Exception");
            var eobj = e.ExceptionObject;
            var exception = eobj as Exception;
            if (exception != null)
            {
                Console.WriteLine("-- details");
                Console.WriteLine(exception);
            }
            else
            {
                Console.WriteLine(eobj);
            }

            //LaunchDebugger();
        }
    }
}
