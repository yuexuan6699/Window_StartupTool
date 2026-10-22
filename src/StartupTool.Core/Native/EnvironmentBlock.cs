using System.Runtime.InteropServices;
using System.Text;

namespace StartupTool.Core.Native;

/// <summary>
/// 构建 CreateProcess 用的 Unicode 环境块（D5：每脚本自定义环境变量）。
/// 块格式：KEY=VALUE\0 逐条排列，末尾再一个 \0。
/// </summary>
internal static class EnvironmentBlock
{
    public static IntPtr Build(IReadOnlyDictionary<string, string>? overrides)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
                dict[key] = entry.Value?.ToString() ?? string.Empty;
        }

        if (overrides is not null)
        {
            foreach (var kv in overrides)
                dict[kv.Key] = kv.Value ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');

        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    /// <summary>
    /// 以 CreateEnvironmentBlock 得到的用户环境块为底，叠加自定义变量后生成新块（HGLOBAL，用 Free 释放）。
    /// 原块由调用方用 DestroyEnvironmentBlock 释放。
    /// </summary>
    public static IntPtr RebuildWithOverrides(IntPtr existingUnicodeBlock, IReadOnlyDictionary<string, string>? overrides)
    {
        var dict = ParseUnicodeBlock(existingUnicodeBlock);

        if (overrides is not null)
        {
            foreach (var kv in overrides)
                dict[kv.Key] = kv.Value ?? string.Empty;
        }

        return AllocBlock(dict);
    }

    /// <summary>解析双 NUL 结尾的 UTF-16 环境块。</summary>
    private static Dictionary<string, string> ParseUnicodeBlock(IntPtr block)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (block == IntPtr.Zero) return dict;

        // 先定位结尾的 \0\0
        var chars = 0;
        while (true)
        {
            var c = Marshal.ReadInt16(block, chars * 2);
            if (c == 0 && Marshal.ReadInt16(block, (chars + 1) * 2) == 0) break;
            chars++;
            if (chars > 1_000_000) break; // 保护性上限
        }

        if (chars == 0) return dict;

        var buffer = new char[chars];
        Marshal.Copy(block, buffer, 0, chars);
        var text = new string(buffer);
        foreach (var entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            dict[entry[..eq]] = entry[(eq + 1)..];
        }
        return dict;
    }

    private static IntPtr AllocBlock(Dictionary<string, string> dict)
    {
        var sb = new StringBuilder();
        foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');

        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    public static void Free(IntPtr block)
    {
        if (block != IntPtr.Zero)
            Marshal.FreeHGlobal(block);
    }
}
