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
/// <item>退出时清掉全速模式位，交还固件 —— 由核心统一调。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UniwillWmiFanBackend : IFanBackend
{
    private readonly UniwillWmiEcTransport transport = new();
    private bool writable;

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
    /// **这条通道给不了任意占空比。**
    ///
    /// <c>0x0751</c> 的那一位在固件里叫"全速模式"，字面意思就是全速：
    /// 置位之后两个风扇都拉满，写多少占空比它都不看。实测要 70% 得到的是 100%。
    /// 真正的任意占空比要走自定义风扇表（<c>0x0F20</c>/<c>0x0F50</c> 加
    /// <c>0x07C5</c>/<c>0x07C6</c> 的使能位），那是另一套，还没做。
    ///
    /// 所以这里如实拒绝 —— 收到 70% 就给 100%，是在骗用户。
    /// </summary>
    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out _, out failureReason))
        {
            return false;
        }
        failureReason = "这条通道只有全速和自动两档，给不了任意占空比。";
        return false;
    }

    /// <summary>全速。置上那一位，两个风扇一起拉满 —— 这个位是整机共用的。</summary>
    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out _, out failureReason))
        {
            return false;
        }
        if (transport.Read(UniwillFanProfileTable.FanModeRegister) is not { } mode)
        {
            failureReason = "读不到风扇模式。";
            return false;
        }
        var wanted = (byte)(mode | UniwillFanProfileTable.FullFanModeBit);
        if (mode != wanted && !transport.Write(UniwillFanProfileTable.FanModeRegister, wanted))
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
        if (transport.Read(UniwillFanProfileTable.FanModeRegister) is not { } mode)
        {
            failureReason = "读不到风扇模式。";
            return false;
        }

        // 清掉全速模式位就是交还固件。**这个位是整机共用的**，
        // 所以清掉之后两个风扇一起回到自动 —— 这正是我们想要的收摊行为。
        var wanted = (byte)(mode & ~UniwillFanProfileTable.FullFanModeBit);
        if (mode != wanted && !transport.Write(UniwillFanProfileTable.FanModeRegister, wanted))
        {
            failureReason = "交还固件自动控制失败。";
            return false;
        }
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
            // 只有全速和自动两档，见 TrySetDuty 上的说明。
            SupportsDuty: false,
            automatic);
    }

    public void Dispose() => transport.Dispose();
}
