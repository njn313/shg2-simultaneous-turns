# Shogun 2 Shared Turns (experimental)

In a multiplayer campaign of Total War: SHOGUN 2, human players who are not at war with each other play their turns
**at the same time** instead of waiting for each other. The AI clans still move one after another.

Works on its own (the normal 2-player campaign) and together with the **8-player co-op mod v1.4** by Tasman Modding
(a separate download) for up to 8 players. On its own it also brings the 8-player mod's desync fixes (below); with
the 8-player mod installed, that mod's own versions are used.

## What it does
| | |
|---|---|
| **Shared turns (experimental)** | Human players who are not at war with each other play their turns **at the same time**. Pressing End Turn only ends your own part: the AI clans move once the last of you has ended (or the turn timer runs out). A human at war with another human still plays alone (**hybrid** turn mode, the default); in the **simultaneous** turn mode every human always plays at the same time, even at war (chosen when installing, see below). Every player of the group can move, attack, build, recruit units and agents, use diplomacy, the clan button and disband units until they end their turn. One negotiation at a time: while one player negotiates (or answers an attack popup), the others' diplomacy waits; a diplomacy screen opened before that shows the clan but its offers do nothing, so close it and try again a moment later. Each player's armies find their way with their own clan's view of the map (fog of war, wars, right of passage), and moves longer than one turn (and new units walking to their general) carry on when the shared turn ends, for every player. Any player of the group can declare war on an AI clan from the attack popup (it opens their diplomacy screen). When the AI attacks a player's army, that player gets the pre-battle choice as usual. When a player of the group fights a battle, everyone waits for it as in the normal game (nobody moves meanwhile); afterwards an army whose move ran into the battle carries on, for every player. A save made during a shared turn keeps it: after loading it (or after a resync) whoever had ended stays ended, and nobody plays twice in the same round. |
| **Desync: save screen bug** | Opening the Save or Load screen switched that PC's maths and text rules to its Windows language for the rest of the session (e.g. decimal comma on German, Czech, French or Russian Windows), so it no longer matched the other players. Fixed: those screens now only change how dates are shown. |
| **Desync: floating-point drift** | The game never locked the CPU's floating-point settings, so anything that changed them on one PC (overlays, anti-virus, drivers, alt-tabbing) made that PC calculate slightly differently. Fixed: the settings are reset to the game's defaults every frame and every battle tick. |
| **Desync recovery** | If a campaign still goes out of sync, the resync starts by itself instead of waiting for someone to press OK (Cancel used to drop the player). |
| **Battle desync: no more kick** | A battle can't be resynced, so the game used to drop the out-of-sync player. Now the battle carries on for everyone. The out-of-sync player's view may drift (a unit alive on their screen that is dead for others). Back on the campaign map, the host's state is copied to everyone by the campaign resync. |
| **Diagnostics log** | The game writes `shared_turns.log` into the game folder: one line whenever a shared turn starts or ends, someone presses End Turn, the turn passes on, a player orders an army to move or attack (and whether the game accepted it), a popup or diplomacy screen waits for a player's answer and is answered, a negotiation starts, war is declared, the game checks whether an army may enter foreign land, an event message reaches the screen (which part of the game sent it and whether it may show), and for battles: who attacks whom, who gets the pre-battle choice, who answered it, and each step until the battle is over, and desyncs and resyncs (without the 8-player mod). Clicks on the map are logged only on the PC that clicked; event-message lines can differ between PCs too. Each game start keeps the previous one as `shared_turns.log.old`. It only records what happens; it changes nothing in the game. Send it along with any problem report. |

## Install / uninstall
1. Everyone closes Shogun 2.
2. **With the 8-player co-op mod:** install that one first (its own `Install.bat`), then this one.
3. Double-click **`Install.bat`**. It asks for the turn mode (below; press Enter for hybrid). It finds the game in your Steam libraries. If yours is somewhere else, run
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

| Installed on, turn mode | `empire.retail.dll` SHA-256 |
|---|---|
| the original game, hybrid | `8098503C10953D3041CCC55E5AEEE98546895309CAC8112B3E5C827E98C0629D` |
| the 8-player co-op mod v1.4, hybrid | `A41B0EAC9D8CD509158DDC796009FC162B9C7F5E464DDD7E9958C38CADA9CC76` |
| the original game, simultaneous | `B3507360A72D5CEE8B407D4B4256AAF5A5A6BC7426AE6316D715738D4E622EC7` |
| the 8-player co-op mod v1.4, simultaneous | `8069FE689639A76BE863360007582D374F6A0616B5A4D11EB25A33677E6482F1` |

So either everyone has the 8-player mod, or nobody does, and everyone picks the same turn mode:
- **hybrid** (default): players who are not at war with each other play at the same time; a player at war with
  another player plays alone, one after the other as in the normal game.
- **simultaneous**: all players always play at the same time, even at war with each other (they can attack each
  other's armies during the shared turn; everyone waits for a battle as usual). Not tested in game yet.

To switch the mode, everyone runs `Install.bat` again and picks the new one. For the normal game's turns one after
the other, uninstall.

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
- A revolution in the land of a player who isn't first doesn't play out during the shared turn: that player plays
  their next turn alone, after the others (not tested in game yet). During a battle everyone waits, as in the normal
  game (nobody keeps moving while one player fights).
- For players who aren't first, event messages may pop up during the AI clans' turns instead of at the start of
  their next turn.
- After loading a save made during a shared turn with build 0.17 or older, whoever had already ended must press End
  Turn again.
- Only the first player can skip army movement with the Space bar.

## Files
| Path | What |
|---|---|
| `Install.bat`, `Uninstall.bat`, `install.ps1` | installer / uninstaller |
| `lib\Patcher.cs` | builds the patched engine DLL from yours (checks it first) |
| `verify_load.ps1` | loads a patched DLL with Windows' loader (no code runs) and checks every patch |
| `make_zip.bat`, `make_zip.ps1` | for the mod author: packs the player files into `Shogun2-SharedTurns_<hash>.zip` (needs the reference game copy in `re\game`) |
| `human_test.md` | how to test a build in multiplayer and what to send back when something goes wrong |

The zip contains no game files: the installer builds the mod from your own copy of the game.

The desync fixes were found by Tasman Modding for the 8-player co-op mod.

## License
MIT, see [LICENSE](LICENSE). It covers this repository's own files (the installer, the patcher, the scripts and the
notes) in every version, including the commits made before the license was added. It covers no game files: none
are included, and you need your own copy of the game.

Not affiliated with or endorsed by SEGA or Creative Assembly. Total War and Total War: SHOGUN 2 are trademarks of
their owners.
