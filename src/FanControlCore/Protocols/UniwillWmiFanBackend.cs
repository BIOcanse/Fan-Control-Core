using System.Runtime.Versioning;
using FanControlCore.Profiles;
using FanControlCore.Transport;

namespace FanControlCore.Protocols;

/// <summary>
/// Uniwill / Tongfang 平台的风扇通道，经由厂商固件的 ACPI WMI 接口。
///
/// 按 README 的分类，这条算 **Raw EC 兜底**：我们读写的是 EC RAM 里的寄存器，
/// 而不是一个"设置风扇转速"的固件方法。所以规矩比别的通道严：
///
/// <list type="bullet">
/// <item>写之前必须过固件指纹检查（<see cref="UniwillFanProfileTable.IsSupported"/>）——
///   固件自己说有风扇控制，我们才写。认不出的机器**只读**。</item>
/// <item>占空比夹在 profile 声明的范围里，而且不允许落进"转不起来"的那一段。</item>
/// <item>任意占空比走自定义风扇表（见 <see cref="UniwillFanTable"/>），
///   它自带一条安全网：温度冲过可控上界时固件会自己拉满速。</item>
/// <item>退出时清掉全速模式位，交还固件 —— 由核心统一调。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UniwillWmiFanBackend : IFanBackend
{
    private readonly UniwillWmiEcTransport transport = new();
    private bool writable;
    /// <summary>自定义风扇表建好了没有。建一次就够，之后只改 0 区的转速。</summary>
    private bool tablesPrepared;

    public string Name => "uniwill-wmi-ec";

    public bool TryOpen(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "这条通道只有 Windows 上有。";
            return false;
        }
        if (!transport.IsAvailable)
        {
            unavailableReason = "这台机器上没有 Uniwill 的 ACPI WMI 接口，或者没有管理员权限。";
            return false;
        }
        if (transport.Read(UniwillFanProfileTable.FanControlStatus) is not { } status)
        {
            unavailableReason = "读不到风扇控制状态。";
            return false;
        }

        // 固件指纹：它自己说支持，我们才敢写。
        writable = UniwillFanProfileTable.IsSupported(status);
        // 读转速这件事不需要它支持 —— 认不出也能只读，那仍然有价值。
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// 两个风扇，能力都由固件指纹决定，不碰 EC。
    /// <c>supportsDuty</c> 在这个固件上恒为 false，原因见 <see cref="TrySetDuty"/>。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe() =>
    [
        new FanDescription(0, "主风扇", writable, SupportsDuty: false),
        new FanDescription(1, "副风扇", writable, SupportsDuty: false)
    ];

    public IReadOnlyList<FanState> Read()
    {
        var mode = transport.Read(UniwillFanProfileTable.FanModeRegister);
        // 全速模式位置着，说明现在是我们（或者别的什么）在直接给定转速。
        var automatic = mode is null
            || (mode.Value & UniwillFanProfileTable.FullFanModeBit) == 0;

        return
        [
            ReadFan(
                0,
                "主风扇",
                UniwillFanProfileTable.MainFanRpmHigh,
                UniwillFanProfileTable.MainFanRpmLow,
                UniwillFanProfileTable.MainFanPwm,
                automatic),
            ReadFan(
                1,
                "副风扇",
                UniwillFanProfileTable.SecondFanRpmHigh,
                UniwillFanProfileTable.SecondFanRpmLow,
                UniwillFanProfileTable.SecondFanPwm,
                automatic)
        ];
    }

    /// <summary>
    /// **这个固件上给不了任意占空比，实测关掉了。**
    ///
    /// 自定义风扇表那条路（<see cref="UniwillFanTable"/>）在参考实现里是对的，
    /// 但这台机器的固件不这么配合，实测踩到两个坑：
    ///
    /// <list type="number">
    /// <item><c>0x07C5</c>（分表使能）写不进去，固件拒绝。</item>
    /// <item>更糟的是它**只能置位不能清位**：试写之后 bit7 从 0 变成 1，
    ///   之后无论怎么写都清不掉。也就是说这条路会把机器推进一个我们出不来的状态。</item>
    /// </list>
    ///
    /// 建表过程中副风扇一度被留在 25% 占空比 —— 比固件自己给的还低。
    /// **一条自己走不回来的路不能留给用户走**，所以这里直接拒绝，
    /// 把 <c>supportsDuty</c> 报成 false，只保留实测可用的全速与自动两档。
    ///
    /// 建表的代码留着（它照参考实现写，在固件配合的机器上应当可用），
    /// 等遇到那样的机器、并且验证过"能清位"之后再打开。
    /// </summary>
    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out _, out failureReason))
        {
            return false;
        }
        failureReason = "这台机器的固件不接受自定义风扇表，只有全速和自动两档。";
        return false;
    }

    /// <summary>
    /// 把自定义风扇表摆好：0 区是可控区，1–15 区是永远到不了的哑区、里面填满速。
    ///
    /// 建表之前先关掉全速模式 —— 那两条路互斥，全速模式开着的时候固件不查表。
    /// </summary>
    private bool EnsureTables(out string? failureReason)
    {
        if (tablesPrepared)
        {
            failureReason = null;
            return true;
        }

        if (!ClearFullFanMode(out failureReason))
        {
            return false;
        }
        if (!SetBit(
                UniwillFanTable.SeparateTablesRegister,
                UniwillFanTable.SeparateTablesBit,
                out failureReason))
        {
            return false;
        }

        for (var fanIndex = 0; fanIndex < 2; fanIndex++)
        {
            var (endBase, startBase, speedBase) = UniwillFanTable.AddressesFor(fanIndex);
            // 0 区：从 0 度一直管到可控上界，转速待会儿再填真值。
            if (!transport.WriteWithRetry(
                    endBase,
                    UniwillFanTable.ControllableUpToCelsius(fanIndex))
                || !transport.WriteWithRetry(startBase, 0)
                || !transport.WriteWithRetry(speedBase, UniwillFanTable.InitialZoneSpeed))
            {
                failureReason = "建风扇表失败（可控区）。";
                return false;
            }
            // 1–15 区：116-117、117-118…… 到不了，而且一律满速当安全网。
            for (var zone = 1; zone < UniwillFanTable.ZoneCount; zone++)
            {
                var start = (byte)(UniwillFanTable.DummyZoneBaseCelsius + zone);
                if (!transport.WriteWithRetry((ushort)(endBase + zone), (byte)(start + 1))
                    || !transport.WriteWithRetry((ushort)(startBase + zone), start)
                    || !transport.WriteWithRetry(
                        (ushort)(speedBase + zone),
                        UniwillFanTable.DummyZoneSpeed))
                {
                    failureReason = "建风扇表失败（哑区）。";
                    return false;
                }
            }
        }

        // 最后才打开"用自定义表"这一位 —— 表还没填完就打开，固件会按半张表跑。
        if (!SetBit(
                UniwillFanTable.UseCustomTablesRegister,
                UniwillFanTable.UseCustomTablesBit,
                out failureReason))
        {
            return false;
        }

        tablesPrepared = true;
        failureReason = null;
        return true;
    }

    private bool SetBit(ushort address, byte bit, out string? failureReason)
        => WriteBit(address, bit, set: true, out failureReason);

    private bool ClearBit(ushort address, byte bit, out string? failureReason)
        => WriteBit(address, bit, set: false, out failureReason);

    private bool WriteBit(ushort address, byte bit, bool set, out string? failureReason)
    {
        if (transport.WriteBit(address, bit, set))
        {
            failureReason = null;
            return true;
        }
        failureReason = $"改不了 0x{address:X4} 的第 0x{bit:X2} 位。";
        return false;
    }

    private bool ClearFullFanMode(out string? failureReason)
        => ClearBit(
            UniwillFanProfileTable.FanModeRegister,
            UniwillFanProfileTable.FullFanModeBit,
            out failureReason);

    /// <summary>全速。置上那一位，两个风扇一起拉满 —— 这个位是整机共用的。</summary>
    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out _, out failureReason))
        {
            return false;
        }
        if (!transport.WriteBit(
                UniwillFanProfileTable.FanModeRegister,
                UniwillFanProfileTable.FullFanModeBit,
                set: true))
        {
            failureReason = "切不到全速模式。";
            return false;
        }
        failureReason = null;
        return true;
    }

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out _, out failureReason))
        {
            return false;
        }
        // 清掉全速模式位。**这个位是整机共用的**，所以两个风扇一起回到自动。
        if (!transport.WriteBit(
                UniwillFanProfileTable.FanModeRegister,
                UniwillFanProfileTable.FullFanModeBit,
                set: false))
        {
            failureReason = "交还固件自动控制失败。";
            return false;
        }

        // 关掉自定义表。**只关"用自定义表"这一位就够** ——
        // 分表那一位（0x07C5 bit7）在这个固件上清不掉，去写它只会得到一次假失败，
        // 而真正决定固件用不用自定义表的是这一位。
        if (!ClearBit(
                UniwillFanTable.UseCustomTablesRegister,
                UniwillFanTable.UseCustomTablesBit,
                out failureReason))
        {
            return false;
        }

        // 下次再设占空比要重新建表：固件自己可能已经把表改回去了。
        tablesPrepared = false;
        failureReason = null;
        return true;
    }

    private bool TryResolveDutyAddress(
        int fanIndex,
        out ushort dutyAddress,
        out string? failureReason)
    {
        dutyAddress = 0;
        if (!writable)
        {
            failureReason = "这台机器的固件没有声明支持风扇控制，只读。";
            return false;
        }
        switch (fanIndex)
        {
            case 0:
                dutyAddress = UniwillFanProfileTable.MainFanDuty;
                break;
            case 1:
                dutyAddress = UniwillFanProfileTable.SecondFanDuty;
                break;
            default:
                failureReason = "没有这个风扇。";
                return false;
        }
        failureReason = null;
        return true;
    }

    private FanState ReadFan(
        int index,
        string name,
        ushort rpmHigh,
        ushort rpmLow,
        ushort pwmAddress,
        bool automatic)
    {
        // 转速是大端：低地址存高字节。
        var rpm = transport.ReadBigEndianWord(rpmHigh, rpmLow);
        var duty = transport.Read(pwmAddress);
        return new FanState(
            index,
            name,
            // 读不到和读到 0 是两回事；0 转也是个有效读数（风扇停了）。
            rpm,
            duty is null ? null : UniwillFanProfileTable.PercentFromDuty(duty.Value),
            duty,
            writable,
            // 见 TrySetDuty 上那段：这个固件上只有全速和自动。
            SupportsDuty: false,
            automatic);
    }

    public void Dispose() => transport.Dispose();
}
