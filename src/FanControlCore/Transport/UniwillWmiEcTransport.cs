using System.Management;
using System.Runtime.Versioning;

namespace FanControlCore.Transport;

/// <summary>
/// Uniwill / Tongfang 的 EC RAM 通道，走**厂商固件自己的 ACPI WMI 接口**。
///
/// 这一点很重要：我们没有自己写内核驱动，也没有直接捅 EC 的 I/O 端口 ——
/// 调的是固件已经暴露出来的方法。所以在开着内存完整性（HVCI）的机器上照样能用，
/// 而 WinRing0 那类端口 I/O 驱动在那些机器上根本加载不了。
///
/// 协议取自公开的 GPL 实现（tuxedo-drivers 的 uniwill_wmi）：
/// GUID <c>ABBC0F6F-…</c>，method id 4，入参是一个 UInt64 ——
/// 低 32 位是参数，第 5 个字节是功能号（0 写 / 1 读）。读的参数就是 16 位 EC 地址，
/// 写的参数是 <c>数据 &lt;&lt; 16 | 地址</c>；返回值低字节是数据。
///
/// Windows 上这个 GUID 落在 <c>root\WMI</c> 的 <c>AcpiTest_MULong</c> ——
/// Uniwill 复用了微软 ACPI 示例 MOF 的 GUID，所以类名看着毫不相干。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UniwillWmiEcTransport : IDisposable
{
    private const string Scope = @"root\WMI";
    private const string ClassName = "AcpiTest_MULong";
    private const string MethodName = "GetSetULong";
    private const string InstanceName = @"ACPI\PNP0C14\1_0";
    private const ulong FunctionRead = 1;
    private const ulong FunctionWrite = 0;
    private const int FunctionBitShift = 40;
    private const uint CallFailed = 0xFEFE_FEFE;

    /// <summary>
    /// 一次读取要连续读到几次相同才作数。
    ///
    /// 直接读 EC 会偶发撞上固件自己的写入，返回一个假的 <c>0x00</c>。
    /// 在风扇这件事上，把假的 0 当成"转速为零"会直接导致误判。
    /// </summary>
    private const int AgreeingReads = 3;
    private const int ReadAttempts = 6;

    private readonly object gate = new();
    private ManagementObject? device;
    private bool probed;
    private bool disposed;

    /// <summary>这台机器有没有这条通道。</summary>
    public bool IsAvailable => ResolveDevice() is not null;

    /// <summary>读一个 EC 字节。读到连续一致的值才返回，否则 null。</summary>
    public byte? Read(ushort address)
    {
        lock (gate)
        {
            var agreed = 0;
            byte? last = null;
            for (var attempt = 0; attempt < ReadAttempts; attempt++)
            {
                if (Invoke((ulong)address | (FunctionRead << FunctionBitShift)) is not { } value)
                {
                    return null;
                }
                var current = (byte)(value & 0xFF);
                if (last == current)
                {
                    agreed++;
                    if (agreed >= AgreeingReads - 1)
                    {
                        return current;
                    }
                }
                else
                {
                    agreed = 0;
                    last = current;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 读两个相邻字节拼成的 16 位**大端**值。转速就是这么存的：低地址放高字节。
    /// </summary>
    public int? ReadBigEndianWord(ushort highAddress, ushort lowAddress)
        => Read(highAddress) is { } high && Read(lowAddress) is { } low
            ? (high << 8) | low
            : null;

    /// <summary>
    /// 写一个 EC 字节，然后**回读核对**。
    ///
    /// 这个固件接口对任何调用都返回成功，所以"写成功"只能由回读来定义。
    /// </summary>
    public bool Write(ushort address, byte value)
    {
        lock (gate)
        {
            if (Invoke((ulong)address
                | ((ulong)value << 16)
                | (FunctionWrite << FunctionBitShift)) is null)
            {
                return false;
            }
        }
        return Read(address) == value;
    }

    private uint? Invoke(ulong data)
    {
        if (ResolveDevice() is not { } target)
        {
            return null;
        }
        try
        {
            using var parameters = target.GetMethodParameters(MethodName);
            parameters["Data"] = data;
            using var result = target.InvokeMethod(MethodName, parameters, null);
            return result?["Return"] is uint returned && returned != CallFailed ? returned : null;
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException)
        {
            // 接口可能刚刚消失（驱动被停用），下次重新解析。
            lock (gate)
            {
                device?.Dispose();
                device = null;
                probed = false;
            }
            return null;
        }
    }

    private ManagementObject? ResolveDevice()
    {
        lock (gate)
        {
            if (probed)
            {
                return device;
            }
            probed = true;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    Scope,
                    $"SELECT * FROM {ClassName} WHERE InstanceName = '{InstanceName.Replace(@"\", @"\\")}'");
                foreach (var found in searcher.Get())
                {
                    device = (ManagementObject)found;
                    return device;
                }
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException)
            {
                // 没有这条通道，或者没有管理员权限。两者都归"用不了"。
            }
            return device;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        lock (gate)
        {
            device?.Dispose();
            device = null;
        }
    }
}
