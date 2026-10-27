namespace HaloAgent.Desktop.Diagnostics;

/// <summary>读 PE 头判断 DLL/EXE 架构。x64 宿主 + x86 libcef 会报 0xc000007b，提前给中文提示。</summary>
public static class BinaryInspector
{
    public enum PeMachine : ushort
    {
        Unknown = 0x0000,
        I386 = 0x014C,
        Amd64 = 0x8664,
        Arm64 = 0xAA64,
    }

    public static PeMachine ReadMachine(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40) return PeMachine.Unknown;
            if (br.ReadUInt16() != 0x5A4D) return PeMachine.Unknown; // 'MZ'
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length) return PeMachine.Unknown;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return PeMachine.Unknown; // 'PE\0\0'
            return (PeMachine)br.ReadUInt16();
        }
        catch
        {
            return PeMachine.Unknown;
        }
    }

    public static string Describe(PeMachine m) => m switch
    {
        PeMachine.I386 => "32 位 (x86)",
        PeMachine.Amd64 => "64 位 (x64)",
        PeMachine.Arm64 => "ARM64",
        _ => "未知架构",
    };

    public static PeMachine CurrentProcess =>
        Environment.Is64BitProcess ? PeMachine.Amd64 : PeMachine.I386;
}
