using System.Runtime.Versioning;
using FanControlCore.Transport;

namespace FanControlCore.Protocols;

/// <summary>
/// Lenovo Legion 平台的风扇通道，走联想固件的两个 WMI 类。
///
/// 按 README 的分类这条是 **WMI / ACPI**。它比别家多一处：
/// **读转速和开关全速在两个不同的类上**，所以这里有两条 WMI 通道。
///
/// 协议事实来自公开实现（只取事实，不抄代码）：
/// <list type="bullet">
/// <item>主线内核文档 <c>lenovo-wmi-gamezone</c>：
///   数据类 GUID <c>887B54E3-DDDC-4B2C-8B88-68A26A8835D0</c>
///   （Windows 上是 <c>root\WMI</c> 的 <c>LENOVO_GAMEZONE_DATA</c>），
///   方法 <c>IsSupportFanCooling</c>(12) / <c>SetFanCooling</c>(13) /
///   <c>GetFanCoolingStatus</c>(20)。那个"fan cooling"就是一键强冷。</item>
/// <item>LegionFanSpeedPlugin：<c>root\WMI</c> 的 <c>LENOVO_FAN_METHOD</c>，
///   <c>Fan_GetCurrentFanSpeed(byte FanID)</c> 回转速。</item>
/// </list>
///
/// **这条通道没有在联想机器上实测过。** 边界照样画死：
/// 固件不说自己支持 fan cooling、或者读不出合理转速，整条通道判不可用。
/// 联想的自定义风扇表是 <c>LENOVO_FAN_TABLE_DATA</c> 那一套，各世代还不一样，
/// 没有机器验证之前不碰，所以任意占空比不声称支持。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LenovoWmiFanBackend : IFanBackend
{
    private const string Scope = @"root\WMI";
    private const string GameZoneClass = "LENOVO_GAMEZONE_DATA";
    private const string FanMethodClass = "LENOVO_FAN_METHOD";

    private const string SupportsCoolingMethod = "IsSupportFanCooling";
    private const string SetCoolingMethod = "SetFanCooling";
    private const string GetCoolingMethod = "GetFanCoolingStatus";
    private const string ReadSpeedMethod = "Fan_GetCurrentFanSpeed";

    /// <summary>联想的风扇号从 1 开始：1 是 CPU 侧，2 是显卡侧。</summary>
    private static readonly (uint Id, string Name)[] CandidateFans =
    [
        (1, "CPU 风扇"),
        (2, "显卡风扇")
    ];

    /// <summary>超出这个范围的"转速"不是转速。</summary>
    private const int MaximumPlausibleRpm = 12_000;

    private readonly WmiMethodChannel gameZone = new(Scope, GameZoneClass);
    private readonly WmiMethodChannel fanMethod = new(Scope, FanMethodClass);
    private readonly List<(uint Id, string Name)> fans = [];
    private bool disposed;

    public string Name => "lenovo-legion-wmi";

    public bool TryOpen(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "这条通道只有 Windows 上有。";
            return false;
        }
        if (!gameZone.IsAvailable || !fanMethod.IsAvailable)
        {
            unavailableReason = "这台机器上没有联想的 Legion WMI 接口，或者没有管理员权限。";
            return false;
        }
        // 固件自己说支不支持强冷。它说不支持就别去写 —— 这是它的接口，它说了算。
        if (gameZone.InvokeOrdered(SupportsCoolingMethod) is not { } supported
            || supported == 0)
        {
            unavailableReason = "联想的 WMI 接口在，但这台机器上没有风扇强冷这一项。";
            return false;
        }

        fans.Clear();
        foreach (var candidate in CandidateFans)
        {
            if (ReadRpm(candidate.Id) is not null)
            {
                fans.Add(candidate);
            }
        }
        if (fans.Count == 0)
        {
            unavailableReason = "联想的 WMI 接口在，但它没有报出任何风扇转速。";
            return false;
        }
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// 联想的强冷也是**整机一个开关**，不是每个风扇一个。
    /// 每个风扇都报可写，但它们会一起动。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe()
    {
        var descriptions = new List<FanDescription>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            descriptions.Add(new FanDescription(
                index,
                fans[index].Name,
                Writable: true,
                SupportsDuty: false));
        }
        return descriptions;
    }

    public IReadOnlyList<FanState> Read()
    {
        var automatic = ReadCooling() != true;
        var states = new List<FanState>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            states.Add(new FanState(
                index,
                fans[index].Name,
                ReadRpm(fans[index].Id),
                // 这条通道给的是转速，不是占空比。没有就是没有。
                DutyPercent: null,
                DutyRaw: null,
                Writable: true,
                SupportsDuty: false,
                AutomaticControl: automatic));
        }
        return states;
    }

    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        failureReason = "联想的这条通道只有全速和自动两档，给不了任意占空比。";
        return false;
    }

    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
        => SetCooling(true, "切不到全速模式。", out failureReason);

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
        => SetCooling(false, "交还固件自动控制失败。", out failureReason);

    /// <summary>开关强冷，然后**回读核对** —— 返回码各机型含义不一，不拿它当凭据。</summary>
    private bool SetCooling(bool enabled, string failureText, out string? failureReason)
    {
        if (gameZone.InvokeOrdered(SetCoolingMethod, enabled ? 1u : 0u) is null)
        {
            failureReason = "联想的 WMI 接口不认这次调用。";
            return false;
        }
        if (ReadCooling() != enabled)
        {
            failureReason = failureText;
            return false;
        }
        failureReason = null;
        return true;
    }

    private bool? ReadCooling()
        => gameZone.InvokeOrdered(GetCoolingMethod) is { } status ? status != 0 : null;

    private double? ReadRpm(uint fanId)
    {
        if (fanMethod.InvokeOrdered(ReadSpeedMethod, fanId) is not { } raw)
        {
            return null;
        }
        // 停转的风扇就是 0 转 —— 那是个真读数，不是"读不到"。
        // 读不到由上面那个 null 表示：固件根本没有这个风扇号的时候才走到那里。
        var rpm = (int)raw;
        return rpm >= 0 && rpm <= MaximumPlausibleRpm ? rpm : null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        gameZone.Dispose();
        fanMethod.Dispose();
    }
}
