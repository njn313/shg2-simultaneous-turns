# Human test guide

How to test a new build of the mod in real multiplayer games, and what to collect when something goes wrong.
The short checklist is in `README.md` ("Test plan"); this file has the details.

## Before a session
- **EXPERIMENTAL.** Steam no longer lists VAC for Shogun 2, but this mod changes the engine DLL: a spare Steam account is the extra-safe choice.
- Everyone installs the same build, either all with the 8-player co-op mod v1.11 or all without. The DLL hash the
  installer prints must be the same for everyone and match the one in `README.md`. Write down the first 8 letters
  of the DLL hash: it says which build you tested (and whether the 8-player mod was installed).
- No other mods enabled (besides the 8-player mod, if you use it).
- Write down for the session: date, build (8 letters), turn mode (hybrid or simultaneous, the installer prints it), with or without the 8-player mod, host, every player and their clan, who is allied with whom.
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
question), that's not a bug. Battles started by a player during a shared turn are tests 9a-9f.

"The first player" below is the human whose clan comes first in the turn order.

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
| 2d | A player who isn't first recruits units in a settlement with an army in it, and into an army standing next to it. | Recruitment works like for the first player; the unit cards are not greyed out and don't say the units can't reach the army. If a card says there's no path, note which. |
| 2f | A player who isn't first attacks a clan they're at peace with (AI or human) and picks "declare war" in the popup. | Against an AI clan: their diplomacy screen opens; after closing it nobody is blocked (`pa=0` again in the log). Note whether the war was really declared. Against a human it freezes in this build: that's test 16. |
| 2g | A player who isn't first recruits units in a settlement with their general standing next to it (outside), the units assigned to that general. | Next turn the units appear with an arrow to the general; when the group's turn ends they walk out and join him, before the AI clans move. The log has `END_MOVES <your clan> resumes an order left from its turn` on every PC. |
| 2h | A player who isn't first orders an army (and an agent) to a place further than one turn's movement, then everyone ends the turn. | When the group's turn ends, that army and agent continue towards their destination like the first player's do, on every PC (`END_MOVES` lines). |
| 2e | While others move: does the cursor or the armies' movement feel slower than in the first player's turn? | No noticeable difference. If it's slower, note on which PC and when. |
| 3 | End turns in this order: the last player, then the middle one, then the first player. | Whoever has ended can't move armies any more; the others keep playing. The AI clans start moving only after the last one has ended. |
| 4 | Next turn. | All of you start together again. Nobody gets a second turn in the same round; movement is reset once. |
| 5 | Repeat 3 in a different order: the first player ends first. | Same as 3. |
| 6 | If the campaign has a turn timer: let it run out without ending. | Everyone's turn ends. |
| 7 | While not the first player: build, recruit units, research, change taxes. | It works and shows up on every PC. |
| 8 | While not the first player (and not ended yet): open diplomacy (button and the clan list on the radar), negotiate with an AI clan (e.g. trade), recruit an agent, open the clan screen, disband a unit (and scupper a ship). Then end your turn and try them again. | All work like for the first player and show up on every PC; after ending your turn they are greyed out again. The log has `DIPLOMACY negotiation <your clan> with ...` and `PENDING` lines on every PC. |
| 8a | Two players negotiate one after the other: the first player opens diplomacy with an AI clan and starts negotiating (the offers screen); while that screen is open, a player who isn't first tries to open diplomacy. Then the other way round. | The second player's diplomacy button is greyed out once the first one is negotiating (not while they only look at the relations list) until the first one closes the screen; then it works. Nobody gets stuck. |
| 8b | Both players open the diplomacy screen at the same time (relations list, no clan picked yet). One picks a clan and starts negotiating; then the other picks a clan too. | No crash (build 0.10 crashed here, on whichever PC was second). The second one's "start negotiations" does nothing: the clan list stays (log: `DIPLOMACY <clan> picked a clan in the diplomacy panel while another clan's negotiation or popup is pending`, once per click). Once the first one has closed their negotiation, it works. Build 0.11 opened an empty offers screen here instead (no buttons, Cancel didn't work, Esc did). The first one's negotiation goes on normally. Note anything else that happens on either PC. |
| 8c | A player who isn't first offers a deal to the first player (both human, e.g. trade). Then the other way round. | The receiver gets the offer: the proposer's clan on the right of the screen, the offer listed, and accept / decline / counter, like an offer from an AI clan. Builds 0.9-0.12 showed the receiver an empty screen. Note what each PC showed. |
| 8d | During a shared turn, each player tries to save (Esc menu). | A player who hasn't ended gets the save dialog; one who has ended gets the "player X saved" message instead (note which you got). If a save is made, it loads as in test 11. |
| 8e | (Optional, needs a battle) A player who isn't first attacks an AI army and uses "demand surrender" in the pre-battle screen. | The demand is made by that player's clan (the AI answers it as usual). Note what happens. |
| 9a | A player who isn't first attacks an AI army and fights the battle (not autoresolve). | Everyone gets the pre-battle screen as in the normal game (the other player may play the AI side or watch). The battle loads for everyone; afterwards every PC shows the same result, the shared turn goes on and everyone can move again. The log has `BATTLE new: <your clan> attacks ...` and `BATTLE state` lines on every PC. |
| 9b | A player who isn't first sends an army on a move that passes close to an enemy army, so that the move turns into a battle on the way (win it or autoresolve it). | If the army survives and has movement left, it carries on to where it was sent, on every PC; the log has `RESUME <your clan> carries on with a move the battle interrupted`. Note if it just stops instead. |
| 9c | While the other player's battle is open on the campaign map (their pre-battle screen, their result, their army moving afterwards, or an autoresolve): note whether you also got a pre-battle screen. If you're on the free map, try to move an army and press End Turn. | Nothing happens (no freeze); both work again once the battle is over. |
| 9d | A player who isn't first attacks an AI army and autoresolves. | The same result on every PC; everyone can move afterwards. |
| 9e | A player who isn't first takes an AI settlement (siege assault). | After the battle the army moves into the settlement and it belongs to that clan on every PC. Note any extra popup or question and who got it. |
| 9f | Two players of the group have armies next to each other; one of them attacks an AI army standing next to both. | Note whether the other player's army joins the battle and who commands it. The same result on every PC; both can move afterwards. |
| 10 | (Hybrid) The first player declares war on another human (diplomacy). | From the next round, that human plays alone, after the others. |
| 22 | (Simultaneous) Two players at war with each other (a head-to-head campaign, or declare war as in test 10). | Both play at the same time from the start of the turn, and in every later turn; the AI clans move after both have ended. The log has `START group, lead ...` with 2 members. |
| 23 | (Simultaneous) One player attacks the other's army while the other is still moving; then the other way round; once a siege of the other's settlement. | The defender gets the pre-battle choice; everyone waits for the battle as usual; afterwards both can move again; the same result on every PC; no "out of sync". |
| 25 | (Simultaneous, or hybrid right after a war declared between two players) A orders an attack on B's army; while A's army is still walking, B moves that army away (far enough that the armies don't touch). Then once without B moving. | When B moved away: A's army stops where B's army stood and no battle starts; the log has `ATTACK <A> reached where the army of <B> stood ..., but that army has moved: no battle` on every PC. A can order the attack again. When B didn't move: the battle starts as usual. |
| 24 | (Simultaneous) Play 3 or more turns at war, ending the turn in either order; save and load once in the middle of a turn. | Nobody plays twice in a round; after the load both are still in the shared turn (test 11). |
| 11 | In the middle of a shared turn, after a few moves (one player has ended): host saves, everyone quits, host loads and the others rejoin. Also with an older save from the middle of a turn. | Everyone can move again at once. Armies that had already moved keep their used-up movement (no fresh movement, no second income); whoever had ended **stays ended** (new in build 0.18). The PC that saved logs `SAVE the group goes into the save ...`; after the load every PC logs `LOAD the group from the save: lead ...`. With a save from build 0.17 or older: `LOAD a save made during a turn ... without the group in it` and `START group ...` instead, and whoever had ended must press End Turn again. |
| 11a | A player who isn't first declares war on the first player during a shared turn (as in test 16; cancel or autoresolve the battle). Both keep playing; then the host saves, everyone quits, the host loads and the other rejoins. Both end the turn. | After the load both are still in the shared turn: each can move and end the turn (build 0.17 refused the second player's End Turn here and gave them a second turn later). The AI clans move after both have ended; nobody plays twice in that round. From the next round the two play one after the other. Log: `LOAD the group from the save: lead <first player>, 2 member slots`. |
| 11b | (3 humans, needs the 8-player co-op mod) The first and the third human in the turn order are allies, the second is at war with the first. The first and third play together, then the second alone. During the second human's turn the host saves; everyone quits, the host loads, the others rejoin; the second human ends the turn. | The AI clans move next: the third human doesn't get a second turn in that round (the log has `SKIP <third human's clan> already played this round`). |
| 12 | Play 5 or more turns normally. | No "out of sync" popup. |
| 13 | A player who isn't first leaves an army where a hostile AI army can reach it, ends the turn, and the host saves just before the last player ends (the save from before the AI turns). Repeat over a few turns until the AI attacks it. | That player gets the pre-battle choice (fight, autoresolve, retreat) like in the normal game. The log has `BATTLE new`, `BATTLE pre-battle entry for <clan>` lines and `BATTLE state` steps. If the army is destroyed without a choice: see below. |
| 14 | Only with the 8-player co-op mod v1.11 installed under this one: host with 3 or more players (co-op). Fight a battle with one player while the others aren't in it; in a simultaneous build also a battle between two players with a third watching. | The lobby has 8 slots and its kick / invite buttons and player count work; every human is on the co-op team, so all play the shared turn together. The players not in a battle follow it (no closed game), and afterwards every PC shows the same result and the shared turn goes on. Anything of the 8-player mod that works without this mod and doesn't with it is a conflict between the two mods: note what. |
| 15 | (Removed in build 0.17: the host resync key Ctrl+Shift+F9 caused a real desync and a crash or kick in build 0.16; it comes back later in another form.) | - |
| 16 | A player who isn't first attacks the first player's army (right-click on it) and declares war in the popup; in another turn the other way round. (It froze before build 0.5; passed in 0.5.) Copy both logs right away. | Note for each PC: which popup or screen appeared, what was clicked, and what each player could still do afterwards (move, End Turn, open menus). The logs should have an `ATTACK ...` line, then `PENDING push` lines and maybe `DIPLOMACY` lines: they show where it stops. If the first player is willing, repeat the other way round in a new session. |
| 17 | A player who isn't first: after ending your turn, watch for event messages (agent discovered, diplomacy, disasters, ...). | Note each message and when it popped up: right after the shared turn ended, during the AI clans' moves (which clan was moving, if you can see it), or at the start of your next turn. Note the same for the first player, for comparison. Copy every player's `shared_turns.log` afterwards, even if nothing odd happened: the `MESSAGE` lines (one per event message) say which part of the game sent each message. |
| 19 | A player who isn't first spends money until the treasury would go negative at the end of the turn, then presses End Turn. | The "will go bankrupt" question appears on that player's own PC (note on which PCs it appeared). After answering, that player's turn ends as usual; the others are blocked only while the question is open. |
| 20 | During a shared turn a player who isn't first (and isn't the host) leaves the game (quits to the main menu) while the others still play. | The others keep playing; the AI clans move once the remaining players have ended. The log has `DROP <clan> handed to the AI or removed: counts as ended` on every PC (new in build 0.21: before, the turn could get stuck waiting for that clan). The AI plays that clan from its next turn. |
| 20a | As 20, but the player who leaves is the first player (not the host). | Same as 20: the others keep playing and end their turn; the first player's clan does nothing more in that shared turn (the AI doesn't play it then), the AI clans move after the others have ended. |
| 26 | (8-player co-op mod v1.11, 3 or more humans) During a shared turn the host saves; everyone quits; the host resumes the save with one player missing (not the host). Once with a player who isn't first missing, once with the first player missing. | A few seconds after the map comes up, the missing player's clan goes to the AI (the 8-player mod does this); the log has `DROP <clan> handed to the AI ...`. The others keep playing and end the turn; nobody waits for the missing clan; the AI clans move next. From the next turn on the AI plays that clan. |
| 26a | Then save again and resume with the missing player back. | Their clan is theirs again a few seconds after the map comes up (the 8-player mod). In that round they may play alone after the others (if the AI hadn't played their clan yet); nobody plays twice in a round; from the next round they play with the others. |
| 21 | (When it happens) A revolution starts in the land of a player who isn't first at the start of a shared turn (rebel armies appear). | Note what happens. The log has `REVOLT <clan>: a revolution started ...`. That player can still play the shared turn; in the next round they play alone, after the others, and the rebels act then. Copy both logs and the host's save from that turn. |
| 27 | (Optional, for a planned feature) During a shared turn, look at the top of the screen on every PC: which clan flags are shown, which one is in the middle and which are grey. Once on the first player's PC, once on another player's. Then the same during the AI turns. | Only a note (a screenshot helps). Expected today: nothing on the first player's PC; on the others the first player's flag in the middle, with grey flags of the clans before and after it in the turn order. |
| 18 | A player who isn't first gets military access from an AI neighbour (note how: diplomacy screen from the attack popup, or the AI offered it), then moves an army into that land, now and again next turn. Note the time of each try. | The army walks in without asking again. If it asks again or shows a red cross: note the time; the log has two `ACCESS` lines there (whose view of the map the game used). |

## When something goes wrong
Note the time, the turn number and the test number first. Then:

| What happens | What to collect |
|---|---|
| **The game crashes** (closes or "stopped working") | On that PC, from the game folder: the newest `shogun2_..._crash_....mdmp` (check its time) and `shared_turns.log` (if the game was started again since, `shared_turns.log.old`). If there's no new `.mdmp`: press Win+R, type `eventvwr.msc`, open **Windows Logs → Application**, find the **Application Error** entry at the crash time for `Shogun2.exe`, press Ctrl+C and paste it into a text file. The other players: their `shared_turns.log`. |
| **The turn gets stuck** (nobody can end, the AI never starts, someone can't move) | Write down who the first player was, who pressed End Turn and in what order, what each player's End Turn button looked like, and whether armies could still move. If it started with an attack or a popup: who attacked whom (AI or human army, at war or at peace, did either side have allies), which popup or screen appeared on which PC, and what was clicked. Copy `shared_turns.log` **from every player** before anything else. Then everyone quits and the host loads the newest save. |
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
Did reloading help?
Files in this folder:
```

Name each player's files after them (e.g. `shared_turns_host.log`, `shared_turns_player2.log`) so the logs
can be compared line by line. Even a session where everything worked is worth one log from each player: it shows
the shared turns working as designed.

Then tell Claude the folder name (Claude can only read files inside the repo folder).
