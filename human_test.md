# Human test guide

How to test a new build of the mod in real multiplayer games, and what to collect when something goes wrong.
The short checklist is in `README.md` ("Test plan"); this file has the details.

## Before a session
- **Spare Steam accounts only.** Multiplayer is VAC-protected and this mod changes the engine DLL.
- Everyone installs the same build, either all with the 8-player co-op mod or all without. The DLL hash the
  installer prints must be the same for everyone and match the one in `README.md`. Write down the first 8 letters
  of the DLL hash: it says which build you tested (and whether the 8-player mod was installed).
- No other mods enabled (besides the 8-player mod, if you use it).
- Write down for the session: date, build (8 letters), with or without the 8-player mod, host, every player and their clan, who is allied with whom.
- Make a folder for the session's results: `re\reports\<date>\` in the repo folder (e.g. `re\reports\2026-10-21\`).
  The host or whoever runs the test collects everything there. Other players send their files to that person.
- The host saves at the start of a turn before trying something risky. A save from just before a problem is the
  most useful thing to have.
- Find your game folder once: in Steam, right-click Total War: SHOGUN 2 → Manage → Browse local files. Two files
  there matter for reports:
  - `shared_turns.log`: the mod writes one line whenever a shared turn starts or ends, someone presses End
    Turn, the turn passes on, someone orders an army to move (`MOVE ... accepted` or `MOVE ... refused, reasons ...`)
    or to attack (`ATTACK ...`), an event message reaches the screen (`MESSAGE ...`), a popup or diplomacy screen starts waiting for an answer or is answered (`PENDING push`
    / `PENDING pop`), a negotiation starts or war is declared (`DIPLOMACY ...`), the game checks whether an army may
    enter foreign land (`ACCESS ...`), a queued move carries on at the end of the turn (`END_MOVES ...`), or a battle
    starts and moves on (`BATTLE ...`), or a desync or resync happens (`DESYNC` / `RESYNC`, only without
    the 8-player mod: with it, that mod's own desync handling runs and isn't logged). `CLICK` lines (an order given on the map) appear only on the PC that clicked and `MESSAGE` lines
    may differ between PCs; every other line should be the same on every PC. Each new game start moves the previous log to
    `shared_turns.log.old`, so **copy it before starting the game a second time** after a problem.
  - `shogun2_..._crash_....mdmp`: the game writes one when it crashes. Only the newest one matters.

## Shared turns (current build)
Setup: a new multiplayer campaign with 2 humans (3 or more need the 8-player co-op mod) who are **allied** (co-op campaign). Write down whether the
clans are allied or only at peace: at peace, moving into the other's land asks first (the game's normal trespass
question), that's not a bug. Avoid battles until test 9.

"The first player" below is the human whose clan comes first in the turn order. In this build it is the only one
of the group who can still open diplomacy.

Known problems in this build (found in the tests so far; no need to report them again unless something new happens):
- Event messages (agent discovered and the like) of a player who isn't first may pop up during the AI turns (test 17).

For each test, note pass / fail and anything odd, even if it didn't break the game.

