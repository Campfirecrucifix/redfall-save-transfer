using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RedfallSaveTransfer
{
    /// <summary>
    /// Lets this tool ask the running Steam copy of Redfall to read or write a save slot.
    ///
    /// The Steam build encrypts its .sav files. The key is not stored anywhere useful, so
    /// instead of reimplementing the encryption, we call the game's own Unreal Engine 4.26
    /// functions:
    ///
    ///   bool UGameplayStatics::LoadDataFromSlot(TArray&lt;uint8&gt;&amp; OutData, const FString&amp; SlotName, int32 UserIndex)
    ///   bool UGameplayStatics::SaveDataToSlot  (const TArray&lt;uint8&gt;&amp; InData, const FString&amp; SlotName, int32 UserIndex)
    ///
    /// LoadDataFromSlot returns the decrypted, plain GVAS bytes. SaveDataToSlot takes
    /// plain GVAS bytes and writes an encrypted file into the Steam save folder.
    ///
    /// Both functions are located at runtime by searching the game executable for the
    /// places where they are called. Nothing in the game is patched or hooked: we
    /// allocate a few bytes in the game process, run one tiny function call on a new
    /// thread, read the result and free the memory again.
    /// </summary>
    internal sealed class GameBridge : IDisposable
    {
        // Call site inside UGameplayStatics::LoadGameFromSlot:
        //   mov r8d,edx / xor esi,esi / mov rdx,rcx / ... / lea rcx,[rsp+40h] / ... / call LoadDataFromSlot
        private static readonly Pattern LoadCallSite = new Pattern(
            "LoadDataFromSlot",
            "44 8B C2 33 F6 48 8B D1 48 89 74 24 40 48 8D 4C 24 40 48 89 74 24 48 E8 ?? ?? ?? ??",
            23);

        // Call site inside UGameplayStatics::SaveGameToSlot:
        //   mov r8d,esi / lea rdx,[rsp+20h] / lea rcx,[rsp+30h] / call SaveDataToSlot / ... / mov bl,al
        private static readonly Pattern SaveCallSite = new Pattern(
            "SaveDataToSlot",
            "44 8B C6 48 8D 54 24 20 48 8D 4C 24 30 E8 ?? ?? ?? ?? 48 8B 4C 24 30 8A D8 48 85 C9 74",
            13);

        private const int CallTimeoutMs = 120000;
        private static readonly Regex SlotNameRegex = new Regex("^[A-Za-z0-9_]{1,64}$");

        private readonly Process process;
        private IntPtr handle;
        private readonly long loadDataFromSlot;
        private readonly long saveDataToSlot;

        public string ExePath { get; private set; }
        public long ImageBase { get; private set; }
        public int ProcessId { get { return process.Id; } }
        public long LoadDataFromSlotAddress { get { return loadDataFromSlot; } }
        public long SaveDataToSlotAddress { get { return saveDataToSlot; } }

        private GameBridge(Process process, IntPtr handle, string exePath, long imageBase, long load, long save)
        {
            this.process = process;
            this.handle = handle;
            ExePath = exePath;
            ImageBase = imageBase;
            loadDataFromSlot = load;
            saveDataToSlot = save;
        }

        // ------------------------------------------------------------------ attach

        /// <summary>Returns the running Steam Redfall process, or null if the game is not running.</summary>
        public static Process FindGameProcess()
        {
            Process found = null;
            foreach (Process p in Process.GetProcessesByName("Redfall"))
            {
                string path = TryGetPath(p);
                if (path != null && path.EndsWith(@"Binaries\Win64\Redfall.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null)
                        throw new UserError("More than one copy of Redfall is running. Close all of them and start the game once.");
                    found = p;
                }
                else if (found == null && path == null)
                {
                    // Could not read the path (e.g. Game Pass build in WindowsApps). Remember it so we can explain.
                    found = p;
                }
            }
            return found;
        }

        private static string TryGetPath(Process p)
        {
            try { return p.MainModule.FileName; }
            catch (Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        public static GameBridge Attach()
        {
            if (!Environment.Is64BitProcess)
                throw new UserError("This tool must run as a 64-bit process.");

            Process p = FindGameProcess();
            if (p == null)
                throw new UserError("Redfall is not running.\n" +
                                    "Start the Steam version of Redfall, wait until you reach the MAIN MENU, then try again.");

            string exe = TryGetPath(p);
            if (exe == null)
                throw new UserError("Could not inspect the running Redfall process.\n" +
                                    "This tool only supports the STEAM version of Redfall (the Game Pass / Microsoft Store\n" +
                                    "version keeps its saves unencrypted and does not need this tool).");

            long imageBase = p.MainModule.BaseAddress.ToInt64();

            PeImage pe;
            try { pe = PeImage.Load(exe); }
            catch (Exception e)
            {
                if (e is UserError) throw;
                throw new UserError("Could not read the game executable (" + e.Message + ").\n" +
                                    "This tool only supports the STEAM version of Redfall.");
            }
            long loadSiteRva, saveSiteRva;
            long loadRva = pe.FindCallTarget(LoadCallSite, out loadSiteRva);
            long saveRva = pe.FindCallTarget(SaveCallSite, out saveSiteRva);

            IntPtr h = Native.OpenProcess(
                Native.PROCESS_CREATE_THREAD | Native.PROCESS_QUERY_INFORMATION |
                Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE,
                false, p.Id);
            if (h == IntPtr.Zero)
                throw new UserError("Could not open the Redfall process (Windows error " + new Win32Exception().Message + ").\n" +
                                    "Make sure the game and this tool run as the same Windows user. If the game runs as\n" +
                                    "administrator, run this tool as administrator too.");

            var bridge = new GameBridge(p, h, exe, imageBase, imageBase + loadRva, imageBase + saveRva);
            try
            {
                // Make sure the code in memory is the same code we analysed on disk.
                bridge.VerifyLiveBytes(LoadCallSite, imageBase + loadSiteRva);
                bridge.VerifyLiveBytes(SaveCallSite, imageBase + saveSiteRva);
            }
            catch
            {
                bridge.Dispose();
                throw;
            }
            return bridge;
        }

        private void VerifyLiveBytes(Pattern pattern, long address)
        {
            byte[] live = Read(address, pattern.Length);
            if (live == null || !pattern.Matches(live, 0))
                throw new UserError("The running game does not match its executable on disk (" + pattern.Name + ").\n" +
                                    "Restart Redfall and try again.");
        }

        // ------------------------------------------------------------------ public API

        public static void ValidateSlotName(string slot)
        {
            if (!SlotNameRegex.IsMatch(slot))
                throw new UserError("Invalid save slot name '" + slot + "'. Only letters, digits and '_' are allowed.");
        }

        /// <summary>Reads and decrypts a save slot. Returns null if the slot does not exist or could not be read.</summary>
        public byte[] LoadSlot(string slot)
        {
            ValidateSlotName(slot);
            var allocations = new List<long>();
            try
            {
                long slotName = WriteFString(slot, allocations);
                long outArray = Alloc(16, allocations);          // TArray<uint8> { Data, Num, Max } = zero
                Write(outArray, new byte[16]);

                bool ok = (CallFunction(loadDataFromSlot, outArray, slotName, 0) & 0xFF) != 0;
                long data = ReadInt64(outArray);
                int num = ReadInt32(outArray + 8);
                if (!ok || data == 0 || num <= 0)
                    return null;
                byte[] bytes = Read(data, num);
                if (bytes == null)
                    throw new InvalidOperationException("Could not read the loaded save data from the game process.");
                // Note: the TArray buffer was allocated by the game's own allocator. It is a few hundred KB
                // at most and is released when the game exits, so we leave it alone.
                return bytes;
            }
            finally
            {
                FreeAll(allocations);
            }
        }

        /// <summary>Encrypts and writes plain GVAS bytes to a save slot. Returns the game's success flag.</summary>
        public bool SaveSlot(string slot, byte[] plainData)
        {
            ValidateSlotName(slot);
            if (plainData == null || plainData.Length == 0)
                throw new ArgumentException("No data to save.");
            var allocations = new List<long>();
            try
            {
                long slotName = WriteFString(slot, allocations);
                long buffer = Alloc(plainData.Length, allocations);
                Write(buffer, plainData);

                long inArray = Alloc(16, allocations);           // TArray<uint8> { Data, Num, Max }
                var arr = new byte[16];
                BitConverter.GetBytes(buffer).CopyTo(arr, 0);
                BitConverter.GetBytes(plainData.Length).CopyTo(arr, 8);
                BitConverter.GetBytes(plainData.Length).CopyTo(arr, 12);
                Write(inArray, arr);

                return (CallFunction(saveDataToSlot, inArray, slotName, 0) & 0xFF) != 0;
            }
            finally
            {
                FreeAll(allocations);
            }
        }

        public void CloseGame()
        {
            if (process.HasExited) return;
            process.Kill();
            process.WaitForExit(30000);
        }

        public bool HasExited { get { return process.HasExited; } }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                Native.CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------------ remote call

        /// <summary>
        /// Runs fn(a1, a2, a3) on a new thread inside the game and returns RAX.
        /// The stub keeps the stack 16-byte aligned and reserves the 32-byte shadow space required by the x64 ABI.
        /// </summary>
        private long CallFunction(long fn, long a1, long a2, long a3)
        {
            var allocations = new List<long>();
            bool canFree = true;
            try
            {
                long result = Alloc(16, allocations);
                Write(result, new byte[16]);

                var c = new List<byte>();
                c.Add(0x53);                                     // push rbx          (aligns stack to 16)
                c.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x20 }); // sub rsp, 20h     (shadow space)
                Emit(c, new byte[] { 0x48, 0xB9 }, a1);          // mov rcx, a1
                Emit(c, new byte[] { 0x48, 0xBA }, a2);          // mov rdx, a2
                Emit(c, new byte[] { 0x49, 0xB8 }, a3);          // mov r8,  a3
                Emit(c, new byte[] { 0x48, 0xB8 }, fn);          // mov rax, fn
                c.AddRange(new byte[] { 0xFF, 0xD0 });           // call rax
                Emit(c, new byte[] { 0x49, 0xBB }, result);      // mov r11, &result
                c.AddRange(new byte[] { 0x49, 0x89, 0x03 });     // mov [r11], rax
                c.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x20 }); // add rsp, 20h
                c.Add(0x5B);                                     // pop rbx
                c.AddRange(new byte[] { 0x31, 0xC0 });           // xor eax, eax
                c.Add(0xC3);                                     // ret
                byte[] code = c.ToArray();

                long codeAddr = Alloc(code.Length, allocations);
                Write(codeAddr, code);
                int oldProtect;
                if (!Native.VirtualProtectEx(handle, (IntPtr)codeAddr, (IntPtr)code.Length, Native.PAGE_EXECUTE_READ, out oldProtect))
                    throw new Win32Exception();
                Native.FlushInstructionCache(handle, (IntPtr)codeAddr, (IntPtr)code.Length);

                int threadId;
                IntPtr thread = Native.CreateRemoteThread(handle, IntPtr.Zero, IntPtr.Zero, (IntPtr)codeAddr, IntPtr.Zero, 0, out threadId);
                if (thread == IntPtr.Zero)
                    throw new UserError("Could not start a thread in the game (" + new Win32Exception().Message + ").\n" +
                                        "Antivirus software sometimes blocks this. See the README.");
                try
                {
                    uint wait = Native.WaitForSingleObject(thread, CallTimeoutMs);
                    if (wait != Native.WAIT_OBJECT_0)
                    {
                        canFree = false; // the thread may still be running our code; never free it under its feet
                        throw new UserError("The game did not finish the save operation in time. Restart Redfall and try again.");
                    }
                    uint exitCode;
                    if (!Native.GetExitCodeThread(thread, out exitCode) || exitCode != 0)
                        throw new InvalidOperationException("Remote call failed (thread exit code " + exitCode + ").");
                }
                finally
                {
                    Native.CloseHandle(thread);
                }
                return ReadInt64(result);
            }
            finally
            {
                if (canFree) FreeAll(allocations);
            }
        }

        private static void Emit(List<byte> code, byte[] opcode, long imm64)
        {
            code.AddRange(opcode);
            code.AddRange(BitConverter.GetBytes(imm64));
        }

        /// <summary>Writes an FString (TArray&lt;TCHAR&gt; {Data, Num, Max}) into the game and returns its address.</summary>
        private long WriteFString(string s, List<long> allocations)
        {
            byte[] chars = Encoding.Unicode.GetBytes(s + "\0");
            long data = Alloc(chars.Length, allocations);
            Write(data, chars);

            long str = Alloc(16, allocations);
            var b = new byte[16];
            BitConverter.GetBytes(data).CopyTo(b, 0);
            BitConverter.GetBytes(s.Length + 1).CopyTo(b, 8);
            BitConverter.GetBytes(s.Length + 1).CopyTo(b, 12);
            Write(str, b);
            return str;
        }

        // ------------------------------------------------------------------ memory helpers

        private long Alloc(int size, List<long> allocations)
        {
            IntPtr p = Native.VirtualAllocEx(handle, IntPtr.Zero, (IntPtr)Math.Max(size, 16),
                                             Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (p == IntPtr.Zero)
                throw new Win32Exception();
            allocations.Add(p.ToInt64());
            return p.ToInt64();
        }

        private void FreeAll(List<long> allocations)
        {
            foreach (long a in allocations)
                Native.VirtualFreeEx(handle, (IntPtr)a, IntPtr.Zero, Native.MEM_RELEASE);
            allocations.Clear();
        }

        private byte[] Read(long address, int size)
        {
            var buffer = new byte[size];
            IntPtr read;
            if (!Native.ReadProcessMemory(handle, (IntPtr)address, buffer, (IntPtr)size, out read) || read.ToInt64() != size)
                return null;
            return buffer;
        }

        private long ReadInt64(long address)
        {
            byte[] b = Read(address, 8);
            if (b == null) throw new InvalidOperationException("Could not read game memory.");
            return BitConverter.ToInt64(b, 0);
        }

        private int ReadInt32(long address)
        {
            byte[] b = Read(address, 4);
            if (b == null) throw new InvalidOperationException("Could not read game memory.");
            return BitConverter.ToInt32(b, 0);
        }

        private void Write(long address, byte[] data)
        {
            IntPtr written;
            if (!Native.WriteProcessMemory(handle, (IntPtr)address, data, (IntPtr)data.Length, out written) || written.ToInt64() != data.Length)
                throw new Win32Exception();
        }
    }

    /// <summary>A byte pattern with wildcards that ends in (or contains) an E8 rel32 call.</summary>
    internal sealed class Pattern
    {
        public readonly string Name;
        public readonly int CallOffset;
        private readonly byte[] bytes;
        private readonly bool[] wildcard;

        public Pattern(string name, string text, int callOffset)
        {
            Name = name;
            CallOffset = callOffset;
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bytes = new byte[parts.Length];
            wildcard = new bool[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "??") wildcard[i] = true;
                else bytes[i] = Convert.ToByte(parts[i], 16);
            }
            if (bytes[callOffset] != 0xE8)
                throw new ArgumentException("Pattern call offset does not point at an E8 call.");
        }

        public int Length { get { return bytes.Length; } }

        public bool Matches(byte[] data, int offset)
        {
            if (offset < 0 || offset + bytes.Length > data.Length) return false;
            for (int i = 0; i < bytes.Length; i++)
                if (!wildcard[i] && data[offset + i] != bytes[i]) return false;
            return true;
        }

        public List<int> FindAll(byte[] data)
        {
            var hits = new List<int>();
            byte first = bytes[0];
            int last = data.Length - bytes.Length;
            for (int i = 0; i <= last; i++)
            {
                if (data[i] == first && Matches(data, i))
                    hits.Add(i);
            }
            return hits;
        }
    }

    /// <summary>Minimal PE reader: enough to scan the executable sections of the game exe on disk.</summary>
    internal sealed class PeImage
    {
        private sealed class Section
        {
            public string Name;
            public uint VirtualAddress, VirtualSize, RawPointer, RawSize, Characteristics;
            public bool Executable { get { return (Characteristics & 0x20000000) != 0; } }
        }

        private const uint MaxScanSectionSize = 256u * 1024 * 1024;  // skip the huge DRM section
        private readonly string path;
        private readonly List<Section> sections = new List<Section>();

        private PeImage(string path) { this.path = path; }

        public static PeImage Load(string path)
        {
            var pe = new PeImage(path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var header = new byte[0x1000];
                fs.Read(header, 0, header.Length);
                if (header[0] != 'M' || header[1] != 'Z') throw new UserError("Redfall.exe is not a valid executable.");
                int peOff = BitConverter.ToInt32(header, 0x3C);
                if (BitConverter.ToUInt32(header, peOff) != 0x00004550) throw new UserError("Redfall.exe is not a valid PE file.");
                if (BitConverter.ToUInt16(header, peOff + 4) != 0x8664) throw new UserError("Redfall.exe is not a 64-bit executable.");
                int count = BitConverter.ToUInt16(header, peOff + 6);
                int optSize = BitConverter.ToUInt16(header, peOff + 20);
                int o = peOff + 24 + optSize;
                for (int i = 0; i < count; i++, o += 40)
                {
                    pe.sections.Add(new Section
                    {
                        Name = Encoding.ASCII.GetString(header, o, 8).TrimEnd('\0'),
                        VirtualSize = BitConverter.ToUInt32(header, o + 8),
                        VirtualAddress = BitConverter.ToUInt32(header, o + 12),
                        RawSize = BitConverter.ToUInt32(header, o + 16),
                        RawPointer = BitConverter.ToUInt32(header, o + 20),
                        Characteristics = BitConverter.ToUInt32(header, o + 36),
                    });
                }
            }
            return pe;
        }

        /// <summary>Finds the single call site matching the pattern and returns the RVA of the called function.</summary>
        public long FindCallTarget(Pattern pattern, out long siteRva)
        {
            var hits = new List<long>();
            long target = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                foreach (Section s in sections)
                {
                    if (!s.Executable || s.RawSize == 0 || s.RawSize > MaxScanSectionSize) continue;
                    var data = new byte[s.RawSize];
                    fs.Seek(s.RawPointer, SeekOrigin.Begin);
                    int got = 0;
                    while (got < data.Length)
                    {
                        int n = fs.Read(data, got, data.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    foreach (int off in pattern.FindAll(data))
                    {
                        long rva = s.VirtualAddress + (long)off;
                        hits.Add(rva);
                        int rel = BitConverter.ToInt32(data, off + pattern.CallOffset + 1);
                        target = rva + pattern.CallOffset + 5 + rel;
                    }
                }
            }

            if (hits.Count != 1)
                throw new UserError("Could not locate " + pattern.Name + " in Redfall.exe (" + hits.Count + " matches).\n" +
                                    "This version of the game is not supported by this tool.");
            Section ts = SectionAt(target);
            if (ts == null || !ts.Executable)
                throw new UserError("Located " + pattern.Name + " at an unexpected address. This version of the game is not supported.");
            siteRva = hits[0];
            return target;
        }

        private Section SectionAt(long rva)
        {
            foreach (Section s in sections)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + (long)Math.Max(s.VirtualSize, s.RawSize))
                    return s;
            return null;
        }
    }
}
