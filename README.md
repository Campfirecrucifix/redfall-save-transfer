# Redfall Save Transfer (Game Pass → Steam)

Move your **Redfall** heroes and campaign progress from the **Game Pass / Xbox app / Microsoft Store**
version to the **Steam** version.

Copying the files by hand does not work. The Steam version encrypts its saves, so it reports
*"The Player save file was corrupted…"* and your heroes show up as **Failed to load**.
This tool gets the game to encrypt your Game Pass saves itself. You end up with saves the Steam
version can read.

- ✅ Transfers every hero, campaign progress and the player profile (plus the `_Backup` copies)
- ✅ Backs up your existing Steam saves before changing anything, with one-command restore
- ✅ Reads every save back after writing it and checks it matches exactly; rolls back automatically on any error
- ✅ Can also **export** (decrypt) your Steam saves to plain `.sav` files
- ❌ Does not go the other way (Steam → Game Pass)
- ❌ Does not modify the game, bypass DRM or touch online features

---

## Requirements

- Windows 10 or 11 (64-bit)
- The **Steam** version of Redfall, installed and launched at least once
- Your Game Pass save files. If the Game Pass version was installed on this PC, they are found
  automatically, **even if the game itself is uninstalled**.

## Quick start

1. Download `RedfallSaveTransfer.exe` from the [Releases](../../releases) page.
2. Start **Redfall on Steam** and wait at the **main menu**. Don't pick a hero.
3. Double-click `RedfallSaveTransfer.exe`.
4. Choose **1) Transfer Game Pass saves to the Steam version**.
5. Pick your Game Pass save set. Your heroes are listed with their play time, so you can tell the sets apart.
6. Confirm. The tool will:
   - run a self test (writes, reads back and deletes a temporary save),
   - back up your current Steam saves,
   - write and verify every save,
   - **close Redfall**. This is needed because the game still has the old saves in memory.
7. Start Redfall from Steam again. **If Steam shows a Cloud sync conflict, choose the files on this PC (Local).**
8. Your heroes are back. Load one, play until it autosaves, then quit normally.

> ⚠️ **Your existing Steam heroes and progress are replaced** by the Game Pass ones. They are
> backed up first (see [Undo](#undo--restore-a-backup)).

## Where are my Game Pass saves?

```
%LOCALAPPDATA%\Packages\BethesdaSoftworks.Redfall_3275kfvn8vcwc\SystemAppData\wgs\<long id>\
```

The `<long id>` folder contains a file called `containers.index` and several folders with long hex names.

If your Game Pass saves are on a **different PC**:

1. Copy that whole `<long id>` folder to a USB stick or cloud drive.
2. On the Steam PC, choose *Transfer* and paste the folder's path when asked, or run:

```
RedfallSaveTransfer import --gp "D:\RedfallGamePassSave"
```

Any parent folder works too, as long as it contains only one save set.

Saves you have already extracted to plain `.sav` files (`Player.sav`, `Hero1.sav`, `Campaign1.sav`, …) also work:
point the tool at that folder.

## Command line

```
RedfallSaveTransfer                      Interactive mode (double-click)

RedfallSaveTransfer import [options]     Copy Game Pass saves into the Steam version
    --gp "<folder>"        Game Pass save folder (containing containers.index) or a folder of
                           plain .sav files. Default: auto-detect on this PC.
    --include-settings     Also transfer Settings.sav (default: keep your Steam settings)
    --keep-existing        Don't remove Steam Player/Hero/Campaign saves that the Game Pass
                           set doesn't have
    --no-close             Don't close the game afterwards (not recommended)
    --yes                  Don't ask for confirmation

RedfallSaveTransfer export ["<folder>"]  Decrypt all Steam saves into plain .sav files
RedfallSaveTransfer restore "<backup>"   Put a backup made by this tool back in place
RedfallSaveTransfer list                 Show the saves this tool can find

Common option: --steam-dir "<folder>"    Steam save folder, if not the default
```

`import` and `export` need the Steam version of Redfall running at the main menu.
`restore` needs the game **closed**. `list` works either way.

## Undo / restore a backup

Before every import, the tool copies your Steam save folder to

```
Documents\RedfallSaveTransfer\Backups\<date>_before-import\
```

The `decrypted` subfolder inside contains readable copies of those saves.

To put a backup back, **close Redfall** and either:
- double-click the exe and choose **3) Restore a backup**, or
- run `RedfallSaveTransfer restore "<backup folder>"`.

Restoring also makes a backup of the current state first, so a restore can be undone too.

## How it works

The Steam build of Redfall (Unreal Engine 4.26) saves the same `GVAS` files as the Game Pass
build, but runs them through encryption before writing them to
`%USERPROFILE%\Saved Games\Arkane Studios\Redfall\Steam\SaveGames`. The key is never stored
anywhere usable, and the save code sits inside the game's DRM-protected code. So this tool doesn't
reimplement the encryption. It asks the game to do it:

1. It reads `Redfall.exe` from disk and finds the engine functions
   `UGameplayStatics::LoadDataFromSlot` and `UGameplayStatics::SaveDataToSlot` by searching for
   the places where the engine calls them. It checks that the same bytes are in the running game.
2. For each save it copies the plain save data and the slot name into the game's memory. Then it
   starts one thread that calls `SaveDataToSlot(data, "Hero1", 0)`. The game encrypts the data and
   writes the file exactly as if it had saved it itself.
3. It calls `LoadDataFromSlot` for the same slot and compares the decrypted result byte-for-byte
   with the original.

Nothing in the game is patched or hooked. All memory the tool allocates in the game is freed again.

`containers.index` (the Xbox/Game Pass save index) is parsed to map the randomly named blob files
back to their slot names (`Player`, `Hero1`, `Campaign1`, …).

## Troubleshooting

| Problem | Fix |
|---|---|
| *Redfall is not running* | Start the **Steam** version and wait for the main menu. |
| *This version of the game is not supported* | The game exe differs from the one this was built for (`++redfall+Release_1.0451`). Open an issue. |
| *Could not start a thread in the game* / exe deleted | Antivirus software. Tools that call functions inside another process look suspicious to some antivirus programs. Add an exception, or build the tool yourself from source (see below). |
| *Could not open the Redfall process* | Run the tool as the same user as the game. If the game runs as administrator, run the tool as administrator too. |
| Heroes still say *Failed to load* | Make sure you picked **Local** on the Steam Cloud conflict prompt. Use `list` to check that all saves say `encrypted`. |
| Steam Cloud brought back old files | Run the transfer again and pick **Local** when Steam asks. |

## Building from source

There's nothing to install. Windows already ships with the C# compiler this needs.

```
build.cmd
```

The output is `bin\RedfallSaveTransfer.exe`. Opening the `src\*.cs` files in Visual Studio / `dotnet` with a
.NET Framework 4.x target also works.

## Disclaimer

This is an unofficial fan tool, not affiliated with Arkane, Bethesda or Microsoft. Back up your saves.
It only uses the game's own save functions on your own save files. It doesn't bypass any copy protection,
and it doesn't affect anyone else's game.

## License

MIT, see [LICENSE](LICENSE).
