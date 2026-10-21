# Shogun 2 Shared Turns (experimental)

In a multiplayer campaign of Total War: SHOGUN 2, human players who are not at war with each other play their turns
**at the same time** instead of waiting for each other. The AI clans still move one after another.

Works on its own (the normal 2-player campaign) and together with the **8-player co-op mod v1.4** by Tasman Modding
(a separate download) for up to 8 players. This mod contains none of the 8-player mod's code; it only makes sure the
two fit together.

## What it does
| | |
|---|---|
| **Shared turns (experimental)** | Human players who are not at war with each other play their turns **at the same time**. Pressing End Turn only ends your own part: the AI clans move once the last of you has ended (or the turn timer runs out). A human at war with another human still plays alone. For now, during a shared turn only the first player of the group can open diplomacy, recruit agents, use the clan button or disband units; the others can move, attack, build, recruit and end their turn. Each player's armies find their way with their own clan's view of the map (fog of war, wars, right of passage). Any player of the group can declare war from the attack popup (it opens their diplomacy screen). Battles during a shared turn are untested. |
| **Diagnostics log** | The game writes `shared_turns.log` into the game folder: one line whenever a shared turn starts or ends, someone presses End Turn, the turn passes on, a player orders an army to move (and whether the game accepted it), and for battles: who attacks whom, who gets the pre-battle choice, who answered it, and each step until the battle is over. Clicks on the map are logged only on the PC that clicked. Each game start keeps the previous one as `shared_turns.log.old`. It only records what happens; it changes nothing in the game. Send it along with any problem report. |

## Install / uninstall
1. Everyone closes Shogun 2.
2. **With the 8-player co-op mod:** install that one first (its own `Install.bat`), then this one.
3. Double-click **`Install.bat`**. It finds the game in your Steam libraries. If yours is somewhere else, run
   `powershell -ExecutionPolicy Bypass -File install.ps1 -GamePath "D:\...\Total War SHOGUN 2"`.
4. Start Shogun 2 from Steam and host or join a multiplayer campaign.

**`Uninstall.bat`** puts back the game file it changed: the original, or the 8-player mod's if that was installed.
Steam's "Verify integrity of game files" restores the original game (and removes the 8-player mod too).

Together with the 8-player mod:
- To **add** the 8-player mod later: uninstall this mod, install the 8-player mod, install this mod again.
- **Reinstalling or uninstalling the 8-player mod** removes this mod as well. Run `Install.bat` again afterwards
  (after a reinstall).
- This version knows the 8-player mod **v1.4** only. With another version the installer refuses, and changes nothing.

Everyone must end up with the same hash, or they can't join each other. The installer prints it:

| Installed on | `empire.retail.dll` SHA-256 |
|---|---|
| the original game | `17E179AEF43DAC52FB590633F4DE6BDD1619BE6D4D2A50E75A00894710F9BEEF` |
| the 8-player co-op mod v1.4 | `353C69072E97220649D6393517790D58F5AA0907B038D5C93C0D1B7C8D55661B` |

So either everyone has the 8-player mod, or nobody does.

## Before you play
- **Use spare Steam accounts.** Shogun 2 multiplayer is VAC-protected and this mod changes the engine DLL.
- It only works on the current Steam version of the game; the installer refuses anything else.
- To give desyncs fewer chances: everyone has the same mods (ideally none besides these) and the same game version.

## If something goes wrong
Copy `shared_turns.log` from every player's game folder **before** starting the game again (a new start moves it to
`.log.old`, the start after that overwrites it). After a crash also copy the newest `shogun2_..._crash_....mdmp` from
the crashed PC's game folder. If a turn is stuck: everyone quits and the host loads the newest save.
What to collect for each kind of problem: [human_test.md](human_test.md).

## Test plan
The tests for the current build, and what to send back when one fails, are in **[human_test.md](human_test.md)**.

## Not done yet
- Not handled yet during a shared turn: diplomacy, agent recruitment, the clan button and disbanding for every player
  of the group, battles while others keep moving, saving and loading in the middle of a shared turn (whoever had
  already ended must end again), revolutions in the regions of anyone but the group's first player.
- The first player declaring war on another human of the group from the attack popup freezes the turn.
- Only the first player can skip army movement with the Space bar.

## Files
| Path | What |
|---|---|
| `Install.bat`, `Uninstall.bat`, `install.ps1` | installer / uninstaller |
| `lib\Patcher.cs` | builds the patched engine DLL from yours (checks it first) |
| `verify_load.ps1` | loads a patched DLL with Windows' loader (no code runs) and checks every patch |
| `make_zip.bat`, `make_zip.ps1` | for the mod author: packs the player files into `Shogun2-SharedTurns_<hash>.zip` (needs the reference game copy in `re\game`) |
| `human_test.md` | how to test a build in multiplayer and what to send back when something goes wrong |
| `NOTES.md` | reverse-engineering notes |

The zip contains no game files: the installer builds the mod from your own copy of the game.
