// Builds the Shared Turns patch of Shogun 2's empire.retail.dll: human clans that are not at war with each other play
// their turns at the same time (AI turns stay one after another), plus a diagnostics log. Never modifies its input:
// it checks the input is exactly a build it knows, and writes a new file.
//
// Inputs it accepts (anything else is refused):
//  - the original empire.retail.dll of the current Steam build (OriginalSha256);
//  - the 8-player co-op mod v1.11 (Tasman Modding) built from that original (EightPlayerV111Sha256), so both mods can
//    be installed together: the 8-player mod first, this one on top.
// All new code goes into a section of its own (".s2st", appended after the last section; the header has room for
// several more); hooks are relative jumps/calls; data lives in .data's slack from DataFloor on, past the 8-player
// mod's data. Same input -> same output on every PC: no timestamps, no paths, no unordered collections.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class SharedTurnsPatcher {
	public const string OriginalSha256 = "022FAE2E2DA92B59B8B1B8EE52887003C624E3C8ECA060B209F5D792A7F874F5";
	public const string EightPlayerV111Sha256 = "971C7A955B045433F54161126A6866B3F37541A90CB23B2F3C88DB9A8ECD2280";
	public const string SectionName = ".s2st";
	const uint ImageBase = 0x10000000;
	// .data ends at 11CB152C in the original; the 8-player mod v1.11 grows it to 11CB1910. Ours start here, leaving it
	// room for later versions, and must stay in the same page (11CB2000 is _RDATA); Build refuses an input whose .data
	// already reaches DataFloor
	const uint DataFloor = 0x11CB1C00;

	// ---- the image being patched (file layout) -------------------------------------------------------------------
	static byte[] b;
	static int peOff, optOff, secTableOff, nSec;
	static uint[] relocVas;   // every address a HIGHLOW relocation rewrites (4 bytes each), sorted
	public static string BaseName;
	// the turn mode, chosen at install (every player must choose the same: the DLLs differ): "hybrid" = humans not at
	// war with each other share a turn, a human at war with one of them plays alone; "simultaneous" = every human
	// shares the turn, at war or not (GroupBuild has no war test). Sequential turns are the unmodded game.
	public static string TurnMode = "hybrid";

	static uint U32(int off) { return BitConverter.ToUInt32(b, off); }
	static void Put32(int off, uint v) { Array.Copy(BitConverter.GetBytes(v), 0, b, off, 4); }
	static int SecOff(int i) { return secTableOff + 40 * i; }
	static string SecName(int i) { return Encoding.ASCII.GetString(b, SecOff(i), 8).TrimEnd('\0'); }
	static uint SecVa(int i) { return ImageBase + U32(SecOff(i) + 12); }
	static uint Align(uint v, uint a) { return (v + a - 1) / a * a; }
	static string Sha(byte[] d) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(d)).Replace("-", ""); }

	static void ReadHeaders() {
		peOff = BitConverter.ToInt32(b, 0x3C);
		if (U32(peOff) != 0x4550) throw new Exception("not a PE file");
		nSec = BitConverter.ToUInt16(b, peOff + 6);
		optOff = peOff + 24;
		secTableOff = optOff + BitConverter.ToUInt16(b, peOff + 20);
	}

	static int FindSection(string name) {
		for (int i = 0; i < nSec; i++) if (SecName(i) == name) return i;
		return -1;
	}

	static int VaToOff(uint va) {
		for (int i = 0; i < nSec; i++) {
			uint start = SecVa(i), raw = U32(SecOff(i) + 16);
			if (va >= start && va - start < raw) return (int)(U32(SecOff(i) + 20) + (va - start));
		}
		throw new Exception(string.Format("{0:X8} is not in any section's file data", va));
	}

	// the base relocation table (data directory 5): blocks of (page RVA, size, 16-bit entries type << 12 | offset)
	static void ReadRelocs() {
		uint rva = U32(optOff + 96 + 5 * 8), size = U32(optOff + 96 + 5 * 8 + 4);
		var list = new List<uint>();
		int at = VaToOff(ImageBase + rva), stop = at + (int)size;
		while (at + 8 <= stop) {
			uint page = U32(at), blockSize = U32(at + 4);
			if (blockSize < 8) break;
			for (int e = at + 8; e < at + (int)blockSize; e += 2) {
				int entry = BitConverter.ToUInt16(b, e);
				if (entry >> 12 == 3) list.Add(ImageBase + page + (uint)(entry & 0xFFF));
				else if (entry >> 12 != 0) throw new Exception("unexpected relocation type " + (entry >> 12));
			}
			at += (int)blockSize;
		}
		list.Sort();
		relocVas = list.ToArray();
	}

	// the loader rewrites 4 bytes at every relocation: a patch must not touch any of them
	static void CheckNoReloc(uint va, int len) {
		int lo = 0, hi = relocVas.Length;
		while (lo < hi) { int mid = (lo + hi) / 2; if (relocVas[mid] + 4 <= va) lo = mid + 1; else hi = mid; }
		if (lo < relocVas.Length && relocVas[lo] < va + (uint)len)
			throw new Exception(string.Format("patch {0:X8}+{1} overlaps the relocation at {2:X8}", va, len, relocVas[lo]));
	}

	// ---- writing patches -------------------------------------------------------------------------------------------
	static void ExpectBytes(uint va, params byte[] expected) {
		int o = VaToOff(va);
		for (int i = 0; i < expected.Length; i++)
			if (b[o + i] != expected[i]) throw new Exception(string.Format("{0:X8}: the original bytes differ (wrong game build, or another mod patched it)", va));
	}

	static void PutBytes(uint va, byte[] bytes) {
		CheckNoReloc(va, bytes.Length);
		Array.Copy(bytes, 0, b, VaToOff(va), bytes.Length);
	}

	// "call expected" at 'at' becomes "call target"
	static void RedirectCall(uint at, uint expected, uint target) {
		int o = VaToOff(at);
		if (b[o] != 0xE8 || unchecked(at + 5 + (uint)BitConverter.ToInt32(b, o + 1)) != expected)
			throw new Exception(string.Format("{0:X8}: expected a call to {1:X8}", at, expected));
		PutBytes(at + 1, BitConverter.GetBytes(unchecked((int)(target - (at + 5)))));
	}

	// "jmp target" over len bytes of whole instructions (the caller checked them with ExpectBytes); the rest are nops
	static void PutJmp(uint at, uint target, int len) {
		PutRel32(at, 0xE9, target, len);
	}

	// "call target" over len bytes of whole instructions (checked with ExpectBytes); the rest are nops
	static void PutCall(uint at, uint target, int len) {
		PutRel32(at, 0xE8, target, len);
	}

	static void PutRel32(uint at, byte opcode, uint target, int len) {
		if (len < 5) throw new Exception("a jmp or call needs 5 bytes");
		var p = new byte[len];
		p[0] = opcode;
		Array.Copy(BitConverter.GetBytes(unchecked((int)(target - (at + 5)))), 0, p, 1, 4);
		for (int i = 5; i < len; i++) p[i] = 0x90;
		PutBytes(at, p);
	}

	// grows .data's VirtualSize (the loader zero-fills past the file data) so that it covers va..va+3
	static void ReserveDataDword(uint va) {
		if (va < DataFloor) throw new Exception(string.Format("{0:X8} is below this mod's data (the game's or the 8-player mod's)", va));
		int i = FindSection(".data");
		uint start = SecVa(i), vs = U32(SecOff(i) + 8), need = va + 4 - start;
		if (va + 4 > SecVa(i + 1)) throw new Exception("data slot runs into the next section");
		if (need > Align(vs, 0x1000)) throw new Exception("data slot would need another page");
		if (need > vs) Put32(SecOff(i) + 8, need);
	}

	// appends a code section (read + execute) with its header in the free space after the section table
	static void AddSection(string name, List<byte> code) {
		uint fileAlign = U32(optOff + 36), sectAlign = U32(optOff + 32);
		uint sizeOfImage = U32(optOff + 56), sizeOfHeaders = U32(optOff + 60);
		if (FindSection(name) >= 0) throw new Exception("the input already has a " + name + " section");
		if (U32(optOff + 96 + 4 * 8 + 4) != 0) throw new Exception("the input is signed (certificate table): not expected");
		int hdr = SecOff(nSec);
		if (hdr + 40 > sizeOfHeaders) throw new Exception("no room for another section header");
		for (int i = 0; i < 40; i++) if (b[hdr + i] != 0) throw new Exception("the next section header slot is in use");
		uint rawPtr = (uint)b.Length, rawSize = Align((uint)code.Count, fileAlign);
		if (rawPtr % fileAlign != 0) throw new Exception("the file does not end on a file-alignment boundary");
		var grown = new byte[rawPtr + rawSize];
		Array.Copy(b, grown, b.Length);
		code.CopyTo(grown, (int)rawPtr);
		b = grown;
		Array.Copy(Encoding.ASCII.GetBytes(name.PadRight(8, '\0')), 0, b, hdr, 8);
		Put32(hdr + 8, (uint)code.Count);                 // VirtualSize
		Put32(hdr + 12, sizeOfImage);                     // VirtualAddress: right after the last section
		Put32(hdr + 16, rawSize);
		Put32(hdr + 20, rawPtr);
		Put32(hdr + 36, 0x60000020);                      // code, execute, read
		nSec++;
		Array.Copy(BitConverter.GetBytes((ushort)nSec), 0, b, peOff + 6, 2);
		Put32(optOff + 56, sizeOfImage + Align((uint)code.Count, sectAlign));
	}

	// ---- a small assembler: raw bytes, rel32 calls/jumps to absolute targets, labels for jumps inside a routine ----
	class Asm {
		public uint Va;
		public readonly List<byte> Bytes = new List<byte>();
		public uint Here { get { return Va + (uint)Bytes.Count; } }
		public void Raw(params byte[] x) { Bytes.AddRange(x); }
		public void Call(uint target) { Raw(0xE8); Rel32To(target); }
		public void Jmp(uint target) { Raw(0xE9); Rel32To(target); }
		void Rel32To(uint target) { Bytes.AddRange(BitConverter.GetBytes(unchecked((int)(target - (Here + 4))))); }

		struct LabelRef { public int Pos; public int Size; public string Name; }
		readonly Dictionary<string, uint> marks = new Dictionary<string, uint>();
		readonly List<LabelRef> refs = new List<LabelRef>();
		public void Mark(string name) { marks[name] = Here; }
		// short jump (opcode + rel8) to a label
		public void J8(byte opcode, string name) { Raw(opcode, 0); refs.Add(new LabelRef { Pos = Bytes.Count - 1, Size = 1, Name = name }); }
		// near jump (opcode bytes + rel32) to a label
		public void J32(string name, params byte[] opcode) { Raw(opcode); Raw(0, 0, 0, 0); refs.Add(new LabelRef { Pos = Bytes.Count - 4, Size = 4, Name = name }); }
		// fills in the label references of the routine just emitted; labels are per routine
		public void Resolve() {
			foreach (var r in refs) {
				uint target;
				if (!marks.TryGetValue(r.Name, out target)) throw new Exception("label " + r.Name + " not marked");
				int d = unchecked((int)(target - (Va + (uint)(r.Pos + r.Size))));
				if (r.Size == 1) {
					if (d < sbyte.MinValue || d > sbyte.MaxValue) throw new Exception("short jump to " + r.Name + " is too far");
					Bytes[r.Pos] = unchecked((byte)(sbyte)d);
				}
				else { var v = BitConverter.GetBytes(d); for (int i = 0; i < 4; i++) Bytes[r.Pos + i] = v[i]; }
			}
			refs.Clear();
			marks.Clear();
		}
	}

	static void SetImm32(Asm a, int pos, uint value) {
		var v = BitConverter.GetBytes(value);
		for (int i = 0; i < 4; i++) a.Bytes[pos + i] = v[i];
	}

	// ---- diagnostics: shared_turns.log ---------------------------------------------------------------------------
	// One line per group-turn, order, battle or desync event, appended to shared_turns.log next to Shogun2.exe. The
	// file is opened, written and closed per line, so a crash loses nothing; the first line of a game launch moves the
	// previous log to shared_turns.log.old. The log only reads game state and feeds nothing back, so it can't
	// desync anything; its timestamps go to the file only, never into the DLL.
	// Line: "2026-10-21 20:31:05.123 T12 ph1 <event> | cur=.. local=.. pb=.. pa=.. l=.. n=3 ended=011 latch=000 [a b c]"
	// (T turn number, ph [world+50h], pb pending-battle state, pa pending action queued, l the +155Fh latch, then
	// the group: ended / latch bits per slot, slot 0 first, and the members). Events whose text starts with '!' are
	// written without the campaign part: no campaign may exist then (campaign construction, desyncs, resyncs).
	// Log(id, a1, a2) is stdcall (ret 0Ch) and keeps every register and the flags, so it can be called anywhere;
	// %f in the event text prints a faction's key ([faction+51Ch] -> engine string: +0 length, +8 wchar_t*; 0 when
	// dead, FUN_105ecef0), %d a number, %b a bool's low byte, taking a1 then a2.
	const string LogBuildTag = "shared turns 0.21 (milestones 1, 2 and 3, milestone 4 part 1, turn modes hybrid / simultaneous, no battle with a target that moved away, members handed to the AI end, 8-player mod v1.11, fixes 2-6, 4b and 6b, recruitment cards for members, the group kept in the save, desync fixes, battle / access / pending / diplomacy / attack / message / resume log)";
	const uint UiGlobal = 0x11BC8FE4;          // ui; [ui+A98h] model, [model+8] campaign, [model+B0h] local faction
	const uint LogOpenedVa = DataFloor + 0x54;      // 1 once this process has moved the previous log to .old
	const uint LogCloseWaitVa = DataFloor + 0x58;   // 1 once "lead's end turn waits" is logged for the running group
	const uint CreateFileWIat = 0x115A1144;
	const uint WriteFileIat = 0x115A1154;
	const uint CloseHandleIat = 0x115A1158;
	const uint GetLocalTimeIat = 0x115A1264;
	const uint GetModuleFileNameWIat = 0x115A118C;
	const uint MoveFileExWIat = 0x115A1060;
	static readonly string[] LogEvents = {
		"!RESET new campaign",                                              // 0
		"!RESET campaign loaded",                                           // 1
		"START group, lead %f",                                             // 2
		"SOLO %f plays alone",                                              // 3
		"LOAD the tick repeats the turn start (a new campaign's first turn): group start for every member",   // 4
		"END_REQ %f flag %b -> vanilla end turn",                           // 5
		"END_REQ %f flag %b -> already ended",                              // 6
		"END_REQ %f flag %b -> treasury goes negative: PendingBankruptcy",  // 7
		"END_REQ %f flag %b -> end-turn lock set: ignored",                 // 8
		"END_REQ %f flag %b -> ended",                                      // 9
		"LATCH %f end request held until it can end",                       // 10
		"DROP %f handed to the AI or removed: counts as ended",             // 11
		"CLOSE all ended, lead %f can't end yet (waits)",                   // 12
		"CLOSE all ended, lead %f ends the turn",                           // 13
		"TURN_END group, pathfinder set up %d times for members",           // 14
		"SKIP %f already played this round",                                // 15
		"ROUND new round",                                                  // 16
		"MOVE %f accepted, pathfinder kept for %f",                         // 17
		"MOVE %f refused, reasons %d (1 busy, 2 another army's stop shown, 4 not its turn, 8 battle, 16 pending action, 32 MP timer, 64 army state, 0 no path)",   // 18
		"CLICK order type %d, character of %f (this PC only)",             // 19
		"BATTLE new: %f attacks %f",                                        // 20
		"BATTLE kind %d, flags %d (1 autoresolve, 256 human vote)",         // 21
		"BATTLE pre-battle entry for %f, ready %b",                         // 22
		"BATTLE state %d -> %d",                                            // 23
		"BATTLE ready from %f, choice %d",                                  // 24
		// 25..31: the desync fixes, standalone builds only (with the 8-player mod its own routines run instead)
		"!DESYNC detected here during a battle: no kick (mod)",             // 25
		"!DESYNC detected here outside a battle: campaign resync, else the vanilla kick",   // 26
		"!DESYNC the host drops reported player %d (vanilla kick)",         // 27
		"!DESYNC this PC was reported: it leaves (vanilla kick)",           // 28
		"!RESYNC started, auto-accepted",                                   // 29
		"!RESYNC host pressed Ctrl+Shift+F9",                               // 30 (unused since build 0.17: no key)
		"!RESYNC forced by the host's key",                                 // 31
		"END_MOVES %f resumes an order left from its turn",                 // 32
		"ACCESS %f can't reach its destination with the pathfinder built for %f",   // 33
		"ACCESS pathfinder kept for %f; reachable if the owner let it through: %b (then the declare war / military access popup)",   // 34
		"CLICK pathfinder built for %f, kept for %f (this PC only)",       // 35
		"PENDING push node vtable %d (291491768 move options popup, 291491884 attack popup, 291491328 negotiation), another waiting %b",   // 36
		"PENDING pop node vtable %d, another waiting %b",                   // 37
		"DIPLOMACY negotiation %f with %f",                                 // 38
		"DIPLOMACY %f declares war on %f",                                  // 39
		"ATTACK %f orders an attack on a character of %f: accepted",        // 40
		"ATTACK %f orders an attack on a character of %f: refused",         // 41
		"LOAD a save made during a turn (or a resync) without the group in it: group rebuilt without a turn start, who had ended ends again",   // 42
		"MESSAGE event message sent from %d (return address), check vtable %d says show",          // 43
		"MESSAGE event message sent from %d (return address), check vtable %d says don't show",    // 44
		"DIPLOMACY negotiation %f with %f dropped: another clan's negotiation or popup is pending",  // 45
		"DIPLOMACY %f picked a clan in the diplomacy panel while another clan's negotiation or popup is pending: no negotiation opened",   // 46
		"RESUME %f carries on with a move the battle interrupted",         // 47
		"LOAD the group from the save: lead %f, %d member slots (who had ended and who played this round are kept)",   // 48
		"REVOLT %f: a revolution started at this member's turn start in a shared turn (it plays its next turn alone)",   // 49
		"SAVE the group goes into the save: lead %f, %d member slots",     // 50
		"ATTACK %f reached where the army of %f stood when the attack was ordered, but that army has moved: no battle",   // 51
	};
	const int LogResetNew = 0, LogResetLoad = 1, LogStart = 2, LogSolo = 3, LogLoad = 4, LogEndVanilla = 5,
		LogEndAlready = 6, LogEndBankrupt = 7, LogEndLocked = 8, LogEndMarked = 9, LogLatch = 10, LogDrop = 11,
		LogCloseWait = 12, LogClose = 13, LogTurnEnd = 14, LogSkip = 15, LogRound = 16, LogMoveOk = 17,
		LogMoveRefused = 18, LogClick = 19, LogBattleNew = 20, LogBattleKind = 21, LogBattleEntry = 22,
		LogBattleState = 23, LogBattleReady = 24, LogDesyncBattle = 25, LogDesyncOther = 26, LogReportHostKick = 27,
		LogReportLeave = 28, LogResyncAuto = 29, LogResyncKey = 30, LogResyncForced = 31, LogEndMoves = 32,
		LogAccessBlocked = 33, LogAccessKept = 34, LogClickPf = 35, LogPendingPush = 36, LogPendingPop = 37,
		LogNegotiation = 38, LogWar = 39, LogAttackOk = 40, LogAttackRefused = 41, LogLoadSave = 42, LogMessageShow = 43,
		LogMessageHide = 44, LogNegotiationDropped = 45, LogNegotiationUiBlocked = 46, LogResumeMembers = 47,
		LogLoadGroup = 48, LogRevolt = 49, LogSaveGroup = 50, LogAttackMoved = 51;
	// Log's stack frame (ebp = esp after "sub esp,LogFrame"; pushfd + pushad + return address sit above it)
	const int LogLine = 0x000, LogPath = 0x400, LogOld = 0x810, LogTime = 0xC20, LogWritten = 0xC30,
		LogCamp = 0xC34, LogWorld = 0xC38, LogModel = 0xC3C, LogArgP = 0xC40, LogIdx = 0xC44, LogLineEnd = 0xC48,
		LogHdr = 0xC4C, LogFrame = 0xC50, LogArgs = LogFrame + 0x28;
	// Log's data (event texts, file names) goes after all other code (EmitLogData), so the section's disassembly
	// stays aligned; its "lea reg,[ebx+data-popAt]" offsets are filled in then
	static uint logPopAt;
	static List<KeyValuePair<int, string>> logDataFixes;

	static void LogData(Asm a, byte modrm, string item) {
		a.Raw(0x8D, modrm, 0, 0, 0, 0);
		logDataFixes.Add(new KeyValuePair<int, string>(a.Bytes.Count - 4, item));
	}

	static void D32(Asm a, int v) { a.Bytes.AddRange(BitConverter.GetBytes(v)); }
	static void D32(Asm a, uint v) { a.Bytes.AddRange(BitConverter.GetBytes(v)); }

	// name -> address of every routine in .s2st, for the minidump reader (re\build\dryrun.ps1 writes it to a .map)
	public static readonly List<string> SymbolMap = new List<string>();
	static uint Sym(string name, uint va) { SymbolMap.Add(string.Format("{0:X8} {1}", va, name)); return va; }

	static uint EmitLog(Asm a) {
		logDataFixes = new List<KeyValuePair<int, string>>();
		uint at = a.Here;
		a.Raw(0x9C);                                      // pushfd
		a.Raw(0x60);                                      // pushad
		a.Raw(0x81, 0xEC); D32(a, LogFrame);              // sub esp,LogFrame
		a.Raw(0x8B, 0xEC);                                // mov ebp,esp
		a.Raw(0xFC);                                      // cld
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		logPopAt = a.Here;
		a.Raw(0x5B);                                      // pop ebx                 (base for data, IAT and globals)
		a.Raw(0xC6, 0x85); D32(a, LogHdr); a.Raw(0x00);   // mov byte ptr [ebp+LogHdr],0
		// local time "YYYY-MM-DD hh:mm:ss.mmm " (SYSTEMTIME: year, month, day of week, day, hour, min, sec, ms)
		a.Raw(0x8D, 0x85); D32(a, LogTime);               // lea eax,[ebp+LogTime]
		a.Raw(0x50);                                      // push eax
		a.Raw(0xFF, 0x93); D32(a, GetLocalTimeIat - logPopAt);   // call [ebx+GetLocalTime]
		a.Raw(0x8B, 0xFD);                                // mov edi,ebp             (line buffer)
		int[,] fields = { { 0, 4, '-' }, { 2, 2, '-' }, { 6, 2, ' ' }, { 8, 2, ':' }, { 10, 2, ':' }, { 12, 2, '.' }, { 14, 3, ' ' } };
		for (int i = 0; i < fields.GetLength(0); i++) {
			a.Raw(0x0F, 0xB7, 0x85); D32(a, LogTime + fields[i, 0]);   // movzx eax,word ptr [ebp+LogTime+field]
			a.Raw(0xB9); D32(a, fields[i, 1]);            // mov ecx,width
			a.J32("log_dec", 0xE8);                       // call log_dec
			a.Raw(0xB0, (byte)fields[i, 2]);              // mov al,separator
			a.Raw(0xAA);                                  // stosb
		}
		// esi = the event's text: skip id NUL-terminated texts
		LogData(a, 0xB3, "events");                       // lea esi,[ebx+events]
		a.Raw(0x8B, 0x8D); D32(a, LogArgs);               // mov ecx,[ebp+LogArgs]   (id)
		a.Mark("log_skip");
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "log_found");                          // jz log_found
		a.Mark("log_skipz");
		a.Raw(0xAC);                                      // lodsb
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "log_skipz");                          // jnz log_skipz
		a.Raw(0x49);                                      // dec ecx
		a.J8(0xEB, "log_skip");                           // jmp log_skip
		a.Mark("log_found");
		a.Raw(0x8D, 0x85); D32(a, LogArgs + 4);           // lea eax,[ebp+LogArgs+4] (a1)
		a.Raw(0x89, 0x85); D32(a, LogArgP);               // mov [ebp+LogArgP],eax
		a.Raw(0xC7, 0x85); D32(a, LogCamp); D32(a, 0);    // mov dword ptr [ebp+LogCamp],0
		a.Raw(0x80, 0x3E, 0x21);                          // cmp byte ptr [esi],'!'
		a.J8(0x75, "log_ctx");                            // jne log_ctx
		a.Raw(0x46);                                      // inc esi                 (no campaign part)
		a.J32("log_body", 0xE9);                          // jmp log_body
		// campaign part: ui -> model -> campaign -> world; if any is missing the line has none
		a.Mark("log_ctx");
		a.Raw(0x8B, 0x83); D32(a, UiGlobal - logPopAt);   // mov eax,[ebx+ui]
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("log_body", 0x0F, 0x84);                    // jz log_body
		a.Raw(0x8B, 0x80, 0x98, 0x0A, 0x00, 0x00);        // mov eax,[eax+0A98h]     (model)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("log_body", 0x0F, 0x84);                    // jz log_body
		a.Raw(0x8B, 0x48, 0x08);                          // mov ecx,[eax+8]         (campaign)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J32("log_body", 0x0F, 0x84);                    // jz log_body
		a.Raw(0x8B, 0x91, 0x98, 0x14, 0x00, 0x00);        // mov edx,[ecx+1498h]     (world)
		a.Raw(0x85, 0xD2);                                // test edx,edx
		a.J32("log_body", 0x0F, 0x84);                    // jz log_body
		a.Raw(0x89, 0x85); D32(a, LogModel);              // mov [ebp+LogModel],eax
		a.Raw(0x89, 0x8D); D32(a, LogCamp);               // mov [ebp+LogCamp],ecx
		a.Raw(0x89, 0x95); D32(a, LogWorld);              // mov [ebp+LogWorld],edx
		a.Raw(0xB0, 0x54);                                // mov al,'T'
		a.Raw(0xAA);                                      // stosb
		a.Raw(0x8B, 0x81, 0xA0, 0x14, 0x00, 0x00);        // mov eax,[ecx+14A0h]     (turn counter object; Lua TurnNumber)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "log_turn");                           // jz log_turn
		a.Raw(0x8B, 0x40, 0x08);                          // mov eax,[eax+8]
		a.Raw(0x40);                                      // inc eax
		a.Mark("log_turn");
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.J32("log_dec", 0xE8);                           // call log_dec
		a.Raw(0x56);                                      // push esi                (event text)
		LogData(a, 0xB3, " ph");                          // lea esi,[ebx+" ph"]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x8B, 0x85); D32(a, LogWorld);              // mov eax,[ebp+LogWorld]
		a.Raw(0x8B, 0x40, 0x50);                          // mov eax,[eax+50h]       (turn phase)
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.J32("log_dec", 0xE8);                           // call log_dec
		a.Raw(0xB0, 0x20);                                // mov al,' '
		a.Raw(0xAA);                                      // stosb
		// the event text, %f / %d take a1 then a2
		a.Mark("log_body");
		a.Raw(0xAC);                                      // lodsb
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "log_tail");                           // jz log_tail
		a.Raw(0x3C, 0x25);                                // cmp al,'%'
		a.J8(0x74, "log_arg");                            // je log_arg
		a.Raw(0xAA);                                      // stosb
		a.J8(0xEB, "log_body");                           // jmp log_body
		a.Mark("log_arg");
		a.Raw(0xAC);                                      // lodsb                   (f or d)
		a.Raw(0x8B, 0x95); D32(a, LogArgP);               // mov edx,[ebp+LogArgP]
		a.Raw(0x83, 0x85); D32(a, LogArgP); a.Raw(0x04);  // add dword ptr [ebp+LogArgP],4
		a.Raw(0x8B, 0x12);                                // mov edx,[edx]
		a.Raw(0x3C, 0x66);                                // cmp al,'f'
		a.J8(0x75, "log_argd");                           // jne log_argd
		a.Raw(0x8B, 0xC2);                                // mov eax,edx
		a.J32("log_fac", 0xE8);                           // call log_fac
		a.J8(0xEB, "log_body");                           // jmp log_body
		a.Mark("log_argd");
		a.Raw(0x3C, 0x62);                                // cmp al,'b'
		a.J8(0x75, "log_argn");                           // jne log_argn
		a.Raw(0x0F, 0xB6, 0xD2);                          // movzx edx,dl            (a bool argument: low byte only)
		a.Mark("log_argn");
		a.Raw(0x8B, 0xC2);                                // mov eax,edx
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.J32("log_dec", 0xE8);                           // call log_dec
		a.J8(0xEB, "log_body");                           // jmp log_body
		// campaign snapshot and the group
		a.Mark("log_tail");
		a.Raw(0x83, 0xBD); D32(a, LogCamp); a.Raw(0x00);  // cmp dword ptr [ebp+LogCamp],0
		a.J32("log_eol", 0x0F, 0x84);                     // je log_eol
		LogData(a, 0xB3, " | cur=");                      // lea esi,[ebx+" | cur="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x85); D32(a, LogWorld);              // mov eax,[ebp+LogWorld]
		a.Raw(0x8B, 0x40, 0x2C);                          // mov eax,[eax+2Ch]       (current faction)
		a.J32("log_fac", 0xE8);                           // call log_fac
		LogData(a, 0xB3, " local=");                      // lea esi,[ebx+" local="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x85); D32(a, LogModel);              // mov eax,[ebp+LogModel]
		a.Raw(0x8B, 0x80, 0xB0, 0x00, 0x00, 0x00);        // mov eax,[eax+0B0h]      (local faction)
		a.J32("log_fac", 0xE8);                           // call log_fac
		LogData(a, 0xB3, " pb=");                         // lea esi,[ebx+" pb="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x8D); D32(a, LogCamp);               // mov ecx,[ebp+LogCamp]
		a.Raw(0x8B, 0x81, 0xA4, 0x14, 0x00, 0x00);        // mov eax,[ecx+14A4h]     (pending battle)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "log_pb");                             // jz log_pb
		a.Raw(0x8B, 0x40, 0x50);                          // mov eax,[eax+50h]       (its state)
		a.Mark("log_pb");
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.J32("log_dec", 0xE8);                           // call log_dec
		LogData(a, 0xB3, " pa=");                         // lea esi,[ebx+" pa="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x8D); D32(a, LogCamp);               // mov ecx,[ebp+LogCamp]
		a.Raw(0x8B, 0x81, 0xA8, 0x14, 0x00, 0x00);        // mov eax,[ecx+14A8h]     (pending actions)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "log_pa");                             // jz log_pa
		a.Raw(0x83, 0x38, 0x00);                          // cmp dword ptr [eax],0   (list head)
		a.Raw(0x0F, 0x95, 0xC0);                          // setne al
		a.Raw(0x0F, 0xB6, 0xC0);                          // movzx eax,al
		a.Mark("log_pa");
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.J32("log_dec", 0xE8);                           // call log_dec
		LogData(a, 0xB3, " l=");                          // lea esi,[ebx+" l="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x8D); D32(a, LogCamp);               // mov ecx,[ebp+LogCamp]
		a.Raw(0x0F, 0xB6, 0x81, 0x5F, 0x15, 0x00, 0x00);  // movzx eax,byte ptr [ecx+155Fh]
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.J32("log_dec", 0xE8);                           // call log_dec
		LogData(a, 0xB3, " n=");                          // lea esi,[ebx+" n="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x83); D32(a, GroupVa - logPopAt);    // mov eax,[ebx+count]
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.J32("log_dec", 0xE8);                           // call log_dec
		a.Raw(0x83, 0xBB); D32(a, GroupVa - logPopAt); a.Raw(0x00);   // cmp dword ptr [ebx+count],0
		a.J32("log_eol", 0x0F, 0x84);                     // je log_eol
		LogData(a, 0xB3, " ended=");                      // lea esi,[ebx+" ended="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x83); D32(a, GroupVa + 4 - logPopAt);   // mov eax,[ebx+ended]
		a.Raw(0x8B, 0x8B); D32(a, GroupVa - logPopAt);    // mov ecx,[ebx+count]
		a.J32("log_bits", 0xE8);                          // call log_bits
		LogData(a, 0xB3, " latch=");                      // lea esi,[ebx+" latch="]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x8B, 0x83); D32(a, GroupVa + 8 - logPopAt);   // mov eax,[ebx+latch]
		a.Raw(0x8B, 0x8B); D32(a, GroupVa - logPopAt);    // mov ecx,[ebx+count]
		a.J32("log_bits", 0xE8);                          // call log_bits
		a.Raw(0xB0, 0x20);                                // mov al,' '
		a.Raw(0xAA);                                      // stosb
		a.Raw(0xB0, 0x5B);                                // mov al,'['
		a.Raw(0xAA);                                      // stosb
		a.Raw(0xC7, 0x85); D32(a, LogIdx); D32(a, 0);     // mov dword ptr [ebp+LogIdx],0
		a.Mark("log_member");
		a.Raw(0x8B, 0x8D); D32(a, LogIdx);                // mov ecx,[ebp+LogIdx]
		a.Raw(0x3B, 0x8B); D32(a, GroupVa - logPopAt);    // cmp ecx,[ebx+count]
		a.J8(0x73, "log_members");                        // jae log_members
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "log_first");                          // jz log_first
		a.Raw(0xB0, 0x20);                                // mov al,' '
		a.Raw(0xAA);                                      // stosb
		a.Mark("log_first");
		a.Raw(0x8B, 0x84, 0x8B); D32(a, GroupVa + 0x34 - logPopAt);   // mov eax,[ebx+ecx*4+members]
		a.J32("log_fac", 0xE8);                           // call log_fac
		a.Raw(0xFF, 0x85); D32(a, LogIdx);                // inc dword ptr [ebp+LogIdx]
		a.J8(0xEB, "log_member");                         // jmp log_member
		a.Mark("log_members");
		a.Raw(0xB0, 0x5D);                                // mov al,']'
		a.Raw(0xAA);                                      // stosb
		a.Mark("log_eol");
		a.Raw(0x66, 0xB8, 0x0D, 0x0A);                    // mov ax,0A0Dh
		a.Raw(0x66, 0xAB);                                // stosw                   (CR LF)
		a.Raw(0x89, 0xBD); D32(a, LogLineEnd);            // mov [ebp+LogLineEnd],edi
		// path: the exe's folder + shared_turns.log
		a.Raw(0x68, 0x04, 0x01, 0x00, 0x00);              // push 104h               (MAX_PATH)
		a.Raw(0x8D, 0x85); D32(a, LogPath);               // lea eax,[ebp+LogPath]
		a.Raw(0x50);                                      // push eax
		a.Raw(0x6A, 0x00);                                // push 0                  (the exe)
		a.Raw(0xFF, 0x93); D32(a, GetModuleFileNameWIat - logPopAt);   // call [ebx+GetModuleFileNameW]
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("log_done", 0x0F, 0x84);                    // jz log_done
		a.Raw(0x3D, 0x04, 0x01, 0x00, 0x00);              // cmp eax,104h
		a.J32("log_done", 0x0F, 0x83);                    // jae log_done            (truncated)
		a.Raw(0x8D, 0x94, 0x45); D32(a, LogPath);         // lea edx,[ebp+eax*2+LogPath]   (end of the path)
		a.Raw(0x8D, 0x8D); D32(a, LogPath);               // lea ecx,[ebp+LogPath]
		a.Mark("log_bs");
		a.Raw(0x3B, 0xD1);                                // cmp edx,ecx
		a.J32("log_done", 0x0F, 0x86);                    // jbe log_done            (no backslash)
		a.Raw(0x83, 0xEA, 0x02);                          // sub edx,2
		a.Raw(0x66, 0x83, 0x3A, 0x5C);                    // cmp word ptr [edx],'\'
		a.J8(0x75, "log_bs");                             // jne log_bs
		a.Raw(0x8D, 0x7A, 0x02);                          // lea edi,[edx+2]
		LogData(a, 0xB3, "file");                         // lea esi,[ebx+L"shared_turns.log"]
		a.Raw(0xB9, 0x15, 0x00, 0x00, 0x00);              // mov ecx,21              (20 chars + NUL)
		a.Raw(0xF3, 0x66, 0xA5);                          // rep movsw
		// first line of this game launch: keep the previous log as .old, start this one with a header
		a.Raw(0x83, 0xBB); D32(a, LogOpenedVa - logPopAt); a.Raw(0x00);   // cmp dword ptr [ebx+opened],0
		a.J8(0x75, "log_open");                           // jne log_open
		a.Raw(0xC7, 0x83); D32(a, LogOpenedVa - logPopAt); D32(a, 1);   // mov dword ptr [ebx+opened],1
		a.Raw(0xC6, 0x85); D32(a, LogHdr); a.Raw(0x01);   // mov byte ptr [ebp+LogHdr],1
		a.Raw(0x8D, 0xB5); D32(a, LogPath);               // lea esi,[ebp+LogPath]
		a.Raw(0x8D, 0xBD); D32(a, LogOld);                // lea edi,[ebp+LogOld]
		a.Mark("log_copy");
		a.Raw(0x66, 0xAD);                                // lodsw
		a.Raw(0x66, 0xAB);                                // stosw
		a.Raw(0x66, 0x85, 0xC0);                          // test ax,ax
		a.J8(0x75, "log_copy");                           // jnz log_copy
		a.Raw(0x83, 0xEF, 0x02);                          // sub edi,2
		LogData(a, 0xB3, "old");                          // lea esi,[ebx+L".old"]
		a.Raw(0xB9, 0x05, 0x00, 0x00, 0x00);              // mov ecx,5
		a.Raw(0xF3, 0x66, 0xA5);                          // rep movsw
		a.Raw(0x6A, 0x01);                                // push 1                  (MOVEFILE_REPLACE_EXISTING)
		a.Raw(0x8D, 0x85); D32(a, LogOld);                // lea eax,[ebp+LogOld]
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8D, 0x85); D32(a, LogPath);               // lea eax,[ebp+LogPath]
		a.Raw(0x50);                                      // push eax
		a.Raw(0xFF, 0x93); D32(a, MoveFileExWIat - logPopAt);   // call [ebx+MoveFileExW]  (fails harmlessly if there is none)
		a.Mark("log_open");
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x68, 0x80, 0x00, 0x00, 0x00);              // push 80h                (FILE_ATTRIBUTE_NORMAL)
		a.Raw(0x6A, 0x04);                                // push 4                  (OPEN_ALWAYS)
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x6A, 0x03);                                // push 3                  (share read / write)
		a.Raw(0x6A, 0x04);                                // push 4                  (FILE_APPEND_DATA)
		a.Raw(0x8D, 0x85); D32(a, LogPath);               // lea eax,[ebp+LogPath]
		a.Raw(0x50);                                      // push eax
		a.Raw(0xFF, 0x93); D32(a, CreateFileWIat - logPopAt);   // call [ebx+CreateFileW]
		a.Raw(0x83, 0xF8, 0xFF);                          // cmp eax,-1
		a.J8(0x74, "log_done");                           // je log_done
		a.Raw(0x8B, 0xF0);                                // mov esi,eax             (handle)
		a.Raw(0x80, 0xBD); D32(a, LogHdr); a.Raw(0x00);   // cmp byte ptr [ebp+LogHdr],0
		a.J8(0x74, "log_write");                          // je log_write
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x8D, 0x85); D32(a, LogWritten);            // lea eax,[ebp+LogWritten]
		a.Raw(0x50);                                      // push eax
		a.Raw(0x68); D32(a, LogHeader().Length);          // push header length
		LogData(a, 0x83, "header");                       // lea eax,[ebx+header]
		a.Raw(0x50);                                      // push eax
		a.Raw(0x56);                                      // push esi
		a.Raw(0xFF, 0x93); D32(a, WriteFileIat - logPopAt);   // call [ebx+WriteFile]
		a.Mark("log_write");
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x8D, 0x85); D32(a, LogWritten);            // lea eax,[ebp+LogWritten]
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8B, 0x85); D32(a, LogLineEnd);            // mov eax,[ebp+LogLineEnd]
		a.Raw(0x2B, 0xC5);                                // sub eax,ebp             (line length)
		a.Raw(0x50);                                      // push eax
		a.Raw(0x55);                                      // push ebp                (the line)
		a.Raw(0x56);                                      // push esi
		a.Raw(0xFF, 0x93); D32(a, WriteFileIat - logPopAt);   // call [ebx+WriteFile]
		a.Raw(0x56);                                      // push esi
		a.Raw(0xFF, 0x93); D32(a, CloseHandleIat - logPopAt);   // call [ebx+CloseHandle]
		a.Mark("log_done");
		a.Raw(0x81, 0xC4); D32(a, LogFrame);              // add esp,LogFrame
		a.Raw(0x61);                                      // popad
		a.Raw(0x9D);                                      // popfd
		a.Raw(0xC2, 0x0C, 0x00);                          // ret 0Ch

		// log_dec: eax = number, ecx = minimum digits -> decimal at edi; changes eax, ecx, edx
		a.Mark("log_dec");
		a.Raw(0x56);                                      // push esi
		a.Raw(0x33, 0xF6);                                // xor esi,esi             (digits pushed)
		a.Mark("log_dec_div");
		a.Raw(0x33, 0xD2);                                // xor edx,edx
		a.Raw(0x51);                                      // push ecx
		a.Raw(0xB9, 0x0A, 0x00, 0x00, 0x00);              // mov ecx,10
		a.Raw(0xF7, 0xF1);                                // div ecx
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x80, 0xC2, 0x30);                          // add dl,'0'
		a.Raw(0x52);                                      // push edx
		a.Raw(0x46);                                      // inc esi
		a.Raw(0x49);                                      // dec ecx
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x75, "log_dec_div");                        // jnz log_dec_div
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x7F, "log_dec_div");                        // jg log_dec_div          (leading zeros)
		a.Mark("log_dec_out");
		a.Raw(0x58);                                      // pop eax
		a.Raw(0xAA);                                      // stosb
		a.Raw(0x4E);                                      // dec esi
		a.J8(0x75, "log_dec_out");                        // jnz log_dec_out
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0xC3);                                      // ret
		// log_str: esi = NUL-terminated text -> copied to edi; changes eax, esi
		a.Mark("log_str");
		a.Raw(0xAC);                                      // lodsb
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "log_str_end");                        // jz log_str_end
		a.Raw(0xAA);                                      // stosb
		a.J8(0xEB, "log_str");                            // jmp log_str
		a.Mark("log_str_end");
		a.Raw(0xC3);                                      // ret
		// log_fac: eax = faction (or 0) -> its key at edi, "-" for none, "(dead)"; at most 40 chars, non-ASCII as '?';
		// changes eax, ecx, edx
		a.Mark("log_fac");
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x75, "log_fac_have");                       // jnz log_fac_have
		a.Raw(0xB0, 0x2D);                                // mov al,'-'
		a.Raw(0xAA);                                      // stosb
		a.Raw(0xC3);                                      // ret
		a.Mark("log_fac_have");
		a.Raw(0x8B, 0x80, 0x1C, 0x05, 0x00, 0x00);        // mov eax,[eax+51Ch]      (name string; 0 = dead, FUN_105cb900)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x75, "log_fac_name");                       // jnz log_fac_name
		a.Raw(0x56);                                      // push esi
		LogData(a, 0xB3, "(dead)");                       // lea esi,[ebx+"(dead)"]
		a.J32("log_str", 0xE8);                           // call log_str
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0xC3);                                      // ret
		a.Mark("log_fac_name");
		a.Raw(0x8B, 0x08);                                // mov ecx,[eax]           (length)
		a.Raw(0x8B, 0x50, 0x08);                          // mov edx,[eax+8]         (wchar_t*)
		a.Raw(0x85, 0xD2);                                // test edx,edx
		a.J8(0x74, "log_fac_end");                        // jz log_fac_end
		a.Raw(0x83, 0xF9, 0x28);                          // cmp ecx,40
		a.J8(0x76, "log_fac_len");                        // jbe log_fac_len
		a.Raw(0xB9, 0x28, 0x00, 0x00, 0x00);              // mov ecx,40
		a.Mark("log_fac_len");
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "log_fac_end");                        // jz log_fac_end
		a.Mark("log_fac_ch");
		a.Raw(0x66, 0x8B, 0x02);                          // mov ax,[edx]
		a.Raw(0x66, 0x83, 0xF8, 0x20);                    // cmp ax,20h
		a.J8(0x72, "log_fac_q");                          // jb log_fac_q
		a.Raw(0x66, 0x83, 0xF8, 0x7E);                    // cmp ax,7Eh
		a.J8(0x76, "log_fac_put");                        // jbe log_fac_put
		a.Mark("log_fac_q");
		a.Raw(0xB0, 0x3F);                                // mov al,'?'
		a.Mark("log_fac_put");
		a.Raw(0xAA);                                      // stosb
		a.Raw(0x83, 0xC2, 0x02);                          // add edx,2
		a.Raw(0x49);                                      // dec ecx
		a.J8(0x75, "log_fac_ch");                         // jnz log_fac_ch
		a.Mark("log_fac_end");
		a.Raw(0xC3);                                      // ret
		// log_bits: eax = mask, ecx = slots -> '0'/'1' per slot, slot 0 first; changes eax, ecx, edx
		a.Mark("log_bits");
		a.Raw(0x8B, 0xD0);                                // mov edx,eax
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "log_bits_end");                       // jz log_bits_end
		a.Mark("log_bit");
		a.Raw(0xB0, 0x30);                                // mov al,'0'
		a.Raw(0xD1, 0xEA);                                // shr edx,1
		a.Raw(0x14, 0x00);                                // adc al,0
		a.Raw(0xAA);                                      // stosb
		a.Raw(0x49);                                      // dec ecx
		a.J8(0x75, "log_bit");                            // jnz log_bit
		a.Mark("log_bits_end");
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static string LogHeader() {
		return "==== Shogun 2 Shared Turns log: game started (build: " + LogBuildTag + ", turn mode: " + TurnMode + ") ====\r\n";
	}

	// Log's data, after all other code in the section; fills in the "lea reg,[ebx+item]" offsets
	static void EmitLogData(Asm a) {
		var at = new Dictionary<string, uint>();
		at["events"] = a.Here;
		foreach (var e in LogEvents) { a.Bytes.AddRange(Encoding.ASCII.GetBytes(e)); a.Raw(0x00); }
		at["file"] = a.Here;
		a.Bytes.AddRange(Encoding.Unicode.GetBytes("shared_turns.log\0"));
		at["old"] = a.Here;
		a.Bytes.AddRange(Encoding.Unicode.GetBytes(".old\0"));
		at["header"] = a.Here;
		a.Bytes.AddRange(Encoding.ASCII.GetBytes(LogHeader()));
		foreach (var f in logDataFixes) {
			if (!at.ContainsKey(f.Value)) { at[f.Value] = a.Here; a.Bytes.AddRange(Encoding.ASCII.GetBytes(f.Value)); a.Raw(0x00); }
			SetImm32(a, f.Key, at[f.Value] - logPopAt);
		}
	}

	// the caller has pushed a2 and a1; Log keeps every register and the flags and pops its arguments
	static void CallLog(Asm a, uint log, int id) {
		a.Raw(0x6A, (byte)id);                            // push id
		a.Call(log);                                      // call Log
	}

	// ---- group turns: humans who are not at war play their turns together ------------------------------
	// The campaign has one current faction ([world+2Ch]); the picker FUN_106a5fd0 hands the turn to one faction at
	// a time and every simulation check (character orders FUN_106aa660, attacks FUN_106e4f60, end turn
	// FUN_106aa590, ...) compares the acting faction with it. So with several humans each waits for the others.
	// Group turn: when a human's turn starts, every later human in the list that is alive and not at war with any
	// member joins it. [world+2Ch] stays the first one (the lead), so the AI, the CAI and the phase machine still
	// see one current faction. The checks accept any member that hasn't ended; a member's End Turn only marks it;
	// once every member has ended, the lead's normal end turn runs. All of it is decided in the simulation
	// (command handlers, tick, picker), so every PC builds the same group. Group state in .data slack:
	const uint GroupVa = DataFloor;            // +00 count of member slots (0 = no group: vanilla)
	                                           // +04 ended: bit i = slot i ended its turn
	                                           // +08 latch: bit i = slot i asked to end early (per-member +155Fh)
	                                           // +0C mover owner (scratch for FUN_108ffb80)
	                                           // +10 played count, +14 played[8]: non-lead members of earlier groups
	                                           //     this round (the picker skips them)
	                                           // +34 members[8]: slot 0 = lead = [world+2Ch], then list order
	const uint GroupLastDword = GroupVa + 0x50;
	const int GroupDwords = 0x54 / 4;
	// the pathfinder set up per clan (fix 3), after the log's two dwords (GroupVa+54h, +58h):
	const uint PfFocusVa = GroupVa + 0x5C;     // the member the simulation set the pathfinder up for (0 = the engine's own)
	const uint PfCountVa = GroupVa + 0x60;     // set-ups for members this group turn (logged at TURN_END)
	const uint UiPfDepthVa = GroupVa + 0x64;   // nesting of the UI wrappers
	const uint UiPfActiveVa = GroupVa + 0x68;  // 1 while the UI has set the pathfinder up for the local member
	const uint UiPfSavedVa = GroupVa + 0x6C;   // +0 pf, +4 its kept faction [pf+1F4h], +8 its kept mode [pf+208h]
	const uint PfLastDword = GroupVa + 0x74;
	// GroupVa + 78h, + 80h: the resync key state until build 0.16 (free now)
	const int PfDwords = 0x1C / 4;             // PfFocusVa .. PfLastDword
	const uint PfBuiltForVa = GroupVa + 0x7C;  // log only: the faction the pathfinder's region flags were last built for
	// the group in the save (milestone 4: SaveGroup, LoadGroup, GroupRestore):
	const uint SaveLoadedVa = GroupVa + 0x84;  // 1 once LoadingGame read a complete group from the save, until GroupTick uses it
	const uint SaveBufVa = GroupVa + 0x88;     // the values read: GroupVa's 21 dwords, factions as list position + 1
	const uint SaveLastDword = SaveBufVa + 0x50;
	const int SaveValues = 0x54 / 4;           // named numbers "s2st_a".."s2st_u" in the save's SCRIPT_VARIABLES record
	const int SaveVersion = 1;                 // saved in place of +0Ch (the mover-owner scratch)
	// attacks on an army that moved away (AttackRecord / AttackCheck): +0 next slot, then AttackSlots entries of 18h
	// bytes: attacker, target, turn, the target's position (3 dwords) when the attack was ordered
	const uint AttackTableVa = GroupVa + 0xE0;
	const int AttackSlots = 16;
	const int AttackDwords = 1 + AttackSlots * 6;
	const uint AttackLastDword = AttackTableVa + (AttackDwords - 1) * 4;
	const uint FactionTurnStart = 0x105FD090;  // thiscall(faction)
	const uint FactionTurnEnd = 0x105B2870;    // thiscall(faction)
	const uint FactionIsDead = 0x105CB900;     // thiscall(faction) -> al
	const uint FactionResumeOrder = 0x105B73D0;   // thiscall(faction) -> al: one of its characters' orders left from the turn moved on
	const uint OrderResume = 0x108D8F20;       // thiscall(order; 1, 0, 0) ret 0Ch: re-issue an interrupted order
	const uint LuaType = 0x10EA3A90;           // cdecl lua_type(L, index): 4 = string
	const uint AttackAllowed = 0x106E4F60;     // thiscall(campaign; faction) ret 4 -> al
	const uint FactionsAtWar = 0x105CB0F0;     // thiscall(faction; other) ret 4 -> al
	const uint CampaignEndTurn = 0x106A5DA0;   // thiscall(campaign; faction, flag) ret 8
	const uint CanEndTurn = 0x106E4F20;        // thiscall(campaign; faction) ret 4 -> al
	const uint TreasuryGoesNegative = 0x107EF460;   // thiscall(faction+90h) -> al
	const uint OwnerOf = 0x103E5960;           // thiscall(char+254h) -> owning faction
	const uint EventInit = 0x10BA5980;         // thiscall(event)
	const uint EventDispatch = 0x10BE0350;     // cdecl(event*)
	const uint CharPosition = 0x106E5630;      // thiscall(char) -> eax = &its position (12 bytes; the settlement's or fleet's when inside one)
	const uint AttackPlan = 0x1085A820;        // thiscall(movement; &order context, target, 0, 0) ret 10h: plan an attack on a character
	const uint BattleStart = 0x106E9F60;       // thiscall(campaign; attacker, target, flag, 0, 0, 0, 0, 0) ret 20h: start a pending battle
	// the save format (ESF), as the Lua save_named_value / load_named_value bindings FUN_1060e5b0 / FUN_1060e800 use it.
	// A name object is +0 length, +4 length, +8 the text, +0Ch NameHash(text); the bindings free a heap copy of the
	// text afterwards, so the save format keeps no pointer to it.
	const uint NameHash = 0x1008FEE0;          // stdcall(char*) -> eax
	const uint EsfChildOpen = 0x10BA6370;      // thiscall(child; record, &name, 0) ret 0Ch: write a named child (18h bytes)
	const uint EsfPutType = 0x1013FBA0;        // thiscall(child; &uint32) ret 4: the value's type (1 = number)
	const uint EsfPutInt = 0x1013FAF0;         // thiscall(child; &int32) ret 4
	const uint EsfChildClose = 0x10BCD5C0;     // thiscall(child)
	const uint EsfHas = 0x10BD01F0;            // thiscall(record; &name) ret 4 -> al: it has a child of that name
	const uint EsfChildRead = 0x10BA5C00;      // thiscall(child; record, &name) ret 8: open that child for reading (44h bytes)
	const uint EsfGetType = 0x1013B390;        // thiscall(child; &uint32) ret 4
	const uint EsfGetInt = 0x1013B2C0;         // thiscall(child; &int32) ret 4
	const uint EsfChildEnd = 0x10BAE740;       // thiscall(child)
	const uint EventDestroy = 0x10BAE630;      // thiscall(event)
	const uint FactionAboutToEndTurnVt = 0x11608E1C;
	const uint IsEqual = 0x105870B0;           // cdecl(a, b) -> al = a == b; changes only eax
	const uint IsNotEqual = 0x105870C0;        // cdecl(a, b) -> al = a != b; changes only eax
	const uint MoverFactionIs = 0x105B4300;    // thiscall(faction; char) ret 4 -> al = char's faction == faction
	const uint CurrentOrProxy = 0x1070B490;    // thunk of FUN_1070b4a0(world)
	const uint CurrentOrProxyFn = 0x1070B4A0;  // thiscall(world) -> eax = current faction, or the rebels in a proxy turn
	const uint PfSetFaction = 0x10778490;      // thiscall(pf; faction, keep) ret 8: pathfinder set up for a faction
	const uint PfRestoreKept = 0x107FD150;     // thiscall(pf): rebuild the kept set-up ([pf+1F4h], [pf+1F8h], mode [pf+208h])
	const uint PfTurnFocus = 0x106FA800;       // thiscall(campaign): set up (and keep) for the current-or-proxy faction

	// r = runtime address of va (call $+5 / pop r / add r, va - popAt: no relocation)
	static void EmitGroupPtr(Asm a, int r, uint va) {
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw((byte)(0x58 + r));                          // pop r
		a.Raw(0x81, (byte)(0xC0 + r)); a.Bytes.AddRange(BitConverter.GetBytes(unchecked(va - popAt)));   // add r, va - popAt
	}

	static uint EmitGroupSlot(Asm a) {
		// ecx = faction -> eax = its member slot or -1 (also -1 without a group), edx = &group; changes eax, edx, flags
		uint at = a.Here;
		EmitGroupPtr(a, 2, GroupVa);                      // edx = &group
		a.Raw(0x8B, 0x02);                                // mov eax,[edx]           (count)
		a.Raw(0x48);                                      // dec eax
		a.J8(0x78, "done");                               // js done
		a.Mark("loop");
		a.Raw(0x39, 0x4C, 0x82, 0x34);                    // cmp [edx+eax*4+34h],ecx (members[i])
		a.J8(0x74, "done");                               // je done
		a.Raw(0x48);                                      // dec eax
		a.J8(0x79, "loop");                               // jns loop
		a.Mark("done");
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitGroupAllows(Asm a, uint groupSlot) {
		// cdecl(cur, f) -> eax = 0/1: may f act now. Vanilla "f == cur" unless f is a member of a running group whose
		// lead is cur and not every member has ended yet ("closing": the lead's own end turn runs); then
		// "f hasn't ended" (the lead too). Changes only eax and the flags, like FUN_105870b0.
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x52);                                      // push edx
		a.Raw(0x53);                                      // push ebx                (cur at +10h, f at +14h)
		a.Raw(0x8B, 0x4C, 0x24, 0x14);                    // mov ecx,[esp+14h]       (f)
		a.Call(groupSlot);
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x78, "vanilla");                            // js vanilla              (no group or not a member)
		a.Raw(0x8B, 0x4C, 0x24, 0x10);                    // mov ecx,[esp+10h]       (cur)
		a.Raw(0x3B, 0x4A, 0x34);                          // cmp ecx,[edx+34h]       (the lead; else a proxy turn: vanilla)
		a.J8(0x75, "vanilla");                            // jne vanilla
		a.Raw(0x8B, 0xD8);                                // mov ebx,eax             (slot)
		a.Raw(0x8B, 0x0A);                                // mov ecx,[edx]           (count)
		a.Raw(0x83, 0xC8, 0xFF);                          // or eax,-1
		a.Raw(0xD3, 0xE0);                                // shl eax,cl
		a.Raw(0xF7, 0xD0);                                // not eax                 (mask of all slots)
		a.Raw(0x8B, 0x4A, 0x04);                          // mov ecx,[edx+4]         (ended)
		a.Raw(0x3B, 0xC8);                                // cmp ecx,eax
		a.J8(0x74, "vanilla");                            // je vanilla              (closing)
		a.Raw(0x0F, 0xA3, 0xD9);                          // bt ecx,ebx
		a.Raw(0x0F, 0x93, 0xC0);                          // setnc al                (slot not ended)
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("vanilla");
		a.Raw(0x8B, 0x44, 0x24, 0x10);                    // mov eax,[esp+10h]
		a.Raw(0x3B, 0x44, 0x24, 0x14);                    // cmp eax,[esp+14h]
		a.Raw(0x0F, 0x94, 0xC0);                          // sete al
		a.Mark("out");
		a.Raw(0x0F, 0xB6, 0xC0);                          // movzx eax,al
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitNeqCurF(Asm a, uint groupAllows) {
		// replaces FUN_105870c0(a = cur, b = f) "a != b": !GroupAllows(cur, f)
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (f)
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (cur)
		a.Call(groupAllows);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x83, 0xF0, 0x01);                          // xor eax,1
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitEqFCur(Asm a, uint groupAllows) {
		// replaces FUN_105870b0(a = f, b = cur) "a == b": GroupAllows(cur, f). (Sites passing (cur, f) call
		// GroupAllows directly.)
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (f)
		a.Raw(0xFF, 0x74, 0x24, 0x0C);                    // push dword ptr [esp+0Ch] (cur)
		a.Call(groupAllows);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitMoverAllowed(Asm a, uint groupAllows) {
		// replaces FUN_105b4300(ecx = cur-or-proxy; char) ret 4 in FUN_106aa660 / FUN_106aa730 (may this mover act):
		// GroupAllows(cur, char's faction). Keeps edx, as FUN_105b4300 does.
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx                (cur)
		a.Raw(0x8B, 0x4C, 0x24, 0x08);                    // mov ecx,[esp+8]         (char)
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);                                  // eax = char's faction
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x50);                                      // push eax                (f)
		a.Raw(0x51);                                      // push ecx                (cur)
		a.Call(groupAllows);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		return at;
	}

	static uint EmitActionAllowed(Asm a, uint groupAllows) {
		// FUN_106aa7e0 (CanSave and other "may this faction act" tests), replaces at 106AA89F
		// "mov eax,[esi+1498h] / cmp [eax+2Ch],edi / jne 106AA8BD" (anyFaction == 0 path).
		// entry: esi = campaign, edi = faction; eax, ecx, edx and the flags are dead (106AA8AA reloads eax)
		uint at = a.Here;
		a.Raw(0x8B, 0x86, 0x98, 0x14, 0x00, 0x00);        // mov eax,[esi+1498h]     (world)
		a.Raw(0x57);                                      // push edi                (f)
		a.Raw(0xFF, 0x70, 0x2C);                          // push dword ptr [eax+2Ch] (cur)
		a.Call(groupAllows);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "ok");                                 // jnz ok
		a.Jmp(0x106AA8BD);                                // refused
		a.Mark("ok");
		a.Jmp(0x106AA8AA);                                // phase test
		a.Resolve();
		return at;
	}

	// ---- group turns: the pathfinder set up per clan (fix 3) ---------------------------------------------
	// [campaign+EA4h] is the pathfinder; FUN_10778490(pf; faction, keep) sets it up for one faction (that faction's
	// shroud, war state and region access) and with keep remembers it ([pf+1F4h] faction, [pf+208h] = 0), so the
	// engine's own temporary set-ups return to it (FUN_107fd150). Turn start / end set it up for the current faction
	// (FUN_106fa800), so in a group every path was searched with the lead's set-up: a member's armies found no path.
	// The simulation now sets it up for a non-lead member before that member's order runs (FUN_106aa660 passed) and
	// before each of its armies' movement steps, and goes back to the turn's set-up after every command record, after
	// the movement update and before the next tick's commands, identically on every PC. Only the set-up the mod made
	// itself is undone: if engine code (a battle) set up another one meanwhile, that one stays.

	static uint EmitPfRestore(Asm a) {
		// stdcall(campaign) ret 4, keeps every register and the flags: if the mod set the pathfinder up for a member and
		// it still is, back to the turn's set-up (FUN_106fa800, as at turn start); forget the member either way
		uint at = a.Here;
		a.Raw(0x9C);                                      // pushfd
		a.Raw(0x60);                                      // pushad                  (campaign at [esp+28h])
		EmitGroupPtr(a, 7, PfFocusVa);                    // edi = &pfFocus
		a.Raw(0x8B, 0x07);                                // mov eax,[edi]
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "out");                                // jz out                  (the engine's own set-up)
		a.Raw(0xC7, 0x07, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [edi],0
		a.Raw(0x8B, 0x74, 0x24, 0x28);                    // mov esi,[esp+28h]       (campaign)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "out");                                // jz out
		a.Raw(0x8B, 0x8E, 0xA4, 0x0E, 0x00, 0x00);        // mov ecx,[esi+0EA4h]     (pathfinder)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "out");                                // jz out
		a.Raw(0x39, 0x81, 0xF4, 0x01, 0x00, 0x00);        // cmp [ecx+1F4h],eax      (still kept for that member?)
		a.J8(0x75, "out");                                // jne out                 (engine code set up another one)
		a.Raw(0x83, 0xB9, 0x08, 0x02, 0x00, 0x00, 0x00);  // cmp dword ptr [ecx+208h],0 (faction mode)
		a.J8(0x75, "out");                                // jne out
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(PfTurnFocus);                              // FUN_106fa800(campaign)
		a.Mark("out");
		a.Raw(0x61);                                      // popad
		a.Raw(0x9D);                                      // popfd
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	static uint EmitPfFocusFor(Asm a, uint groupSlot, uint pfRestore) {
		// stdcall(campaign, f) ret 8, keeps every register and the flags: f is about to act or move. While a group plays
		// (the lead is current) and no battle is pending: a non-lead member -> set the pathfinder up for f (faction mode
		// with keep, what vanilla gives the current human), unless it already is; the lead or anyone else -> PfRestore.
		// A pending battle (state not 0/10) sets up the pathfinder itself: then nothing changes.
		uint at = a.Here;
		a.Raw(0x9C);                                      // pushfd
		a.Raw(0x60);                                      // pushad                  (campaign at [esp+28h], f at [esp+2Ch])
		a.Raw(0x8B, 0x74, 0x24, 0x28);                    // mov esi,[esp+28h]       (campaign)
		a.Raw(0x8B, 0x6C, 0x24, 0x2C);                    // mov ebp,[esp+2Ch]       (f)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J32("out", 0x0F, 0x84);                         // jz out
		a.Raw(0x8B, 0xBE, 0xA4, 0x0E, 0x00, 0x00);        // mov edi,[esi+0EA4h]     (pathfinder)
		a.Raw(0x85, 0xFF);                                // test edi,edi
		a.J32("out", 0x0F, 0x84);                         // jz out
		a.Raw(0x8B, 0xCD);                                // mov ecx,ebp
		a.Call(groupSlot);                                // eax = f's slot or -1, edx = &group
		a.Raw(0x83, 0x3A, 0x00);                          // cmp dword ptr [edx],0
		a.J8(0x74, "restore");                            // je restore              (no group)
		a.Raw(0x8B, 0x8E, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[esi+1498h]     (world)
		a.Raw(0x8B, 0x59, 0x2C);                          // mov ebx,[ecx+2Ch]
		a.Raw(0x3B, 0x5A, 0x34);                          // cmp ebx,[edx+34h]       (the lead is current)
		a.J8(0x75, "restore");                            // jne restore
		a.Raw(0x8B, 0x8E, 0xA4, 0x14, 0x00, 0x00);        // mov ecx,[esi+14A4h]     (pending battle)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "nobattle");                           // jz nobattle
		a.Raw(0x8B, 0x49, 0x50);                          // mov ecx,[ecx+50h]       (its state)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "nobattle");                           // jz nobattle
		a.Raw(0x83, 0xF9, 0x0A);                          // cmp ecx,0Ah
		a.J8(0x75, "out");                                // jne out                 (a battle is running)
		a.Mark("nobattle");
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x7E, "restore");                            // jle restore             (the lead, or not a member)
		EmitGroupPtr(a, 3, PfFocusVa);                    // ebx = &pfFocus
		a.Raw(0x89, 0x2B);                                // mov [ebx],ebp           (this set-up is the mod's)
		a.Raw(0x39, 0xAF, 0xF4, 0x01, 0x00, 0x00);        // cmp [edi+1F4h],ebp
		a.J8(0x75, "set");                                // jne set
		a.Raw(0x83, 0xBF, 0x08, 0x02, 0x00, 0x00, 0x00);  // cmp dword ptr [edi+208h],0
		a.J8(0x74, "out");                                // je out                  (already set up for f)
		a.Mark("set");
		a.Raw(0xFF, 0x43, 0x04);                          // inc dword ptr [ebx+4]   (pfCount)
		a.Raw(0x6A, 0x01);                                // push 1                  (keep: the engine's restores come back to f)
		a.Raw(0x55);                                      // push ebp                (f)
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Call(PfSetFaction);                             // FUN_10778490(pf; f, 1)
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("restore");
		a.Raw(0x56);                                      // push esi                (campaign)
		a.Call(pfRestore);
		a.Mark("out");
		a.Raw(0x61);                                      // popad
		a.Raw(0x9D);                                      // popfd
		a.Raw(0xC2, 0x08, 0x00);                          // ret 8
		a.Resolve();
		return at;
	}

	static uint EmitEqOwnerScratch(Asm a, uint eqFCur, uint pfFocusFor) {
		// replaces FUN_105870b0(owner, cur-or-proxy) in update_positions FUN_108ffb80 (108FFE3B), right before the mover's
		// step FUN_108fe740 (which searches paths again): remember the mover's owner for ScratchOwner below, set the
		// pathfinder up for it (PfFocusFor), then GroupAllows(cur, owner). Changes only eax.
		// entry: ebx = movement manager (FUN_108ffb80's this), ebp = mover, esi = cur-or-proxy
		uint at = a.Here;
		a.Raw(0x52);                                      // push edx
		EmitGroupPtr(a, 2, GroupVa + 0x0C);               // edx = &moverOwner
		a.Raw(0x8B, 0x44, 0x24, 0x08);                    // mov eax,[esp+8]         (owner)
		a.Raw(0x89, 0x02);                                // mov [edx],eax
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x50);                                      // push eax                (f)
		a.Raw(0x8B, 0x43, 0x18);                          // mov eax,[ebx+18h]
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(pfFocusFor);
		a.Jmp(eqFCur);
		return at;
	}

	static uint EmitScratchOwner(Asm a) {
		// replaces thunk 1070B490 (ecx = world) where update_positions resumes the current faction's other
		// interrupted orders after one of its movers finished (FUN_108d93b0(faction, mover)); only reached when the
		// 108FFE3B test passed, where vanilla's owner == current. Returns that mover's owner instead, so a member's
		// orders resume (taken before FUN_108fe740 ran: the mover may be gone by now).
		uint at = a.Here;
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x8B, 0x80); a.Bytes.AddRange(BitConverter.GetBytes(unchecked(GroupVa + 0x0C - popAt)));   // mov eax,[eax+moverOwner]
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitLatchEq(Asm a, uint groupSlot, uint log) {
		// replaces FUN_105870b0(cur, f) in CCQ_END_TURN FUN_10886490 (1088655A): an end-turn request that couldn't be
		// granted yet sets the campaign+155Fh latch only for the current faction. A non-lead member gets its own
		// latch bit instead (the tick retries it) and 0 is returned; an ended member gets nothing. The lead keeps
		// +155Fh. Keeps ecx and edx.
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x52);                                      // push edx                (cur at +0Ch, f at +10h)
		a.Raw(0x8B, 0x4C, 0x24, 0x10);                    // mov ecx,[esp+10h]       (f)
		a.Call(groupSlot);
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x78, "vanilla");                            // js vanilla
		a.Raw(0x8B, 0x4C, 0x24, 0x0C);                    // mov ecx,[esp+0Ch]       (cur)
		a.Raw(0x3B, 0x4A, 0x34);                          // cmp ecx,[edx+34h]
		a.J8(0x75, "vanilla");                            // jne vanilla
		a.Raw(0x0F, 0xA3, 0x42, 0x04);                    // bt [edx+4],eax          (already ended, or closing)
		a.J8(0x72, "zero");                               // jc zero
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "vanilla");                            // je vanilla              (the lead: +155Fh)
		a.Raw(0x0F, 0xAB, 0x42, 0x08);                    // bts [edx+8],eax         (latch |= bit)
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0xFF, 0x74, 0x24, 0x14);                    // push dword ptr [esp+14h] (f)
		CallLog(a, log, LogLatch);
		a.Mark("zero");
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("vanilla");
		a.Raw(0x8B, 0x44, 0x24, 0x0C);                    // mov eax,[esp+0Ch]
		a.Raw(0x3B, 0x44, 0x24, 0x10);                    // cmp eax,[esp+10h]
		a.Raw(0x0F, 0x94, 0xC0);                          // sete al
		a.Raw(0x0F, 0xB6, 0xC0);                          // movzx eax,al
		a.Mark("out");
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitZeroGroup(Asm a) {
		// clears the whole group state, the pathfinder block, SaveLoadedVa and the attack table (they would outlive a campaign in the
		// same process), not the log's dwords between them; keeps every register. The load constructor FUN_106463e0
		// runs it (10646DCA) before it reads the save (LoadGroup at 106559A7).
		uint at = a.Here;
		a.Raw(0x57);                                      // push edi
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x50);                                      // push eax
		EmitGroupPtr(a, 7, GroupVa);                      // edi = &group
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0xB9); a.Bytes.AddRange(BitConverter.GetBytes(GroupDwords));   // mov ecx,GroupDwords
		a.Raw(0xF3, 0xAB);                                // rep stosd
		a.Raw(0x83, 0xC7, (byte)(PfFocusVa - (GroupVa + GroupDwords * 4)));   // add edi,8 (skip the log's dwords)
		a.Raw(0xB9); a.Bytes.AddRange(BitConverter.GetBytes(PfDwords));   // mov ecx,PfDwords
		a.Raw(0xF3, 0xAB);                                // rep stosd
		EmitGroupPtr(a, 7, SaveLoadedVa);                 // edi = &saveLoaded
		a.Raw(0x89, 0x07);                                // mov [edi],eax
		EmitGroupPtr(a, 7, AttackTableVa);                // edi = &attackTable
		a.Raw(0xB9); a.Bytes.AddRange(BitConverter.GetBytes(AttackDwords));   // mov ecx,AttackDwords
		a.Raw(0xF3, 0xAB);                                // rep stosd
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitGroupBuild(Asm a, uint log) {
		// thiscall(world), keeps ebx/esi/edi/ebp: builds the group for the current faction [world+2Ch] (the lead).
		// GroupStart (what the thunk at 10701DB0, "mov ecx,[ecx+2Ch] / jmp FUN_105fd090", called by the transition
		// FUN_1068a8d0 for the new current faction, now reaches) then runs the turn starts; after loading a save made
		// during a turn GroupTick calls it alone (the turn starts ran before the save).
		// Members: human, not removed (+7A8h), not skipped (+894h), not without a CAI step (+895h, a saved flag), no
		// revolution pending (+798h: it plays alone, so the vanilla proxy turn runs; a member's revolution starts in
		// its turn start in GroupStart, after the group was formed), alive, not in played[], not at war with any
		// member (hybrid mode only: the simultaneous build leaves the war test out); only factions after the lead
		// (earlier ones have played this round). Only a human lead without a proxy turn (+798h) forms a group; a lead
		// alone leaves count = 0.
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x57);                                      // push edi
		a.Raw(0x55);                                      // push ebp
		a.Raw(0x8B, 0xE9);                                // mov ebp,ecx             (world)
		EmitGroupPtr(a, 3, GroupVa);                      // ebx = &group
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x89, 0x43, 0x04);                          // mov [ebx+4],eax         (ended)
		a.Raw(0x89, 0x43, 0x08);                          // mov [ebx+8],eax         (latch)
		a.Raw(0x89, 0x43, (byte)(PfCountVa - GroupVa));   // mov [ebx+60h],eax       (pfCount)
		a.Raw(0x8B, 0x75, 0x2C);                          // mov esi,[ebp+2Ch]       (lead)
		a.Raw(0x89, 0x73, 0x34);                          // mov [ebx+34h],esi       (members[0])
		a.Raw(0xC7, 0x03, 0x01, 0x00, 0x00, 0x00);        // mov dword ptr [ebx],1
		a.Raw(0x80, 0xBE, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+6A0h],0 (human)
		a.J32("start", 0x0F, 0x84);                       // je start
		a.Raw(0x80, 0xBE, 0x98, 0x07, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+798h],0 (proxy turn)
		a.J32("start", 0x0F, 0x85);                       // jne start
		a.Raw(0x8B, 0x7D, 0x44);                          // mov edi,[ebp+44h]       (list begin)
		a.Raw(0x8B, 0x45, 0x40);                          // mov eax,[ebp+40h]       (count)
		a.Raw(0x8D, 0x04, 0x87);                          // lea eax,[edi+eax*4]
		a.Raw(0x50);                                      // push eax                ([esp] = list end)
		a.Mark("findlead");
		a.Raw(0x3B, 0x3C, 0x24);                          // cmp edi,[esp]
		a.J32("scan_done", 0x0F, 0x83);                   // jae scan_done
		a.Raw(0x39, 0x37);                                // cmp [edi],esi
		a.Raw(0x8D, 0x7F, 0x04);                          // lea edi,[edi+4]         (keeps the flags)
		a.J8(0x75, "findlead");                           // jne findlead
		a.Mark("scan");                                   // edi = next entry after the lead
		a.Raw(0x3B, 0x3C, 0x24);                          // cmp edi,[esp]
		a.J32("scan_done", 0x0F, 0x83);                   // jae scan_done
		a.Raw(0x8B, 0x37);                                // mov esi,[edi]           (candidate)
		a.Raw(0x83, 0xC7, 0x04);                          // add edi,4
		a.Raw(0x83, 0x3B, 0x08);                          // cmp dword ptr [ebx],8
		a.J32("scan_done", 0x0F, 0x83);                   // jae scan_done           (full)
		a.Raw(0x80, 0xBE, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+6A0h],0 (human)
		a.J8(0x74, "scan");                               // je scan
		a.Raw(0x80, 0xBE, 0xA8, 0x07, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+7A8h],0 (removed)
		a.J8(0x75, "scan");                               // jne scan
		a.Raw(0x80, 0xBE, 0x94, 0x08, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+894h],0 (skipped in turn order)
		a.J8(0x75, "scan");                               // jne scan
		a.Raw(0x80, 0xBE, 0x95, 0x08, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+895h],0 (no CAI step: auto-ended)
		a.J8(0x75, "scan");                               // jne scan
		a.Raw(0x80, 0xBE, 0x98, 0x07, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+798h],0 (a revolution pending)
		a.J8(0x75, "scan");                               // jne scan
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(FactionIsDead);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J32("scan", 0x0F, 0x85);                        // jne scan
		a.Raw(0x8B, 0x4B, 0x10);                          // mov ecx,[ebx+10h]       (played count)
		a.Mark("played");
		a.Raw(0x49);                                      // dec ecx
		a.J8(0x78, "notplayed");                          // js notplayed
		a.Raw(0x39, 0x74, 0x8B, 0x14);                    // cmp [ebx+ecx*4+14h],esi
		a.J32("scan", 0x0F, 0x84);                        // je scan                 (played this round already)
		a.J8(0xEB, "played");                             // jmp played
		a.Mark("notplayed");
		if (TurnMode != "simultaneous") {
			a.Raw(0x33, 0xED);                            // xor ebp,ebp             (member index; world no longer needed)
			a.Mark("war");
			a.Raw(0x3B, 0x2B);                            // cmp ebp,[ebx]
			a.J8(0x73, "add");                            // jae add
			a.Raw(0xFF, 0x74, 0xAB, 0x34);                // push dword ptr [ebx+ebp*4+34h] (member)
			a.Raw(0x8B, 0xCE);                            // mov ecx,esi
			a.Call(FactionsAtWar);
			a.Raw(0x84, 0xC0);                            // test al,al
			a.J32("scan", 0x0F, 0x85);                    // jne scan                (at war with a member)
			a.Raw(0x45);                                  // inc ebp
			a.J8(0xEB, "war");                            // jmp war
		}
		a.Mark("add");
		a.Raw(0x8B, 0x03);                                // mov eax,[ebx]
		a.Raw(0x89, 0x74, 0x83, 0x34);                    // mov [ebx+eax*4+34h],esi (members[count] = candidate)
		a.Raw(0xFF, 0x03);                                // inc dword ptr [ebx]
		a.J32("scan", 0xE9);                              // jmp scan
		a.Mark("scan_done");
		a.Raw(0x58);                                      // pop eax                 (list end)
		a.Mark("start");
		a.Raw(0x8B, 0x4B, 0x34);                          // mov ecx,[ebx+34h]       (lead)
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x83, 0x3B, 0x02);                          // cmp dword ptr [ebx],2
		a.J8(0x73, "have");                               // jae have
		a.Raw(0xC7, 0x03, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [ebx],0   (alone: no group, vanilla)
		a.Raw(0x80, 0xB9, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [ecx+6A0h],0 (log a human playing alone)
		a.J8(0x74, "nolog");                              // je nolog
		CallLog(a, log, LogSolo);
		a.J8(0xEB, "logged");                             // jmp logged
		a.Mark("nolog");
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8               (the log arguments)
		a.J8(0xEB, "logged");                             // jmp logged
		a.Mark("have");
		CallLog(a, log, LogStart);
		a.Mark("logged");
		EmitGroupPtr(a, 0, LogCloseWaitVa);               // eax = &closeWaitLogged
		a.Raw(0xC7, 0x00, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [eax],0
		a.Raw(0x5D);                                      // pop ebp
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitGroupStart(Asm a, uint groupBuild, uint log) {
		// thiscall(world): GroupBuild, then the faction turn start FUN_105fd090 (movement points, FactionTurnStart,
		// ...) for the lead and then every other member. A member's turn start may begin a revolution (+798h, per
		// force FUN_10871e60 -> FUN_105f7160): only the lead's proxy turn runs (FUN_1070b4a0), so that is logged and
		// GroupBuild leaves the member out of its next group.
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x57);                                      // push edi
		a.Call(groupBuild);
		EmitGroupPtr(a, 3, GroupVa);                      // ebx = &group
		a.Raw(0x8B, 0x4B, 0x34);                          // mov ecx,[ebx+34h]       (lead first, as vanilla)
		a.Call(FactionTurnStart);
		a.Raw(0xBF, 0x01, 0x00, 0x00, 0x00);              // mov edi,1
		a.Mark("starts");
		a.Raw(0x3B, 0x3B);                                // cmp edi,[ebx]
		a.J8(0x73, "done");                               // jae done
		a.Raw(0x8B, 0x4C, 0xBB, 0x34);                    // mov ecx,[ebx+edi*4+34h]
		a.Call(FactionTurnStart);
		a.Raw(0x8B, 0x4C, 0xBB, 0x34);                    // mov ecx,[ebx+edi*4+34h]
		a.Raw(0x80, 0xB9, 0x98, 0x07, 0x00, 0x00, 0x00);  // cmp byte ptr [ecx+798h],0
		a.J8(0x74, "norevolt");                           // je norevolt
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x51);                                      // push ecx
		CallLog(a, log, LogRevolt);
		a.Mark("norevolt");
		a.Raw(0x47);                                      // inc edi
		a.J8(0xEB, "starts");                             // jmp starts
		a.Mark("done");
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitGroupStartLoad(Asm a, uint groupStart, uint log) {
		// replaces "call FUN_105fd090" at 10704313: the tick runs the current faction's turn start once campaign+151Ch
		// is set. 151Ch is in the save (written by FUN_106f7740 at 106F7C78, read back by the load constructor
		// FUN_106463e0 at 10647CC2): the startpos has 1, so this is a new campaign's first turn; a save made during a
		// turn has 0 and goes through GroupTick's rebuild instead. Runs GroupStart; played[] is cleared.
		// entry: ecx = current faction, ebx = campaign; eax is dead (the call returns nothing)
		uint at = a.Here;
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, LogLoad);
		a.Raw(0x8B, 0x8B, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[ebx+1498h]     (world)
		EmitGroupPtr(a, 0, GroupVa + 0x10);               // eax = &playedCount
		a.Raw(0xC7, 0x00, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [eax],0
		a.Jmp(groupStart);
		return at;
	}

	static uint EmitGroupTurnEnd(Asm a, uint log) {
		// replaces "call FUN_105b2870" (faction turn end, FactionTurnEnd; ecx = [world+2Ch]) at the start of the
		// picker FUN_106a5fd0: ends every member's turn, lead first, puts the others in played[] so the picker
		// skips them for the rest of the round, and dissolves the group.
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		EmitGroupPtr(a, 3, GroupVa);                      // ebx = &group
		a.Raw(0x83, 0x3B, 0x00);                          // cmp dword ptr [ebx],0
		a.J8(0x75, "group");                              // jne group
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Jmp(FactionTurnEnd);                            // no group: vanilla
		a.Mark("group");
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0xFF, 0x73, (byte)(PfCountVa - GroupVa));   // push dword ptr [ebx+60h] (pfCount)
		CallLog(a, log, LogTurnEnd);
		a.Call(FactionTurnEnd);                           // the lead (ecx)
		a.Raw(0xBE, 0x01, 0x00, 0x00, 0x00);              // mov esi,1
		a.Mark("loop");
		a.Raw(0x3B, 0x33);                                // cmp esi,[ebx]
		a.J8(0x73, "done");                               // jae done
		a.Raw(0x8B, 0x4C, 0xB3, 0x34);                    // mov ecx,[ebx+esi*4+34h]
		a.Call(FactionTurnEnd);
		a.Raw(0x8B, 0x43, 0x10);                          // mov eax,[ebx+10h]       (played count)
		a.Raw(0x83, 0xF8, 0x08);                          // cmp eax,8
		a.J8(0x73, "next");                               // jae next
		a.Raw(0x8B, 0x4C, 0xB3, 0x34);                    // mov ecx,[ebx+esi*4+34h]
		a.Raw(0x89, 0x4C, 0x83, 0x14);                    // mov [ebx+eax*4+14h],ecx (played[count] = member)
		a.Raw(0xFF, 0x43, 0x10);                          // inc dword ptr [ebx+10h]
		a.Mark("next");
		a.Raw(0x46);                                      // inc esi
		a.J8(0xEB, "loop");                               // jmp loop
		a.Mark("done");
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x89, 0x03);                                // mov [ebx],eax           (count)
		a.Raw(0x89, 0x43, 0x04);                          // mov [ebx+4],eax         (ended)
		a.Raw(0x89, 0x43, 0x08);                          // mov [ebx+8],eax         (latch)
		a.Raw(0x89, 0x43, (byte)(PfCountVa - GroupVa));   // mov [ebx+60h],eax       (pfCount)
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitPickerSkip(Asm a, uint log) {
		// replaces "cmp byte [eax+894h],0 / je 106A604B" in the picker's next-faction loop (106A603B).
		// entry: eax = candidate, esi = &list[i], ecx = list end, edi = world, bl = 1 when the scan restarted from
		// the list start (the current faction died: new round); edx and the flags are dead (106A604B recomputes
		// cmp esi,ecx). Also skips the members of earlier groups this round.
		uint at = a.Here;
		a.Raw(0x80, 0xB8, 0x94, 0x08, 0x00, 0x00, 0x00);  // cmp byte ptr [eax+894h],0 (replaced instructions)
		a.J8(0x75, "skip");                               // jne skip
		a.Raw(0x84, 0xDB);                                // test bl,bl
		a.J8(0x75, "take");                               // jnz take                (new round: played[] is reset)
		a.Raw(0x51);                                      // push ecx
		EmitGroupPtr(a, 2, GroupVa);                      // edx = &group
		a.Raw(0x8B, 0x4A, 0x10);                          // mov ecx,[edx+10h]       (played count)
		a.Mark("loop");
		a.Raw(0x49);                                      // dec ecx
		a.J8(0x78, "notin");                              // js notin
		a.Raw(0x39, 0x44, 0x8A, 0x14);                    // cmp [edx+ecx*4+14h],eax
		a.J8(0x75, "loop");                               // jne loop
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x50);                                      // push eax
		CallLog(a, log, LogSkip);
		a.Mark("skip");
		a.Jmp(0x106A6044);                                // next candidate
		a.Mark("notin");
		a.Raw(0x59);                                      // pop ecx
		a.Mark("take");
		a.Jmp(0x106A604B);                                // this one plays next
		a.Resolve();
		return at;
	}

	static uint EmitNewRound(Asm a, uint log) {
		// replaces "xor esi,esi / cmp [edi+40h],esi" at 106A609E, the start of the picker's new-round block
		// (edi = world): played[] starts empty each round
		uint at = a.Here;
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, LogRound);
		EmitGroupPtr(a, 6, GroupVa + 0x10);               // esi = &playedCount
		a.Raw(0xC7, 0x06, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [esi],0
		a.Raw(0x33, 0xF6);                                // xor esi,esi             (replaced instructions)
		a.Raw(0x39, 0x77, 0x40);                          // cmp [edi+40h],esi
		a.Jmp(0x106A60A3);                                // jbe there uses these flags
		return at;
	}

	static uint EmitGroupEndTurn(Asm a, uint groupSlot, uint log) {
		// thiscall(campaign; f, flag) ret 8, replaces "call FUN_106a5da0" in CCQ_END_TURN (10886507, after
		// CanEndTurn(f) passed) and in the tick's +155Fh retry (1070420A). FUN_106a5da0 moves the GLOBAL phase
		// 1 -> 2 whatever f is, so in a group it may only run once, for the lead, after every member ended:
		// here a member's request only sets its ended bit (and fires FactionAboutToEndTurn for a non-lead member;
		// the lead's comes from that final FUN_106a5da0, run by the tick hook).
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x57);                                      // push edi                (f at +10h, flag at +14h)
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx             (campaign)
		a.Raw(0x8B, 0x7C, 0x24, 0x10);                    // mov edi,[esp+10h]       (f)
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Call(groupSlot);
		a.Raw(0x8B, 0xDA);                                // mov ebx,edx             (&group)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("vanilla", 0x0F, 0x88);                     // js vanilla
		a.Raw(0x8B, 0x8E, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[esi+1498h]     (world)
		a.Raw(0x83, 0x79, 0x50, 0x01);                    // cmp dword ptr [ecx+50h],1 (phase: playing)
		a.J32("vanilla", 0x0F, 0x85);                     // jne vanilla
		a.Raw(0x8B, 0x49, 0x2C);                          // mov ecx,[ecx+2Ch]
		a.Raw(0x3B, 0x4B, 0x34);                          // cmp ecx,[ebx+34h]       (current is the lead)
		a.J32("vanilla", 0x0F, 0x85);                     // jne vanilla
		a.Raw(0xFF, 0x74, 0x24, 0x14);                    // push dword ptr [esp+14h] (flag, for the log)
		a.Raw(0x57);                                      // push edi                (f)
		a.Raw(0x0F, 0xA3, 0x43, 0x04);                    // bt [ebx+4],eax
		a.J32("already", 0x0F, 0x82);                     // jc already
		// PendingBankruptcy deferral, the test of 106A5E32..106A5E89: FUN_106a5da0 fires the event and queues the
		// pending action itself and returns without ending; the player confirms and END_TURN comes again.
		// Every path from here calls Log once, which pops the two log arguments.
		a.Raw(0x80, 0x7C, 0x24, 0x1C, 0x00);              // cmp byte ptr [esp+1Ch],0 (flag)
		a.J8(0x75, "mark");                               // jne mark
		a.Raw(0x80, 0xBF, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [edi+6A0h],0 (human)
		a.J8(0x74, "mark");                               // je mark
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8D, 0x8F, 0x90, 0x00, 0x00, 0x00);        // lea ecx,[edi+90h]
		a.Call(TreasuryGoesNegative);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.Raw(0x58);                                      // pop eax
		a.J8(0x74, "mark");                               // je mark
		a.Raw(0x80, 0xBF, 0x98, 0x07, 0x00, 0x00, 0x00);  // cmp byte ptr [edi+798h],0 (FUN_105c6a60: proxy turn)
		a.J8(0x74, "defer");                              // je defer
		a.Raw(0x80, 0xBF, 0xA0, 0x07, 0x00, 0x00, 0x00);  // cmp byte ptr [edi+7A0h],0
		a.J8(0x74, "mark");                               // je mark
		a.Mark("defer");
		CallLog(a, log, LogEndBankrupt);
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x57);                                      // push edi
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(CampaignEndTurn);
		a.J32("done", 0xE9);                              // jmp done
		a.Mark("already");
		CallLog(a, log, LogEndAlready);
		a.J32("done", 0xE9);                              // jmp done
		a.Mark("locked");
		CallLog(a, log, LogEndLocked);
		a.J32("done", 0xE9);                              // jmp done
		a.Mark("mark");
		// the end-turn lock vanilla checks before changing the phase (106A5F4B)
		a.Raw(0x8B, 0x8E, 0xE0, 0x14, 0x00, 0x00);        // mov ecx,[esi+14E0h]
		a.Raw(0x80, 0xB9, 0xFE, 0x00, 0x00, 0x00, 0x00);  // cmp byte ptr [ecx+0FEh],0
		a.J8(0x75, "locked");                             // jne locked
		a.Raw(0x0F, 0xAB, 0x43, 0x04);                    // bts [ebx+4],eax         (ended |= bit)
		CallLog(a, log, LogEndMarked);
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "done");                               // je done                 (the lead)
		// FactionAboutToEndTurn for f, as 106A5F20..106A5F46 (event object: +0 vtable, +10h faction)
		a.Raw(0x83, 0xEC, 0x20);                          // sub esp,20h
		a.Raw(0x8B, 0xCC);                                // mov ecx,esp
		a.Call(EventInit);
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x05); a.Bytes.AddRange(BitConverter.GetBytes(unchecked(FactionAboutToEndTurnVt - popAt)));   // add eax, vtable - popAt
		a.Raw(0x89, 0x04, 0x24);                          // mov [esp],eax           (event vtable)
		a.Raw(0x89, 0x7C, 0x24, 0x10);                    // mov [esp+10h],edi       (faction)
		a.Raw(0x54);                                      // push esp
		a.Call(EventDispatch);
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
		a.Raw(0x8B, 0xCC);                                // mov ecx,esp
		a.Call(EventDestroy);
		a.Raw(0x83, 0xC4, 0x20);                          // add esp,20h
		a.Mark("done");
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC2, 0x08, 0x00);                          // ret 8
		a.Mark("vanilla");
		a.Raw(0x80, 0xBF, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [edi+6A0h],0 (log humans only)
		a.J8(0x74, "nolog");                              // je nolog
		a.Raw(0xFF, 0x74, 0x24, 0x14);                    // push dword ptr [esp+14h] (flag)
		a.Raw(0x57);                                      // push edi                (f)
		CallLog(a, log, LogEndVanilla);
		a.Mark("nolog");
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Jmp(CampaignEndTurn);
		a.Resolve();
		return at;
	}

	static uint EmitGroupTick(Asm a, uint groupEndTurn, uint groupBuild, uint groupRestore, uint log) {
		// campaign tick FUN_10703ea0, replaces "cmp byte [ebx+155Fh],0 / je 10704216" at 107041CA (the +155Fh
		// retry; ebx = campaign). While a group plays (current = lead, phase 1):
		//   1. members handed to the AI (+6A0h cleared by CCQ_FACTION_SWITCH_HUMAN_TO_AI, FUN_108868f0 ->
		//      FUN_10600c20: a player who left, or the 8-player mod v1.11 giving a missing player's clan to the AI
		//      after a resume), without a CAI step (+895h) or removed (+7A8h) count as ended. Nobody would end such a
		//      member's part (the CAI only steps for the current faction), and an AI lead marked ended can't take
		//      its CAI turn now (CanEndTurn refuses it until the group closes), so it can't end the group early
		//      either (vanilla's auto-end at 10704344.. only looks at the current faction);
		//   2. latched members: retry CanEndTurn, then end them (GroupEndTurn with flag 1, as vanilla's retry);
		//   3. every member ended: the lead's real end turn FUN_106a5da0(lead, 1), retried each tick until
		//      CanEndTurn passes.
		// First the rebuild after a load: a save made during a turn (resyncs too: they reload on every PC) has
		// campaign+151Ch = 0, so the tick doesn't repeat the turn start (members[0] is 0 only between a campaign's
		// construction and its first group start). Every member's turn start ran before the save (GroupStart runs
		// them all before the transition's autosave FUN_106d5bc0), so only the group comes back: from the save
		// (GroupRestore: members, ended, latch, played[]), or for a save without it (an older build, the unmodded
		// game) rebuilt by GroupBuild, where who had ended must end again. In phase 2 or later (a resync after the
		// lead's end) everyone had.
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		EmitGroupPtr(a, 7, GroupVa);                      // edi = &group
		a.Raw(0x83, 0x7F, 0x34, 0x00);                    // cmp dword ptr [edi+34h],0 (members[0])
		a.J8(0x75, "built");                              // jne built
		a.Raw(0x80, 0xBB, 0x1C, 0x15, 0x00, 0x00, 0x00);  // cmp byte ptr [ebx+151Ch],0
		a.J8(0x75, "built");                              // jne built               (the turn start below: GroupStartLoad)
		a.Raw(0x8B, 0x8B, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[ebx+1498h]     (world)
		a.Call(groupRestore);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "restored");                           // jne restored
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, LogLoadSave);
		a.Raw(0x8B, 0x8B, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[ebx+1498h]     (world)
		a.Call(groupBuild);
		a.Mark("restored");
		a.Raw(0x8B, 0x83, 0x98, 0x14, 0x00, 0x00);        // mov eax,[ebx+1498h]
		a.Raw(0x83, 0x78, 0x50, 0x02);                    // cmp dword ptr [eax+50h],2 (phase)
		a.J8(0x72, "built");                              // jb built
		a.Raw(0x8B, 0x0F);                                // mov ecx,[edi]           (count)
		a.Raw(0xB8, 0x01, 0x00, 0x00, 0x00);              // mov eax,1
		a.Raw(0xD3, 0xE0);                                // shl eax,cl
		a.Raw(0x48);                                      // dec eax
		a.Raw(0x89, 0x47, 0x04);                          // mov [edi+4],eax         (ended = every slot)
		a.Mark("built");
		a.Raw(0x83, 0x3F, 0x00);                          // cmp dword ptr [edi],0
		a.J32("out", 0x0F, 0x84);                         // je out
		a.Raw(0x8B, 0x83, 0x98, 0x14, 0x00, 0x00);        // mov eax,[ebx+1498h]     (world)
		a.Raw(0x8B, 0x48, 0x2C);                          // mov ecx,[eax+2Ch]
		a.Raw(0x3B, 0x4F, 0x34);                          // cmp ecx,[edi+34h]
		a.J32("out", 0x0F, 0x85);                         // jne out
		a.Raw(0x83, 0x78, 0x50, 0x01);                    // cmp dword ptr [eax+50h],1
		a.J32("out", 0x0F, 0x85);                         // jne out
		a.Raw(0x33, 0xF6);                                // xor esi,esi
		a.Mark("drop");
		a.Raw(0x3B, 0x37);                                // cmp esi,[edi]
		a.J8(0x73, "latches");                            // jae latches
		a.Raw(0x8B, 0x44, 0xB7, 0x34);                    // mov eax,[edi+esi*4+34h]
		a.Raw(0x80, 0xB8, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [eax+6A0h],0 (human)
		a.J8(0x74, "dropped");                            // je dropped
		a.Raw(0x80, 0xB8, 0x95, 0x08, 0x00, 0x00, 0x00);  // cmp byte ptr [eax+895h],0
		a.J8(0x75, "dropped");                            // jne dropped
		a.Raw(0x80, 0xB8, 0xA8, 0x07, 0x00, 0x00, 0x00);  // cmp byte ptr [eax+7A8h],0
		a.J8(0x74, "dropnext");                           // je dropnext
		a.Mark("dropped");
		a.Raw(0x0F, 0xAB, 0x77, 0x04);                    // bts [edi+4],esi
		a.J8(0x72, "dropnext");                           // jc dropnext             (already ended: no log line)
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x50);                                      // push eax
		CallLog(a, log, LogDrop);
		a.Mark("dropnext");
		a.Raw(0x46);                                      // inc esi
		a.J8(0xEB, "drop");                               // jmp drop
		a.Mark("latches");
		a.Raw(0x33, 0xF6);                                // xor esi,esi
		a.Mark("latch");
		a.Raw(0x3B, 0x37);                                // cmp esi,[edi]
		a.J8(0x73, "closing");                            // jae closing
		a.Raw(0x0F, 0xA3, 0x77, 0x08);                    // bt [edi+8],esi
		a.J8(0x73, "latchnext");                          // jnc latchnext
		a.Raw(0x0F, 0xA3, 0x77, 0x04);                    // bt [edi+4],esi
		a.J8(0x72, "latchclear");                         // jc latchclear           (ended meanwhile)
		a.Raw(0x8B, 0x6C, 0xB7, 0x34);                    // mov ebp,[edi+esi*4+34h]
		a.Raw(0x55);                                      // push ebp
		a.Raw(0x8B, 0xCB);                                // mov ecx,ebx
		a.Call(CanEndTurn);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "latchnext");                          // je latchnext            (retry next tick)
		a.Raw(0x6A, 0x01);                                // push 1
		a.Raw(0x55);                                      // push ebp
		a.Raw(0x8B, 0xCB);                                // mov ecx,ebx
		a.Call(groupEndTurn);
		a.Mark("latchclear");
		a.Raw(0x0F, 0xB3, 0x77, 0x08);                    // btr [edi+8],esi
		a.Mark("latchnext");
		a.Raw(0x46);                                      // inc esi
		a.J8(0xEB, "latch");                              // jmp latch
		a.Mark("closing");
		a.Raw(0x8B, 0x0F);                                // mov ecx,[edi]           (count)
		a.Raw(0x83, 0xC8, 0xFF);                          // or eax,-1
		a.Raw(0xD3, 0xE0);                                // shl eax,cl
		a.Raw(0xF7, 0xD0);                                // not eax
		a.Raw(0x3B, 0x47, 0x04);                          // cmp eax,[edi+4]
		a.J8(0x75, "out");                                // jne out                 (someone still plays)
		a.Raw(0x8B, 0x83, 0x98, 0x14, 0x00, 0x00);        // mov eax,[ebx+1498h]
		a.Raw(0x83, 0x78, 0x50, 0x01);                    // cmp dword ptr [eax+50h],1
		a.J8(0x75, "out");                                // jne out
		a.Raw(0xFF, 0x77, 0x34);                          // push dword ptr [edi+34h] (lead)
		a.Raw(0x8B, 0xCB);                                // mov ecx,ebx
		a.Call(CanEndTurn);
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0xFF, 0x77, 0x34);                          // push dword ptr [edi+34h] (lead, for the log)
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "close");                              // jne close
		EmitGroupPtr(a, 2, LogCloseWaitVa);               // edx = &closeWaitLogged
		a.Raw(0x83, 0x3A, 0x00);                          // cmp dword ptr [edx],0
		a.J8(0x75, "waitlogged");                         // jne waitlogged          (once per group)
		a.Raw(0xC7, 0x02, 0x01, 0x00, 0x00, 0x00);        // mov dword ptr [edx],1
		CallLog(a, log, LogCloseWait);
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("waitlogged");
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8               (the log arguments)
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("close");
		CallLog(a, log, LogClose);
		a.Raw(0x6A, 0x01);                                // push 1
		a.Raw(0xFF, 0x77, 0x34);                          // push dword ptr [edi+34h]
		a.Raw(0x8B, 0xCB);                                // mov ecx,ebx
		a.Call(CampaignEndTurn);
		a.Mark("out");
		a.Raw(0x61);                                      // popad
		a.Raw(0x80, 0xBB, 0x5F, 0x15, 0x00, 0x00, 0x00);  // cmp byte ptr [ebx+155Fh],0 (replaced instructions)
		a.J8(0x75, "latched");                            // jne latched
		a.Jmp(0x10704216);
		a.Mark("latched");
		a.Jmp(0x107041D3);
		a.Resolve();
		return at;
	}

	static uint EmitFactionAt(Asm a) {
		// eax = list position + 1 (0 = none), ebp = world -> eax = that faction of the list [world+44h] (count
		// [world+40h]), 0 for 0 or out of range; changes ecx and the flags
		uint at = a.Here;
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "out");                                // je out
		a.Raw(0x3B, 0x45, 0x40);                          // cmp eax,[ebp+40h]
		a.J8(0x77, "none");                               // ja none
		a.Raw(0x8B, 0x4D, 0x44);                          // mov ecx,[ebp+44h]
		a.Raw(0x8B, 0x44, 0x81, 0xFC);                    // mov eax,[ecx+eax*4-4]
		a.Raw(0xC3);                                      // ret
		a.Mark("none");
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Mark("out");
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	// esi = value index -> the name "s2st_" + ('a' + index) at [esp+text] and its name object at [esp+obj] (+0 and +4
	// the length 6, +8 the text, +0Ch NameHash); changes eax, ecx, edx
	static void EmitSaveName(Asm a, byte obj, byte text) {
		a.Raw(0xC7, 0x44, 0x24, text, 0x73, 0x32, 0x73, 0x74);   // mov dword ptr [esp+text],'s2st'
		a.Raw(0x8D, 0x46, 0x61);                          // lea eax,[esi+61h]       ('a' + index)
		a.Raw(0xC1, 0xE0, 0x08);                          // shl eax,8
		a.Raw(0xB0, 0x5F);                                // mov al,5Fh              ('_', the letter, two zero bytes)
		a.Raw(0x89, 0x44, 0x24, (byte)(text + 4));        // mov [esp+text+4],eax
		a.Raw(0xC7, 0x44, 0x24, obj, 0x06, 0x00, 0x00, 0x00);   // mov dword ptr [esp+obj],6
		a.Raw(0xC7, 0x44, 0x24, (byte)(obj + 4), 0x06, 0x00, 0x00, 0x00);   // mov dword ptr [esp+obj+4],6
		a.Raw(0x8D, 0x44, 0x24, text);                    // lea eax,[esp+text]
		a.Raw(0x89, 0x44, 0x24, (byte)(obj + 8));         // mov [esp+obj+8],eax
		a.Raw(0x50);                                      // push eax
		a.Call(NameHash);
		a.Raw(0x89, 0x44, 0x24, (byte)(obj + 0x0C));      // mov [esp+obj+0Ch],eax
	}

	static uint EmitSaveGroup(Asm a, uint log) {
		// cdecl(event*), replaces "call FUN_10be0350" (EventDispatch) at 1066CCDC in the campaign save FUN_1066b760
		// (only caller FUN_106f7740: every save, autosaves and resyncs too): SavingGame, payload +10h = the
		// SCRIPT_VARIABLES record (the Lua save_named_value store). After the scripts' values, GroupVa's 21 dwords
		// go in as named numbers "s2st_a".."s2st_u" (ESF int32): factions as list position + 1 (0 = none), +0Ch (the
		// mover-owner scratch) as SaveVersion. Children are found by name, so the scripts never see them.
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (event)
		a.Call(EventDispatch);
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
		a.Raw(0x60);                                      // pushad
		a.Raw(0x8B, 0x44, 0x24, 0x24);                    // mov eax,[esp+24h]       (event)
		a.Raw(0x8B, 0x78, 0x10);                          // mov edi,[eax+10h]       (the record)
		EmitGroupPtr(a, 0, UiGlobal);                     // eax = &ui
		a.Raw(0x8B, 0x00);                                // mov eax,[eax]
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("out", 0x0F, 0x84);                         // je out
		a.Raw(0x8B, 0x80, 0x98, 0x0A, 0x00, 0x00);        // mov eax,[eax+0A98h]     (model)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("out", 0x0F, 0x84);                         // je out
		a.Raw(0x8B, 0x40, 0x08);                          // mov eax,[eax+8]         (campaign)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("out", 0x0F, 0x84);                         // je out
		a.Raw(0x8B, 0xA8, 0x98, 0x14, 0x00, 0x00);        // mov ebp,[eax+1498h]     (world)
		a.Raw(0x85, 0xED);                                // test ebp,ebp
		a.J32("out", 0x0F, 0x84);                         // je out
		EmitGroupPtr(a, 3, GroupVa);                      // ebx = &group
		a.Raw(0x83, 0xEC, 0x40);                          // sub esp,40h  (name object +0, child +10h, text +30h, type +38h, value +3Ch)
		a.Raw(0x33, 0xF6);                                // xor esi,esi
		a.Mark("value");
		a.Raw(0x8B, 0x04, 0xB3);                          // mov eax,[ebx+esi*4]
		a.Raw(0x83, 0xFE, 0x03);                          // cmp esi,3
		a.J8(0x75, "notver");                             // jne notver
		a.Raw(0xB8); D32(a, SaveVersion);                 // mov eax,SaveVersion
		a.Mark("notver");
		a.Raw(0x83, 0xFE, 0x05);                          // cmp esi,5
		a.J8(0x72, "put");                                // jb put                  (not a faction)
		a.Raw(0x8B, 0x4D, 0x40);                          // mov ecx,[ebp+40h]       (count)
		a.Raw(0x8B, 0x55, 0x44);                          // mov edx,[ebp+44h]       (list)
		a.Mark("find");
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "found");                              // je found                (0 or not in the list: 0)
		a.Raw(0x39, 0x44, 0x8A, 0xFC);                    // cmp [edx+ecx*4-4],eax
		a.J8(0x74, "found");                              // je found
		a.Raw(0x49);                                      // dec ecx
		a.J8(0xEB, "find");                               // jmp find
		a.Mark("found");
		a.Raw(0x8B, 0xC1);                                // mov eax,ecx             (list position + 1)
		a.Mark("put");
		a.Raw(0x89, 0x44, 0x24, 0x3C);                    // mov [esp+3Ch],eax
		EmitSaveName(a, 0x00, 0x30);
		a.Raw(0x8B, 0xC4);                                // mov eax,esp             (&name)
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]       (child)
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x50);                                      // push eax
		a.Raw(0x57);                                      // push edi
		a.Call(EsfChildOpen);
		a.Raw(0xC7, 0x44, 0x24, 0x38, 0x01, 0x00, 0x00, 0x00);   // mov dword ptr [esp+38h],1 (number)
		a.Raw(0x8D, 0x44, 0x24, 0x38);                    // lea eax,[esp+38h]
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]
		a.Raw(0x50);                                      // push eax
		a.Call(EsfPutType);
		a.Raw(0x8D, 0x44, 0x24, 0x3C);                    // lea eax,[esp+3Ch]
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]
		a.Raw(0x50);                                      // push eax
		a.Call(EsfPutInt);
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]
		a.Call(EsfChildClose);
		a.Raw(0x46);                                      // inc esi
		a.Raw(0x83, 0xFE, (byte)SaveValues);              // cmp esi,SaveValues
		a.J32("value", 0x0F, 0x82);                       // jb value
		a.Raw(0x83, 0xC4, 0x40);                          // add esp,40h
		a.Raw(0xFF, 0x33);                                // push dword ptr [ebx]    (count)
		a.Raw(0xFF, 0x73, 0x34);                          // push dword ptr [ebx+34h] (lead)
		CallLog(a, log, LogSaveGroup);
		a.Mark("out");
		a.Raw(0x61);                                      // popad
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitLoadGroup(Asm a) {
		// cdecl(event*), replaces "call FUN_10be0350" at 106559A7 in the campaign load FUN_10652870 (only caller the
		// load constructor FUN_106463e0, after its CampaignReset): LoadingGame, payload +10h = the SCRIPT_VARIABLES
		// record being read. After the scripts, reads SaveGroup's "s2st_a".."s2st_u" into SaveBufVa; SaveLoadedVa = 1
		// only when every one is there as a number and the version matches (a save from an older build or the
		// unmodded game has none). GroupTick's rebuild on the first tick uses them (GroupRestore).
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (event)
		a.Call(EventDispatch);
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
		a.Raw(0x60);                                      // pushad
		a.Raw(0x8B, 0x44, 0x24, 0x24);                    // mov eax,[esp+24h]       (event)
		a.Raw(0x8B, 0x78, 0x10);                          // mov edi,[eax+10h]       (the record)
		EmitGroupPtr(a, 3, SaveLoadedVa);                 // ebx = &saveLoaded (the values follow at +4)
		a.Raw(0xC7, 0x03, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [ebx],0
		a.Raw(0x83, 0xEC, 0x70);                          // sub esp,70h  (name object +0, child +10h, text +58h, type +60h, value +64h)
		a.Raw(0x33, 0xF6);                                // xor esi,esi
		a.Mark("value");
		EmitSaveName(a, 0x00, 0x58);
		a.Raw(0x8B, 0xC4);                                // mov eax,esp             (&name)
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi             (the record)
		a.Raw(0x50);                                      // push eax
		a.Call(EsfHas);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J32("done", 0x0F, 0x84);                        // je done
		a.Raw(0x8B, 0xC4);                                // mov eax,esp
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]       (child)
		a.Raw(0x50);                                      // push eax                (&name)
		a.Raw(0x57);                                      // push edi                (the record)
		a.Call(EsfChildRead);
		a.Raw(0xC7, 0x44, 0x24, 0x60, 0xFF, 0xFF, 0xFF, 0xFF);   // mov dword ptr [esp+60h],-1
		a.Raw(0xC7, 0x44, 0x24, 0x64, 0x00, 0x00, 0x00, 0x00);   // mov dword ptr [esp+64h],0
		a.Raw(0x8D, 0x44, 0x24, 0x60);                    // lea eax,[esp+60h]
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]
		a.Raw(0x50);                                      // push eax
		a.Call(EsfGetType);
		a.Raw(0x83, 0x7C, 0x24, 0x60, 0x01);              // cmp dword ptr [esp+60h],1 (number)
		a.J8(0x75, "close");                              // jne close
		a.Raw(0x8D, 0x44, 0x24, 0x64);                    // lea eax,[esp+64h]
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]
		a.Raw(0x50);                                      // push eax
		a.Call(EsfGetInt);
		a.Mark("close");
		a.Raw(0x8D, 0x4C, 0x24, 0x10);                    // lea ecx,[esp+10h]
		a.Call(EsfChildEnd);
		a.Raw(0x83, 0x7C, 0x24, 0x60, 0x01);              // cmp dword ptr [esp+60h],1
		a.J8(0x75, "done");                               // jne done
		a.Raw(0x8B, 0x44, 0x24, 0x64);                    // mov eax,[esp+64h]
		a.Raw(0x89, 0x44, 0xB3, 0x04);                    // mov [ebx+esi*4+4],eax
		a.Raw(0x46);                                      // inc esi
		a.Raw(0x83, 0xFE, (byte)SaveValues);              // cmp esi,SaveValues
		a.J32("value", 0x0F, 0x82);                       // jb value
		a.Raw(0x83, 0x7B, 0x10, (byte)SaveVersion);       // cmp dword ptr [ebx+10h],SaveVersion (value 3)
		a.J8(0x75, "done");                               // jne done
		a.Raw(0xC7, 0x03, 0x01, 0x00, 0x00, 0x00);        // mov dword ptr [ebx],1
		a.Mark("done");
		a.Raw(0x83, 0xC4, 0x70);                          // add esp,70h
		a.Raw(0x61);                                      // popad
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitGroupRestore(Asm a, uint factionAt, uint log) {
		// thiscall(world) -> al = 1 when the group came back from the save (LoadGroup at this load), keeps
		// ebx/esi/edi/ebp. Only when the saved lead is the current faction [world+2Ch] (it is for every save:
		// GroupStart runs before the transition's autosave and members[0] stays the lead until the next group start)
		// and the counts fit; else al = 0 and GroupTick rebuilds the group with GroupBuild. Brings back the members,
		// who had ended or asked to end early, and played[] (the picker skips them for the rest of the round), with
		// no war test: members who went to war during the shared turn stay in it.
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x57);                                      // push edi
		a.Raw(0x55);                                      // push ebp
		a.Raw(0x8B, 0xE9);                                // mov ebp,ecx             (world)
		EmitGroupPtr(a, 7, SaveLoadedVa);                 // edi = &saveLoaded (the values at +4)
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x39, 0x07);                                // cmp [edi],eax
		a.J8(0x74, "no");                                 // je no
		a.Raw(0x89, 0x07);                                // mov [edi],eax           (used once)
		a.Raw(0x83, 0x7F, 0x04, 0x08);                    // cmp dword ptr [edi+4],8 (count)
		a.J8(0x77, "no");                                 // ja no
		a.Raw(0x83, 0x7F, 0x14, 0x08);                    // cmp dword ptr [edi+14h],8 (played count)
		a.J8(0x77, "no");                                 // ja no
		a.Raw(0x8B, 0x47, 0x38);                          // mov eax,[edi+38h]       (members[0])
		a.Call(factionAt);
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "no");                                 // je no
		a.Raw(0x3B, 0x45, 0x2C);                          // cmp eax,[ebp+2Ch]
		a.J8(0x75, "no");                                 // jne no
		EmitGroupPtr(a, 3, GroupVa);                      // ebx = &group
		a.Raw(0x33, 0xF6);                                // xor esi,esi
		a.Mark("copy");
		a.Raw(0x8B, 0x44, 0xB7, 0x04);                    // mov eax,[edi+esi*4+4]
		a.Raw(0x83, 0xFE, 0x03);                          // cmp esi,3
		a.J8(0x75, "notver");                             // jne notver
		a.Raw(0x33, 0xC0);                                // xor eax,eax             (+0Ch: the mover-owner scratch)
		a.Mark("notver");
		a.Raw(0x83, 0xFE, 0x05);                          // cmp esi,5
		a.J8(0x72, "put");                                // jb put                  (not a faction)
		a.Call(factionAt);
		a.Mark("put");
		a.Raw(0x89, 0x04, 0xB3);                          // mov [ebx+esi*4],eax
		a.Raw(0x46);                                      // inc esi
		a.Raw(0x83, 0xFE, (byte)SaveValues);              // cmp esi,SaveValues
		a.J8(0x72, "copy");                               // jb copy
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x89, 0x43, (byte)(PfCountVa - GroupVa));   // mov [ebx+60h],eax       (pfCount, as GroupBuild)
		EmitGroupPtr(a, 2, LogCloseWaitVa);               // edx = &closeWaitLogged
		a.Raw(0x89, 0x02);                                // mov [edx],eax
		a.Raw(0xFF, 0x33);                                // push dword ptr [ebx]    (count)
		a.Raw(0xFF, 0x73, 0x34);                          // push dword ptr [ebx+34h] (lead)
		CallLog(a, log, LogLoadGroup);
		a.Raw(0xB0, 0x01);                                // mov al,1
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("no");
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Mark("out");
		a.Raw(0x5D);                                      // pop ebp
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitAttackRecord(Asm a) {
		// replaces "call FUN_1085a820" (the attack planner, thiscall(movement; &order context, target, 0, 0) ret 10h) at
		// 10883846 in CCQ_CHARACTER_ATTACK FUN_10883780, after AttackAllowed passed; ebx = campaign. Notes in
		// AttackTableVa the attacker ([context], the mover the planner uses), the target, the turn and where the target
		// stands now (FUN_106e5630), for AttackCheck. An attacker's newer order replaces its entry; else the next slot.
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad                  (&context at +24h, target at +28h)
		a.Raw(0x8B, 0x44, 0x24, 0x24);                    // mov eax,[esp+24h]
		a.Raw(0x8B, 0x30);                                // mov esi,[eax]           (attacker)
		a.Raw(0x8B, 0x7C, 0x24, 0x28);                    // mov edi,[esp+28h]       (target)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "out");                                // je out
		a.Raw(0x85, 0xFF);                                // test edi,edi
		a.J8(0x74, "out");                                // je out
		EmitGroupPtr(a, 2, AttackTableVa);                // edx = &attackTable
		a.Raw(0x33, 0xC9);                                // xor ecx,ecx
		a.Mark("find");
		a.Raw(0x8D, 0x04, 0x49);                          // lea eax,[ecx+ecx*2]
		a.Raw(0x8D, 0x44, 0xC2, 0x04);                    // lea eax,[edx+eax*8+4]   (entry ecx)
		a.Raw(0x39, 0x30);                                // cmp [eax],esi
		a.J8(0x74, "have");                               // je have                 (this attacker's earlier order)
		a.Raw(0x41);                                      // inc ecx
		a.Raw(0x83, 0xF9, (byte)AttackSlots);             // cmp ecx,AttackSlots
		a.J8(0x72, "find");                               // jb find
		a.Raw(0x8B, 0x0A);                                // mov ecx,[edx]           (next slot)
		a.Raw(0x8D, 0x41, 0x01);                          // lea eax,[ecx+1]
		a.Raw(0x83, 0xE0, (byte)(AttackSlots - 1));       // and eax,AttackSlots-1
		a.Raw(0x89, 0x02);                                // mov [edx],eax
		a.Raw(0x8D, 0x04, 0x49);                          // lea eax,[ecx+ecx*2]
		a.Raw(0x8D, 0x44, 0xC2, 0x04);                    // lea eax,[edx+eax*8+4]
		a.Mark("have");
		a.Raw(0x89, 0x30);                                // mov [eax],esi
		a.Raw(0x89, 0x78, 0x04);                          // mov [eax+4],edi
		a.Raw(0x8B, 0x8B, 0xA0, 0x14, 0x00, 0x00);        // mov ecx,[ebx+14A0h]     (turn counter object)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "turn");                               // jz turn
		a.Raw(0x8B, 0x49, 0x08);                          // mov ecx,[ecx+8]
		a.Mark("turn");
		a.Raw(0x89, 0x48, 0x08);                          // mov [eax+8],ecx
		a.Raw(0x8B, 0xE8);                                // mov ebp,eax             (entry)
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Call(CharPosition);
		a.Raw(0x8B, 0x08);                                // mov ecx,[eax]
		a.Raw(0x89, 0x4D, 0x0C);                          // mov [ebp+0Ch],ecx
		a.Raw(0x8B, 0x48, 0x04);                          // mov ecx,[eax+4]
		a.Raw(0x89, 0x4D, 0x10);                          // mov [ebp+10h],ecx
		a.Raw(0x8B, 0x48, 0x08);                          // mov ecx,[eax+8]
		a.Raw(0x89, 0x4D, 0x14);                          // mov [ebp+14h],ecx
		a.Mark("out");
		a.Raw(0x61);                                      // popad
		a.Jmp(AttackPlan);
		a.Resolve();
		return at;
	}

	static uint EmitAttackCheck(Asm a, uint log) {
		// replaces "call FUN_106e9f60" (start a battle, thiscall(campaign; attacker, target, flag, 0, 0, 0, 0, 0)
		// ret 20h, result unused) at 108A7986 in FUN_108a7920, the attack order's last step (vtable 11621CDC slot
		// +1Ch), which runs when the attacker reaches the end of its path and starts the battle with the target wherever
		// it stands now. Vanilla needs no more: the target can't move during the attacker's turn. In a shared turn a
		// human target can (members at war), so: if AttackRecord noted this attacker and target in this same turn and
		// the target has moved since, no battle (the attacker stops there; its player can order the attack again).
		// Orders from an earlier turn (an attack over several turns) or from before a load have no entry: vanilla. The
		// entry is used once.
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad                  (attacker at +24h, target at +28h)
		a.Raw(0x8B, 0x74, 0x24, 0x24);                    // mov esi,[esp+24h]
		a.Raw(0x8B, 0x7C, 0x24, 0x28);                    // mov edi,[esp+28h]
		a.Raw(0x8B, 0xD9);                                // mov ebx,ecx             (campaign)
		EmitGroupPtr(a, 2, AttackTableVa + 4);            // edx = &entries
		a.Raw(0x33, 0xC9);                                // xor ecx,ecx
		a.Mark("find");
		a.Raw(0x8D, 0x04, 0x49);                          // lea eax,[ecx+ecx*2]
		a.Raw(0x8D, 0x2C, 0xC2);                          // lea ebp,[edx+eax*8]     (entry ecx)
		a.Raw(0x39, 0x75, 0x00);                          // cmp [ebp+0],esi
		a.J8(0x75, "next");                               // jne next
		a.Raw(0x39, 0x7D, 0x04);                          // cmp [ebp+4],edi
		a.J8(0x74, "found");                              // je found
		a.Mark("next");
		a.Raw(0x41);                                      // inc ecx
		a.Raw(0x83, 0xF9, (byte)AttackSlots);             // cmp ecx,AttackSlots
		a.J8(0x72, "find");                               // jb find
		a.J8(0xEB, "battle");                             // jmp battle              (no entry: vanilla)
		a.Mark("found");
		a.Raw(0xC7, 0x45, 0x00, 0x00, 0x00, 0x00, 0x00);  // mov dword ptr [ebp+0],0 (used once)
		a.Raw(0x8B, 0x83, 0xA0, 0x14, 0x00, 0x00);        // mov eax,[ebx+14A0h]     (turn counter object)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "turn");                               // jz turn
		a.Raw(0x8B, 0x40, 0x08);                          // mov eax,[eax+8]
		a.Mark("turn");
		a.Raw(0x3B, 0x45, 0x08);                          // cmp eax,[ebp+8]
		a.J8(0x75, "battle");                             // jne battle              (ordered in an earlier turn)
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Call(CharPosition);
		a.Raw(0x8B, 0x08);                                // mov ecx,[eax]
		a.Raw(0x3B, 0x4D, 0x0C);                          // cmp ecx,[ebp+0Ch]
		a.J8(0x75, "moved");                              // jne moved
		a.Raw(0x8B, 0x48, 0x04);                          // mov ecx,[eax+4]
		a.Raw(0x3B, 0x4D, 0x10);                          // cmp ecx,[ebp+10h]
		a.J8(0x75, "moved");                              // jne moved
		a.Raw(0x8B, 0x48, 0x08);                          // mov ecx,[eax+8]
		a.Raw(0x3B, 0x4D, 0x14);                          // cmp ecx,[ebp+14h]
		a.J8(0x74, "battle");                             // je battle               (it stands where it stood)
		a.Mark("moved");
		a.Raw(0x8D, 0x8F, 0x54, 0x02, 0x00, 0x00);        // lea ecx,[edi+254h]
		a.Call(OwnerOf);
		a.Raw(0x50);                                      // push eax                (target's faction)
		a.Raw(0x8D, 0x8E, 0x54, 0x02, 0x00, 0x00);        // lea ecx,[esi+254h]
		a.Call(OwnerOf);
		a.Raw(0x50);                                      // push eax                (attacker's faction)
		CallLog(a, log, LogAttackMoved);
		a.Raw(0x61);                                      // popad
		a.Raw(0xC2, 0x20, 0x00);                          // ret 20h                 (no battle)
		a.Mark("battle");
		a.Raw(0x61);                                      // popad
		a.Jmp(BattleStart);
		a.Resolve();
		return at;
	}

	static uint EmitCampaignReset(Asm a, uint zeroGroup, byte baseModRm, byte value, uint resume, uint log, int logId) {
		// replaces "mov word ptr [reg+151Ch],value" (9 bytes) in a campaign constructor: clear the group, redo it
		uint at = a.Here;
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, logId);
		a.Call(zeroGroup);
		a.Raw(0x66, 0xC7, baseModRm, 0x1C, 0x15, 0x00, 0x00, value, 0x00);   // mov word ptr [edi/ebx+151Ch],value
		a.Jmp(resume);
		return at;
	}

	static uint EmitPfMoverAct(Asm a, uint pfFocusFor) {
		// replaces the success exit of FUN_106aa660 (106AA710 "pop ebx / pop edi / mov al,1 / pop esi / ret 4", 8 bytes):
		// the mover passed every test (not busy, no other mover's stop shown, its turn, no pending battle or action, MP
		// timer idle, FUN_106bc690), so the order of one of the 27 character commands runs now (all reach it through the
		// movement manager's wrappers): set the pathfinder up for the mover's owner first.
		// entry: esi = campaign, edi = mover ([edi+1Ch] = its character); [esp] = saved ebx, [esp+4] = saved edi,
		// [esp+8] = saved esi, [esp+0Ch] = return address, [esp+10h] = mover; ecx, edx and the flags are dead; eax is
		// kept (vanilla returns FUN_106bc690's eax with al = 1)
		uint at = a.Here;
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8B, 0x4F, 0x1C);                          // mov ecx,[edi+1Ch]       (character)
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);                                  // eax = its faction
		a.Raw(0x50);                                      // push eax                (f)
		a.Raw(0x56);                                      // push esi                (campaign)
		a.Call(pfFocusFor);
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x5B);                                      // pop ebx                 (replaced instructions)
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0xB0, 0x01);                                // mov al,1
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		return at;
	}

	static uint EmitPfFocusMover(Asm a, uint pfFocusFor) {
		// replaces "call FUN_107f94f0" at 10900082, where update_positions re-issues the move of the mover whose stop was
		// shown ([movement+94h]) through FUN_10903190 with the bypass flag (no FUN_106aa660): set the pathfinder up for
		// its owner first. entry: ecx = pathfinder, esi = that mover's character, ebx = movement manager; three
		// arguments pushed
		uint at = a.Here;
		a.Raw(0x50);                                      // push eax
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x52);                                      // push edx
		a.Raw(0x8D, 0x8E, 0x54, 0x02, 0x00, 0x00);        // lea ecx,[esi+254h]
		a.Call(OwnerOf);                                  // eax = its faction
		a.Raw(0x50);                                      // push eax                (f)
		a.Raw(0x8B, 0x43, 0x18);                          // mov eax,[ebx+18h]
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(pfFocusFor);
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x58);                                      // pop eax
		a.Jmp(0x107F94F0);
		return at;
	}

	static uint EmitPfMovesDone(Asm a, uint pfRestore) {
		// replaces "call FUN_107df8c0" (ecx = pathfinder) at the end of update_positions FUN_108ffb80 (10900137): back to
		// the turn's set-up first, so it runs as in vanilla. entry: ecx = pathfinder, ebx = movement manager
		uint at = a.Here;
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8B, 0x43, 0x18);                          // mov eax,[ebx+18h]
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(pfRestore);
		a.Raw(0x58);                                      // pop eax
		a.Jmp(0x107DF8C0);
		return at;
	}

	static uint EmitPfCommandDone(Asm a, uint pfRestore) {
		// replaces "call eax / add esp,8" at 108D2699 in the command executor FUN_108d2550 (thiscall(buffer; campaign)),
		// the call of each record's handler(reader, campaign): afterwards back to the turn's set-up.
		// entry: eax = handler, [esp] = reader, [esp+4] = campaign; after the add FUN_108d2550's campaign argument is at
		// [esp+3Ch] again; 108D269E reloads ebp and eax
		uint at = a.Here;
		a.Raw(0xFF, 0xD0);                                // call eax                (replaced instructions)
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0xFF, 0x74, 0x24, 0x3C);                    // push dword ptr [esp+3Ch] (campaign)
		a.Call(pfRestore);
		a.Jmp(0x108D269E);
		return at;
	}

	static uint EmitPfCommands(Asm a, uint pfRestore) {
		// replaces "call FUN_108d2550" at 10703F27 in the campaign tick (ecx = command buffer, campaign pushed): back to
		// the turn's set-up before the tick's commands run, for anything the previous tick's loop left (a pending action
		// applied there)
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (campaign)
		a.Call(pfRestore);
		a.Jmp(0x108D2550);
		return at;
	}

	static uint EmitUiPfEnter(Asm a, uint groupSlot) {
		// UI side: the order cursor FUN_1118c060 and the path preview search paths for the local faction's characters.
		// On a member's PC while its group plays, set the pathfinder up for the local faction around the hover and the
		// click order (UiPfWrap); UiPfLeave puts back exactly the kept set-up found here and rebuilds it with
		// FUN_107fd150, the engine's own restore (the vanilla UI does the same round trip in FUN_107da3c0). The lead's PC,
		// solo and AI turns change nothing. Only the outermost of nested wrappers switches.
		// No arguments; keeps every register and the flags.
		uint at = a.Here;
		a.Raw(0x9C);                                      // pushfd
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xFF, 0x83); D32(a, UiPfDepthVa - popAt);   // inc dword ptr [ebx+depth]
		a.Raw(0x83, 0xBB); D32(a, UiPfDepthVa - popAt); a.Raw(0x01);   // cmp dword ptr [ebx+depth],1
		a.J32("out", 0x0F, 0x85);                         // jne out                 (nested)
		a.Raw(0x8B, 0x83); D32(a, UiGlobal - popAt);      // mov eax,[ebx+ui]
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("out", 0x0F, 0x84);                         // jz out
		a.Raw(0x8B, 0x80, 0x98, 0x0A, 0x00, 0x00);        // mov eax,[eax+0A98h]     (model)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("out", 0x0F, 0x84);                         // jz out
		a.Raw(0x8B, 0x70, 0x08);                          // mov esi,[eax+8]         (campaign)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J32("out", 0x0F, 0x84);                         // jz out
		a.Raw(0x8B, 0xA8, 0xB0, 0x00, 0x00, 0x00);        // mov ebp,[eax+0B0h]      (local faction)
		a.Raw(0x8B, 0xBE, 0xA4, 0x0E, 0x00, 0x00);        // mov edi,[esi+0EA4h]     (pathfinder)
		a.Raw(0x85, 0xFF);                                // test edi,edi
		a.J32("out", 0x0F, 0x84);                         // jz out
		a.Raw(0x8B, 0xCD);                                // mov ecx,ebp
		a.Call(groupSlot);                                // eax = local's slot or -1, edx = &group
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("out", 0x0F, 0x8E);                         // jle out                 (not a member other than the lead)
		a.Raw(0x8B, 0x8E, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[esi+1498h]     (world)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J32("out", 0x0F, 0x84);                         // jz out
		a.Raw(0x8B, 0x49, 0x2C);                          // mov ecx,[ecx+2Ch]
		a.Raw(0x3B, 0x4A, 0x34);                          // cmp ecx,[edx+34h]       (the lead is current)
		a.J32("out", 0x0F, 0x85);                         // jne out
		a.Raw(0x8B, 0x8E, 0xA4, 0x14, 0x00, 0x00);        // mov ecx,[esi+14A4h]     (pending battle)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "nobattle");                           // jz nobattle
		a.Raw(0x8B, 0x49, 0x50);                          // mov ecx,[ecx+50h]       (its state)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "nobattle");                           // jz nobattle
		a.Raw(0x83, 0xF9, 0x0A);                          // cmp ecx,0Ah
		a.J8(0x75, "out");                                // jne out                 (a battle is running: no orders)
		a.Mark("nobattle");
		a.Raw(0x39, 0xAF, 0xF4, 0x01, 0x00, 0x00);        // cmp [edi+1F4h],ebp
		a.J8(0x75, "set");                                // jne set
		a.Raw(0x83, 0xBF, 0x08, 0x02, 0x00, 0x00, 0x00);  // cmp dword ptr [edi+208h],0
		a.J8(0x74, "out");                                // je out                  (already set up for it)
		a.Mark("set");
		a.Raw(0x89, 0xBB); D32(a, UiPfSavedVa - popAt);   // mov [ebx+savedPf],edi
		a.Raw(0x8B, 0x87, 0xF4, 0x01, 0x00, 0x00);        // mov eax,[edi+1F4h]
		a.Raw(0x89, 0x83); D32(a, UiPfSavedVa + 4 - popAt);   // mov [ebx+savedFaction],eax
		a.Raw(0x8B, 0x87, 0x08, 0x02, 0x00, 0x00);        // mov eax,[edi+208h]
		a.Raw(0x89, 0x83); D32(a, UiPfSavedVa + 8 - popAt);   // mov [ebx+savedMode],eax
		a.Raw(0xC7, 0x83); D32(a, UiPfActiveVa - popAt); D32(a, 1);   // mov dword ptr [ebx+active],1
		a.Raw(0x6A, 0x01);                                // push 1                  (keep: the engine's restores come back to it)
		a.Raw(0x55);                                      // push ebp                (local faction)
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Call(PfSetFaction);                             // FUN_10778490(pf; local, 1)
		a.Mark("out");
		a.Raw(0x61);                                      // popad
		a.Raw(0x9D);                                      // popfd
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitUiPfLeave(Asm a) {
		// the outermost wrapper's end: if UiPfEnter switched, put the kept faction and mode back and rebuild that set-up
		// (FUN_107fd150); no arguments, keeps every register and the flags
		uint at = a.Here;
		a.Raw(0x9C);                                      // pushfd
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0x83, 0xBB); D32(a, UiPfDepthVa - popAt); a.Raw(0x01);   // cmp dword ptr [ebx+depth],1
		a.J8(0x75, "dec");                                // jne dec                 (nested)
		a.Raw(0x83, 0xBB); D32(a, UiPfActiveVa - popAt); a.Raw(0x00);  // cmp dword ptr [ebx+active],0
		a.J8(0x74, "dec");                                // je dec
		a.Raw(0xC7, 0x83); D32(a, UiPfActiveVa - popAt); D32(a, 0);    // mov dword ptr [ebx+active],0
		a.Raw(0x8B, 0x8B); D32(a, UiPfSavedVa - popAt);   // mov ecx,[ebx+savedPf]
		a.Raw(0x8B, 0x83); D32(a, UiPfSavedVa + 4 - popAt);   // mov eax,[ebx+savedFaction]
		a.Raw(0x89, 0x81, 0xF4, 0x01, 0x00, 0x00);        // mov [ecx+1F4h],eax
		a.Raw(0x8B, 0x83); D32(a, UiPfSavedVa + 8 - popAt);   // mov eax,[ebx+savedMode]
		a.Raw(0x89, 0x81, 0x08, 0x02, 0x00, 0x00);        // mov [ecx+208h],eax
		a.Call(PfRestoreKept);                            // FUN_107fd150(pf)
		a.Mark("dec");
		a.Raw(0x83, 0xBB); D32(a, UiPfDepthVa - popAt); a.Raw(0x00);   // cmp dword ptr [ebx+depth],0
		a.J8(0x74, "out");                                // je out                  (cleared by a campaign reset)
		a.Raw(0xFF, 0x8B); D32(a, UiPfDepthVa - popAt);   // dec dword ptr [ebx+depth]
		a.Mark("out");
		a.Raw(0x61);                                      // popad
		a.Raw(0x9D);                                      // popfd
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitUiPfWrap(Asm a, uint target, int args, uint enter, uint leave) {
		// replaces "call target" (thiscall(ui; args dwords) ret 4*args) at its call sites: UiPfEnter, the target with
		// the same ecx and arguments, UiPfLeave (keeps the result in eax)
		uint at = a.Here;
		a.Call(enter);
		for (int i = 0; i < args; i++)
			a.Raw(0xFF, 0x74, 0x24, (byte)(4 * args));    // push dword ptr [esp+4*args] (the arguments, last first)
		a.Call(target);
		a.Call(leave);
		a.Raw(0xC2, (byte)(4 * args), 0x00);              // ret 4*args
		return at;
	}

	static uint EmitUiPfRecruitPanel(Asm a, uint enter, uint leave) {
		// FUN_112517a0 (thiscall(panel; 1 dword) ret 4; a vtable method, slot at 11729A68: the recruitment panel's
		// refresh) asks its provider [this+14h] (vt+0..+1Ch) for the recruitable units and the queue, then fills the cards
		// (FUN_112478a0 -> FUN_112441e0 -> FUN_11248200: reason [card+74h] = info+30h, active = info+18h == 0; reason 8 of
		// the table FUN_11241150 is `no_path_available`). Those path tests ran with the lead's kept set-up on a member's
		// PC, so its cards said the units couldn't reach a general outside the settlement (the turn start, fix 5, places
		// them correctly). The whole refresh now runs between UiPfEnter and UiPfLeave. Its first instruction is relocated
		// (mov eax,[11AE5974]), so 112517A5, right after it, jumps here and the rest runs as a sub-call.
		// entry: ecx = panel, eax = [11AE5974], [esp] = return address, [esp+4] = the argument; flags dead (sub esp
		// follows)
		uint body = a.Here;
		a.Raw(0x83, 0xEC, 0x4C);                          // sub esp,4Ch             (replaced)
		a.Raw(0x53);                                      // push ebx                (replaced)
		a.Raw(0x55);                                      // push ebp                (replaced)
		a.Raw(0x8B, 0xE9);                                // mov ebp,ecx             (replaced)
		a.Raw(0x56);                                      // push esi                (replaced)
		a.Jmp(0x112517AD);                                // the rest of FUN_112517a0 (its ret 4 comes back below)
		uint at = a.Here;
		a.Call(enter);                                    // keeps every register and the flags
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (the argument again)
		a.Call(body);
		a.Call(leave);                                    // keeps eax
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		return at;
	}

	static uint EmitMoveLog(Asm a, uint groupAllows, uint log) {
		// replaces "call FUN_10903190" in CCQ_CHARACTER_LOCOMOTE_TO FUN_10883d30 (10883DD0), same contract
		// (thiscall(movement manager; order, destination, 0, 0) ret 10h): runs the order, then for a human owner logs
		// MOVE accepted (with the faction the pathfinder is kept for) or refused with the tests of FUN_106aa660 that
		// fail now (1 busy FUN_106d1d70, 2 another mover's stop shown, 4 not its turn (GroupAllows, as MoverAllowed),
		// 8 pending battle, 16 pending action, 32 MP timer FUN_105b4160, 64 FUN_106bc690; 0 = the gate passed, so no
		// path or the order failed after it). The mover is [order+18h], else [order] (what FUN_10903190 hands
		// FUN_106aa660), taken before the call. Only reads state: the simulation is unchanged.
		// entry: ecx = movement manager ([campaign+149Ch]); the handler reads only its own stack afterwards
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x57);                                      // push edi
		a.Raw(0x55);                                      // push ebp                (the arguments at [esp+14h])
		a.Raw(0x8B, 0xE9);                                // mov ebp,ecx             (movement manager)
		a.Raw(0x8B, 0x7C, 0x24, 0x14);                    // mov edi,[esp+14h]       (order)
		a.Raw(0x8B, 0x77, 0x18);                          // mov esi,[edi+18h]
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x75, "have");                               // jnz have
		a.Raw(0x8B, 0x37);                                // mov esi,[edi]
		a.Mark("have");                                   // esi = mover
		a.Raw(0x8B, 0x4E, 0x1C);                          // mov ecx,[esi+1Ch]       (character)
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);
		a.Raw(0x8B, 0xF8);                                // mov edi,eax             (owner)
		for (int i = 0; i < 4; i++)
			a.Raw(0xFF, 0x74, 0x24, 0x20);                // push dword ptr [esp+20h] (the four arguments)
		a.Raw(0x8B, 0xCD);                                // mov ecx,ebp
		a.Call(0x10903190);
		a.Raw(0x50);                                      // push eax                (the result)
		a.Raw(0x80, 0xBF, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [edi+6A0h],0 (human)
		a.J32("done", 0x0F, 0x84);                        // je done
		a.Raw(0x8B, 0x4D, 0x18);                          // mov ecx,[ebp+18h]
		a.Raw(0x8B, 0x59, 0x08);                          // mov ebx,[ecx+8]         (campaign)
		a.Raw(0x80, 0x3C, 0x24, 0x00);                    // cmp byte ptr [esp],0
		a.J8(0x74, "refused");                            // je refused
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x8B, 0x8B, 0xA4, 0x0E, 0x00, 0x00);        // mov ecx,[ebx+0EA4h]     (pathfinder)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "kept");                               // jz kept
		a.Raw(0x8B, 0x81, 0xF4, 0x01, 0x00, 0x00);        // mov eax,[ecx+1F4h]      (the faction it is kept for)
		a.Mark("kept");
		a.Raw(0x50);                                      // push eax
		a.Raw(0x57);                                      // push edi                (owner)
		CallLog(a, log, LogMoveOk);
		a.J32("done", 0xE9);                              // jmp done
		a.Mark("refused");
		a.Raw(0x6A, 0x00);                                // push 0                  ([esp] = reason bits)
		a.Raw(0x8B, 0x4E, 0x1C);                          // mov ecx,[esi+1Ch]
		a.Call(0x106D1D70);                               // character busy
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "r2");                                 // jz r2
		a.Raw(0x83, 0x0C, 0x24, 0x01);                    // or dword ptr [esp],1
		a.Mark("r2");
		a.Raw(0x8B, 0x8B, 0x9C, 0x14, 0x00, 0x00);        // mov ecx,[ebx+149Ch]     (movement manager)
		a.Call(0x108B99F0);                               // a mover's stop is shown
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "r3");                                 // jz r3
		a.Raw(0x56);                                      // push esi
		a.Raw(0x8B, 0x8B, 0x9C, 0x14, 0x00, 0x00);        // mov ecx,[ebx+149Ch]
		a.Call(0x108B99E0);                               // is it this mover's
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "r3");                                 // jnz r3
		a.Raw(0x83, 0x0C, 0x24, 0x02);                    // or dword ptr [esp],2
		a.Mark("r3");
		a.Raw(0x8B, 0x8B, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[ebx+1498h]     (world)
		a.Call(CurrentOrProxyFn);
		a.Raw(0x57);                                      // push edi                (f)
		a.Raw(0x50);                                      // push eax                (cur-or-proxy)
		a.Call(groupAllows);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "r4");                                 // jnz r4
		a.Raw(0x83, 0x0C, 0x24, 0x04);                    // or dword ptr [esp],4
		a.Mark("r4");
		a.Raw(0x8B, 0x8B, 0xA4, 0x14, 0x00, 0x00);        // mov ecx,[ebx+14A4h]     (pending battle)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "r5");                                 // jz r5
		a.Raw(0x8B, 0x41, 0x50);                          // mov eax,[ecx+50h]       (its state)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "r5");                                 // jz r5
		a.Raw(0x83, 0xF8, 0x0A);                          // cmp eax,0Ah
		a.J8(0x74, "r5");                                 // je r5
		a.Raw(0x83, 0x0C, 0x24, 0x08);                    // or dword ptr [esp],8
		a.Mark("r5");
		a.Raw(0x8B, 0x8B, 0xA8, 0x14, 0x00, 0x00);        // mov ecx,[ebx+14A8h]     (pending actions)
		a.Call(0x101BE5D0);                               // one is queued
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "r6");                                 // jz r6
		a.Raw(0x83, 0x0C, 0x24, 0x10);                    // or dword ptr [esp],10h
		a.Mark("r6");
		a.Raw(0x8D, 0x8B, 0x50, 0x15, 0x00, 0x00);        // lea ecx,[ebx+1550h]     (MP turn timer)
		a.Call(0x105B4160);                               // it runs
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "r7");                                 // jz r7
		a.Raw(0x83, 0x0C, 0x24, 0x20);                    // or dword ptr [esp],20h
		a.Mark("r7");
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(0x106BC690);                               // mover state allows it
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "r8");                                 // jnz r8
		a.Raw(0x83, 0x0C, 0x24, 0x40);                    // or dword ptr [esp],40h
		a.Mark("r8");
		a.Raw(0xFF, 0x34, 0x24);                          // push dword ptr [esp]    (reason bits)
		a.Raw(0x57);                                      // push edi                (owner)
		CallLog(a, log, LogMoveRefused);
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4               (the bits)
		a.Mark("done");
		a.Raw(0x58);                                      // pop eax                 (the result)
		a.Raw(0x5D);                                      // pop ebp
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC2, 0x10, 0x00);                          // ret 10h
		a.Resolve();
		return at;
	}

	static uint EmitClickLog(Asm a, uint log) {
		// replaces "call FUN_111943e0" (thiscall(cursor; ui, flag) ret 8: sends the cursor's order) at 11129E27 (click
		// order FUN_11129a30) and 1115199D (the cursor the hover stored): logs the order type [cursor+7Ch] (0 = none,
		// the red cross) and the owner of the cursor's character [cursor+78h] (FUN_1118c060 reads its +254h itself).
		// A line of this PC only (UI); then the original call. entry: ecx = cursor, two arguments pushed; the flags are
		// dead at a call
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx             (cursor)
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x8B, 0x4E, 0x78);                          // mov ecx,[esi+78h]       (character)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "noowner");                            // jz noowner
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);
		a.Mark("noowner");
		a.Raw(0x50);                                      // push eax                (owner)
		a.Raw(0xFF, 0x76, 0x7C);                          // push dword ptr [esi+7Ch] (order type)
		CallLog(a, log, LogClick);
		a.Raw(0x61);                                      // popad
		a.Jmp(0x111943E0);
		a.Resolve();
		return at;
	}

	static uint EmitForeignPending(Asm a) {
		// cdecl(campaign, f) -> eax = 1 if the pending actions ([campaign+14A8h], list head at +0, next at node+4) hold
		// another clan's negotiation (vtable 115FCE00, +8 = the faction negotiating) or another clan's popup (the four
		// popup nodes of OwnActionTurn, +8 = the acting character); else 0. Keeps every register but eax.
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x57);                                      // push edi
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x52);                                      // push edx                (campaign at +18h, f at +1Ch)
		EmitGroupPtr(a, 7, 0x115FCE00);                   // edi = the negotiation vtable
		a.Raw(0x8B, 0x74, 0x24, 0x18);                    // mov esi,[esp+18h]       (campaign)
		a.Raw(0x8B, 0xB6, 0xA8, 0x14, 0x00, 0x00);        // mov esi,[esi+14A8h]     (pending actions)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "none");                               // jz none
		a.Raw(0x8B, 0x36);                                // mov esi,[esi]           (head)
		a.Mark("loop");
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "none");                               // jz none
		a.Raw(0x8B, 0x06);                                // mov eax,[esi]           (vtable)
		a.Raw(0x2B, 0xC7);                                // sub eax,edi
		a.J8(0x75, "notneg");                             // jne notneg
		a.Raw(0x8B, 0x46, 0x08);                          // mov eax,[esi+8]         (the faction negotiating)
		a.Raw(0x3B, 0x44, 0x24, 0x1C);                    // cmp eax,[esp+1Ch]
		a.J8(0x75, "found");                              // jne found
		a.J8(0xEB, "next");                               // jmp next
		a.Mark("notneg");
		a.Raw(0x3D); D32(a, 0x115FCFB8u - 0x115FCE00u);   // cmp eax,1B8h            (move options)
		a.J8(0x74, "popup");                              // je popup
		a.Raw(0x3D); D32(a, 0x115FD02Cu - 0x115FCE00u);   // cmp eax,22Ch            (attack on a character)
		a.J8(0x74, "popup");                              // je popup
		a.Raw(0x3D); D32(a, 0x115FD488u - 0x115FCE00u);   // cmp eax,688h            (the character node 115FD488)
		a.J8(0x74, "popup");                              // je popup
		a.Raw(0x3D); D32(a, 0x115FD4F4u - 0x115FCE00u);   // cmp eax,6F4h            (the object node 115FD4F4)
		a.J8(0x75, "next");                               // jne next
		a.Mark("popup");
		a.Raw(0x8B, 0x4E, 0x08);                          // mov ecx,[esi+8]         (the acting character)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "next");                               // jz next
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);                                  // eax = its faction
		a.Raw(0x3B, 0x44, 0x24, 0x1C);                    // cmp eax,[esp+1Ch]
		a.J8(0x75, "found");                              // jne found
		a.Mark("next");
		a.Raw(0x8B, 0x76, 0x04);                          // mov esi,[esi+4]         (next)
		a.J8(0xEB, "loop");                               // jmp loop
		a.Mark("found");
		a.Raw(0xB8, 0x01, 0x00, 0x00, 0x00);              // mov eax,1
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("none");
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Mark("out");
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitOwnActionTurn(Asm a, uint groupSlot, uint groupAllows, uint foreignPending) {
		// Lua IsPlayersTurn FUN_11141720, replaces "cmp eax,[ecx+2Ch] / sete cl / movzx ecx,cl" at 1114174C (9 bytes).
		// It decides whether ToggleDiplomacyPopup opens the diplomacy panel and, in the panel, whether the local player
		// acts as the proposer (in turn) or as the receiver of an offer (out of turn).
		// - The lead of a running group (local == current): vanilla true, but false while another clan's negotiation or
		//   popup is pending (ForeignPending), so its panel answers a member's offer as the receiver.
		// - A group member that hasn't ended (milestone 2): true while nothing is pending, so it can open diplomacy; also
		//   while the head pending action is its own: a node whose activation shows the move-options popup for its
		//   character at +8 (FUN_11153910 opens it on the PC whose clan owns that character): move options 115FCFB8 (a
		//   move into a clan at peace), the attack node 115FD02C (CCQ_CHARACTER_ATTACK on a character: FUN_105f2d60,
		//   fix 4b), its siblings 115FD488 / 115FD4F4 (FUN_105f2f60 / FUN_105f3060, same channel [campaign+3C0h]); or a
		//   negotiation node (vtable 115FCE00, +8 = the faction negotiating) it opened. So the popup's DeclareWar opens
		//   its diplomacy panel and the panel's exit finalises the pending action (fix 4: without the panel the Lua
		//   stops on a nil panel and the pending action blocks everyone).
		// - Without a running group: vanilla (local == current).
		// entry: eax = local faction, ecx = world, esi = ui, edi = Lua state (kept); eax, ecx, edx and the flags are
		// dead (11141755 pushes ecx as the result)
		uint at = a.Here;
		a.Raw(0x3B, 0x41, 0x2C);                          // cmp eax,[ecx+2Ch]       (replaced: local == current)
		a.J32("member", 0x0F, 0x85);                      // jne member
		a.Raw(0x8B, 0xC8);                                // mov ecx,eax             (local)
		a.Call(groupSlot);                                // eax = its slot, edx = &group
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("yes", 0x0F, 0x85);                         // jnz yes                 (not the lead of a group: vanilla)
		a.Raw(0x83, 0x3A, 0x02);                          // cmp dword ptr [edx],2
		a.J32("yes", 0x0F, 0x82);                         // jb yes                  (no group running)
		a.Raw(0x51);                                      // push ecx                (f = local)
		a.Raw(0x8B, 0x86, 0x98, 0x0A, 0x00, 0x00);        // mov eax,[esi+0A98h]     (model)
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(foreignPending);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("no", 0x0F, 0x85);                          // jnz no
		a.J32("yes", 0xE9);                               // jmp yes
		a.Mark("member");
		a.Raw(0x8B, 0xD0);                                // mov edx,eax             (local)
		a.Raw(0x52);                                      // push edx                (f)
		a.Raw(0xFF, 0x71, 0x2C);                          // push dword ptr [ecx+2Ch] (cur)
		a.Call(groupAllows);                              // keeps ecx and edx
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "no");                                 // jz no                   (no group, not a member, or ended)
		a.Raw(0x8B, 0x8E, 0x98, 0x0A, 0x00, 0x00);        // mov ecx,[esi+0A98h]     (model)
		a.Raw(0x8B, 0x49, 0x08);                          // mov ecx,[ecx+8]         (campaign)
		a.Raw(0x8B, 0x89, 0xA8, 0x14, 0x00, 0x00);        // mov ecx,[ecx+14A8h]     (pending actions)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "no");                                 // jz no
		a.Raw(0x8B, 0x09);                                // mov ecx,[ecx]           (head)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "yes");                                // jz yes                  (nothing pending: its turn)
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x05); D32(a, unchecked(0x115FCE00u - popAt));   // add eax, negotiation vtable - popAt
		a.Raw(0x39, 0x01);                                // cmp [ecx],eax
		a.J8(0x75, "notneg");                             // jne notneg
		a.Raw(0x39, 0x51, 0x08);                          // cmp [ecx+8],edx         (its own negotiation)
		a.J8(0x74, "yes");                                // je yes
		a.J8(0xEB, "no");                                 // jmp no
		a.Mark("notneg");
		a.Raw(0x05); D32(a, 0x115FCFB8u - 0x115FCE00u);   // add eax, move-options vtable - negotiation vtable
		a.Raw(0xF7, 0xD8);                                // neg eax
		a.Raw(0x03, 0x01);                                // add eax,[ecx]           (head's vtable - move-options vtable)
		a.J8(0x74, "popup");                              // je popup
		a.Raw(0x83, 0xF8, (byte)(0x115FD02Cu - 0x115FCFB8u));   // cmp eax,74h        (attack on a character)
		a.J8(0x74, "popup");                              // je popup
		a.Raw(0x3D); D32(a, 0x115FD488u - 0x115FCFB8u);   // cmp eax,4D0h            (the character node 115FD488)
		a.J8(0x74, "popup");                              // je popup
		a.Raw(0x3D); D32(a, 0x115FD4F4u - 0x115FCFB8u);   // cmp eax,53Ch            (the object node 115FD4F4)
		a.J8(0x75, "no");                                 // jne no
		a.Mark("popup");
		a.Raw(0x8B, 0x49, 0x08);                          // mov ecx,[ecx+8]         (the acting character)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "no");                                 // jz no
		a.Raw(0x52);                                      // push edx
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);                                  // eax = its faction
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x3B, 0xC2);                                // cmp eax,edx
		a.J8(0x74, "yes");                                // je yes
		a.Mark("no");
		a.Raw(0x33, 0xC9);                                // xor ecx,ecx
		a.Jmp(0x11141755);
		a.Mark("yes");
		a.Raw(0xB9, 0x01, 0x00, 0x00, 0x00);              // mov ecx,1
		a.Jmp(0x11141755);
		a.Resolve();
		return at;
	}

	static uint EmitLocalAsCurIfAllowed(Asm a, uint groupAllows) {
		// replaces "call FUN_11124580" (ecx = ui -> eax = local faction; changes only eax) at 11142F57 (Lua
		// MultiplayerPlayerOutOfTurn FUN_11142f20: true shows "player X saved" instead of the save dialog) and 1122FF86
		// (HudClanButton vt+8 FUN_1122ff50: the Clan button is inactive in MP unless local == current). Both compare
		// the result with esi = current next. Returns current when GroupAllows(current, local), so a member that hasn't
		// ended counts as in turn; else local, or ~current for the lead after its own end (local == current).
		uint at = a.Here;
		a.Call(0x11124580);                               // eax = local (replaced call)
		a.Raw(0x52);                                      // push edx
		a.Raw(0x50);                                      // push eax                (f = local)
		a.Raw(0x56);                                      // push esi                (cur)
		a.Call(groupAllows);                              // keeps ecx and edx
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.Raw(0x58);                                      // pop eax                 (cur)
		a.Raw(0x5A);                                      // pop edx                 (local)
		a.J8(0x75, "out");                                // jnz out
		a.Raw(0x8B, 0xC2);                                // mov eax,edx
		a.Raw(0x3B, 0xC6);                                // cmp eax,esi
		a.J8(0x75, "out");                                // jne out
		a.Raw(0xF7, 0xD0);                                // not eax                 (the lead has ended: any other value)
		a.Mark("out");
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitUnitPanelZF(Asm a, uint groupAllows) {
		// replaces "mov eax,[eax+2Ch] / cmp eax,[ecx+0B0h]" (9 bytes, a jne to the inactive branch follows) at 112457A9,
		// 1124ED39, 1125458A, 11254772: the unit panels' disband / scupper buttons FUN_11245750, FUN_1124ece0,
		// FUN_11254520, FUN_11254710, active only when local == current. entry: eax = world, ecx = model.
		// Exit: ZF = GroupAllows(cur, local), eax = cur as before, every other register kept.
		uint at = a.Here;
		a.Raw(0x8B, 0x40, 0x2C);                          // mov eax,[eax+2Ch]       (cur)
		a.Raw(0xFF, 0xB1, 0xB0, 0x00, 0x00, 0x00);        // push dword ptr [ecx+0B0h] (f = local)
		a.Raw(0x50);                                      // push eax                (cur)
		a.Call(groupAllows);                              // keeps ecx and edx
		a.Raw(0x83, 0xF8, 0x01);                          // cmp eax,1               (ZF = allowed)
		a.Raw(0x58);                                      // pop eax                 (cur)
		a.Raw(0x8D, 0x64, 0x24, 0x04);                    // lea esp,[esp+4]         (keeps the flags)
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitDemandActor(Asm a, uint groupAllows) {
		// replaces "call FUN_1070b4a0" (thiscall(world) -> current-or-proxy) at 11143D98 (Lua PreBattleDemandSurrender
		// FUN_11143d60) and 111798B2 (the pre-battle panel's DemandSurrender FUN_11179890): the faction that
		// CCQ_DEMAND_SURRENDER names. A group member that hasn't ended names itself (the local faction) instead of the
		// lead. entry: ecx = world; after the call eax is stored, ecx is reloaded, edx is dead (both sites).
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx                (world)
		a.Call(CurrentOrProxyFn);                         // eax = current-or-proxy (replaced call)
		a.Raw(0x59);                                      // pop ecx
		EmitGroupPtr(a, 2, UiGlobal);                     // edx = &ui
		a.Raw(0x8B, 0x12);                                // mov edx,[edx]
		a.Raw(0x8B, 0x92, 0x98, 0x0A, 0x00, 0x00);        // mov edx,[edx+0A98h]     (model)
		a.Raw(0x8B, 0x92, 0xB0, 0x00, 0x00, 0x00);        // mov edx,[edx+0B0h]      (local)
		a.Raw(0x3B, 0xD0);                                // cmp edx,eax
		a.J8(0x74, "out");                                // je out
		a.Raw(0x50);                                      // push eax                (current-or-proxy)
		a.Raw(0x52);                                      // push edx                (f = local)
		a.Raw(0xFF, 0x71, 0x2C);                          // push dword ptr [ecx+2Ch] (cur)
		a.Call(groupAllows);                              // keeps ecx and edx
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.Raw(0x58);                                      // pop eax                 (current-or-proxy)
		a.J8(0x74, "out");                                // jz out
		a.Raw(0x8B, 0xC2);                                // mov eax,edx             (local)
		a.Mark("out");
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitNegotiationGate(Asm a, uint groupSlot, uint foreignPending, uint negotiationLog, uint log) {
		// replaces "call FUN_10692d90" (thiscall(campaign; f1, f2, 1) ret 0Ch, result unused) at 10884342 in
		// CCQ_DIPLOMACY_BEGIN_NEGOTIATION FUN_108842f0, in front of NegotiationLog. There is one negotiation slot
		// ([campaign+14D4h]) and the PROPOSE / ACCEPT / DECLINE commands act on it without naming a sender, so in a
		// running group (the current faction is the lead of 2+ members) a negotiation is dropped while another clan's
		// negotiation or popup is pending (two members picking a clan within the network delay; the UI side,
		// UiNegotiationGate, stops it otherwise). Simulation: every PC decides the same.
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx                (campaign; f1 at +0Ch, f2 at +10h)
		a.Raw(0x8B, 0x81, 0x98, 0x14, 0x00, 0x00);        // mov eax,[ecx+1498h]     (world)
		a.Raw(0x8B, 0x48, 0x2C);                          // mov ecx,[eax+2Ch]       (cur)
		a.Call(groupSlot);                                // eax = cur's slot, edx = &group
		a.Raw(0x59);                                      // pop ecx                 (campaign)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x75, "pass");                               // jnz pass                (cur isn't the lead of a group)
		a.Raw(0x83, 0x3A, 0x02);                          // cmp dword ptr [edx],2
		a.J8(0x72, "pass");                               // jb pass                 (no group running)
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (f1)
		a.Raw(0x51);                                      // push ecx                (campaign)
		a.Call(foreignPending);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "pass");                               // jz pass
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (f2)
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (f1)
		CallLog(a, log, LogNegotiationDropped);
		a.Raw(0xC2, 0x0C, 0x00);                          // ret 0Ch                 (dropped)
		a.Mark("pass");
		a.Jmp(negotiationLog);
		a.Resolve();
		return at;
	}

	static uint EmitUiNegotiationGate(Asm a, uint groupSlot, uint foreignPending, uint log) {
		// replaces "call FUN_1110e1b0" (thiscall(new object; L, 1) ret 8, the constructor that sends
		// CCQ_DIPLOMACY_BEGIN_NEGOTIATION) at 11152230 in the Lua UIDiplomacyNegotiation(player, opponent) FUN_11152200,
		// which diplomacy_panel Initialise(faction) calls when a clan is picked. The panel can be open (relations list,
		// no negotiation) while another member's negotiation or popup becomes pending; picking a clan then would act on
		// that negotiation. In a running group with another clan's negotiation or popup pending (as this PC sees it),
		// the object isn't built and the binding returns nil: no command is sent and the panel's offers do nothing
		// (its Lua stops on the nil negotiation; closing the panel works). The object's memory is left (small, rare).
		// Only the outgoing form: the ctor sends the command only when the last argument is a string (the factions);
		// otherwise it wraps an incoming negotiation (RequestDiplomacy, the receiver of another player's offer), which
		// must pass (build 0.12 and earlier gave the receiver an empty panel).
		// entry: ecx = edi = the new object, esi = Lua state; the caller pushes nil and returns 1 when edi = 0.
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx                (the object)
		a.Raw(0x6A, 0xFF);                                // push -1
		a.Raw(0x56);                                      // push esi                (L)
		a.Call(LuaType);                                  // lua_type(L, -1), the ctor's own test
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x83, 0xF8, 0x04);                          // cmp eax,4               (LUA_TSTRING)
		a.J8(0x75, "ctor");                               // jne ctor                (an incoming negotiation)
		EmitGroupPtr(a, 0, UiGlobal);                     // eax = &ui
		a.Raw(0x8B, 0x00);                                // mov eax,[eax]
		a.Raw(0x8B, 0x80, 0x98, 0x0A, 0x00, 0x00);        // mov eax,[eax+0A98h]     (model)
		a.Raw(0xFF, 0xB0, 0xB0, 0x00, 0x00, 0x00);        // push dword ptr [eax+0B0h] (f = local)
		a.Raw(0x8B, 0x40, 0x08);                          // mov eax,[eax+8]         (campaign)
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8B, 0x80, 0x98, 0x14, 0x00, 0x00);        // mov eax,[eax+1498h]     (world)
		a.Raw(0x8B, 0x48, 0x2C);                          // mov ecx,[eax+2Ch]       (cur)
		a.Call(groupSlot);                                // eax = cur's slot, edx = &group
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x75, "pass");                               // jnz pass                (cur isn't the lead of a group)
		a.Raw(0x83, 0x3A, 0x02);                          // cmp dword ptr [edx],2
		a.J8(0x72, "pass");                               // jb pass                 (no group running)
		a.Call(foreignPending);                           // (campaign, local)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "pass");                               // jz pass
		a.Raw(0x8B, 0x44, 0x24, 0x04);                    // mov eax,[esp+4]         (local)
		a.Raw(0x89, 0x04, 0x24);                          // mov [esp],eax           (Log pops id and two arguments: local, local)
		CallLog(a, log, LogNegotiationUiBlocked);
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x33, 0xFF);                                // xor edi,edi             (the caller pushes nil)
		a.Raw(0xC2, 0x08, 0x00);                          // ret 8
		a.Mark("pass");
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Mark("ctor");
		a.Raw(0x59);                                      // pop ecx                 (the object)
		a.Jmp(0x1110E1B0);
		a.Resolve();
		return at;
	}

	static uint EmitOpenNegotiationsGate(Asm a, uint groupSlot, uint foreignPending, uint log) {
		// jumped to from 111D7230, the entry of the diplomacy panel's OpenNegotiations FUN_111d7230 (thiscall(panel; x)
		// ret 4, returns 0): the relations list's "start negotiations" button, which hides the list and calls the
		// panel's Lua Initialise(faction). The panel can be open on the relations list while another member's
		// negotiation or popup becomes pending; then (same test as UiNegotiationGate) the click does nothing and the
		// list stays, instead of a negotiation view without a negotiation (no buttons, Cancel stops on the nil
		// negotiation in its Lua). entry: ecx = the panel; eax, edx are free.
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx                (the panel)
		EmitGroupPtr(a, 0, UiGlobal);                     // eax = &ui
		a.Raw(0x8B, 0x00);                                // mov eax,[eax]
		a.Raw(0x8B, 0x80, 0x98, 0x0A, 0x00, 0x00);        // mov eax,[eax+0A98h]     (model)
		a.Raw(0xFF, 0xB0, 0xB0, 0x00, 0x00, 0x00);        // push dword ptr [eax+0B0h] (f = local)
		a.Raw(0x8B, 0x40, 0x08);                          // mov eax,[eax+8]         (campaign)
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8B, 0x80, 0x98, 0x14, 0x00, 0x00);        // mov eax,[eax+1498h]     (world)
		a.Raw(0x8B, 0x48, 0x2C);                          // mov ecx,[eax+2Ch]       (cur)
		a.Call(groupSlot);                                // eax = cur's slot, edx = &group
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x75, "pass");                               // jnz pass                (cur isn't the lead of a group)
		a.Raw(0x83, 0x3A, 0x02);                          // cmp dword ptr [edx],2
		a.J8(0x72, "pass");                               // jb pass                 (no group running)
		a.Call(foreignPending);                           // (campaign, local)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "pass");                               // jz pass
		a.Raw(0x8B, 0x44, 0x24, 0x04);                    // mov eax,[esp+4]         (local)
		a.Raw(0x89, 0x04, 0x24);                          // mov [esp],eax           (Log pops id and two arguments: local, local)
		CallLog(a, log, LogNegotiationUiBlocked);
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Mark("pass");
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x59);                                      // pop ecx                 (the panel)
		a.Raw(0x83, 0xEC, 0x54);                          // sub esp,54h             (replaced)
		a.Raw(0x53);                                      // push ebx                (replaced)
		a.Raw(0x56);                                      // push esi                (replaced)
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx             (replaced)
		a.Jmp(0x111D7237);
		a.Resolve();
		return at;
	}

	static uint EmitPfTurnStart(Asm a, uint pfFocusFor) {
		// replaces "call FUN_106fa800" at 105FD550 in the faction turn start FUN_105fd090 (ebx = the faction starting,
		// ecx = campaign = [[ebx+8Ch]+8]): after the vanilla set-up for the current faction, set the pathfinder up for
		// the faction whose turn starts if it is a non-lead group member (PfFocusFor), so its new units find the way to
		// their general and its characters reset with its own map view (fix 5). eax, ecx, edx are dead after the call
		uint at = a.Here;
		a.Call(PfTurnFocus);                              // FUN_106fa800(campaign) (replaced call)
		a.Raw(0x53);                                      // push ebx                (f)
		a.Raw(0x8B, 0x83, 0x8C, 0x00, 0x00, 0x00);        // mov eax,[ebx+8Ch]
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(pfFocusFor);
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitGroupStartPf(Asm a, uint groupStart, uint pfRestore) {
		// thiscall(world), what 10701DB0 jumps to: GroupStart, then back to the turn's set-up (the last member's turn
		// start left the pathfinder set up for that member; the rest of the transition expects the lead's)
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx                (world)
		a.Call(groupStart);
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x8B, 0x40, 0x2C);                          // mov eax,[eax+2Ch]       (lead)
		a.Raw(0x8B, 0x80, 0x8C, 0x00, 0x00, 0x00);        // mov eax,[eax+8Ch]
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(pfRestore);
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitBattleNewLog(Asm a, uint log) {
		// replaces "call FUN_105e6dc0" (thiscall(pb): opens the vote if +55h) right after a pending battle is created, at
		// 106EA3B9 (FUN_106e9f60, ecx = [campaign+14A4h]) and 106EA603 (FUN_106ea4a0, siege): logs the sides' factions
		// ([side+0Ch], attacker side +60h, defender +64h), the battle kind +58h, autoresolve +54h / human vote +55h and
		// every pre-battle entry ([pb+7Ch] count, [pb+80h] 16-byte entries: +0 the faction key as an engine string,
		// +0Ch ready), the key resolved with FUN_106cdde0(world; key) (a lookup, read-only). Then the original call.
		// entry: ecx = pb; no arguments
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx             (pb)
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x8B, 0x4E, 0x64);                          // mov ecx,[esi+64h]       (defender side)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "nodef");                              // jz nodef
		a.Raw(0x8B, 0x41, 0x0C);                          // mov eax,[ecx+0Ch]       (its faction)
		a.Mark("nodef");
		a.Raw(0x50);                                      // push eax
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x8B, 0x4E, 0x60);                          // mov ecx,[esi+60h]       (attacker side)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "noatt");                              // jz noatt
		a.Raw(0x8B, 0x41, 0x0C);                          // mov eax,[ecx+0Ch]
		a.Mark("noatt");
		a.Raw(0x50);                                      // push eax
		CallLog(a, log, LogBattleNew);
		a.Raw(0x0F, 0xB7, 0x46, 0x54);                    // movzx eax,word ptr [esi+54h] (autoresolve, human vote)
		a.Raw(0x50);                                      // push eax
		a.Raw(0xFF, 0x76, 0x58);                          // push dword ptr [esi+58h] (kind)
		CallLog(a, log, LogBattleKind);
		a.Raw(0x8B, 0xBE, 0x80, 0x00, 0x00, 0x00);        // mov edi,[esi+80h]       (entries)
		a.Raw(0x8B, 0x5E, 0x7C);                          // mov ebx,[esi+7Ch]       (count)
		a.Mark("next");
		a.Raw(0x85, 0xDB);                                // test ebx,ebx
		a.J8(0x74, "done");                               // jz done
		a.Raw(0x8B, 0x46, 0x30);                          // mov eax,[esi+30h]       (model)
		a.Raw(0x8B, 0x40, 0x08);                          // mov eax,[eax+8]         (campaign)
		a.Raw(0x8B, 0x88, 0x98, 0x14, 0x00, 0x00);        // mov ecx,[eax+1498h]     (world)
		a.Raw(0x57);                                      // push edi                (key)
		a.Call(0x106CDDE0);                               // eax = faction or 0; keeps ebx, esi, edi
		a.Raw(0x0F, 0xB6, 0x4F, 0x0C);                    // movzx ecx,byte ptr [edi+0Ch] (ready)
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x50);                                      // push eax
		CallLog(a, log, LogBattleEntry);
		a.Raw(0x83, 0xC7, 0x10);                          // add edi,10h
		a.Raw(0x4B);                                      // dec ebx
		a.J8(0xEB, "next");                               // jmp next
		a.Mark("done");
		a.Raw(0x61);                                      // popad
		a.Jmp(0x105E6DC0);
		a.Resolve();
		return at;
	}

	static uint EmitBattleStateLog(Asm a, uint log) {
		// state setter FUN_1057fca0 (thiscall(sm = pb+4Ch; state) ret 4, every caller is pending-battle code), replaces
		// "mov eax,[esp+4] / push esi" at its entry (5 bytes): logs old [sm+4] -> new when they differ. eax and the
		// flags are dead at the entry
		uint at = a.Here;
		a.Raw(0x8B, 0x44, 0x24, 0x04);                    // mov eax,[esp+4]         (new)
		a.Raw(0x3B, 0x41, 0x04);                          // cmp eax,[ecx+4]         (old)
		a.J8(0x74, "same");                               // je same
		a.Raw(0x50);                                      // push eax
		a.Raw(0xFF, 0x71, 0x04);                          // push dword ptr [ecx+4]
		CallLog(a, log, LogBattleState);
		a.Mark("same");
		a.Raw(0x8B, 0x44, 0x24, 0x04);                    // mov eax,[esp+4]         (replaced)
		a.Raw(0x56);                                      // push esi                (replaced)
		a.Jmp(0x1057FCA5);
		a.Resolve();
		return at;
	}

	static uint EmitBattleReadyLog(Asm a, uint log) {
		// replaces "call FUN_106fe6c0" (thiscall(campaign; faction, choice, flag) ret 0Ch -> FUN_105b5140(pb+F8h; ...))
		// at 1088A5DD in CCQ_SET_PENDING_BATTLE_READY_TO_START FUN_1088a580: logs the sender and its choice. The first
		// argument is logged as a faction only if it is in the world's list ([world+40h] count, [world+44h] array), so
		// a value that isn't one can't crash the log. entry: ecx = campaign, three arguments pushed
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0x8B, 0x54, 0x24, 0x24);                    // mov edx,[esp+24h]       (first argument)
		a.Raw(0x8B, 0x81, 0x98, 0x14, 0x00, 0x00);        // mov eax,[ecx+1498h]     (world)
		a.Raw(0x8B, 0x70, 0x44);                          // mov esi,[eax+44h]
		a.Raw(0x8B, 0x58, 0x40);                          // mov ebx,[eax+40h]
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Mark("find");
		a.Raw(0x85, 0xDB);                                // test ebx,ebx
		a.J8(0x74, "log");                                // jz log                  (not a faction: "-")
		a.Raw(0x39, 0x16);                                // cmp [esi],edx
		a.J8(0x74, "found");                              // je found
		a.Raw(0x83, 0xC6, 0x04);                          // add esi,4
		a.Raw(0x4B);                                      // dec ebx
		a.J8(0xEB, "find");                               // jmp find
		a.Mark("found");
		a.Raw(0x8B, 0xC2);                                // mov eax,edx
		a.Mark("log");
		a.Raw(0xFF, 0x74, 0x24, 0x28);                    // push dword ptr [esp+28h] (choice)
		a.Raw(0x50);                                      // push eax
		CallLog(a, log, LogBattleReady);
		a.Raw(0x61);                                      // popad
		a.Jmp(0x106FE6C0);
		a.Resolve();
		return at;
	}

	static uint EmitEndMoves(Asm a, uint pfFocusFor, uint pfRestore, uint log) {
		// replaces "call FUN_105b73d0" at 106B4088 in FUN_106b4070 (phase 2, after the current faction's End Turn; asked
		// whenever movement is idle until it returns 0): FUN_105b73d0(f) moves on the first of f's characters whose order
		// is left from the turn ([char+94h], +14h = 1; FUN_108d8f90, which drops the order if the movement manager refuses
		// it). Vanilla only asks the current faction, so the members' queued moves (multi-turn moves, new units walking
		// to their general) never ran. After the lead's, each living non-lead member's in slot order, with its ended bit
		// cleared for the call (all have ended: "closing" GroupAllows would refuse its movers) and the pathfinder set up
		// for it (fix 6b: FUN_108d8f90 first asks the order's vt+10h, whose access test FUN_10874570 runs before the
		// order reaches FUN_106aa660 / PfMoverAct; with the lead's set-up a recruit walking to its general through the
		// member's own land was refused and its order dropped, tenth test), then the turn's set-up again.
		// entry: ecx = current faction; returns al = 1 while something moved on; keeps ebx, esi, edi, ebp
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x57);                                      // push edi
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx             (current)
		a.Call(FactionResumeOrder);                       // FUN_105b73d0(current) (replaced call)
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "moved");                              // jnz moved
		EmitGroupPtr(a, 7, GroupVa);                      // edi = &group
		a.Raw(0x83, 0x3F, 0x02);                          // cmp dword ptr [edi],2
		a.J8(0x7C, "none");                               // jl none                 (no group, or the lead alone)
		a.Raw(0x3B, 0x77, 0x34);                          // cmp esi,[edi+34h]
		a.J8(0x75, "none");                               // jne none                (not this group's lead)
		a.Raw(0xBB, 0x01, 0x00, 0x00, 0x00);              // mov ebx,1               (slot)
		a.Mark("loop");
		a.Raw(0x8B, 0x4C, 0x9F, 0x34);                    // mov ecx,[edi+ebx*4+34h] (member)
		a.Call(FactionIsDead);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "next");                               // jnz next
		a.Raw(0xFF, 0x77, 0x04);                          // push dword ptr [edi+4]  (ended)
		a.Raw(0x0F, 0xB3, 0x5F, 0x04);                    // btr [edi+4],ebx         (acts as if it hadn't ended)
		a.Raw(0xFF, 0x74, 0x9F, 0x34);                    // push dword ptr [edi+ebx*4+34h] (member)
		a.Raw(0x8B, 0x8E, 0x8C, 0x00, 0x00, 0x00);        // mov ecx,[esi+8Ch]
		a.Raw(0xFF, 0x71, 0x08);                          // push dword ptr [ecx+8]  (campaign)
		a.Call(pfFocusFor);                               // keeps every register and the flags
		a.Raw(0x8B, 0x4C, 0x9F, 0x34);                    // mov ecx,[edi+ebx*4+34h]
		a.Call(FactionResumeOrder);                       // FUN_105b73d0(member)
		a.Raw(0x8F, 0x47, 0x04);                          // pop dword ptr [edi+4]
		a.Raw(0x8B, 0x8E, 0x8C, 0x00, 0x00, 0x00);        // mov ecx,[esi+8Ch]
		a.Raw(0xFF, 0x71, 0x08);                          // push dword ptr [ecx+8]  (campaign)
		a.Call(pfRestore);                                // keeps every register and the flags
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "next");                               // jz next
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0xFF, 0x74, 0x9F, 0x34);                    // push dword ptr [edi+ebx*4+34h]
		CallLog(a, log, LogEndMoves);
		a.J8(0xEB, "moved");                              // jmp moved
		a.Mark("next");
		a.Raw(0x43);                                      // inc ebx
		a.Raw(0x3B, 0x1F);                                // cmp ebx,[edi]
		a.J8(0x7C, "loop");                               // jl loop
		a.Mark("none");
		a.Raw(0x32, 0xC0);                                // xor al,al
		a.J8(0xEB, "out");                                // jmp out
		a.Mark("moved");
		a.Raw(0xB0, 0x01);                                // mov al,1
		a.Mark("out");
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitResumeMembers(Asm a, uint pfFocusFor, uint pfRestore, uint log) {
		// replaces thunk 1070B490 at 10607876 in the battle driver FUN_10604260, reached once the battle is back in
		// state 10 with +C2h / +C8h set (orders were interrupted): vanilla then re-issues the interrupted orders
		// ([order+16h] set) of the current faction's characters only (1060787B..106078CB), so a member's move that ran
		// into a battle (ninth test, kind 1) stopped for good. First the same loop for each living non-lead member in
		// slot order, with its ended bit cleared for the calls (in the lead's end of turn every member has ended and
		// "closing" GroupAllows would refuse its movers, as in EndMoves) and the pathfinder set up for it (state 10:
		// PfFocusFor acts), then the turn's set-up again; then the replaced call for vanilla's loop.
		// entry: ecx = world, ebx = the pending battle; returns eax = current-or-proxy; keeps ebx, esi, edi, ebp
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x57);                                      // push edi
		a.Raw(0x55);                                      // push ebp
		a.Raw(0x51);                                      // push ecx                (world)
		EmitGroupPtr(a, 7, GroupVa);                      // edi = &group
		a.Raw(0x83, 0x3F, 0x02);                          // cmp dword ptr [edi],2
		a.J32("done", 0x0F, 0x8C);                        // jl done                 (no group, or the lead alone)
		a.Call(CurrentOrProxy);                           // thunk 1070B490(world)
		a.Raw(0x3B, 0x47, 0x34);                          // cmp eax,[edi+34h]
		a.J32("done", 0x0F, 0x85);                        // jne done                (a proxy turn: vanilla)
		a.Raw(0xBD, 0x01, 0x00, 0x00, 0x00);              // mov ebp,1               (slot)
		a.Mark("loop");
		a.Raw(0x8B, 0x4C, 0xAF, 0x34);                    // mov ecx,[edi+ebp*4+34h] (member)
		a.Call(FactionIsDead);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J32("next", 0x0F, 0x85);                        // jnz next
		a.Raw(0xFF, 0x77, 0x04);                          // push dword ptr [edi+4]  (ended)
		a.Raw(0x0F, 0xB3, 0x6F, 0x04);                    // btr [edi+4],ebp         (acts as if it hadn't ended)
		a.Raw(0x8B, 0x44, 0xAF, 0x34);                    // mov eax,[edi+ebp*4+34h]
		a.Raw(0x8B, 0xB0, 0x18, 0x07, 0x00, 0x00);        // mov esi,[eax+718h]      (its characters)
		a.Raw(0x8B, 0x88, 0x14, 0x07, 0x00, 0x00);        // mov ecx,[eax+714h]      (count)
		a.Raw(0x8D, 0x14, 0x8E);                          // lea edx,[esi+ecx*4]
		a.Raw(0x52);                                      // push edx                (end)
		a.Mark("char");
		a.Raw(0x3B, 0x34, 0x24);                          // cmp esi,[esp]
		a.J8(0x73, "chars done");                         // jae chars done
		a.Raw(0x8B, 0x06);                                // mov eax,[esi]
		a.Raw(0x8B, 0x00);                                // mov eax,[eax]           (character)
		a.Raw(0x8B, 0x88, 0x94, 0x00, 0x00, 0x00);        // mov ecx,[eax+94h]       (its order)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "char next");                          // jz char next
		a.Raw(0x80, 0x79, 0x16, 0x00);                    // cmp byte ptr [ecx+16h],0 (interrupted)
		a.J8(0x74, "char next");                          // je char next
		a.Raw(0xFF, 0x74, 0xAF, 0x34);                    // push dword ptr [edi+ebp*4+34h] (member)
		a.Raw(0x8B, 0x43, 0x30);                          // mov eax,[ebx+30h]
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(pfFocusFor);                               // keeps every register and the flags
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0x6A, 0x01);                                // push 1
		a.Call(OrderResume);                              // FUN_108d8f20(order; 1, 0, 0), as at 106078C0
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0xFF, 0x74, 0xAF, 0x34);                    // push dword ptr [edi+ebp*4+34h]
		CallLog(a, log, LogResumeMembers);
		a.Mark("char next");
		a.Raw(0x83, 0xC6, 0x04);                          // add esi,4
		a.J8(0xEB, "char");                               // jmp char
		a.Mark("chars done");
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x8B, 0x43, 0x30);                          // mov eax,[ebx+30h]
		a.Raw(0xFF, 0x70, 0x08);                          // push dword ptr [eax+8]  (campaign)
		a.Call(pfRestore);                                // nothing to undo unless PfFocusFor set it up
		a.Raw(0x8F, 0x47, 0x04);                          // pop dword ptr [edi+4]
		a.Mark("next");
		a.Raw(0x45);                                      // inc ebp
		a.Raw(0x3B, 0x2F);                                // cmp ebp,[edi]
		a.J32("loop", 0x0F, 0x8C);                        // jl loop
		a.Mark("done");
		a.Raw(0x59);                                      // pop ecx                 (world)
		a.Call(CurrentOrProxy);                           // thunk 1070B490(world) (replaced call)
		a.Raw(0x5D);                                      // pop ebp
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	// ---- log only: pathfinder set-ups at the access decision, pending actions, diplomacy, attacks -----------------
	// None of these change anything the simulation reads (PfBuiltForVa is read only by the log), so they can't desync.

	// eax = the vtable at [esi] as a static address: the image's runtime base is this code's runtime address minus
	// its static offset. Changes eax, edx and the flags
	static void EmitStaticVtable(Asm a) {
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x81, 0xEA); D32(a, popAt - ImageBase);     // sub edx,popAt-ImageBase (runtime image base)
		a.Raw(0x8B, 0x06);                                // mov eax,[esi]
		a.Raw(0x2B, 0xC2);                                // sub eax,edx
		a.Raw(0x05); D32(a, ImageBase);                   // add eax,ImageBase
	}

	static uint EmitPfBuiltFor(Asm a) {
		// FUN_1081a7e0(sub; f) (thiscall ret 4: one sub-view's region flags for f, the part of every pathfinder set-up
		// that decides territory access; called by the faction set-ups FUN_10778500 / FUN_10778890 / FUN_10778f10)
		// replaces "sub esp,10h / push ebx / mov ebx,ecx" at its entry (6 bytes): remembers f. Unlike [pf+1F4h] this
		// also follows the engine's temporary set-ups (keep 0)
		uint at = a.Here;
		a.Raw(0x50);                                      // push eax
		a.Raw(0x51);                                      // push ecx
		EmitGroupPtr(a, 0, PfBuiltForVa);                 // eax = &builtFor
		a.Raw(0x8B, 0x4C, 0x24, 0x0C);                    // mov ecx,[esp+0Ch]       (f)
		a.Raw(0x89, 0x08);                                // mov [eax],ecx
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x83, 0xEC, 0x10);                          // sub esp,10h             (replaced)
		a.Raw(0x53);                                      // push ebx                (replaced)
		a.Raw(0x8B, 0xD9);                                // mov ebx,ecx             (replaced)
		a.Jmp(0x1081A7E6);
		return at;
	}

	static uint EmitAccessLog(Asm a, uint log) {
		// replaces "call FUN_107a2bf0" (thiscall(pf; char, dest, FLT_MAX) ret 0Ch -> al) in FUN_10874640 (108746B1) and
		// its twin FUN_10874570 (108745E1). Reached when a human's character can't reach dest under the pathfinder's
		// current set-up (FUN_1078fad0); FUN_107a2bf0 tests whether it could if the destination region's owner let it
		// through, and if so the caller pushes the "declare war / military access" popup. Logs the character's owner,
		// the faction the region flags were built for, the kept faction [pf+1F4h] and the result.
		// entry: ecx = pf, three arguments pushed
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx                (pf)
		a.Raw(0xFF, 0x74, 0x24, 0x10);                    // push dword ptr [esp+10h] (FLT_MAX)
		a.Raw(0xFF, 0x74, 0x24, 0x10);                    // push dword ptr [esp+10h] (dest)
		a.Raw(0xFF, 0x74, 0x24, 0x10);                    // push dword ptr [esp+10h] (char)
		a.Call(0x107A2BF0);                               // the replaced call
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x60);                                      // pushad                  (char at [esp+24h])
		a.Raw(0x8B, 0xD9);                                // mov ebx,ecx             (pf)
		a.Raw(0x0F, 0xB6, 0xF0);                          // movzx esi,al            (result)
		a.Raw(0x8B, 0x4C, 0x24, 0x24);                    // mov ecx,[esp+24h]       (char)
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);                                  // eax = its owner
		a.Raw(0x8B, 0xF8);                                // mov edi,eax
		EmitGroupPtr(a, 0, PfBuiltForVa);                 // eax = &builtFor
		a.Raw(0xFF, 0x30);                                // push dword ptr [eax]
		a.Raw(0x57);                                      // push edi
		CallLog(a, log, LogAccessBlocked);
		a.Raw(0x56);                                      // push esi
		a.Raw(0xFF, 0xB3, 0xF4, 0x01, 0x00, 0x00);        // push dword ptr [ebx+1F4h] (kept faction)
		CallLog(a, log, LogAccessKept);
		a.Raw(0x61);                                      // popad                   (eax = the result)
		a.Raw(0xC2, 0x0C, 0x00);                          // ret 0Ch
		return at;
	}

	static uint EmitClickPfLog(Asm a, uint clickLog, uint log) {
		// in front of ClickLog where the UI sends an order (11129E27 inside the click order, which runs with the local
		// member's set-up; 1115199D, the cursor the hover stored): the pathfinder set-up at that moment, this PC only
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		EmitGroupPtr(a, 3, UiGlobal);                     // ebx = &ui
		a.Raw(0x8B, 0x1B);                                // mov ebx,[ebx]
		a.Raw(0x85, 0xDB);                                // test ebx,ebx
		a.J8(0x74, "skip");                               // jz skip
		a.Raw(0x8B, 0x9B, 0x98, 0x0A, 0x00, 0x00);        // mov ebx,[ebx+0A98h]     (model)
		a.Raw(0x85, 0xDB);                                // test ebx,ebx
		a.J8(0x74, "skip");                               // jz skip
		a.Raw(0x8B, 0x5B, 0x08);                          // mov ebx,[ebx+8]         (campaign)
		a.Raw(0x85, 0xDB);                                // test ebx,ebx
		a.J8(0x74, "skip");                               // jz skip
		a.Raw(0x8B, 0x9B, 0xA4, 0x0E, 0x00, 0x00);        // mov ebx,[ebx+0EA4h]     (pathfinder)
		a.Raw(0x85, 0xDB);                                // test ebx,ebx
		a.J8(0x74, "skip");                               // jz skip
		a.Raw(0xFF, 0xB3, 0xF4, 0x01, 0x00, 0x00);        // push dword ptr [ebx+1F4h] (kept faction)
		EmitGroupPtr(a, 0, PfBuiltForVa);                 // eax = &builtFor
		a.Raw(0xFF, 0x30);                                // push dword ptr [eax]
		CallLog(a, log, LogClickPf);
		a.Mark("skip");
		a.Raw(0x61);                                      // popad
		a.Jmp(clickLog);
		a.Resolve();
		return at;
	}

	static uint EmitPendingPushLog(Asm a, uint log) {
		// FUN_105ea330(holder; node) (thiscall ret 4: pushes a pending action onto [campaign+14A8h], see "Pending
		// actions") replaces "push esi / mov esi,ecx / mov ecx,[esp+8]" at its entry (7 bytes): logs the node's vtable
		// and whether another action was already waiting
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad                  (node at [esp+24h], ecx = holder)
		a.Raw(0x8B, 0x74, 0x24, 0x24);                    // mov esi,[esp+24h]
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "skip");                               // jz skip
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x83, 0x39, 0x00);                          // cmp dword ptr [ecx],0   (head)
		a.Raw(0x0F, 0x95, 0xC0);                          // setne al
		a.Raw(0x50);                                      // push eax
		EmitStaticVtable(a);                              // eax = node's vtable
		a.Raw(0x50);                                      // push eax
		CallLog(a, log, LogPendingPush);
		a.Mark("skip");
		a.Raw(0x61);                                      // popad
		a.Raw(0x56);                                      // push esi                (replaced)
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx             (replaced)
		a.Raw(0x8B, 0x4C, 0x24, 0x08);                    // mov ecx,[esp+8]         (replaced)
		a.Jmp(0x105EA337);
		a.Resolve();
		return at;
	}

	static uint EmitPendingPopLog(Asm a, uint log) {
		// FUN_105b60e0(holder) (thiscall: pops the head pending action, applies and deletes it; from
		// CCQ_FINALISE_PENDING_ACTION, the agent / ally-war answers and the per-tick countdown) replaces "push esi /
		// mov esi,ecx / push edi / mov edi,[esi]" at its entry (6 bytes): logs the head's vtable and whether another
		// action waits behind it
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad                  (ecx = holder)
		a.Raw(0x8B, 0x31);                                // mov esi,[ecx]           (head)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "skip");                               // jz skip
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x83, 0x7E, 0x04, 0x00);                    // cmp dword ptr [esi+4],0 (next)
		a.Raw(0x0F, 0x95, 0xC0);                          // setne al
		a.Raw(0x50);                                      // push eax
		EmitStaticVtable(a);                              // eax = head's vtable
		a.Raw(0x50);                                      // push eax
		CallLog(a, log, LogPendingPop);
		a.Mark("skip");
		a.Raw(0x61);                                      // popad
		a.Raw(0x56);                                      // push esi                (replaced)
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx             (replaced)
		a.Raw(0x57);                                      // push edi                (replaced)
		a.Raw(0x8B, 0x3E);                                // mov edi,[esi]           (replaced)
		a.Jmp(0x105B60E6);
		a.Resolve();
		return at;
	}

	static uint EmitNegotiationLog(Asm a, uint log) {
		// replaces "call FUN_10692d90" (thiscall(campaign; f1, f2, 1) ret 0Ch) at 10884342 in
		// CCQ_DIPLOMACY_BEGIN_NEGOTIATION FUN_108842f0
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (f2)
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (f1)
		CallLog(a, log, LogNegotiation);
		a.Jmp(0x10692D90);
		return at;
	}

	static uint EmitWarLog(Asm a, uint log) {
		// replaces "call FUN_107a4bd0" ([f1+53Ch]; f2, 1, 1) at 108840E2 in CCQ_DECLARE_WAR FUN_10883fc0, where
		// ebp = f1 and ebx = f2 (loaded at 10884001 / 1088400D, read again after the call)
		uint at = a.Here;
		a.Raw(0x53);                                      // push ebx                (f2)
		a.Raw(0x55);                                      // push ebp                (f1)
		CallLog(a, log, LogWar);
		a.Jmp(0x107A4BD0);
		return at;
	}

	static uint EmitAttackLog(Asm a, uint log) {
		// replaces "call FUN_106e4f60" (the attack gate, thiscall(campaign; f) ret 4 -> al; NeqCurF sits inside it at
		// 106E4F73) at 1088381B in CCQ_CHARACTER_ATTACK FUN_10883780, which then hands the attack to the movement manager
		// (FUN_1085a820). Both clans are taken from the characters, not from the command's faction argument: esi = the
		// attacking character, the handler's [esp+8] = the target character (checked non-zero before the call).
		// entry: ecx = campaign, f pushed; the target is at [esp+10h]
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (f)
		a.Call(AttackAllowed);                            // the replaced call
		a.Raw(0x60);                                      // pushad                  (target at [esp+30h])
		a.Raw(0x0F, 0xB6, 0xF8);                          // movzx edi,al            (result)
		a.Raw(0x8B, 0x4C, 0x24, 0x30);                    // mov ecx,[esp+30h]       (target)
		a.Raw(0x81, 0xC1, 0x54, 0x02, 0x00, 0x00);        // add ecx,254h
		a.Call(OwnerOf);                                  // eax = the target's owner
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8D, 0x8E, 0x54, 0x02, 0x00, 0x00);        // lea ecx,[esi+254h]      (attacker)
		a.Call(OwnerOf);                                  // eax = the attacker's owner
		a.Raw(0x50);                                      // push eax
		a.Raw(0x85, 0xFF);                                // test edi,edi
		a.J8(0x74, "refused");                            // jz refused
		CallLog(a, log, LogAttackOk);
		a.J8(0xEB, "done");                               // jmp done
		a.Mark("refused");
		CallLog(a, log, LogAttackRefused);
		a.Mark("done");
		a.Raw(0x61);                                      // popad                   (al = the result)
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	static uint EmitMessageLog(Asm a, uint log) {
		// the thunk 1104EBC0 ("jmp FUN_110bd000": the event-message callback the campaign keeps at [obj+C4h], see "Event
		// messages"; nothing else jumps there) jumps here instead: logs who sent the message (the model's return address,
		// not found statically) and what its check object [event+2Ch] answers at vt+8 (show it; FUN_110bd000 and
		// FUN_110bd790 ask it too, so asking once more changes nothing (?)). FUN_110bd000 calls vt+14h on it without a
		// null test, the end only with one; null here logs vtable 0, "don't show".
		// entry: [esp] = the caller's return address, [esp+4] = the event (cdecl); flags and eax/ecx/edx are dead
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad                  (return address at [esp+20h], event at [esp+24h])
		a.Raw(0x8B, 0x7C, 0x24, 0x24);                    // mov edi,[esp+24h]       (event)
		a.Raw(0x85, 0xFF);                                // test edi,edi
		a.J8(0x74, "skip");                               // jz skip
		a.Raw(0x33, 0xDB);                                // xor ebx,ebx             (the check's vtable)
		a.Raw(0x33, 0xED);                                // xor ebp,ebp             (its answer)
		a.Raw(0x8B, 0x77, 0x2C);                          // mov esi,[edi+2Ch]       (the check)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "nocheck");                            // jz nocheck
		EmitStaticVtable(a);                              // eax = the check's vtable
		a.Raw(0x8B, 0xD8);                                // mov ebx,eax
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Raw(0x8B, 0x06);                                // mov eax,[esi]
		a.Raw(0xFF, 0x50, 0x08);                          // call dword ptr [eax+8]  (show it?)
		a.Raw(0x0F, 0xB6, 0xE8);                          // movzx ebp,al
		a.Mark("nocheck");
		a.Raw(0x53);                                      // push ebx                (a2: the check's vtable)
		a.Raw(0x8D, 0x74, 0x24, 0x24);                    // lea esi,[esp+24h]       (-> the return address)
		EmitStaticVtable(a);                              // eax = the return address as a static address
		a.Raw(0x50);                                      // push eax                (a1)
		a.Raw(0x85, 0xED);                                // test ebp,ebp
		a.J8(0x74, "hide");                               // jz hide
		CallLog(a, log, LogMessageShow);
		a.J8(0xEB, "skip");                               // jmp skip
		a.Mark("hide");
		CallLog(a, log, LogMessageHide);
		a.Mark("skip");
		a.Raw(0x61);                                      // popad
		a.Jmp(0x110BD000);                                // the replaced jmp
		a.Resolve();
		return at;
	}

	static void ApplyGroupTurns(Asm s, uint log) {
		ReserveDataDword(GroupLastDword);
		ReserveDataDword(LogOpenedVa);
		ReserveDataDword(LogCloseWaitVa);
		ReserveDataDword(PfLastDword);
		ReserveDataDword(SaveLastDword);
		ReserveDataDword(AttackLastDword);
		uint groupSlot = Sym("GroupSlot", EmitGroupSlot(s));
		uint groupAllows = Sym("GroupAllows", EmitGroupAllows(s, groupSlot));   // also EqCurF: same arguments as FUN_105870b0(cur, f)
		uint neqCurF = Sym("NeqCurF", EmitNeqCurF(s, groupAllows));
		uint eqFCur = Sym("EqFCur", EmitEqFCur(s, groupAllows));
		uint moverAllowed = Sym("MoverAllowed", EmitMoverAllowed(s, groupAllows));
		uint actionAllowed = Sym("ActionAllowed", EmitActionAllowed(s, groupAllows));
		uint pfRestore = Sym("PfRestore", EmitPfRestore(s));
		uint pfFocusFor = Sym("PfFocusFor", EmitPfFocusFor(s, groupSlot, pfRestore));
		uint eqOwnerScratch = Sym("EqOwnerScratch", EmitEqOwnerScratch(s, eqFCur, pfFocusFor));
		uint scratchOwner = Sym("ScratchOwner", EmitScratchOwner(s));
		uint latchEq = Sym("LatchEq", EmitLatchEq(s, groupSlot, log));
		uint zeroGroup = Sym("ZeroGroup", EmitZeroGroup(s));
		uint groupBuild = Sym("GroupBuild", EmitGroupBuild(s, log));
		uint groupStart = Sym("GroupStart", EmitGroupStart(s, groupBuild, log));
		uint groupStartLoad = Sym("GroupStartLoad", EmitGroupStartLoad(s, groupStart, log));
		uint groupTurnEnd = Sym("GroupTurnEnd", EmitGroupTurnEnd(s, log));
		uint pickerSkip = Sym("PickerSkip", EmitPickerSkip(s, log));
		uint newRound = Sym("NewRound", EmitNewRound(s, log));
		uint groupEndTurn = Sym("GroupEndTurn", EmitGroupEndTurn(s, groupSlot, log));
		uint factionAt = Sym("FactionAt", EmitFactionAt(s));
		uint groupRestore = Sym("GroupRestore", EmitGroupRestore(s, factionAt, log));
		uint groupTick = Sym("GroupTick", EmitGroupTick(s, groupEndTurn, groupBuild, groupRestore, log));

		// campaign constructors FUN_106463e0 (edi = campaign) and FUN_10649180 (load, ebx = campaign)
		uint resetNew = Sym("CampaignReset", EmitCampaignReset(s, zeroGroup, 0x87, 0x00, 0x10646DD3, log, LogResetNew));
		ExpectBytes(0x10646DCA, 0x66, 0xC7, 0x87, 0x1C, 0x15, 0x00, 0x00, 0x00, 0x00);
		PutJmp(0x10646DCA, resetNew, 9);
		uint resetLoad = Sym("CampaignReset", EmitCampaignReset(s, zeroGroup, 0x83, 0x01, 0x10649B3C, log, LogResetLoad));
		ExpectBytes(0x10649B33, 0x66, 0xC7, 0x83, 0x1C, 0x15, 0x00, 0x00, 0x01, 0x00);
		PutJmp(0x10649B33, resetLoad, 9);
		// group start / end, picker
		ExpectBytes(0x10701DB0, 0x8B, 0x49, 0x2C, 0xE9, 0xD8, 0xB2, 0xEF, 0xFF);   // jmp to GroupStartPf: end of this method
		RedirectCall(0x10704313, FactionTurnStart, groupStartLoad);
		RedirectCall(0x106A5FD9, FactionTurnEnd, groupTurnEnd);
		ExpectBytes(0x106A603B, 0x80, 0xB8, 0x94, 0x08, 0x00, 0x00, 0x00, 0x74, 0x07);
		PutJmp(0x106A603B, pickerSkip, 9);
		ExpectBytes(0x106A609E, 0x33, 0xF6, 0x39, 0x77, 0x40);
		PutJmp(0x106A609E, newRound, 5);
		// end turn, latch, tick
		RedirectCall(0x10886507, CampaignEndTurn, groupEndTurn);
		RedirectCall(0x1070420A, CampaignEndTurn, groupEndTurn);
		RedirectCall(0x1088655A, IsEqual, latchEq);
		ExpectBytes(0x107041CA, 0x80, 0xBB, 0x5F, 0x15, 0x00, 0x00, 0x00, 0x74, 0x43);
		PutJmp(0x107041CA, groupTick, 9);
		// milestone 4: the group goes into every campaign save, after the scripts' SavingGame values, and comes back
		// at load (LoadGroup after LoadingGame, then GroupRestore in GroupTick's rebuild)
		RedirectCall(0x1066CCDC, EventDispatch, Sym("SaveGroup", EmitSaveGroup(s, log)));
		RedirectCall(0x106559A7, EventDispatch, Sym("LoadGroup", EmitLoadGroup(s)));
		// simulation guards
		RedirectCall(0x106AA5E8, IsNotEqual, neqCurF);      // FUN_106aa590 CanEndTurn core (proxy branch)
		RedirectCall(0x106AA5FE, IsNotEqual, neqCurF);      // FUN_106aa590 CanEndTurn core
		RedirectCall(0x106AA6AB, MoverFactionIs, moverAllowed);   // FUN_106aa660 character orders
		RedirectCall(0x106AA777, MoverFactionIs, moverAllowed);   // FUN_106aa730
		ExpectBytes(0x106AA89F, 0x8B, 0x86, 0x98, 0x14, 0x00, 0x00, 0x39, 0x78, 0x2C, 0x75, 0x13);
		PutJmp(0x106AA89F, actionAllowed, 11);              // FUN_106aa7e0
		RedirectCall(0x106E4F73, IsNotEqual, neqCurF);      // FUN_106e4f60 attacks
		RedirectCall(0x108FFE3B, IsEqual, eqOwnerScratch);  // FUN_108ffb80 update_positions
		RedirectCall(0x108FFEC8, CurrentOrProxy, scratchOwner);
		RedirectCall(0x108FFEF4, CurrentOrProxy, scratchOwner);
		RedirectCall(0x108FFF4A, CurrentOrProxy, scratchOwner);
		RedirectCall(0x106EC0E0, IsNotEqual, neqCurF);      // FUN_106ebf70 character-moved notifications
		RedirectCall(0x106C5A84, IsEqual, groupAllows);     // FUN_106c5a70
		RedirectCall(0x1069867B, IsNotEqual, neqCurF);      // FUN_10698630 disband allowed
		// End Turn UI: FUN_1111e830 accepts a member (also reached from the TurnTimeExpired time-out) and names the
		// local faction in CCQ_END_TURN instead of [world+2Ch]; TurnTimeExpired fires on every active member
		RedirectCall(0x1111E84F, IsEqual, groupAllows);
		ExpectBytes(0x1111E8BF, 0x8B, 0x41, 0x08, 0x8B, 0x80, 0x98, 0x14, 0x00, 0x00, 0x8B, 0x40, 0x2C);
		var localFaction = new byte[] { 0x8B, 0x81, 0xB0, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };   // mov eax,[ecx+0B0h] (ecx = model) / 6x nop
		PutBytes(0x1111E8BF, localFaction);
		RedirectCall(0x11155403, IsEqual, eqFCur);
		// Character orders UI: FUN_1118c060 decides which order a click on the map gives (the order type at +7Ch that
		// FUN_111943e0 sends, e.g. CCQ_CHARACTER_LOCOMOTE_TO); it gives "no order" unless FUN_105870b0(cur, [+80h])
		// with [+80h] = the local faction (set by the ctors FUN_11164590.., called from FUN_11129a30 with [model+B0h]).
		// Found in the first in-game test: a non-lead member couldn't move anything.
		RedirectCall(0x1118C09F, IsEqual, groupAllows);

		// Fix 3, the pathfinder set up per clan (see "the pathfinder set up per clan" above). Simulation: a member's
		// order (FUN_106aa660 passed), each movement step (EqOwnerScratch at 108FFE3B, above) and the re-issued move
		// of a stopped mover set it up for the member; after each command record, at the end of the movement update and
		// before the tick's commands it goes back to the turn's set-up.
		uint pfMoverAct = Sym("PfMoverAct", EmitPfMoverAct(s, pfFocusFor));
		ExpectBytes(0x106AA710, 0x5B, 0x5F, 0xB0, 0x01, 0x5E, 0xC2, 0x04, 0x00);
		PutJmp(0x106AA710, pfMoverAct, 8);
		RedirectCall(0x10900082, 0x107F94F0, Sym("PfFocusMover", EmitPfFocusMover(s, pfFocusFor)));
		RedirectCall(0x10900137, 0x107DF8C0, Sym("PfMovesDone", EmitPfMovesDone(s, pfRestore)));
		uint pfCommandDone = Sym("PfCommandDone", EmitPfCommandDone(s, pfRestore));
		ExpectBytes(0x108D2699, 0xFF, 0xD0, 0x83, 0xC4, 0x08);
		PutJmp(0x108D2699, pfCommandDone, 5);
		RedirectCall(0x10703F27, 0x108D2550, Sym("PfCommands", EmitPfCommands(s, pfRestore)));
		// UI: the hover FUN_1112b110 (cursor and path preview; from the map input handler FUN_1115aa70 and 111516B2) and
		// the click order FUN_11129a30 (its first instruction is relocated, so the call sites are redirected) run with
		// the pathfinder set up for the local member, then the kept set-up is rebuilt
		uint uiPfEnter = Sym("UiPfEnter", EmitUiPfEnter(s, groupSlot));
		uint uiPfLeave = Sym("UiPfLeave", EmitUiPfLeave(s));
		uint uiPfHover = Sym("UiPfHover", EmitUiPfWrap(s, 0x1112B110, 1, uiPfEnter, uiPfLeave));
		RedirectCall(0x1115AB26, 0x1112B110, uiPfHover);
		RedirectCall(0x111516B2, 0x1112B110, uiPfHover);
		uint uiPfClick = Sym("UiPfClick", EmitUiPfWrap(s, 0x11129A30, 3, uiPfEnter, uiPfLeave));
		RedirectCall(0x11160272, 0x11129A30, uiPfClick);
		RedirectCall(0x1114AE22, 0x11129A30, uiPfClick);
		// UI: the recruitment panel's refresh FUN_112517a0 (unit cards, their reasons, the queue) and the unit card's
		// own recruitable test in its animation (FUN_11232c00 -> FUN_107ea880) with the local member's set-up too
		ExpectBytes(0x112517A5, 0x83, 0xEC, 0x4C, 0x53, 0x55, 0x8B, 0xE9, 0x56);
		PutJmp(0x112517A5, Sym("UiPfRecruit", EmitUiPfRecruitPanel(s, uiPfEnter, uiPfLeave)), 8);
		RedirectCall(0x11232FB1, 0x107EA880, Sym("UiPfCardState", EmitUiPfWrap(s, 0x107EA880, 0, uiPfEnter, uiPfLeave)));
		// log only: MOVE in CCQ_CHARACTER_LOCOMOTE_TO, CLICK where the UI sends an order
		RedirectCall(0x10883DD0, 0x10903190, Sym("MoveLog", EmitMoveLog(s, groupAllows, log)));
		uint clickLog = Sym("ClickLog", EmitClickLog(s, log));   // its two call sites are at the end (ClickPfLog)

		// Fix 4 and milestone 2: Lua IsPlayersTurn is true for a member that hasn't ended while nothing is pending or
		// its own popup / negotiation is the pending action (so declaring war from the attack popup opens its diplomacy
		// panel and the panel releases the pending action), and false for the lead while another clan's negotiation or
		// popup is pending
		uint foreignPending = Sym("ForeignPending", EmitForeignPending(s));
		uint ownActionTurn = Sym("OwnActionTurn", EmitOwnActionTurn(s, groupSlot, groupAllows, foreignPending));
		ExpectBytes(0x1114174C, 0x3B, 0x41, 0x2C, 0x0F, 0x94, 0xC1, 0x0F, 0xB6, 0xC9);
		PutJmp(0x1114174C, ownActionTurn, 9);

		// Milestone 2, UI gates for members: FUN_11122970 (the diplomacy button and the radar's clan list, agent
		// recruitment and its other UI callers), the save message and the Clan button, the unit panels' disband /
		// scupper buttons, CCQ_DEMAND_SURRENDER naming the local member, and one negotiation at a time (simulation and
		// the diplomacy panel)
		RedirectCall(0x11122988, IsEqual, eqFCur);
		uint localAsCur = Sym("LocalAsCurIfAllowed", EmitLocalAsCurIfAllowed(s, groupAllows));
		RedirectCall(0x11142F57, 0x11124580, localAsCur);
		RedirectCall(0x1122FF86, 0x11124580, localAsCur);
		uint unitPanelZF = Sym("UnitPanelZF", EmitUnitPanelZF(s, groupAllows));
		foreach (uint site in new uint[] { 0x112457A9, 0x1124ED39, 0x1125458A, 0x11254772 }) {
			ExpectBytes(site, 0x8B, 0x40, 0x2C, 0x3B, 0x81, 0xB0, 0x00, 0x00, 0x00);
			PutCall(site, unitPanelZF, 9);
		}
		uint demandActor = Sym("DemandActor", EmitDemandActor(s, groupAllows));
		RedirectCall(0x11143D98, CurrentOrProxyFn, demandActor);
		RedirectCall(0x111798B2, CurrentOrProxyFn, demandActor);
		RedirectCall(0x11152230, 0x1110E1B0, Sym("UiNegotiationGate", EmitUiNegotiationGate(s, groupSlot, foreignPending, log)));
		ExpectBytes(0x111D7230, 0x83, 0xEC, 0x54, 0x53, 0x56, 0x8B, 0xF1);
		PutJmp(0x111D7230, Sym("OpenNegotiationsGate", EmitOpenNegotiationsGate(s, groupSlot, foreignPending, log)), 7);

		// Fix 5: a member's turn start (new units walking to their general, character resets) runs with the member's
		// pathfinder set-up; the group start ends with the turn's set-up again
		RedirectCall(0x105FD550, PfTurnFocus, Sym("PfTurnStart", EmitPfTurnStart(s, pfFocusFor)));
		PutJmp(0x10701DB0, Sym("GroupStartPf", EmitGroupStartPf(s, groupStart, pfRestore)), 8);

		// Log only: pending battles (created, state changes, pre-battle votes received) for the AI-attack question
		uint battleNewLog = Sym("BattleNewLog", EmitBattleNewLog(s, log));
		RedirectCall(0x106EA3B9, 0x105E6DC0, battleNewLog);
		RedirectCall(0x106EA603, 0x105E6DC0, battleNewLog);
		ExpectBytes(0x1057FCA0, 0x8B, 0x44, 0x24, 0x04, 0x56, 0x8B, 0xF1);
		PutJmp(0x1057FCA0, Sym("BattleStateLog", EmitBattleStateLog(s, log)), 5);
		RedirectCall(0x1088A5DD, 0x106FE6C0, Sym("BattleReadyLog", EmitBattleReadyLog(s, log)));

		// Fix 6: the members' orders left from the turn (queued moves) move on during the lead's end of turn, as vanilla
		// does for the current faction's
		RedirectCall(0x106B4088, FactionResumeOrder, Sym("EndMoves", EmitEndMoves(s, pfFocusFor, pfRestore, log)));

		// Milestone 3: after a battle the members' interrupted moves carry on too, not only the current faction's
		RedirectCall(0x10607876, CurrentOrProxy, Sym("ResumeMembers", EmitResumeMembers(s, pfFocusFor, pfRestore, log)));

		// An attack on an army that moved away since the order (members at war in one shared turn): no battle
		RedirectCall(0x10883846, AttackPlan, Sym("AttackRecord", EmitAttackRecord(s)));
		RedirectCall(0x108A7986, BattleStart, Sym("AttackCheck", EmitAttackCheck(s, log)));

		// Log only: which set-up the pathfinder had when a member was asked for military access it already had, and
		// where a war declaration between humans stalls (pending actions, negotiations, wars, attack orders)
		ReserveDataDword(PfBuiltForVa);
		ExpectBytes(0x1081A7E0, 0x83, 0xEC, 0x10, 0x53, 0x8B, 0xD9);
		PutJmp(0x1081A7E0, Sym("PfBuiltFor", EmitPfBuiltFor(s)), 6);
		uint accessLog = Sym("AccessLog", EmitAccessLog(s, log));
		RedirectCall(0x108746B1, 0x107A2BF0, accessLog);
		RedirectCall(0x108745E1, 0x107A2BF0, accessLog);
		uint clickPfLog = Sym("ClickPfLog", EmitClickPfLog(s, clickLog, log));
		RedirectCall(0x11129E27, 0x111943E0, clickPfLog);
		RedirectCall(0x1115199D, 0x111943E0, clickPfLog);
		ExpectBytes(0x105EA330, 0x56, 0x8B, 0xF1, 0x8B, 0x4C, 0x24, 0x08);
		PutJmp(0x105EA330, Sym("PendingPushLog", EmitPendingPushLog(s, log)), 7);
		ExpectBytes(0x105B60E0, 0x56, 0x8B, 0xF1, 0x57, 0x8B, 0x3E);
		PutJmp(0x105B60E0, Sym("PendingPopLog", EmitPendingPopLog(s, log)), 6);
		uint negotiationLog = Sym("NegotiationLog", EmitNegotiationLog(s, log));
		RedirectCall(0x10884342, 0x10692D90, Sym("NegotiationGate", EmitNegotiationGate(s, groupSlot, foreignPending, negotiationLog, log)));
		RedirectCall(0x108840E2, 0x107A4BD0, Sym("WarLog", EmitWarLog(s, log)));
		RedirectCall(0x1088381B, AttackAllowed, Sym("AttackLog", EmitAttackLog(s, log)));

		// Log only: who sends the event messages and what their check says (a member's messages during the AI turns)
		ExpectBytes(0x1104EBC0, 0xE9, 0x3B, 0xE4, 0x06, 0x00);   // jmp FUN_110bd000
		PutJmp(0x1104EBC0, Sym("MessageLog", EmitMessageLog(s, log)), 5);
	}

	// ---- desync fixes (standalone builds only) -------------------------------------------------------------------
	// Found by Tasman Modding (8-player co-op mod). On top of the 8-player mod its own versions sit at these sites
	// already, so Build applies these only to the original DLL; players get the same desync handling either way.

	// Floating-point modes. Every PC must run the simulation's maths identically. The engine never sets the CPU's
	// floating-point modes itself (the only FLDCW/LDMXCSR in it are float->int helpers that restore the previous mode,
	// and CRT internals), and TBB copies the main thread's modes to its workers. So anything that changes them on one
	// PC (overlays, anti-virus hooks, drivers, code run on alt-tab) makes that PC's simulation drift. Fix: load the
	// MSVC defaults (x87 control 027Fh: 53-bit precision, round to nearest, exceptions masked; MXCSR 1F80h) at the
	// start of each frame's game update and of each battle tick. Where nothing changed them this does nothing.
	const uint FrameUpdateCall = 0x100B190C;   // main frame: call FUN_10f36680 (multiplayer update), first game logic
	const uint MultiplayerUpdate = 0x10F36680;
	const uint BattleTickAt = 0x10227D40;      // battle tick (vtable slot 33): push [esp+8] / mov ecx,[esp+10h]
	const uint BattleTickResume = 0x10227D48;

	static void EmitPinFloatModes(Asm a) {
		a.Raw(0x83, 0xEC, 0x04);                          // sub esp,4
		a.Raw(0x66, 0xC7, 0x04, 0x24, 0x7F, 0x02);        // mov word ptr [esp],027Fh
		a.Raw(0xD9, 0x2C, 0x24);                          // fldcw word ptr [esp]
		a.Raw(0xC7, 0x04, 0x24, 0x80, 0x1F, 0x00, 0x00);  // mov dword ptr [esp],1F80h
		a.Raw(0x0F, 0xAE, 0x14, 0x24);                    // ldmxcsr dword ptr [esp]
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
	}

	static uint EmitFramePin(Asm a) {
		// replaces "call FUN_10f36680" (ecx = its this): the modes, then tail-call the original. Build 0.15 showed this
		// call doesn't run on the campaign map (its first-run log line never came), so the campaign tick pins too.
		uint at = a.Here;
		EmitPinFloatModes(a);
		a.Jmp(MultiplayerUpdate);
		return at;
	}

	const uint CampaignTick = 0x10703EA0;      // FUN_10703ea0 entry: mov eax,5090h / call __alloca_probe (10 bytes)
	const uint AllocaProbe = 0x114FC1B0;

	static uint EmitTickPin(Asm a) {
		// jumped to from the campaign tick's entry (ecx = campaign, every PC, every tick, any phase): the modes (the
		// campaign simulation starts here), then the replaced prologue
		uint at = a.Here;
		EmitPinFloatModes(a);
		a.Raw(0xB8, 0x90, 0x50, 0x00, 0x00);              // mov eax,5090h           (replaced)
		a.Call(AllocaProbe);                              // call __alloca_probe     (replaced)
		a.Jmp(CampaignTick + 10);
		return at;
	}

	static uint EmitBattleTickPin(Asm a) {
		uint at = a.Here;
		EmitPinFloatModes(a);
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (replaced instructions)
		a.Raw(0x8B, 0x4C, 0x24, 0x10);                    // mov ecx,[esp+10h]
		a.Jmp(BattleTickResume);
		return at;
	}

	// Battle desyncs no longer drop the player. A battle can't be resynced, so when its checksums differ the engine
	// disconnects (mp_desync_kick_enabled): the desync handler FUN_10ee9a50 drops the local player, and FUN_10ef9a20
	// (another player reported a desync) has the host kick the reported players / the reported client leave. Both
	// skip the kick while a battle runs ([session+596D0h] = battle environment, the handler's own test for
	// "mp_battle_desync"). The battle carries on; back on the map the campaign checksums catch any difference and the
	// automatic resync copies the host's state to everyone. Campaign desyncs keep the original behaviour; the absolute
	// flag reads (relocated) stay untouched.
	const uint DesyncKickAt = 0x10EE9A71;        // mov ecx,[edi+2BE74h] (6 bytes); edi = session+4
	const uint DesyncKickResume = 0x10EE9A77;
	const uint DesyncKickSkip = 0x10EE9A8D;
	const uint ReportKickAt = 0x10EF9A87;        // push ebp / push edi / lea eax,[esp+10h] (6 bytes); ebx = session
	const uint ReportKickResume = 0x10EF9A8D;
	const uint ReportKickSkip = 0x10EF9B29;

	static uint EmitBattleNoKick(Asm a, uint log) {
		uint at = a.Here;
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		a.Raw(0x83, 0xBF, 0xCC, 0x96, 0x05, 0x00, 0x00);  // cmp dword ptr [edi+596CCh],0   (battle running?)
		a.J32("skip", 0x0F, 0x85);                        // jne -> no kick
		CallLog(a, log, LogDesyncOther);
		a.Raw(0x8B, 0x8F, 0x74, 0xBE, 0x02, 0x00);        // mov ecx,[edi+2BE74h]          (replaced instruction)
		a.Jmp(DesyncKickResume);
		a.Mark("skip");
		CallLog(a, log, LogDesyncBattle);
		a.Jmp(DesyncKickSkip);
		a.Resolve();
		return at;
	}

	static uint EmitReportNoKick(Asm a) {
		uint at = a.Here;
		a.Raw(0x83, 0xBB, 0xD0, 0x96, 0x05, 0x00, 0x00);  // cmp dword ptr [ebx+596D0h],0
		a.J32("skip", 0x0F, 0x85);                        // jne -> no kick
		a.Raw(0x55, 0x57);                                // push ebp / push edi            (replaced instructions)
		a.Raw(0x8D, 0x44, 0x24, 0x10);                    // lea eax,[esp+10h]
		a.Jmp(ReportKickResume);
		a.Mark("skip");
		a.Jmp(ReportKickSkip);
		a.Resolve();
		return at;
	}

	// Log lines for the drops FUN_10ef9a20 really makes. It runs whenever [conn+18DCh] changes (about every 300 ms, in
	// the lobby too) and walks a list of 8-byte entries (first dword = player id (?)), normally empty: the host drops
	// each entry (FUN_10f02c10(entry, 2)), a client leaves when an entry is itself (FUN_10f033e0(3)). Only reached
	// outside battles (ReportNoKick skips the walk in a battle). Both sites have no relocation and no jump into them;
	// esi = &entry, edi = [session+2BE50h].
	const uint ReportHostKickAt = 0x10EF9AC1;    // push 2 / push esi / mov ecx,edi (5 bytes), then call FUN_10f02c10
	const uint ReportLeaveAt = 0x10EF9AD1;       // jne 10EF9ADC / push 3 / mov ecx,edi (6 bytes), then call FUN_10f033e0

	static uint EmitReportHostKickLog(Asm a, uint log) {
		uint at = a.Here;
		a.Raw(0x6A, 0x00);                                // push 0
		a.Raw(0xFF, 0x36);                                // push dword ptr [esi]   (player id (?))
		CallLog(a, log, LogReportHostKick);
		a.Raw(0x6A, 0x02);                                // push 2                 (replaced instructions)
		a.Raw(0x56);                                      // push esi
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Jmp(0x10EF9AC6);                                // call FUN_10f02c10
		return at;
	}

	static uint EmitReportLeaveLog(Asm a, uint log) {
		uint at = a.Here;
		a.J8(0x74, "leave");                              // je leave               (the entry is this PC; flags of cmp eax,[edi])
		a.Jmp(0x10EF9ADC);                                // next entry
		a.Mark("leave");
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, LogReportLeave);
		a.Raw(0x6A, 0x03);                                // push 3                 (replaced instructions)
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Jmp(0x10EF9AD7);                                // call FUN_10f033e0
		a.Resolve();
		return at;
	}

	// Save/load screens leak the player's Windows locale into the simulation. FUN_110b83f0 (run when the save list,
	// load list or save dialog opens, to format save dates) calls _wsetlocale(LC_ALL, "<language>_<country>.<cp>").
	// Only some of those screens call FUN_110b55d0 to go back to "C", and only if they still own the token; so after a
	// player opens Save or Load, the C runtime on that PC stays in the Windows locale for the rest of the session:
	// number parsing/printing (decimal comma on German, Czech, French, Russian... Windows), case rules (Turkish I).
	// Fix: switch only LC_TIME there (all those screens need is the dates); everything else stays "C".
	const uint SaveScreenLocaleCategory = 0x110B84E1;   // push 0 (LC_ALL) -> push 5 (LC_TIME), before call _wsetlocale

	// Automatic campaign resync. On a campaign desync FUN_10f058d0 marks a resync as pending ([resync+30h] = 1) and
	// tells its listeners, which open the "mp_campaign_desync" popup. Nothing happens until someone presses OK there
	// (FUN_11103d10 -> FUN_10f2d4b0(session, 1), which sends message 0xAC); Cancel or leaving it drops the player.
	// Fix: accept straight away, exactly as the OK button does. The popup still shows the resync progress.
	const uint ResyncNotifyCall = 0x10F05914;  // call FUN_102fc670 (notify listeners); esi = resync, edi = session
	const uint NotifyListeners = 0x102FC670;   // thiscall(list; arg) ret 4
	const uint AnswerResync = 0x10F2D4B0;      // thiscall(resync; session, bool accept) ret 8

	static uint EmitAutoResync(Asm a, uint log) {
		uint at = a.Here;
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, LogResyncAuto);
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (the listeners' argument)
		a.Call(NotifyListeners);                          // ecx = esi on entry, as the original call
		a.Raw(0x6A, 0x01);                                // push 1                  (accept)
		a.Raw(0x57);                                      // push edi                (session)
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi             (resync state)
		a.Call(AnswerResync);
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		return at;
	}

	// Host resync key (Ctrl+Shift+F9): removed in build 0.17. Polled at the campaign tick it started the host's resync
	// alone, which made a real desync on every PC and a crash or the kick (NOTES "Next steps", build 0.16). Kept: the
	// "force" answer below (no PC sends value 2 now). The original design:
	// A resync only starts on a PC that noticed a checksum mismatch itself. Desyncs
	// that hang the game instead (one PC stuck on an AI turn while another thinks it is a player's turn) never start
	// one, and players who didn't notice ignore the others' 0xAC "accept" messages (FUN_10efa140 needs state 1..3).
	// Once every PC has accepted, the host saves the campaign, sends the file and everyone loads it. Fix:
	//   - the host presses Ctrl+Shift+F9 (campaign, no resync running): it starts its own resync and broadcasts 0xAC
	//     with value 2 ("force");
	//   - every PC, the host included, that receives a "force" while idle starts a resync exactly as on a detected
	//     desync (FUN_10f058d0: popup + the auto-accept above), then the message counts as a normal accept.
	// FUN_10f058d0 still requires the campaign map, so the key does nothing in battles or the front end.
	const uint ResyncAnswerCall = 0x10F10E2D;  // message 0xAC: call FUN_10efa140(resync; session, is_local, accept)
	const uint ResyncAnswer = 0x10EFA140;
	const uint StartResync = 0x10F058D0;       // thiscall(resync; session) ret 4

	static uint EmitForcedResyncAnswer(Asm a, uint log) {
		uint at = a.Here;
		a.Raw(0x80, 0x7C, 0x24, 0x0C, 0x02);              // cmp byte ptr [esp+0Ch],2   (accept == force?)
		a.J8(0x75, "go");                                 // jne go
		a.Raw(0xC6, 0x44, 0x24, 0x0C, 0x01);              // mov byte ptr [esp+0Ch],1   (counts as a normal accept)
		a.Raw(0x83, 0x79, 0x30, 0x00);                    // cmp dword ptr [ecx+30h],0  (no resync running)
		a.J8(0x75, "go");                                 // jne go
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, LogResyncForced);
		a.Raw(0x51);                                      // push ecx
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]     (session)
		a.Call(StartResync);                              // ecx = resync
		a.Raw(0x59);                                      // pop ecx
		a.Mark("go");
		a.Jmp(ResyncAnswer);
		a.Resolve();
		return at;
	}

	static void ApplyDesyncFixes(Asm s, uint log) {
		ExpectBytes(SaveScreenLocaleCategory, 0x6A, 0x00, 0xE8);
		PutBytes(SaveScreenLocaleCategory + 1, new byte[] { 0x05 });
		RedirectCall(FrameUpdateCall, MultiplayerUpdate, Sym("FramePin", EmitFramePin(s)));
		ExpectBytes(CampaignTick, 0xB8, 0x90, 0x50, 0x00, 0x00, 0xE8, 0x06, 0x83, 0xDF, 0x00);
		PutJmp(CampaignTick, Sym("TickPin", EmitTickPin(s)), 10);
		ExpectBytes(BattleTickAt, 0xFF, 0x74, 0x24, 0x08, 0x8B, 0x4C, 0x24, 0x10);
		PutJmp(BattleTickAt, Sym("BattleTickPin", EmitBattleTickPin(s)), 8);
		ExpectBytes(DesyncKickAt, 0x8B, 0x8F, 0x74, 0xBE, 0x02, 0x00);
		PutJmp(DesyncKickAt, Sym("BattleNoKick", EmitBattleNoKick(s, log)), 6);
		ExpectBytes(ReportKickAt, 0x55, 0x57, 0x8D, 0x44, 0x24, 0x10);
		PutJmp(ReportKickAt, Sym("ReportNoKick", EmitReportNoKick(s)), 6);
		ExpectBytes(ReportHostKickAt, 0x6A, 0x02, 0x56, 0x8B, 0xCF);
		PutJmp(ReportHostKickAt, Sym("ReportHostKickLog", EmitReportHostKickLog(s, log)), 5);
		ExpectBytes(ReportLeaveAt, 0x75, 0x09, 0x6A, 0x03, 0x8B, 0xCF);
		PutJmp(ReportLeaveAt, Sym("ReportLeaveLog", EmitReportLeaveLog(s, log)), 6);
		RedirectCall(ResyncNotifyCall, NotifyListeners, Sym("AutoResync", EmitAutoResync(s, log)));
		RedirectCall(ResyncAnswerCall, ResyncAnswer, Sym("ForcedResyncAnswer", EmitForcedResyncAnswer(s, log)));
	}

	// ---- build ---------------------------------------------------------------------------------------------------
	// is the file a build of this mod (any version)? The installer uses it to tell an older build from an unknown DLL
	public static bool IsThisMod(string path) {
		b = File.ReadAllBytes(path);
		ReadHeaders();
		return FindSection(SectionName) >= 0;
	}

	public static string Build(string inPath, string outPath) {
		return Build(inPath, outPath, "hybrid");
	}

	public static string Build(string inPath, string outPath, string turnMode) {
		if (turnMode != "hybrid" && turnMode != "simultaneous") throw new Exception("turn mode must be hybrid or simultaneous, not " + turnMode);
		TurnMode = turnMode;
		b = File.ReadAllBytes(inPath);
		SymbolMap.Clear();
		string inHash = Sha(b);
		if (inHash == OriginalSha256) BaseName = "original";
		else if (inHash == EightPlayerV111Sha256) BaseName = "8-player mod v1.11";
		else throw new Exception("input is neither the original empire.retail.dll this mod was made for nor the 8-player mod v1.11 built from it (sha256 " + inHash + ")");
		ReadHeaders();
		ReadRelocs();
		// the 8-player mod keeps its data at the end of .data, below ours (a later version accepted here must too)
		int dataSec = FindSection(".data");
		uint dataEnd = SecVa(dataSec) + U32(SecOff(dataSec) + 8);
		if (dataEnd > DataFloor) throw new Exception(string.Format("the input's .data reaches {0:X8}, past this mod's data at {1:X8}", dataEnd, DataFloor));

		var s = new Asm { Va = ImageBase + U32(optOff + 56) };   // the new section goes right after the last one
		uint log = Sym("Log", EmitLog(s));
		ApplyGroupTurns(s, log);
		if (inHash == OriginalSha256) ApplyDesyncFixes(s, log);   // the 8-player mod has its own at these sites
		Sym("LogData", s.Here);
		EmitLogData(s);
		AddSection(SectionName, s.Bytes);
		Sym("DataFloor", DataFloor);   // for re\build\compat.py: this mod's .data starts here

		File.WriteAllBytes(outPath, b);
		return string.Format("patched ({0}, {5} turns): {1} at {2:X8}, {3} bytes; output sha256 {4}", BaseName, SectionName, s.Va, s.Bytes.Count, Sha(b), TurnMode);
	}
}
