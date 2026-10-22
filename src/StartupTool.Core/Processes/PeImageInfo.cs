namespace StartupTool.Core.Processes;

/// <summary>
/// 读取 PE 可选头的 Subsystem 字段，判断可执行文件是 Windows GUI(2) 还是控制台(3)。
/// 用于启动决策（S2）：控制台程序加 CREATE_NO_WINDOW+SW_HIDE 避免黑框；
/// GUI 程序绝不能隐藏首窗口，并在服务端以用户令牌启动时落到 winsta0\default 桌面。
/// 读取失败一律按控制台处理（启动失败的场景随后由 CreateProcess 报错）。
/// </summary>
public static class PeImageInfo
{
    private const ushort ImageSubsystemWindowsGui = 2;
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B;

    /// <summary>Subsystem 在 IMAGE_OPTIONAL_HEADER 内的偏移（PE32 与 PE32+ 相同）。</summary>
    private const int SubsystemOffsetInOptionalHeader = 68;
    private const int CoffHeaderSize = 20;
    private const int PeSignatureSize = 4;

    public static bool IsWindowsGui(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (fs.Length < 64) return false;

            // DOS 头：MZ 标记 + 0x3C 处的 e_lfanew
            if (br.ReadUInt16() != 0x5A4D) return false;
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + PeSignatureSize + CoffHeaderSize + 2 > fs.Length)
                return false;

            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return false; // "PE\0\0"

            var optionalHeaderOffset = peOffset + PeSignatureSize + CoffHeaderSize;
            fs.Position = optionalHeaderOffset;
            var magic = br.ReadUInt16();
            if (magic != Pe32Magic && magic != Pe32PlusMagic) return false;

            long subsystemPosition = optionalHeaderOffset + SubsystemOffsetInOptionalHeader;
            if (subsystemPosition + 2 > fs.Length) return false;
            fs.Position = subsystemPosition;
            return br.ReadUInt16() == ImageSubsystemWindowsGui;
        }
        catch
        {
            // 文件占用/损坏/不可读：按控制台处理，CreateProcess 自身会给出最终错误
            return false;
        }
    }
}
