# 已退役的 Uniwill 建表说明

下列说明属于未使用的旧建表路径，不代表当前行为。当前实现保留已有温区结构或写入用户明确选择的曲线，不插入高温强制区。

## 旧表说明

```csharp
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
```

## 旧后台说明

```csharp
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
```

## 未调用的建表路径

```csharp
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

```
