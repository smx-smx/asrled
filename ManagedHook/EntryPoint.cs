/**
  * Copyright (C) 2023 Stefano Moioli <smxdev4@gmail.com>
  */
using System.Runtime.InteropServices;

namespace ManagedHook
{
    public class EntryPoint
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate void MainDelegate(string[] args);

        private static string[] ReadArgv(IntPtr args, int sizeBytes)
        {
            int nargs = sizeBytes / IntPtr.Size;
            string[] argv = new string[nargs];

            for (int i = 0; i < nargs; i++, args += IntPtr.Size)
            {
                IntPtr charPtr = Marshal.ReadIntPtr(args);
                argv[i] = Marshal.PtrToStringAnsi(charPtr);
            }
            return argv;
        }

        private static int Entry(IntPtr args, int sizeBytes)
        {
            string[] argv = ReadArgv(args, sizeBytes);

            Action<MainDelegate> initializer;
            {
                initializer = (main) => {
                    main(argv);
                };
            }
            initializer(InternalMain);
            return 0;
        }

        private static void InternalMain(string[] args)
        {
            Program.Main(args);
        }
    }
}
