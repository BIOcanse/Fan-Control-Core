using FanControlCore.Protocols;

namespace FanControlCore.Discovery;

/// <summary>
/// 风扇核心：挑通道、守规矩、退出时收摊。
///
/// **发现顺序是有讲究的，不是随便排的**（见 README 的通道表）：
/// 有固件接口就不绕到 Raw EC 去，因为固件那条路是厂商自己维护的，
/// 而 Raw EC 是我们照着别人逆出来的寄存器表在猜。
///
/// <list type="number">
/// <item>Linux：先扫 hwmon。内核已经导出可写 PWM 就直接用，不必知道是什么品牌。</item>
/// <item>Windows：先探 OEM 的 WMI / ACPI 接口。</item>
/// <item>都没有：才落到 Raw EC，而且必须匹配固件指纹。</item>
/// </list>
///
/// **同一时刻只有一个后端持有写权限。** 两个后端同时写风扇，
/// 结果是谁也不知道现在是谁说了算。
/// </summary>
public sealed class FanCore : IDisposable
{
    private readonly List<IFanBackend> candidates = [];
    private IFanBackend? active;
    private string? unavailableReason;
    private bool disposed;

    /// <summary>被我们固定过的风扇。退出时要把它们交还固件。</summary>
    private readonly HashSet<int> overridden = [];

    public FanCore()
    {
        // **一级路由**：按平台筛，按通道类别排。这一步只看这台机器是什么系统，
        // 不碰任何硬件 —— 碰硬件是二级路由（各后端的识别依据）的事。
        var platform = OperatingSystem.IsLinux() ? FanPlatform.Linux : FanPlatform.Windows;
        foreach (var route in FanChannelRoute.For(platform))
        {
            candidates.Add(route.Create());
        }
    }

    /// <summary>选中的通道叫什么。还没选中就是 null。</summary>
    public string? ActiveBackendName => active?.Name;

    /// <summary>没选中任何通道时，为什么。</summary>
    public string? UnavailableReason => unavailableReason;

    /// <summary>
    /// **二级路由**：在一级路由排好的候选里，逐条问它的识别依据命中没有。
    ///
    /// 各家的识别依据互不重叠（固件接口的 GUID、固件指纹），所以一台机器
    /// 最多命中一条 —— 这里的先后只决定"先问谁"，不决定答案。
    /// 一条都不命中就如实把每条的理由摆出来，而不是笼统说一句用不了。
    /// </summary>
    public bool Open()
    {
        if (active is not null)
        {
            return true;
        }

        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate.TryOpen(out var reason))
            {
                active = candidate;
                unavailableReason = null;
                return true;
            }
            reasons.Add($"{candidate.Name}：{reason ?? "不可用"}");
        }

        unavailableReason = reasons.Count == 0
            ? "这个平台上还没有可用的风扇通道。"
            : string.Join("；", reasons);
        return false;
    }

    /// <summary>有几个风扇、各自能做什么。不碰硬件。</summary>
    public IReadOnlyList<FanDescription> Describe()
        => active?.Describe() ?? [];

    public IReadOnlyList<FanState> Read()
        => active?.Read() ?? [];

    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        if (active is null)
        {
            failureReason = unavailableReason ?? "没有可用的风扇通道。";
            return false;
        }
        if (!active.TrySetDuty(fanIndex, percent, out failureReason))
        {
            return false;
        }
        // 记下来，退出时要交还固件。
        overridden.Add(fanIndex);
        return true;
    }

    /// <summary>全速。给不了任意占空比的通道也有这一档。</summary>
    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
    {
        if (active is null)
        {
            failureReason = unavailableReason ?? "没有可用的风扇通道。";
            return false;
        }
        if (!active.TrySetFullSpeed(fanIndex, out failureReason))
        {
            return false;
        }
        overridden.Add(fanIndex);
        return true;
    }

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
    {
        if (active is null)
        {
            failureReason = unavailableReason ?? "没有可用的风扇通道。";
            return false;
        }
        if (!active.TrySetAutomatic(fanIndex, out failureReason))
        {
            return false;
        }
        overridden.Remove(fanIndex);
        return true;
    }

    /// <summary>
    /// 退出即恢复。
    ///
    /// **这条不可协商**：一个把风扇停在 20% 然后退出的程序，会把机器烤了。
    /// 所以凡是被我们固定过的风扇，走之前一律交还固件的自动模式；
    /// 交不回去也要继续试下一个，不能因为一个失败就把其余的丢在那儿。
    /// </summary>
    public void RestoreAll()
    {
        if (active is null)
        {
            return;
        }
        foreach (var fanIndex in overridden.ToArray())
        {
            active.TrySetAutomatic(fanIndex, out _);
        }
        overridden.Clear();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        RestoreAll();
        foreach (var candidate in candidates)
        {
            candidate.Dispose();
        }
    }
}
