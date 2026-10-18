using System.Buffers.Binary;
using System.Text;
using FfxRandomizer.Core.Vbf;

namespace FfxRandomizer.Core.GameData;

/// <summary>
/// Random-encounter battles. jppc/battle/kernel/btl.bin lists, per field (map), groups of formation numbers; each
/// formation is a battle file jppc/battle/btl/{field}_{NN}/{field}_{NN}.bin whose chunk 2 holds up to eight monsters.
/// </summary>
public static class EncounterFiles
{
    public const string Table = GamePaths.Master + "jppc/battle/kernel/btl.bin";
    public const int MonsterSlots = 8;
    public const ushort EmptySlot = 0xFFFF;

    /// <summary>Formation chunk: monster ids (stored as 0x1000 + monster index) start at 0x0C, two bytes each.</summary>
    public const int FirstMonsterOffset = 0x0C;
    private const int MonsterIdBase = 0x1000;

    // FFXRando skips these fields' tables: the system and test maps.
    private static readonly HashSet<string> SkippedFields = ["system", "test00", "test10", "test11"];

    public static string PathFor(string battle) => $"{GamePaths.Master}jppc/battle/btl/{battle}/{battle}.bin";

    /// <summary>
    /// Every battle file named by the encounter tables, in table order. FFXRando reads each shared table only once (under
    /// the first field that uses it); battle files that exist under the other fields' names come after those.
    /// </summary>
    public static IReadOnlyList<string> RandomEncounterBattles(VbfArchive archive)
    {
        byte[] btl = archive.ReadFile(Table);
        int fieldsAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(btl.AsSpan(4));
        int dataAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(btl.AsSpan(8));
        var data = btl.AsSpan(dataAt);

        var first = new List<string>();
        var others = new List<string>();
        var tablesSeen = new HashSet<int>();
        for (int entry = fieldsAt; entry + 14 <= dataAt; entry += 14)
        {
            int tableOffset = BinaryPrimitives.ReadUInt16LittleEndian(btl.AsSpan(entry + 2));
            string field = Encoding.ASCII.GetString(btl, entry + 6, 6);
            if (SkippedFields.Contains(field)) continue;
            var into = tablesSeen.Add(tableOffset) ? first : others;

            int groups = data[tableOffset + 1];
            int at = tableOffset + 2;
            for (int group = 0; group < groups; group++)
            {
                int formations = data[at];
                for (int f = 0; f < formations; f++)
                {
                    string battle = $"{field}_{data[at + 5 + f * 2]:D2}";
                    if (!first.Contains(battle) && !others.Contains(battle)) into.Add(battle);
                }
                at += 5 + formations * 2;
            }
        }
        return [.. first, .. others.Where(b => archive.IndexOf(PathFor(b)) >= 0)];
    }

    /// <summary>Byte offset of the formation chunk (chunk 2) in a battle file.</summary>
    public static int FormationOffset(byte[] battleFile) => (int)BinaryPrimitives.ReadUInt32LittleEndian(battleFile.AsSpan(4 + 2 * 4));

    /// <summary>The monster index in each slot, or -1 for an empty slot.</summary>
    public static int[] Monsters(byte[] battleFile)
    {
        int formation = FormationOffset(battleFile);
        var monsters = new int[MonsterSlots];
        for (int slot = 0; slot < MonsterSlots; slot++)
        {
            ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(battleFile.AsSpan(formation + FirstMonsterOffset + slot * 2));
            monsters[slot] = raw == EmptySlot ? -1 : raw - MonsterIdBase;
        }
        return monsters;
    }

    public static void SetMonster(byte[] battleFile, int slot, int monster) =>
        BinaryPrimitives.WriteUInt16LittleEndian(battleFile.AsSpan(FormationOffset(battleFile) + FirstMonsterOffset + slot * 2), (ushort)(monster + MonsterIdBase));
}
