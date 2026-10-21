# Soft reset: re-layout crash notes (paused 2026-10-19)

## In-game facts (from in-game testing, softreset2 = commit 019f0d6/e66ce94)
- L3 + R3 (game buttons 9/10) are never detected on PC. L1 + R1 + Start (2, 3, 11) worked in the first build (slot 11).
  To do: make L1 + R1 + Start the default (`SoftReset.Combos[0]`); either drop L3/R3 or mark them "may not work on PC".
- bsil0300 (slot 13) crashes on load whenever the event is edited (batch-1 cutscene skip, soft reset v1, soft reset v2),
  but loads fine with Restore original. So the earlier GameMoment-123 theory doesn't explain the crash (vanilla loads).
- Slot 11: soft reset off + skip on is fine; with softreset2 on, after the stadium skip, "once Tidus stands up" it
  crashes. Suspect the soft-reset edit of the room the stadium warps to (znkd1300's exit; check which room, likely znkd1400).
- bsyt0100 / bsvr0000: no reset during cutscenes with v1 (FieldObject hosts). v2 (Scenario host / own worker) untested
  with a working combo.

## Finding so far: an untracked pointer in bsil0300
`ffxr softreset pointerstmp <ids>` scans the script chunk after the code for aligned words that look like pointers into
the data after the code (value between code end and chunk end, multiple of 4) but aren't a field EventScriptEditor
relocates (`KnownPointerFields`).

    bsil0300 code end 501C chunk 5F30  suspects 1: 56DC->55AC
    bsil0600, bsvr0300, znkd1300, znkd1400, bsil0700, bsvr0000: 0 suspects
    bsyt0100: 12 suspects (11ACC->11620 ...; one, 119F4->13C88, equals a known pointer's target)

bsil0300 is the only one of the sampled "crash" rooms with a candidate, and none of the "works" rooms have one: a word at
chunk 0x56DC holding 0x55AC. Any code growth moves the data after the code, but this word isn't relocated, so it goes
stale. That would explain "crashes whenever edited, whatever the edit". Not yet confirmed. Next steps:
1. Find which structure 0x56DC sits in (shared data, meta struct, event data 0x20, map table 0x24, area names 0x28,
   per-worker tables/private data) and what lives at 0x55AC. FFXDataParser's AtelScriptObject may read it under a name.
2. bsyt0100's 12 suspects: many may be false positives (data that happens to look like offsets); check whether they
   sit in one structure (e.g. inside the meta struct or private data) the editor doesn't model.
3. Run the scan over all 324 field events (`ffxr softreset pointerstmp` with no ids) and over the room after znkd1300.
4. Fix: either relocate that structure's pointers in EventScriptEditor.PointerFields, or make edits append-only so
   nothing after the code moves (append code after all data and point to it). Add a validator: after an edit, every
   aligned word in the moved region that pointed into it before must point to the same moved data after (compare
   before/after by mapping), and a test over all field events.
5. Also worth checking even if the pointer explains bsil0300: total file/chunk size limits, alignment beyond 16 bytes,
   and map-entrance unknown fields (0x00-0x06, 0x18, 0x1C).

## State of the code
- Last good feature commits: 3ef9bd2 (Scenario host / own worker), 019f0d6 (combo), e66ce94 (save recipes).
- WIP commit 5498847 adds the diagnostic (`pointerstmp`, temporary) and `EventScriptEditor.KnownPointerFields`/`Chunk`.
- Cutscene skipping is on hold.
