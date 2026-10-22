using System.Text;
using Microsoft.Win32.SafeHandles;
using StartupTool.Core.Native;

namespace StartupTool.Core.Processes;

/// <summary>
/// 脚本输出按行读取器（C8）。
/// 管道字节流按 \n 切行，编码采用"逐行探测"：
/// 先以严格 UTF-8 解码（现代脚本：node/python/pwsh/UTF-8 批处理），
/// 遇非法字节再回退到系统 OEM 代码页（中文系统即 GBK/936，ping/cmd/传统批处理）。
/// 纯 ASCII 行两种编码结果一致；含高位字节的行被误判的概率很低（UTF-8 双字节序列约束严格）。
/// M6 若需要可再扩展为每脚本可配置编码。
/// </summary>
public sealed class OutputLineReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _buffer = new byte[4096];
    private readonly List<byte> _carry = new();
    private readonly Encoding _fallback;
    private bool _disposed;
    private bool _eof;

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public OutputLineReader(SafeFileHandle handle)
    {
        _stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        _fallback = BuildFallbackEncoding();
    }

    /// <summary>读取下一行（不含行尾 \r/\n）；流结束返回 null。</summary>
    public async Task<string?> ReadLineAsync()
    {
        while (true)
        {
            var nl = _carry.IndexOf((byte)'\n');
            if (nl >= 0) return ExtractLine(nl);

            if (_eof)
            {
                if (_carry.Count == 0) return null;
                var last = DecodeLine(_carry);
                _carry.Clear();
                return last;
            }

            int read;
            try
            {
                read = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length)).ConfigureAwait(false);
            }
            catch
            {
                // 管道随进程结束关闭时可能抛异常，按 EOF 处理（返回残余行）
                read = 0;
            }

            if (read == 0)
            {
                _eof = true;
                continue;
            }

            for (var i = 0; i < read; i++)
                _carry.Add(_buffer[i]);
        }
    }

    private string ExtractLine(int nl)
    {
        var end = nl > 0 && _carry[nl - 1] == '\r' ? nl - 1 : nl;
        var line = DecodeLine(_carry.GetRange(0, end));
        _carry.RemoveRange(0, nl + 1);
        return line;
    }

    private static string DecodeLine(List<byte> bytes)
    {
        if (bytes.Count == 0) return string.Empty;
        var raw = bytes.ToArray();
        try
        {
            return Utf8Strict.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return FallbackEncoding.GetString(raw);
        }
    }

    // 回退编码为进程级单例：多数系统启动后 OEMCP 不变
    private static readonly Encoding FallbackEncoding = BuildFallbackEncoding();

    private static Encoding BuildFallbackEncoding()
    {
        try
        {
            // .NET 10 时代码页可能已内置于框架；反射探测 provider，存在则注册（兼容早期版本需包的场景）
            var providerType = Type.GetType(
                "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
            if (providerType?.GetProperty("Instance")?.GetValue(null) is EncodingProvider provider)
                Encoding.RegisterProvider(provider);

            return Encoding.GetEncoding((int)NativeMethods.GetOEMCP());
        }
        catch
        {
            // OEM 代码页实在不可用时退回 UTF-8（替换回退，不抛异常）
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
