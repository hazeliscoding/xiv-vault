using System.Text;

namespace XivVault.Tests.Support;

/// <summary>
/// The game's own settings folder as the game leaves it, including the chat logs, screenshots and
/// old copies XIV Vault must skip. Character folders are named by made-up content IDs.
/// </summary>
public sealed class FakeGameSettings
{
    public const string FolderName = "FINAL FANTASY XIV - A Realm Reborn";

    public static readonly string[] Characters = ["FFXIV_CHR004000174A1B2C3D", "FFXIV_CHR0040002E5F6A7B8C"];

    public static readonly string[] CharacterFiles = ["ACQ.DAT", "ADDON.DAT", "COMMON.DAT", "GEARSET.DAT", "HOTBAR.DAT", "KEYBIND.DAT", "MACRO.DAT"];

    private FakeGameSettings(string root) => Root = root;

    public string Root { get; }

    /// <summary>Every file a backup holds, as archive paths.</summary>
    public static IReadOnlyList<string> ArchivePaths =>
    [
        "payload/game/FFXIV.cfg",
        "payload/game/FFXIV_CHARA_01.dat",
        "payload/game/FFXIV_CHARA_02.dat",
        "payload/game/MACROSYS.dat",
        .. Characters.SelectMany(character => CharacterFiles.Select(file => $"payload/game/{character}/{file}")),
    ];

    public static FakeGameSettings Create(string documents)
    {
        var fake = new FakeGameSettings(Path.Combine(documents, "My Games", FolderName));
        fake.Write("<FINAL FANTASY XIV Config File>\r\n\r\n<Display Settings>\r\nScreenMode\t0\r\n", "FFXIV.cfg");
        fake.Write("an older FFXIV.cfg", "FFXIV.cfg.old");
        fake.Write("<FINAL FANTASY XIV Boot Config File>\r\n", "FFXIV_BOOT.cfg");
        fake.Write("shared macros", "MACROSYS.dat");
        fake.Write("appearance 1", "FFXIV_CHARA_01.dat");
        fake.Write("appearance 2", "FFXIV_CHARA_02.dat");
        fake.Write("an older appearance 1", "FFXIV_CHARA_01.dat.old");
        fake.Write("copied settings", "cfgcopy", "FFXIV_CFGCPYCEB40C2CE7B641AE.dat");
        fake.Write("not a real png", "screenshots", "ffxiv_10092026_102200_123.png");
        foreach (var character in Characters)
        {
            foreach (var file in CharacterFiles)
            {
                fake.Write($"{file} of {character}", character, file);
            }

            fake.Write("an older hotbar", character, "HOTBAR.DAT.old");
            fake.Write("[12:00] a private chat line", character, "log", "00000000.log");
        }

        return fake;
    }

    public string Read(params string[] relative) => File.ReadAllText(Path.Combine([Root, .. relative]));

    public byte[] ReadBytes(params string[] relative) => File.ReadAllBytes(Path.Combine([Root, .. relative]));

    public void Write(string contents, params string[] relative)
    {
        var path = Path.Combine([Root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(contents));
    }
}
