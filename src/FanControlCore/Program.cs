using System.Text.Json;
using FanControlCore.Discovery;

// 标准输入一行一条请求，标准输出一行一条应答。
// 父进程关掉标准输入，这个进程就结束 —— 而且**走之前把风扇交还固件**。
using var core = new FanCore();
core.Open();

// 崩溃、被杀、Ctrl+C 都要收摊。一个把风扇停在 20% 然后退出的程序会把机器烤了。
AppDomain.CurrentDomain.ProcessExit += (_, _) => core.RestoreAll();
Console.CancelKeyPress += (_, arguments) =>
{
    core.RestoreAll();
    arguments.Cancel = false;
};

string? line;
while ((line = Console.ReadLine()) is not null)
{
    // 有的调用方第一行会带 BOM，容错掉 —— 为一个字节的编码习惯让整条通道断掉不值得。
    line = line.Trim().TrimStart('﻿', '​');
    if (line.Length == 0)
    {
        continue;
    }
    Console.WriteLine(JsonSerializer.Serialize(Handle(core, line)));
    Console.Out.Flush();
}

static Dictionary<string, object?> Handle(FanCore core, string line)
{
    long id = 0;
    try
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        id = request.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
        var operation = request.TryGetProperty("op", out var opElement)
            ? opElement.GetString()
            : null;

        switch (operation)
        {
            case "describe":
                return Ok(id, new Dictionary<string, object?>
                {
                    ["available"] = core.ActiveBackendName is not null,
                    ["backend"] = core.ActiveBackendName,
                    ["reason"] = core.UnavailableReason,
                    ["fans"] = core.Read()
                });

            case "read":
                return Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() });

            case "set":
            {
                var fan = (int)Number(request, "fan");
                var percent = Number(request, "percent");
                return core.TrySetDuty(fan, percent, out var reason)
                    ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() })
                    : Error(id, reason ?? "设不了。");
            }

            case "boost":
            {
                var fan = (int)Number(request, "fan");
                return core.TrySetFullSpeed(fan, out var reason)
                    ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() })
                    : Error(id, reason ?? "开不了全速。");
            }

            case "auto":
            {
                var fan = (int)Number(request, "fan");
                return core.TrySetAutomatic(fan, out var reason)
                    ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() })
                    : Error(id, reason ?? "交还不了。");
            }

            default:
                return Error(id, $"不认识的操作：{operation}");
        }
    }
    catch (Exception error)
    {
        // 一条坏请求不该把整条通道打断。如实回过去，继续听下一条。
        return Error(id, error.Message);
    }
}

static double Number(JsonElement request, string name)
    => request.TryGetProperty(name, out var element)
        ? element.GetDouble()
        : throw new ArgumentException($"这条请求缺少 {name}。");

static Dictionary<string, object?> Ok(long id, IReadOnlyDictionary<string, object?> payload)
{
    var result = new Dictionary<string, object?> { ["id"] = id, ["ok"] = true };
    foreach (var (key, value) in payload)
    {
        result[key] = value;
    }
    return result;
}

static Dictionary<string, object?> Error(long id, string message)
    => new() { ["id"] = id, ["ok"] = false, ["error"] = message };
