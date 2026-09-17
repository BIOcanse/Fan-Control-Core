namespace FanControlCore.Profiles;

/// <summary>
/// Uniwill 的自定义风扇表 —— 真正能给任意占空比的那条路。
///
/// 固件为每个风扇留了 **16 个温区**，每区三个字节：起始温度、结束温度、转速。
/// 手法是：把 **0 区**做成"从 0 度一直到 115 度，转速就是用户要的那个"，
/// 剩下的 1–15 区做成 116-117、117-118…… 这种**永远到不了**的哑区。
/// 于是任何正常温度下，固件查表都落在 0 区，风扇就跑我们给的那个速度。
///
/// 这不是我们发明的花招，是这类固件本来就提供的机制（tuxedo-drivers 也这么用）——
/// 固件仍然在按自己的逻辑查表，我们只是把表填成了一条水平线。
/// 好处是：温度真的失控冲到 116 度以上时，哑区里填的是满速，
/// 风扇会自己拉满，而不是继续傻乎乎地保持用户设的低速。
/// </summary>
public static class UniwillFanTable
{
    /// <summary>使能位一：两个风扇各用各的表。</summary>
    public const ushort SeparateTablesRegister = 0x07C5;
    public const byte SeparateTablesBit = 1 << 7;

    /// <summary>使能位二：启用 0x0Fxx 这组自定义表。</summary>
    public const ushort UseCustomTablesRegister = 0x07C6;
    public const byte UseCustomTablesBit = 1 << 2;

    /// <summary>CPU 风扇的表：结束温度 / 起始温度 / 转速，各 16 个字节。</summary>
    public const ushort CpuEndTemperature = 0x0F00;
    public const ushort CpuStartTemperature = 0x0F10;
    public const ushort CpuFanSpeed = 0x0F20;

    /// <summary>GPU 风扇的表。</summary>
    public const ushort GpuEndTemperature = 0x0F30;
    public const ushort GpuStartTemperature = 0x0F40;
    public const ushort GpuFanSpeed = 0x0F50;

    public const int ZoneCount = 16;

    /// <summary>
    /// 可控区的上界。
    ///
    /// 到这个温度为止都按用户设的转速走；再往上是哑区，里面填满速 ——
    /// **这是故意留的安全网**：温度真冲上去了，风扇自己拉满，
    /// 而不是继续保持用户设的低速。
    /// </summary>
    public const byte CpuControllableUpToCelsius = 115;
    public const byte GpuControllableUpToCelsius = 120;

    /// <summary>哑区从这个温度往上排，每区一度。</summary>
    public const byte DummyZoneBaseCelsius = 115;

    /// <summary>哑区里一律填满速。见上面那段关于安全网的说明。</summary>
    public const byte DummyZoneSpeed = 0xC8;

    /// <summary>
    /// 表刚建好时 0 区先填这个值。
    ///
    /// 不填 0：这个固件对转速 0 有个专门的行为（先拉到 30% 转三分钟再停），
    /// 而建表阶段我们只是想先把结构摆好，不想触发那一套。
    /// </summary>
    public const byte InitialZoneSpeed = 0x01;

    /// <summary>某个风扇的三张表各自的起始地址。</summary>
    public static (ushort End, ushort Start, ushort Speed) AddressesFor(int fanIndex)
        => fanIndex == 0
            ? (CpuEndTemperature, CpuStartTemperature, CpuFanSpeed)
            : (GpuEndTemperature, GpuStartTemperature, GpuFanSpeed);

    public static byte ControllableUpToCelsius(int fanIndex)
        => fanIndex == 0 ? CpuControllableUpToCelsius : GpuControllableUpToCelsius;
}
