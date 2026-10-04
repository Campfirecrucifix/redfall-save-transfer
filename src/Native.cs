using System;
using System.Runtime.InteropServices;

namespace RedfallSaveTransfer
{
    /// <summary>Win32 imports used to talk to the running game process.</summary>
    internal static class Native
    {
        public const int PROCESS_CREATE_THREAD = 0x0002;
        public const int PROCESS_VM_OPERATION = 0x0008;
        public const int PROCESS_VM_READ = 0x0010;
        public const int PROCESS_VM_WRITE = 0x0020;
        public const int PROCESS_QUERY_INFORMATION = 0x0400;

        public const int MEM_COMMIT = 0x1000;
        public const int MEM_RESERVE = 0x2000;
        public const int MEM_RELEASE = 0x8000;

        public const int PAGE_READWRITE = 0x04;
        public const int PAGE_EXECUTE_READ = 0x20;

        public const uint WAIT_OBJECT_0 = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, int allocationType, int protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, int freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualProtectEx(IntPtr process, IntPtr address, IntPtr size, int newProtect, out int oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushInstructionCache(IntPtr process, IntPtr address, IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, IntPtr stackSize, IntPtr startAddress, IntPtr parameter, int flags, out int threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, int milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pointer);

        public static readonly Guid FOLDERID_SavedGames = new Guid("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");

        public static string GetSavedGamesFolder()
        {
            IntPtr p;
            if (SHGetKnownFolderPath(FOLDERID_SavedGames, 0, IntPtr.Zero, out p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); }
                finally { CoTaskMemFree(p); }
            }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        }
    }
}
