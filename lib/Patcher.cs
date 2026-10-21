// Builds the Shared Turns patch of Shogun 2's empire.retail.dll: human clans that are not at war with each other play
// their turns at the same time (AI turns stay one after another), plus a diagnostics log. Never modifies its input:
// it checks the input is exactly a build it knows, and writes a new file.
//
// Inputs it accepts (anything else is refused):
//  - the original empire.retail.dll of the current Steam build (OriginalSha256);
//  - the 8-player co-op mod v1.4 (Tasman Modding) built from that original (EightPlayerV14Sha256), so both mods can
//    be installed together: the 8-player mod first, this one on top (NOTES.md "Compatibility with the 8-player mod").
// All new code goes into a section of its own (".s2st", appended after the last section; the header has room for
// several more); hooks are relative jumps/calls; data lives in .data's slack from DataFloor on, past the 8-player
// mod's two dwords. Same input -> same output on every PC: no timestamps, no paths, no unordered collections.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class SharedTurnsPatcher {
	public const string OriginalSha256 = "022FAE2E2DA92B59B8B1B8EE52887003C624E3C8ECA060B209F5D792A7F874F5";
	public const string EightPlayerV14Sha256 = "AE3A3F4E25DF8EFC9C0A4BC439AF287507558D65A3191B6F508FDD47ACF56424";
	public const string SectionName = ".s2st";
	const uint ImageBase = 0x10000000;
	// .data ends at 11CB152C in the original; the 8-player mod keeps two dwords at 11CB1530 / 11CB1534. Ours start
	// here and must stay in the same page (11CB2000 is _RDATA)
	const uint DataFloor = 0x11CB1538;

	// ---- the image being patched (file layout) -------------------------------------------------------------------
	static byte[] b;
	static int peOff, optOff, secTableOff, nSec;
	static uint[] relocVas;   // every address a HIGHLOW relocation rewrites (4 bytes each), sorted
	public static string BaseName;

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
		if (len < 5) throw new Exception("a jmp needs 5 bytes");
		var p = new byte[len];
		p[0] = 0xE9;
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
	const string LogBuildTag = "shared turns 0.2 (milestone 1, fixes 2-5, battle log, desync fixes)";
	const uint UiGlobal = 0x11BC8FE4;          // ui; [ui+A98h] model, [model+8] campaign, [model+B0h] local faction
	const uint LogOpenedVa = 0x11CB158C;       // 1 once this process has moved the previous log to .old
	const uint LogCloseWaitVa = 0x11CB1590;    // 1 once "lead's end turn waits" is logged for the running group
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
		"LOAD rebuild the group after a load",                              // 4
		"END_REQ %f flag %b -> vanilla end turn",                           // 5
		"END_REQ %f flag %b -> already ended",                              // 6
		"END_REQ %f flag %b -> treasury goes negative: PendingBankruptcy",  // 7
		"END_REQ %f flag %b -> end-turn lock set: ignored",                 // 8
		"END_REQ %f flag %b -> ended",                                      // 9
		"LATCH %f end request held until it can end",                       // 10
		"DROP %f player left or faction removed: counts as ended",          // 11
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
		"!RESYNC host pressed Ctrl+Shift+F9",                               // 30
		"!RESYNC forced by the host's key",                                 // 31
	};
	const int LogResetNew = 0, LogResetLoad = 1, LogStart = 2, LogSolo = 3, LogLoad = 4, LogEndVanilla = 5,
		LogEndAlready = 6, LogEndBankrupt = 7, LogEndLocked = 8, LogEndMarked = 9, LogLatch = 10, LogDrop = 11,
		LogCloseWait = 12, LogClose = 13, LogTurnEnd = 14, LogSkip = 15, LogRound = 16, LogMoveOk = 17,
		LogMoveRefused = 18, LogClick = 19, LogBattleNew = 20, LogBattleKind = 21, LogBattleEntry = 22,
		LogBattleState = 23, LogBattleReady = 24, LogDesyncBattle = 25, LogDesyncOther = 26, LogReportHostKick = 27,
		LogReportLeave = 28, LogResyncAuto = 29, LogResyncKey = 30, LogResyncForced = 31;
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
		return "==== Shogun 2 Shared Turns log: game started (build: " + LogBuildTag + ") ====\r\n";
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
	// GroupVa + 78h: ResyncKeyPrevVa (desync fixes, standalone builds only)
	const int PfDwords = 0x1C / 4;             // PfFocusVa .. PfLastDword
	const uint FactionTurnStart = 0x105FD090;  // thiscall(faction)
	const uint FactionTurnEnd = 0x105B2870;    // thiscall(faction)
	const uint FactionIsDead = 0x105CB900;     // thiscall(faction) -> al
	const uint FactionsAtWar = 0x105CB0F0;     // thiscall(faction; other) ret 4 -> al
	const uint CampaignEndTurn = 0x106A5DA0;   // thiscall(campaign; faction, flag) ret 8
	const uint CanEndTurn = 0x106E4F20;        // thiscall(campaign; faction) ret 4 -> al
	const uint TreasuryGoesNegative = 0x107EF460;   // thiscall(faction+90h) -> al
	const uint OwnerOf = 0x103E5960;           // thiscall(char+254h) -> owning faction
	const uint EventInit = 0x10BA5980;         // thiscall(event)
	const uint EventDispatch = 0x10BE0350;     // cdecl(event*)
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
		// clears the whole group state and the pathfinder block (they would outlive a campaign in the same process),
		// not the log's dwords between them; keeps every register
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
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0xC3);                                      // ret
		return at;
	}

	static uint EmitGroupStart(Asm a, uint log) {
		// thiscall(world), replaces the thunk at 10701DB0 ("mov ecx,[ecx+2Ch] / jmp FUN_105fd090", called by the
		// transition FUN_1068a8d0 for the new current faction). Builds the group, then runs the faction turn start
		// FUN_105fd090 (movement points, FactionTurnStart, ...) for the lead and then every other member.
		// Members: human, not removed (+7A8h), not skipped (+894h), not AI-driven (+895h, dropped player), alive,
		// not in played[], not at war with any member; only factions after the lead (earlier ones have played this
		// round). Only a human lead without a proxy turn (+798h) forms a group; a lead alone leaves count = 0.
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
		a.Raw(0x80, 0xBE, 0x95, 0x08, 0x00, 0x00, 0x00);  // cmp byte ptr [esi+895h],0 (no player: AI ends its turn)
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
		a.Raw(0x33, 0xED);                                // xor ebp,ebp             (member index; world no longer needed)
		a.Mark("war");
		a.Raw(0x3B, 0x2B);                                // cmp ebp,[ebx]
		a.J8(0x73, "add");                                // jae add
		a.Raw(0xFF, 0x74, 0xAB, 0x34);                    // push dword ptr [ebx+ebp*4+34h] (member)
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(FactionsAtWar);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J32("scan", 0x0F, 0x85);                        // jne scan                (at war with a member)
		a.Raw(0x45);                                      // inc ebp
		a.J8(0xEB, "war");                                // jmp war
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
		a.Raw(0x8B, 0x4B, 0x34);                          // mov ecx,[ebx+34h]       (lead first, as vanilla)
		a.Call(FactionTurnStart);
		a.Raw(0xBF, 0x01, 0x00, 0x00, 0x00);              // mov edi,1
		a.Mark("starts");
		a.Raw(0x3B, 0x3B);                                // cmp edi,[ebx]
		a.J8(0x73, "done");                               // jae done
		a.Raw(0x8B, 0x4C, 0xBB, 0x34);                    // mov ecx,[ebx+edi*4+34h]
		a.Call(FactionTurnStart);
		a.Raw(0x47);                                      // inc edi
		a.J8(0xEB, "starts");                             // jmp starts
		a.Mark("done");
		a.Raw(0x5D);                                      // pop ebp
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC3);                                      // ret
		a.Resolve();
		return at;
	}

	static uint EmitGroupStartLoad(Asm a, uint groupStart, uint log) {
		// replaces "call FUN_105fd090" at 10704313: after every load (campaign+151Ch, resyncs included: they reload on
		// every PC) the tick re-runs the current faction's turn start. The group isn't saved, so it is rebuilt here
		// from the loaded diplomacy; members who had ended must end again. played[] is lost too (cleared).
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

	static uint EmitGroupTick(Asm a, uint groupEndTurn, uint log) {
		// campaign tick FUN_10703ea0, replaces "cmp byte [ebx+155Fh],0 / je 10704216" at 107041CA (the +155Fh
		// retry; ebx = campaign). While a group plays (current = lead, phase 1):
		//   1. members whose player dropped (+895h) or that were removed (+7A8h) count as ended (vanilla's auto-end
		//      at 10704344.. only looks at the current faction, and its CanEndTurn now waits for the group);
		//   2. latched members: retry CanEndTurn, then end them (GroupEndTurn with flag 1, as vanilla's retry);
		//   3. every member ended: the lead's real end turn FUN_106a5da0(lead, 1), retried each tick until
		//      CanEndTurn passes.
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		EmitGroupPtr(a, 7, GroupVa);                      // edi = &group
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

	static uint EmitOwnActionTurn(Asm a, uint groupAllows) {
		// Lua IsPlayersTurn FUN_11141720, replaces "cmp eax,[ecx+2Ch] / sete cl / movzx ecx,cl" at 1114174C (9 bytes).
		// A member's attack on a clan at peace leaves the "move options" pending action; its popup's DeclareWar calls
		// ToggleDiplomacyPopup, which opens the diplomacy panel only if IsPlayersTurn, and the panel's exit finalises
		// the pending action. Without the panel the Lua stops on a nil panel and the pending action blocks everyone
		// (fix 4, third in-game test). True as vanilla for local == current; also for a group member that hasn't ended
		// while the head pending action is its own: a move-options node (vtable 115FCFB8, +8 = the order's character)
		// of its character, or a negotiation node (vtable 115FCE00, +8 = the faction negotiating) it opened.
		// entry: eax = local faction, ecx = world, esi = ui, edi = Lua state (kept); eax, ecx, edx and the flags are
		// dead (11141755 pushes ecx as the result)
		uint at = a.Here;
		a.Raw(0x3B, 0x41, 0x2C);                          // cmp eax,[ecx+2Ch]       (replaced: local == current)
		a.J32("yes", 0x0F, 0x84);                         // je yes
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
		a.J8(0x74, "no");                                 // jz no                   (none: not its turn)
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
		a.Raw(0x39, 0x01);                                // cmp [ecx],eax
		a.J8(0x75, "no");                                 // jne no
		a.Raw(0x8B, 0x49, 0x08);                          // mov ecx,[ecx+8]         (the order's character)
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

	static void ApplyGroupTurns(Asm s, uint log) {
		ReserveDataDword(GroupLastDword);
		ReserveDataDword(LogOpenedVa);
		ReserveDataDword(LogCloseWaitVa);
		ReserveDataDword(PfLastDword);
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
		uint groupStart = Sym("GroupStart", EmitGroupStart(s, log));
		uint groupStartLoad = Sym("GroupStartLoad", EmitGroupStartLoad(s, groupStart, log));
		uint groupTurnEnd = Sym("GroupTurnEnd", EmitGroupTurnEnd(s, log));
		uint pickerSkip = Sym("PickerSkip", EmitPickerSkip(s, log));
		uint newRound = Sym("NewRound", EmitNewRound(s, log));
		uint groupEndTurn = Sym("GroupEndTurn", EmitGroupEndTurn(s, groupSlot, log));
		uint groupTick = Sym("GroupTick", EmitGroupTick(s, groupEndTurn, log));

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
		// log only: MOVE in CCQ_CHARACTER_LOCOMOTE_TO, CLICK where the UI sends an order
		RedirectCall(0x10883DD0, 0x10903190, Sym("MoveLog", EmitMoveLog(s, groupAllows, log)));
		uint clickLog = Sym("ClickLog", EmitClickLog(s, log));
		RedirectCall(0x11129E27, 0x111943E0, clickLog);
		RedirectCall(0x1115199D, 0x111943E0, clickLog);

		// Fix 4: Lua IsPlayersTurn is also true for a member whose own popup / negotiation is the pending action, so
		// declaring war from the attack popup opens its diplomacy panel and the panel releases the pending action
		uint ownActionTurn = Sym("OwnActionTurn", EmitOwnActionTurn(s, groupAllows));
		ExpectBytes(0x1114174C, 0x3B, 0x41, 0x2C, 0x0F, 0x94, 0xC1, 0x0F, 0xB6, 0xC9);
		PutJmp(0x1114174C, ownActionTurn, 9);

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

	static uint EmitFramePin(Asm a, uint log) {
		// replaces "call FUN_10f36680" (ecx = its this): the resync key, the modes, then tail-call the original
		uint at = a.Here;
		EmitResyncKeyPoll(a, log);
		EmitPinFloatModes(a);
		a.Jmp(MultiplayerUpdate);
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

	// Host resync key (Ctrl+Shift+F9). A resync only starts on a PC that noticed a checksum mismatch itself. Desyncs
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
	const uint SendResyncAnswer = 0x10F27820;  // thiscall(session; value) ret 4: broadcast message 0xAC
	const uint SessionIsHost = 0x10F00940;     // thiscall(session) -> al
	const uint GetAsyncKeyStateIat = 0x115A1558;
	const uint ResyncKeyPrevVa = GroupVa + 0x78;   // 11CB15B0: the key was held last frame

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

	// polled once per frame from the frame hook (ebp = owner, [ebp+58h] = session); keeps every register
	static void EmitResyncKeyPoll(Asm a, uint log) {
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x8B, 0x9E); D32(a, GetAsyncKeyStateIat - popAt);   // mov ebx,[esi+iat]
		a.Raw(0x8D, 0xBE); D32(a, ResyncKeyPrevVa - popAt);       // lea edi,[esi+prev]
		foreach (byte vk in new byte[] { 0x78, 0x11, 0x10 }) {   // F9, Ctrl, Shift
			a.Raw(0x6A, vk);                              // push vk
			a.Raw(0xFF, 0xD3);                            // call ebx                (GetAsyncKeyState)
			a.Raw(0xF6, 0xC4, 0x80);                      // test ah,80h             (held)
			a.J32("up", 0x0F, 0x84);                      // jz up
		}
		a.Raw(0x83, 0x3F, 0x00);                          // cmp dword ptr [edi],0   (only on the press itself)
		a.J8(0x75, "done");                               // jne done
		a.Raw(0xC7, 0x07, 0x01, 0x00, 0x00, 0x00);        // mov dword ptr [edi],1
		a.Raw(0x8B, 0x4D, 0x58);                          // mov ecx,[ebp+58h]       (session)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "done");                               // jz done
		a.Call(SessionIsHost);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "done");                               // jz done
		a.Raw(0x8B, 0x4D, 0x58);                          // mov ecx,[ebp+58h]
		a.Raw(0x8B, 0x81, 0x78, 0xBE, 0x02, 0x00);        // mov eax,[ecx+2BE78h]    (resync state)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "done");                               // jz done
		a.Raw(0x83, 0x78, 0x30, 0x00);                    // cmp dword ptr [eax+30h],0
		a.J8(0x75, "done");                               // jne done
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // push 0 / push 0
		CallLog(a, log, LogResyncKey);
		a.Raw(0x8B, 0xF0);                                // mov esi,eax
		a.Raw(0x51);                                      // push ecx                (session)
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(StartResync);                              // the host's own resync (popup + auto-accept)
		a.Raw(0x83, 0x7E, 0x30, 0x00);                    // cmp dword ptr [esi+30h],0
		a.J8(0x74, "done");                               // je done                 (not on the campaign map)
		a.Raw(0x8B, 0x4D, 0x58);                          // mov ecx,[ebp+58h]
		a.Raw(0x6A, 0x02);                                // push 2                  (force: everyone else joins)
		a.Call(SendResyncAnswer);                         // ecx = session
		a.J8(0xEB, "done");                               // jmp done
		a.Mark("up");
		a.Raw(0xC7, 0x07, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [edi],0
		a.Mark("done");
		a.Raw(0x61);                                      // popad
		a.Resolve();
	}

	static void ApplyDesyncFixes(Asm s, uint log) {
		ReserveDataDword(ResyncKeyPrevVa);
		ExpectBytes(SaveScreenLocaleCategory, 0x6A, 0x00, 0xE8);
		PutBytes(SaveScreenLocaleCategory + 1, new byte[] { 0x05 });
		RedirectCall(FrameUpdateCall, MultiplayerUpdate, Sym("FramePin", EmitFramePin(s, log)));
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
		b = File.ReadAllBytes(inPath);
		SymbolMap.Clear();
		string inHash = Sha(b);
		if (inHash == OriginalSha256) BaseName = "original";
		else if (inHash == EightPlayerV14Sha256) BaseName = "8-player mod v1.4";
		else throw new Exception("input is neither the original empire.retail.dll this mod was made for nor the 8-player mod v1.4 built from it (sha256 " + inHash + ")");
		ReadHeaders();
		ReadRelocs();

		var s = new Asm { Va = ImageBase + U32(optOff + 56) };   // the new section goes right after the last one
		uint log = Sym("Log", EmitLog(s));
		ApplyGroupTurns(s, log);
		if (inHash == OriginalSha256) ApplyDesyncFixes(s, log);   // the 8-player mod has its own at these sites
		Sym("LogData", s.Here);
		EmitLogData(s);
		AddSection(SectionName, s.Bytes);

		File.WriteAllBytes(outPath, b);
		return string.Format("patched ({0}): {1} at {2:X8}, {3} bytes; output sha256 {4}", BaseName, SectionName, s.Va, s.Bytes.Count, Sha(b));
	}
}
