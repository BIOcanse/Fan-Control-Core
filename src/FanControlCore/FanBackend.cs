namespace FanControlCore;

/// <summary>
/// 一个风扇。
///
/// <paramref name="DutyPercent"/> 和 <paramref name="Rpm"/> 都可能读不到 ——
/// **读不到和读到 0 是两回事**，所以用 null 表示读不到，不填一个 0 糊弄过去。
/// </summary>
public sealed record FanState(
    int Index,
    string Name,
    double? Rpm,
    double? DutyPercent,
    /// <summary>
    /// 占空比的原始字节。
    ///
    /// 百分比是按这个平台的满速值换算出来的，而那个满速值各家不一样；
    /// 把原始值也带上，调用方要核对时不必反推。
    /// </summary>
    int? DutyRaw,
    /// <summary>能不能写。只读的风扇照样列出来，用户要看得见它存在。</summary>
    bool Writable,
    /// <summary>
    /// 能不能给任意占空比。
    ///
    /// **有的通道只有"全速"和"自动"两档**（Uniwill 的全速模式位就是这样）。
    /// 那种通道上不该假装能设 70% —— 收到 70% 就给 100%，是在骗用户。
    /// </summary>
    bool SupportsDuty,
    /// <summary>现在是固件在自动控制，还是被我们固定住了。</summary>
    bool AutomaticControl);

/// <summary>
/// 一条风扇控制通道。
///
/// **后端只回答"这台机器上这条通道能做什么"，不做策略。**
/// 用哪条通道、谁持有写权限、退出时怎么恢复，都是核心的事 ——
/// 两处各存一份策略，迟早会对不上。
/// </summary>
public interface IFanBackend : IDisposable
{
    /// <summary>这条通道的名字，出现在 describe 里，方便用户和我们自己定位问题。</summary>
    string Name { get; }

    /// <summary>
    /// 这台机器上这条通道能不能用。
    ///
    /// 用不了要说为什么 —— "认不出这台机器"和"没有权限"对用户是完全不同的两件事。
    /// </summary>
    bool TryOpen(out string? unavailableReason);

    /// <summary>现在有几个风扇、各自什么状态。</summary>
    IReadOnlyList<FanState> Read();

    /// <summary>
    /// 把某个风扇固定在这个占空比。
    ///
    /// 只有 <see cref="FanState.SupportsDuty"/> 为真的通道才支持。
    /// 越界由后端按机型 profile 夹住 —— 但**夹了要说**，不能默默写一个别的值。
    /// </summary>
    bool TrySetDuty(int fanIndex, double percent, out string? failureReason);

    /// <summary>
    /// 全速。
    ///
    /// 这是最低共同能力：几乎每条通道都有"一键强冷"，哪怕给不了任意占空比。
    /// </summary>
    bool TrySetFullSpeed(int fanIndex, out string? failureReason);

    /// <summary>把这个风扇交还给固件自动控制。</summary>
    bool TrySetAutomatic(int fanIndex, out string? failureReason);
}
