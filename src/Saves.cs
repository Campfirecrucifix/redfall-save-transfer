using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RedfallSaveTransfer
{
    internal sealed class SaveFile
    {
        public string Slot;      // e.g. "Hero1", "Campaign1_Backup"
        public byte[] Data;      // plain GVAS bytes
        public string Origin;    // where it came from (for display)

        public bool IsBackupCopy { get { return Slot.EndsWith("_Backup", StringComparison.OrdinalIgnoreCase); } }
        public bool IsSettings { get { return Slot.StartsWith("Settings", StringComparison.OrdinalIgnoreCase); } }
    }

    /// <summary>Tiny helpers for Unreal Engine "GVAS" save files. Only what is needed for validation and display.</summary>
    internal static class Gvas
    {
        public static bool IsPlain(byte[] d)
        {
            return d != null && d.Length >= 32 && d[0] == 'G' && d[1] == 'V' && d[2] == 'A' && d[3] == 'S';
        }

        /// <summary>Engine branch string from the header, e.g. "++redfall+Release_1.0451".</summary>
        public static string Branch(byte[] d)
        {
            try
            {
                int p = 4 + 4 + 4 + 2 + 2 + 2 + 4; // magic, save ver, package ver, engine major/minor/patch, changelist
                return ReadFString(d, ref p);
            }
            catch { return "?"; }
        }

        /// <summary>The save game class, e.g. "/Script/Redfall.ArkCharacterSaveGame".</summary>
        public static string SaveClass(byte[] d)
        {
            try
            {
                int p = 22;
                ReadFString(d, ref p);                         // branch
                p += 4;                                        // custom version format
                int n = BitConverter.ToInt32(d, p); p += 4;
                p += n * 20;                                   // custom versions (guid + int)
                return ReadFString(d, ref p);
            }
            catch { return "?"; }
        }

        /// <summary>Human readable one-liner for a Redfall save.</summary>
        public static string Describe(SaveFile s)
        {
            byte[] d = s.Data;
            string cls = SaveClass(d);
            if (cls.EndsWith("ArkCharacterSaveGame"))
            {
                string hero = FindEnum(d, "CharacterClass");
                int seconds = FindInt(d, "TimePlayedSeconds");
                int xp = FindInt(d, "XP");
                var sb = new StringBuilder("Hero: ");
                sb.Append(HeroName(hero));
                if (seconds > 0) sb.AppendFormat(", {0:0.0} h played", seconds / 3600.0);
                if (xp > 0) sb.AppendFormat(", {0} XP", xp);
                return sb.ToString();
            }
            if (cls.EndsWith("ArkCampaignSaveGame")) return "Campaign progress";
            if (cls.EndsWith("ArkPlayerSaveGame")) return "Player profile (hero list / tutorials)";
            if (cls.Contains("Settings")) return "Game settings";
            return cls;
        }

        private static string HeroName(string cls)
        {
            if (cls == null) return "(class not stored in save)";
            string c = cls.Replace("ECharacterClass::", "");
            switch (c)
            {
                case "CombatEngineer": return "Remi";
                case "TestSubject": return "Layla";
                case "Sniper": return "Jacob";
                case "Cryptozoologist": return "Devinder";
                default: return c;
            }
        }

        // Property layout: FString name, FString type, int64 size, [type specific header], uint8 hasGuid, [guid], value
        private static int FindProperty(byte[] d, string name, string type)
        {
            byte[] needle = FStringBytes(name);
            byte[] typeBytes = FStringBytes(type);
            for (int i = 0; i + needle.Length + typeBytes.Length <= d.Length; i++)
            {
                if (!Same(d, i, needle) || !Same(d, i + needle.Length, typeBytes)) continue;
                return i + needle.Length + typeBytes.Length + 8;   // position after the int64 size
            }
            return -1;
        }

        private static int FindInt(byte[] d, string name)
        {
            int p = FindProperty(d, name, "IntProperty");
            if (p < 0 || p + 5 > d.Length) return 0;
            if (d[p] != 0) p += 16;
            return BitConverter.ToInt32(d, p + 1);
        }

        private static string FindEnum(byte[] d, string name)
        {
            try
            {
                int p = FindProperty(d, name, "EnumProperty");
                if (p < 0) return null;
                ReadFString(d, ref p);                          // enum type name
                if (d[p++] != 0) p += 16;
                return ReadFString(d, ref p);
            }
            catch { return null; }
        }

        private static byte[] FStringBytes(string s)
        {
            var b = new byte[4 + s.Length + 1];
            BitConverter.GetBytes(s.Length + 1).CopyTo(b, 0);
            Encoding.ASCII.GetBytes(s).CopyTo(b, 4);
            return b;
        }

        private static bool Same(byte[] d, int at, byte[] needle)
        {
            for (int k = 0; k < needle.Length; k++)
                if (d[at + k] != needle[k]) return false;
            return true;
        }

        private static string ReadFString(byte[] d, ref int p)
        {
            int n = BitConverter.ToInt32(d, p); p += 4;
            if (n == 0) return "";
            if (n > 0)
            {
                if (n > 4096) throw new FormatException();
                string s = Encoding.ASCII.GetString(d, p, n - 1); p += n; return s;
            }
            n = -n;
            if (n > 4096) throw new FormatException();
            string w = Encoding.Unicode.GetString(d, p, (n - 1) * 2); p += n * 2; return w;
        }
    }

    /// <summary>Reads Game Pass (Xbox app / Microsoft Store) Redfall saves from a "wgs" container folder.</summary>
    internal static class GamePassSaves
    {
        public const string PackageFamily = "BethesdaSoftworks.Redfall_3275kfvn8vcwc";

        /// <summary>All Game Pass save folders (one per Xbox account) on this PC.</summary>
        public static List<string> FindDefaultFolders()
        {
            var result = new List<string>();
            string wgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "Packages", PackageFamily, "SystemAppData", "wgs");
            if (!Directory.Exists(wgs)) return result;
            foreach (string dir in Directory.GetDirectories(wgs))
                if (File.Exists(Path.Combine(dir, "containers.index")))
                    result.Add(dir);
            return result;
        }

        /// <summary>
        /// Accepts either the folder that contains containers.index, or any parent of it
        /// (e.g. a copied "SystemAppData" folder), as long as there is exactly one match.
        /// </summary>
        public static string ResolveFolder(string path)
        {
            if (!Directory.Exists(path)) throw new UserError("Folder not found: " + path);
            if (File.Exists(Path.Combine(path, "containers.index"))) return path;
            var found = Directory.GetFiles(path, "containers.index", SearchOption.AllDirectories)
                                 .Select(Path.GetDirectoryName).ToList();
            if (found.Count == 1) return found[0];
            if (found.Count == 0) return null;
            throw new UserError("Found more than one Game Pass save set under " + path + ":\n  " +
                                string.Join("\n  ", found) + "\nPass one of these folders instead.");
        }

        public static List<SaveFile> Read(string folder)
        {
            string indexPath = Path.Combine(folder, "containers.index");
            byte[] b = File.ReadAllBytes(indexPath);
            int p = 0;
            Func<int> i32 = () => { int v = BitConverter.ToInt32(b, p); p += 4; return v; };
            Func<string> wstr = () =>
            {
                int n = i32();
                if (n < 0 || n > 4096) throw new UserError("containers.index looks corrupted: " + indexPath);
                string s = Encoding.Unicode.GetString(b, p, n * 2); p += n * 2; return s;
            };

            var saves = new List<SaveFile>();
            try
            {
                i32();                      // version
                int count = i32();
                i32();                      // unknown
                wstr();                     // package name
                p += 8;                     // timestamp
                i32();                      // unknown
                wstr();                     // id
                p += 8;                     // unknown
                for (int i = 0; i < count; i++)
                {
                    string name = wstr();
                    wstr();                 // secondary name
                    wstr();                 // etag-ish id
                    p += 1;                 // seq
                    p += 4;                 // flags
                    byte[] guid = new byte[16];
                    Array.Copy(b, p, guid, 0, 16); p += 16;
                    p += 8;                 // last modified
                    p += 8;                 // unknown
                    long size = BitConverter.ToInt64(b, p); p += 8;

                    string dir = Path.Combine(folder, new Guid(guid).ToString("N").ToUpperInvariant());
                    if (!Directory.Exists(dir)) continue;
                    string blob = Directory.GetFiles(dir)
                        .Where(f => !Path.GetFileName(f).StartsWith("container", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).Length == size)
                        .FirstOrDefault();
                    if (blob == null) continue;
                    saves.Add(new SaveFile { Slot = name, Data = File.ReadAllBytes(blob), Origin = blob });
                }
            }
            catch (ArgumentException)
            {
                throw new UserError("containers.index looks corrupted: " + indexPath);
            }
            return saves;
        }
    }

    /// <summary>Reads plain (already decrypted / Game Pass) .sav files from a normal folder.</summary>
    internal static class LooseSaves
    {
        public static List<SaveFile> Read(string folder)
        {
            if (!Directory.Exists(folder)) throw new UserError("Folder not found: " + folder);
            var list = new List<SaveFile>();
            foreach (string f in Directory.GetFiles(folder, "*.sav"))
                list.Add(new SaveFile { Slot = Path.GetFileNameWithoutExtension(f), Data = File.ReadAllBytes(f), Origin = f });
            return list;
        }
    }

    /// <summary>The Steam version's save folder and backups of it.</summary>
    internal static class SteamSaves
    {
        private static readonly Regex CharacterSlot = new Regex(@"^(Player|Hero\d+|Campaign\d+)(_Backup)?$", RegexOptions.IgnoreCase);

        public static string DefaultFolder()
        {
            return Path.Combine(Native.GetSavedGamesFolder(), "Arkane Studios", "Redfall", "Steam", "SaveGames");
        }

        public static string BackupRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RedfallSaveTransfer", "Backups");
        }

        public static bool IsCharacterSlot(string slot) { return CharacterSlot.IsMatch(slot); }

        public static List<string> Slots(string folder)
        {
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.GetFiles(folder, "*.sav").Select(Path.GetFileNameWithoutExtension)
                            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Copies every file of the save folder into a new timestamped backup folder.</summary>
        public static string Backup(string folder, string label)
        {
            string dest = Path.Combine(BackupRoot(), DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + label);
            Directory.CreateDirectory(dest);
            if (Directory.Exists(folder))
            {
                foreach (string f in Directory.GetFiles(folder))
                {
                    string target = Path.Combine(dest, Path.GetFileName(f));
                    File.Copy(f, target, true);
                    if (!File.ReadAllBytes(f).SequenceEqual(File.ReadAllBytes(target)))
                        throw new UserError("Backup verification failed for " + f);
                }
            }
            return dest;
        }

        /// <summary>Makes the save folder contain exactly the .sav files of a backup (other files are left alone).</summary>
        public static void Restore(string backup, string folder)
        {
            Directory.CreateDirectory(folder);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(backup))
            {
                string name = Path.GetFileName(f);
                keep.Add(name);
                File.Copy(f, Path.Combine(folder, name), true);
            }
            foreach (string f in Directory.GetFiles(folder, "*.sav"))
                if (!keep.Contains(Path.GetFileName(f)))
                    File.Delete(f);
        }
    }
}