| # | Do | Expected |
|---|---|---|
| 1 | Wait for the human clans' turn. | Everyone's turn starts at the same time. Every player's End Turn button works; armies have full movement. |
| 2 | Everyone moves a few armies at the same time. | Each move shows up in the same place on every PC. |
| 2a | A player who isn't first moves an army in the field, then one out of a castle, inside their own land and into an ally's land. Hover over the map with the army selected. | The cursor shows a normal move order (no red cross), the path preview appears, the army moves. The log says `MOVE <your clan> accepted, pathfinder kept for <your clan>` on every PC. |
| 2b | The first player moves armies while the others' armies are still walking. | The first player's moves work as before (`MOVE ... kept for <first player's clan>`). Nothing jumps or stops on any PC. |
| 2c | A player who isn't first orders an army to attack an AI army or settlement (cancel or autoresolve the battle if one starts). | The order is accepted: the army walks up and the attack starts. |
| 2d | A player who isn't first recruits units in a settlement with an army in it, and into an army standing next to it. | Recruitment works like for the first player. If a card says there's no path, note which. |
| 2f | A player who isn't first attacks a clan they're at peace with (AI or human) and picks "declare war" in the popup. | Against an AI clan: their diplomacy screen opens; after closing it nobody is blocked (`pa=0` again in the log). Note whether the war was really declared. Against a human it freezes in this build: that's test 16. |
| 2g | A player who isn't first recruits units in a settlement with their general standing next to it (outside), the units assigned to that general. | Next turn the units appear with an arrow to the general; when the group's turn ends they walk out and join him, before the AI clans move (the card may still warn on the non-first player's PC: cosmetic). The log has `END_MOVES <your clan> resumes an order left from its turn` on every PC. |
| 2h | A player who isn't first orders an army (and an agent) to a place further than one turn's movement, then everyone ends the turn. | When the group's turn ends, that army and agent continue towards their destination like the first player's do, on every PC (`END_MOVES` lines). |
| 2e | While others move: does the cursor or the armies' movement feel slower than in the first player's turn? | No noticeable difference. If it's slower, note on which PC and when. |
| 3 | End turns in this order: the last player, then the middle one, then the first player. | Whoever has ended can't move armies any more; the others keep playing. The AI clans start moving only after the last one has ended. |
| 4 | Next turn. | All of you start together again. Nobody gets a second turn in the same round; movement is reset once. |
| 5 | Repeat 3 in a different order: the first player ends first. | Same as 3. |
| 6 | If the campaign has a turn timer: let it run out without ending. | Everyone's turn ends. |
| 7 | While not the first player: build, recruit units, research, change taxes. | It works and shows up on every PC. |
| 8 | While not the first player: try diplomacy, agent recruitment, the clan button, disbanding a unit. | Greyed out / does nothing. That's known for this build, not a bug. |
| 9 | (Optional) A player who isn't first attacks an AI army while the others keep moving. | Not supported yet. Note what happens: who sees the battle, whether the others' moves stop, whether the turn can still end. |
| 10 | The first player declares war on another human (diplomacy). | From the next round, that human plays alone, after the others. |
| 11 | In the middle of a shared turn, after a few moves (one player has ended): host saves, everyone quits, host loads and the others rejoin. Also with an older save from the middle of a turn. | Everyone can move again at once. Armies that had already moved keep their used-up movement (no fresh movement, no second income); whoever had ended must press End Turn again. The log has `LOAD a save made during a turn ...` followed by `START group ...` on every PC. |
| 12 | Play 5 or more turns normally. | No "out of sync" popup. |
| 13 | A player who isn't first leaves an army where a hostile AI army can reach it, ends the turn, and the host saves just before the last player ends (the save from before the AI turns). Repeat over a few turns until the AI attacks it. | That player gets the pre-battle choice (fight, autoresolve, retreat) like in the normal game. The log has `BATTLE new`, `BATTLE pre-battle entry for <clan>` lines and `BATTLE state` steps. If the army is destroyed without a choice: see below. |
| 14 | Only with the 8-player co-op mod installed under this one: host with 3 or more players. | The lobby has 8 slots and its kick / invite buttons work; after a battle every player sees the right result; the host's Ctrl+Shift+F9 resyncs. The shared turns work for all allied humans. Anything of the 8-player mod that worked before and doesn't now is a conflict between the two mods: note what. |
| 15 | On the campaign map, during a shared turn, the host presses **Ctrl+Shift+F9** (with and without the 8-player mod). | The resync popup appears for everyone and the game carries on afterwards; everyone can still move (the log has `LOAD a save made during a turn ...` and `START group ...` after the resync; whoever had ended ends again). Without the 8-player mod the log has `RESYNC host pressed Ctrl+Shift+F9` on the host and `RESYNC forced by the host's key` on the others. |
| 16 | A player who isn't first attacks the first player's army (right-click on it) and declares war in the popup; in another turn the other way round. (It froze before build 0.5; passed in 0.5.) Copy both logs right away. | Note for each PC: which popup or screen appeared, what was clicked, and what each player could still do afterwards (move, End Turn, open menus). The logs should have an `ATTACK ...` line, then `PENDING push` lines and maybe `DIPLOMACY` lines: they show where it stops. If the first player is willing, repeat the other way round in a new session. |
| 17 | A player who isn't first: after ending your turn, watch for event messages (agent discovered, diplomacy, disasters, ...). | Note each message and when it popped up: right after the shared turn ended, during the AI clans' moves (which clan was moving, if you can see it), or at the start of your next turn. Note the same for the first player, for comparison. Copy every player's `shared_turns.log` afterwards, even if nothing odd happened: the `MESSAGE` lines (one per event message) say which part of the game sent each message. |
| 18 | A player who isn't first gets military access from an AI neighbour (note how: diplomacy screen from the attack popup, or the AI offered it), then moves an army into that land, now and again next turn. Note the time of each try. | The army walks in without asking again. If it asks again or shows a red cross: note the time; the log has two `ACCESS` lines there (whose view of the map the game used). |

## When something goes wrong
Note the time, the turn number and the test number first. Then:

| What happens | What to collect |
|---|---|
| **The game crashes** (closes or "stopped working") | On that PC, from the game folder: the newest `shogun2_..._crash_....mdmp` (check its time) and `shared_turns.log` (if the game was started again since, `shared_turns.log.old`). If there's no new `.mdmp`: press Win+R, type `eventvwr.msc`, open **Windows Logs → Application**, find the **Application Error** entry at the crash time for `Shogun2.exe`, press Ctrl+C and paste it into a text file. The other players: their `shared_turns.log`. |
| **The turn gets stuck** (nobody can end, the AI never starts, someone can't move) | Write down who the first player was, who pressed End Turn and in what order, what each player's End Turn button looked like, and whether armies could still move. If it started with an attack or a popup: who attacked whom (AI or human army, at war or at peace, did either side have allies), which popup or screen appeared on which PC, and what was clicked. Copy `shared_turns.log` **from every player** before anything else. Then the host presses **Ctrl+Shift+F9** (resync). Note whether that fixed it; if not, everyone quits and the host loads the newest save. |
| **"Out of sync" popup** | `shared_turns.log` and the newest files from the game's `logs` folder **from every player**, and what everyone did just before. |
| **An army won't move** (red cross, or the order is ignored) | Which player (first or not), which army, from where to where, allied or at peace with the land it enters. `shared_turns.log` from every player: the `CLICK` line says what the cursor offered (order type 0 = red cross), the `MOVE` line why the game refused (reason bits are listed in the line). |
| **An army is attacked or destroyed without the pre-battle choice** (test 13) | Who owned the army, what each player saw (enemy army visible, attack sound, the approach), `shared_turns.log` **from every player** (the `BATTLE` lines) and the host's save from before the AI turns. |
| **Something behaves wrong** (a double turn, movement not reset, the AI moving too early, a player's moves missing on another PC) | A description, `shared_turns.log` from every player, and the host's save from the start of that turn if there is one. Screenshots from two PCs showing the difference help. |

## Report
One `report.md` per session in the session folder, the collected files next to it:

```
Date / build (8 letters):
Host / players and clans / who is allied:
Test # and turn number:
What we did:
What we expected:
What happened (each player's view):
With or without the 8-player mod:
Did Ctrl+Shift+F9 help? Did reloading?
Files in this folder:
```

Name each player's files after them (e.g. `shared_turns_host.log`, `shared_turns_player2.log`) so the logs
can be compared line by line. Even a session where everything worked is worth one log from each player: it shows
the shared turns working as designed.

Then tell Claude the folder name (Claude can only read files inside the repo folder).
