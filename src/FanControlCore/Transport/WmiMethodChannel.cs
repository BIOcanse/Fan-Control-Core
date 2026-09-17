using System.Management;
using System.Runtime.Versioning;

namespace FanControlCore.Transport;

/// <summary>
/// Windows 上调一个 WMI 类方法的通道。
///
/// 各家 OEM 在 Windows 上的风扇接口长得都是同一个样子：
/// <c>root\WMI</c>（偶尔是别的 scope）下有一个厂商类，类上有一两个方法，
/// 参数塞进去、结果读出来。**不一样的只有三件事**：调哪个类的哪个方法、
/// 参数怎么放、返回里哪一位是转速。那三件事属于各家的协议，
/// 连接、调用、异常、设备失效之后重新解析这些是共同的，都在这里。
///
/// 这一层**不认识风扇**。它不知道什么是转速、什么是占空比 ——
/// 那样各家的语义就会渗进来，最后变成一个谁都看不懂的公共分母。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiMethodChannel : IDisposable
{
    /// <summary>
    /// 厂商方法失效之后要不要重新找设备。
    ///
    /// 驱动被停用、设备被重新枚举之后，缓存的那个 <see cref="ManagementObject"/>
    /// 就是个死引用。发现它不行了就丢掉，下次重新解析 —— 而不是从此报"没有这条通道"。
    /// </summary>
    private readonly object gate = new();
    private readonly string scope;
    private readonly string className;
    private readonly string? instanceName;
    private ManagementObject? device;
    private bool probed;
    private bool disposed;

    public WmiMethodChannel(string scope, string className, string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        this.scope = scope;
        this.className = className;
        this.instanceName = instanceName;
    }

    /// <summary>这台机器上有没有这个类的实例。没有就是没有这条通道。</summary>
    public bool IsAvailable => Resolve() is not null;

    /// <summary>
    /// 调一次方法。
    ///
    /// 返回 null 表示**这次调用没成功**（没有这条通道、方法不认、权限不够）。
    /// 调用方拿到 null 不该当成"值是 0" —— 这两件事在风扇上差得很远。
    /// 出参怎么解读是各家协议的事，这里只把它原样交出去。
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Invoke(
        string methodName,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return null;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                if (arguments is not null)
                {
                    foreach (var (name, value) in arguments)
                    {
                        parameters[name] = value;
                    }
                }
                using var result = target.InvokeMethod(methodName, parameters, null);
                if (result is null)
                {
                    return null;
                }
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in result.Properties)
                {
                    values[property.Name] = property.Value;
                }
                return values;
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                // 接口可能刚刚消失（驱动被停用、设备重新枚举）。丢掉引用，下次重新解析。
                Forget();
                return null;
            }
        }
    }

    /// <summary>
    /// 按**位置**调一次方法，拿回第一个出参。
    ///
    /// 各家 OEM 的 MOF 里参数名五花八门（<c>Arg0</c>、<c>Device_ID</c>、
    /// <c>Data</c>……），而这些接口清一色是"按顺序塞几个整数进去、拿一个整数出来"。
    /// 按名字填就得为每台机器猜一遍名字，猜错要到真机上才发现 —— 而我们手上
    /// 没有那些机器。所以这里按 WMI 自己记录的参数序号（<c>ID</c> 限定符）填，
    /// 名字是什么都不用管。
    /// </summary>
    public uint? InvokeOrdered(string methodName, params uint[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return null;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                var ordered = InParametersByPosition(parameters);
                if (ordered.Count < arguments.Length)
                {
                    // 方法收的参数比我们要给的少：协议对不上，宁可不调。
                    return null;
                }
                for (var index = 0; index < arguments.Length; index++)
                {
                    parameters[ordered[index]] = arguments[index];
                }
                using var result = target.InvokeMethod(methodName, parameters, null);
                return FirstUnsignedValue(result);
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                Forget();
                return null;
            }
        }
    }

    /// <summary>
    /// 造一个这个 scope 里的对象实例，用来当**嵌套入参**。
    ///
    /// 有的厂商（HP 就是）不是"塞几个整数进去"，而是塞一个结构体：
    /// 方法只收一个参数，那个参数本身是另一个 WMI 类的实例。
    /// 造实例这件事和调方法是一回事的两半，所以放在同一层。
    /// </summary>
    public ManagementObject? CreateInstance(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        try
        {
            using var definition = new ManagementClass(
                new ManagementScope(scope),
                new ManagementPath(className),
                null);
            return definition.CreateInstance();
        }
        catch (Exception error) when (error is ManagementException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 调一个**只收一个结构体**的方法，拿回它的结构体出参。
    ///
    /// 同样不按名字找参数：方法只有一个入参，按序号取第一个就是它。
    /// 出参里哪个字段是什么，由各家的协议自己去读。
    /// </summary>
    public ManagementBaseObject? InvokeEmbedded(
        string methodName,
        ManagementBaseObject input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(input);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return null;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                var ordered = InParametersByPosition(parameters);
                if (ordered.Count == 0)
                {
                    return null;
                }
                parameters[ordered[0]] = input;
                using var result = target.InvokeMethod(methodName, parameters, null);
                return FirstEmbeddedValue(result);
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                Forget();
                return null;
            }
        }
    }

    private static ManagementBaseObject? FirstEmbeddedValue(ManagementBaseObject? result)
    {
        if (result is null)
        {
            return null;
        }
        foreach (var property in result.Properties)
        {
            if (property.Value is ManagementBaseObject embedded)
            {
                return embedded;
            }
        }
        return null;
    }

    /// <summary>入参按 WMI 记录的序号排好。序号缺失的排在后面，顺序稳定。</summary>
    private static List<string> InParametersByPosition(ManagementBaseObject parameters)
    {
        var ordered = new List<(int Position, string Name)>();
        foreach (var property in parameters.Properties)
        {
            var position = int.MaxValue;
            try
            {
                if (property.Qualifiers["ID"]?.Value is { } id)
                {
                    position = Convert.ToInt32(id, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (ManagementException)
            {
                // 没有 ID 限定符。保持 int.MaxValue，排在后面。
            }
            ordered.Add((position, property.Name));
        }
        ordered.Sort((left, right) => left.Position != right.Position
            ? left.Position.CompareTo(right.Position)
            : string.CompareOrdinal(left.Name, right.Name));
        return ordered.ConvertAll((entry) => entry.Name);
    }

    private static uint? FirstUnsignedValue(ManagementBaseObject? result)
    {
        if (result is null)
        {
            return null;
        }
        foreach (var property in result.Properties)
        {
            if (property.Value is null)
            {
                continue;
            }
            try
            {
                return Convert.ToUInt32(
                    property.Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception error) when (error is FormatException
                or InvalidCastException
                or OverflowException)
            {
                // 不是个整数，看下一个。
            }
        }
        return null;
    }

    private ManagementObject? Resolve()
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
                var query = instanceName is null
                    ? $"SELECT * FROM {className}"
                    : $"SELECT * FROM {className} WHERE InstanceName = "
                        + $"'{instanceName.Replace(@"\", @"\\")}'";
                using var searcher = new ManagementObjectSearcher(scope, query);
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

    private void Forget()
    {
        lock (gate)
        {
            device?.Dispose();
            device = null;
            probed = false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Forget();
    }
}
