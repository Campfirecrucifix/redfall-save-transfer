using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

[assembly: AssemblyTitle("Redfall Save Transfer")]
[assembly: AssemblyDescription("Transfers Redfall saves from the Game Pass version to the Steam version.")]
[assembly: AssemblyProduct("RedfallSaveTransfer")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace RedfallSaveTransfer
{
    /// <summary>An error with a message meant for the user (no stack trace).</summary>
    internal sealed class UserError : Exception
    {
        public UserError(string message) : base(message) { }
    }

    internal sealed class Options
    {
        public string Command;
        public string Source;          // --gp / --from / --source
        public string SteamDir;        // --steam-dir
        public string Output;          // export output folder
        public string BackupPath;      // restore input folder
        public bool IncludeSettings;   // --include-settings
        public bool KeepExisting;      // --keep-existing
        public bool NoClose;           // --no-close
        public bool Yes;               // --yes
    }

    internal static class Program
    {
        private const string Version = "1.0.0";
        private const string SelfTestSlot = "RedfallSaveTransfer_SelfTest";
        private static bool interactive;

        private static int Main(string[] args)
        {
            interactive = args.Length == 0;
            Console.WriteLine("Redfall Save Transfer " + Version + "  -  Game Pass -> Steam");
            Console.WriteLine(new string('=', 60));
            try
            {
                if (interactive) return InteractiveMenu();

                Options o = ParseArgs(args);
                switch (o.Command)
                {
                    case "import": return Import(o);
                    case "export": return Export(o);
                    case "restore": return Restore(o);
                    case "list": return List(o);
                    default: PrintHelp(); return o.Command == "help" ? 0 : 1;
                }
            }
            catch (UserError e)
            {
                Error(e.Message);
                return 2;
            }
            catch (Exception e)
            {
                Error("Unexpected error: " + e);
                return 3;
            }
            finally
            {
                if (interactive)
                {
                    Console.WriteLine();
                    Console.Write("Press Enter to close...");
                    Console.ReadLine();
                }
            }
        }

        // ================================================================== commands

        private static int Import(Options o)
        {
            string steamDir = o.SteamDir ?? SteamSaves.DefaultFolder();

            // 1. Load and validate the source saves -----------------------------------
            Step("Reading the Game Pass saves");
            List<SaveFile> all = LoadSource(o.Source);
            var saves = all.Where(s => o.IncludeSettings || !s.IsSettings)
                           .OrderBy(s => s.Slot, StringComparer.OrdinalIgnoreCase).ToList();
            if (saves.Count == 0)
                throw new UserError("No saves found to transfer.");
            foreach (SaveFile s in saves)
            {
                GameBridge.ValidateSlotName(s.Slot);
                if (!Gvas.IsPlain(s.Data))
                    throw new UserError(s.Slot + " (" + s.Origin + ") is not a plain Redfall save.\n" +
                                        "If these files came from the Steam version they are already encrypted and do not need converting.");
            }
            PrintSaves(saves);
            if (!o.IncludeSettings && all.Any(s => s.IsSettings))
                Info("Settings.sav is not transferred (your Steam settings are kept). Use --include-settings to change that.");
            if (!saves.Any(s => s.Slot.Equals("Player", StringComparison.OrdinalIgnoreCase)))
                Warn("There is no Player save among the source files. Heroes may not show up without it.");
            string branch = Gvas.Branch(saves[0].Data);
            Info("Save format version: " + branch);

            // 2. Attach to the game ----------------------------------------------------
            Step("Connecting to the running Steam version of Redfall");
            using (GameBridge game = GameBridge.Attach())
            {
                PrintGame(game);

                // 3. Prove that we can write + read a save before touching anything ---
                Step("Self test (writes, reads back and deletes a temporary save slot)");
                SelfTest(game, steamDir, saves.First(s => !s.IsSettings || saves.Count == 1).Data);

                if (File.Exists(Path.Combine(steamDir, "Player.sav")))
                {
                    byte[] steamPlayer = game.LoadSlot("Player");
                    if (steamPlayer != null && Gvas.Branch(steamPlayer) != branch)
                    {
                        Warn("Your Steam saves use format '" + Gvas.Branch(steamPlayer) + "' but the source saves use '" + branch + "'.\n" +
                             "The game may not accept them. Make sure both versions of the game are fully updated.");
                        if (!Confirm(o, "Continue anyway?")) return 1;
                    }
                }

                // 4. Show the plan ------------------------------------------------------
                List<string> existing = SteamSaves.Slots(steamDir);
                var importing = new HashSet<string>(saves.Select(s => s.Slot), StringComparer.OrdinalIgnoreCase);
                List<string> overwritten = existing.Where(importing.Contains).ToList();
                List<string> removed = o.KeepExisting
                    ? new List<string>()
                    : existing.Where(s => SteamSaves.IsCharacterSlot(s) && !importing.Contains(s)).ToList();

                Step("Plan");
                Console.WriteLine("  Steam save folder : " + steamDir);
                Console.WriteLine("  Write             : " + string.Join(", ", saves.Select(s => s.Slot)));
                Console.WriteLine("  Replace existing  : " + (overwritten.Count > 0 ? string.Join(", ", overwritten) : "(none)"));
                Console.WriteLine("  Remove old Steam  : " + (removed.Count > 0 ? string.Join(", ", removed) : "(none)"));
                Console.WriteLine("  Backup to         : " + SteamSaves.BackupRoot());
                if (overwritten.Count + removed.Count > 0)
                    Warn("Your current Steam heroes/progress will be REPLACED by the Game Pass saves (a backup is made first).");
                if (!Confirm(o, "Start the transfer?")) return 1;

                // 5. Backup -------------------------------------------------------------
                Step("Backing up your current Steam saves");
                string backup = SteamSaves.Backup(steamDir, "before-import");
                string decrypted = Path.Combine(backup, "decrypted");
                foreach (string slot in existing)
                {
                    if (slot.Equals(SelfTestSlot, StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] plain = TryLoad(game, slot);
                    if (plain == null) continue;
                    Directory.CreateDirectory(decrypted);
                    File.WriteAllBytes(Path.Combine(decrypted, slot + ".sav"), plain);
                }
                Ok("Backup: " + backup);

                // 6. Write + verify -----------------------------------------------------
                Step("Writing saves through the game (encrypting them for Steam)");
                string failure = null;
                var writtenFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string slot in removed)
                    {
                        File.Delete(Path.Combine(steamDir, slot + ".sav"));
                        Console.WriteLine("  removed  " + slot);
                    }
                    foreach (SaveFile s in saves)
                    {
                        string path = Path.Combine(steamDir, s.Slot + ".sav");
                        bool written = game.SaveSlot(s.Slot, s.Data);
                        byte[] back = written ? game.LoadSlot(s.Slot) : null;
                        bool verified = back != null && back.SequenceEqual(s.Data) && File.Exists(path);
                        Console.WriteLine("  {0,-8} {1,-20} {2,8:N0} bytes", verified ? "ok" : "FAILED", s.Slot, s.Data.Length);
                        if (!verified) { failure = "Could not write or verify " + s.Slot + "."; break; }
                        writtenFiles[s.Slot] = File.ReadAllBytes(path);
                    }
                }
                catch (Exception e)
                {
                    failure = e.Message;
                }

                if (failure != null)
                {
                    Error(failure);
                    Step("Rolling back");
                    SteamSaves.Restore(backup, steamDir);
                    Ok("Your Steam saves were restored from " + backup);
                    throw new UserError("The transfer failed and was rolled back. Nothing was changed.");
                }
                Ok("All " + saves.Count + " saves written and verified (each one was read back by the game and matched exactly).");

                // 7. Close the game -----------------------------------------------------
                if (o.NoClose)
                {
                    Warn("The game is still running and still has your OLD saves loaded in memory.\n" +
                         "Close it from Task Manager now (do not load a hero first), otherwise it may write the old data back.");
                }
                else
                {
                    Step("Closing Redfall");
                    Info("The game still has your old saves loaded in memory. It is closed now so it cannot write them back.");
                    game.CloseGame();
                    Ok("Redfall closed.");

                    // The game can save on its own while sitting in the menu. Make sure it did not
                    // overwrite anything between our verification and closing it.
                    List<string> changed = saves.Where(s => !FileBytesEqual(Path.Combine(steamDir, s.Slot + ".sav"), writtenFiles[s.Slot]))
                                                .Select(s => s.Slot).ToList();
                    if (changed.Count > 0)
                        throw new UserError("The game rewrote " + string.Join(", ", changed) + " before it was closed.\n" +
                                            "Run the transfer again (your backup is in " + backup + ").");
                    Ok("Files on disk are unchanged after closing the game.");
                }
            }

            Step("Done!");
            Console.WriteLine("  1. Start Redfall from Steam again.");
            Console.WriteLine("  2. If Steam reports a Cloud sync conflict, choose the LOCAL files (this PC).");
            Console.WriteLine("  3. Your Game Pass heroes should now be on the hero select screen.");
            Console.WriteLine("  4. Load a hero, play until the game autosaves, then quit normally.");
            Console.WriteLine();
            Console.WriteLine("  To undo: RedfallSaveTransfer restore \"<backup folder>\"   (backups: " + SteamSaves.BackupRoot() + ")");
            return 0;
        }

        private static void SelfTest(GameBridge game, string steamDir, byte[] sample)
        {
            string testFile = Path.Combine(steamDir, SelfTestSlot + ".sav");
            if (File.Exists(testFile)) File.Delete(testFile);
            try
            {
                if (!game.SaveSlot(SelfTestSlot, sample))
                    throw new UserError("The game refused to write a test save. Nothing was changed.\n" +
                                        "Make sure the game is sitting at the main menu and try again.");
                if (!File.Exists(testFile))
                    throw new UserError("The game did not write its test save to\n  " + steamDir + "\n" +
                                        "If your Steam save folder is somewhere else, pass it with --steam-dir \"<folder>\".");
                byte[] back = game.LoadSlot(SelfTestSlot);
                if (back == null || !back.SequenceEqual(sample))
                    throw new UserError("The test save did not read back correctly. Nothing was changed.");
                bool encrypted = !Gvas.IsPlain(File.ReadAllBytes(testFile));
                Ok("Self test passed" + (encrypted ? " (game encrypted and decrypted the test save correctly)." : "."));
            }
            finally
            {
                // Deleted while the game is still running, so Steam Cloud also forgets it.
                if (File.Exists(testFile)) File.Delete(testFile);
            }
        }

        private static int Export(Options o)
        {
            string steamDir = o.SteamDir ?? SteamSaves.DefaultFolder();
            List<string> slots = SteamSaves.Slots(steamDir).Where(s => !s.Equals(SelfTestSlot, StringComparison.OrdinalIgnoreCase)).ToList();
            if (slots.Count == 0)
                throw new UserError("No Steam saves found in " + steamDir);
            string outDir = o.Output ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                                     "RedfallSaveTransfer", "Exports", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));

            Step("Connecting to the running Steam version of Redfall");
            int failed = 0;
            using (GameBridge game = GameBridge.Attach())
            {
                PrintGame(game);
                Step("Decrypting " + slots.Count + " saves into " + outDir);
                Directory.CreateDirectory(outDir);
                foreach (string slot in slots)
                {
                    byte[] plain = TryLoad(game, slot);
                    if (plain == null)
                    {
                        Console.WriteLine("  {0,-8} {1}", "FAILED", slot);
                        failed++;
                        continue;
                    }
                    File.WriteAllBytes(Path.Combine(outDir, slot + ".sav"), plain);
                    var sf = new SaveFile { Slot = slot, Data = plain };
                    Console.WriteLine("  {0,-8} {1,-20} {2,8:N0} bytes  {3}", "ok", slot, plain.Length, Gvas.IsPlain(plain) ? Gvas.Describe(sf) : "(not a GVAS file)");
                }
            }
            if (failed > 0) Warn(failed + " save(s) could not be read.");
            Ok("Plain copies written to " + outDir);
            Info("Nothing in your Steam save folder was changed.");
            return failed > 0 ? 2 : 0;
        }

        private static int Restore(Options o)
        {
            string steamDir = o.SteamDir ?? SteamSaves.DefaultFolder();
            string backup = o.BackupPath;
            if (backup == null)
            {
                if (!interactive) throw new UserError("Usage: RedfallSaveTransfer restore \"<backup folder>\"");
                backup = PickBackup();
                if (backup == null) return 1;
            }
            if (!Directory.Exists(backup) || Directory.GetFiles(backup, "*.sav").Length == 0)
                throw new UserError("Not a backup folder (no .sav files): " + backup);
            if (GameBridge.FindGameProcess() != null)
                throw new UserError("Close Redfall before restoring a backup.");

            Step("Restore");
            Console.WriteLine("  From : " + backup);
            Console.WriteLine("  To   : " + steamDir);
            Console.WriteLine("  Saves: " + string.Join(", ", Directory.GetFiles(backup, "*.sav").Select(Path.GetFileNameWithoutExtension)));
            if (!Confirm(o, "Replace the current Steam saves with this backup?")) return 1;

            string safety = SteamSaves.Backup(steamDir, "before-restore");
            Ok("Current saves backed up to " + safety);
            SteamSaves.Restore(backup, steamDir);
            Ok("Backup restored.");
            Info("If Steam reports a Cloud sync conflict on the next launch, choose the LOCAL files.");
            return 0;
        }

        private static int List(Options o)
        {
            string steamDir = o.SteamDir ?? SteamSaves.DefaultFolder();
            Step("Steam save folder");
            Console.WriteLine("  " + steamDir + (Directory.Exists(steamDir) ? "" : "   (does not exist yet)"));
            foreach (string slot in SteamSaves.Slots(steamDir))
            {
                var fi = new FileInfo(Path.Combine(steamDir, slot + ".sav"));
                byte[] head = File.ReadAllBytes(fi.FullName);
                Console.WriteLine("  {0,-22} {1,8:N0} bytes  {2:yyyy-MM-dd HH:mm}  {3}", slot, fi.Length, fi.LastWriteTime,
                                  Gvas.IsPlain(head) ? "PLAIN (the Steam version cannot read this!)" : "encrypted");
            }

            Step("Game Pass saves");
            List<string> folders;
            if (o.Source != null) folders = new List<string> { o.Source };
            else folders = GamePassSaves.FindDefaultFolders();
            if (folders.Count == 0) Console.WriteLine("  (none found on this PC)");
            foreach (string f in folders)
            {
                Console.WriteLine("  " + f);
                try { PrintSaves(LoadSource(f)); }
                catch (UserError e) { Console.WriteLine("    " + e.Message); }
            }

            Step("Game");
            Console.WriteLine(GameBridge.FindGameProcess() != null ? "  Redfall is running." : "  Redfall is not running.");
            Console.WriteLine("  Backups: " + SteamSaves.BackupRoot());
            return 0;
        }

        // ================================================================== interactive mode

        private static int InteractiveMenu()
        {
            Console.WriteLine();
            Console.WriteLine("  1) Transfer Game Pass saves to the Steam version");
            Console.WriteLine("  2) Export (decrypt) my Steam saves to a folder");
            Console.WriteLine("  3) Restore a backup");
            Console.WriteLine("  4) Show detected saves");
            Console.WriteLine("  Q) Quit");
            string choice = Ask("Choose").ToUpperInvariant();
            var o = new Options();
            switch (choice)
            {
                case "1":
                    o.Source = PickSource();
                    if (o.Source == null) return 1;
                    o.IncludeSettings = Ask("Also transfer your Game Pass game settings (Settings.sav)? [y/N]").StartsWith("y", StringComparison.OrdinalIgnoreCase);
                    if (!WaitForGame()) return 1;
                    return Import(o);
                case "2":
                    if (!WaitForGame()) return 1;
                    return Export(o);
                case "3":
                    return Restore(o);
                case "4":
                    return List(o);
                default:
                    return 0;
            }
        }

        private static string PickSource()
        {
            List<string> found = GamePassSaves.FindDefaultFolders();
            Step("Where are your Game Pass saves?");
            for (int i = 0; i < found.Count; i++)
            {
                Console.WriteLine("  " + (i + 1) + ") " + found[i]);
                try
                {
                    foreach (SaveFile s in LoadSource(found[i]).Where(s => !s.IsBackupCopy && !s.IsSettings))
                        Console.WriteLine("       {0,-12} {1}", s.Slot, Gvas.IsPlain(s.Data) ? Gvas.Describe(s) : "");
                }
                catch (UserError) { }
            }
            if (found.Count == 0)
                Console.WriteLine("  No Game Pass saves were found on this PC automatically.");
            Console.WriteLine("  Or type/paste the path of a folder that contains either 'containers.index'");
            Console.WriteLine("  (a copied Game Pass save folder) or plain .sav files.");
            string answer = Ask(found.Count > 0 ? "Number or folder" : "Folder").Trim().Trim('"');
            if (answer.Length == 0) return null;
            int n;
            if (int.TryParse(answer, out n) && n >= 1 && n <= found.Count) return found[n - 1];
            return answer;
        }

        private static string PickBackup()
        {
            string root = SteamSaves.BackupRoot();
            var backups = Directory.Exists(root)
                ? Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).ToList()
                : new List<string>();
            if (backups.Count == 0) { Info("No backups found in " + root); return null; }
            Step("Backups (newest first)");
            for (int i = 0; i < backups.Count; i++)
                Console.WriteLine("  " + (i + 1) + ") " + Path.GetFileName(backups[i]));
            int n;
            string answer = Ask("Number");
            if (int.TryParse(answer, out n) && n >= 1 && n <= backups.Count) return backups[n - 1];
            return null;
        }

        private static bool WaitForGame()
        {
            Step("Start the game");
            Console.WriteLine("  Start the STEAM version of Redfall and wait until the MAIN MENU is shown.");
            Console.WriteLine("  Do not select or load a hero.");
            while (true)
            {
                string a = Ask("Press Enter when the game is at the main menu (or Q to cancel)");
                if (a.Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
                if (GameBridge.FindGameProcess() != null) return true;
                Warn("Redfall does not seem to be running yet.");
            }
        }

        // ================================================================== helpers

        private static List<SaveFile> LoadSource(string path)
        {
            if (path == null)
            {
                List<string> found = GamePassSaves.FindDefaultFolders();
                if (found.Count == 0)
                    throw new UserError("No Game Pass saves found on this PC.\n" +
                                        "Copy your Game Pass save folder here and pass it with --gp \"<folder>\".");
                if (found.Count > 1)
                    throw new UserError("Found Game Pass saves for more than one Xbox account:\n  " + string.Join("\n  ", found) +
                                        "\nChoose one with --gp \"<folder>\".");
                path = found[0];
            }
            path = Path.GetFullPath(path.Trim().Trim('"'));
            string container = GamePassSaves.ResolveFolder(path);
            List<SaveFile> saves = container != null ? GamePassSaves.Read(container) : LooseSaves.Read(path);
            if (saves.Count == 0)
                throw new UserError("No Redfall saves found in " + path + "\n" +
                                    "Expected a Game Pass save folder (with containers.index) or a folder of .sav files.");
            return saves;
        }

        private static bool FileBytesEqual(string path, byte[] expected)
        {
            return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(expected);
        }

        private static byte[] TryLoad(GameBridge game, string slot)
        {
            try { return game.LoadSlot(slot); }
            catch (Exception) { return null; }
        }

        private static void PrintSaves(List<SaveFile> saves)
        {
            foreach (SaveFile s in saves.OrderBy(x => x.Slot, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine("    {0,-20} {1,8:N0} bytes  {2}", s.Slot, s.Data.Length,
                                  Gvas.IsPlain(s.Data) ? (s.IsBackupCopy ? "(backup copy)" : Gvas.Describe(s)) : "NOT A PLAIN SAVE");
        }

        private static void PrintGame(GameBridge game)
        {
            Ok("Found Redfall (PID " + game.ProcessId + "): " + game.ExePath);
            Info(string.Format("LoadDataFromSlot = 0x{0:X}, SaveDataToSlot = 0x{1:X}", game.LoadDataFromSlotAddress, game.SaveDataToSlotAddress));
        }

        private static bool Confirm(Options o, string question)
        {
            if (o.Yes) return true;
            return Ask(question + " [y/N]").StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }

        private static string Ask(string prompt)
        {
            Console.Write("  " + prompt + ": ");
            return (Console.ReadLine() ?? "").Trim();
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options { Command = args[0].TrimStart('-', '/').ToLowerInvariant() };
            if (o.Command == "h" || o.Command == "?") o.Command = "help";
            var positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> value = () =>
                {
                    if (i + 1 >= args.Length) throw new UserError("Missing value after " + a);
                    return args[++i];
                };
                switch (a.ToLowerInvariant())
                {
                    case "--gp":
                    case "--from":
                    case "--source": o.Source = value(); break;
                    case "--steam-dir": o.SteamDir = value(); break;
                    case "--include-settings": o.IncludeSettings = true; break;
                    case "--keep-existing": o.KeepExisting = true; break;
                    case "--no-close": o.NoClose = true; break;
                    case "--yes":
                    case "-y": o.Yes = true; break;
                    default:
                        if (a.StartsWith("-")) throw new UserError("Unknown option " + a + ". Run 'RedfallSaveTransfer help'.");
                        positional.Add(a);
                        break;
                }
            }
            if (positional.Count > 1) throw new UserError("Too many arguments. Run 'RedfallSaveTransfer help'.");
            if (positional.Count == 1)
            {
                if (o.Command == "export") o.Output = positional[0];
                else if (o.Command == "restore") o.BackupPath = positional[0];
                else if (o.Command == "import" || o.Command == "list") o.Source = positional[0];
                else throw new UserError("Unexpected argument " + positional[0]);
            }
            return o;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
Usage:
  RedfallSaveTransfer                      Interactive mode (just double-click the exe)

  RedfallSaveTransfer import [options]     Copy Game Pass saves into the Steam version
      --gp ""<folder>""        Game Pass save folder (containing containers.index) or a folder
                             of plain .sav files. Default: auto-detect on this PC.
      --include-settings     Also transfer Settings.sav (default: keep your Steam settings)
      --keep-existing        Do not remove Steam Player/Hero/Campaign saves that the
                             Game Pass set does not have
      --no-close             Do not close the game afterwards (not recommended)
      --yes                  Do not ask for confirmation

  RedfallSaveTransfer export [""<folder>""]  Decrypt all Steam saves into plain .sav files
  RedfallSaveTransfer restore ""<backup>""   Put a backup made by this tool back in place
  RedfallSaveTransfer list                 Show the saves this tool can find

  Common option: --steam-dir ""<folder>""    Steam save folder, if not the default
                 (%USERPROFILE%\Saved Games\Arkane Studios\Redfall\Steam\SaveGames)

import and export need the Steam version of Redfall running and sitting at the MAIN MENU.
");
        }

        private static void Step(string text) { Console.WriteLine(); Console.WriteLine("== " + text); }
        private static void Info(string text) { Console.WriteLine("  " + text.Replace("\n", "\n  ")); }

        private static void Ok(string text) { Colored(ConsoleColor.Green, "  [OK] " + text); }
        private static void Warn(string text) { Colored(ConsoleColor.Yellow, "  [!] " + text.Replace("\n", "\n      ")); }
        private static void Error(string text) { Console.WriteLine(); Colored(ConsoleColor.Red, "ERROR: " + text.Replace("\n", "\n       ")); }

        private static void Colored(ConsoleColor color, string text)
        {
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }
    }
}
