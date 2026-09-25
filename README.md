# Fan Control Core

Fan Control Core is a standalone fan-monitoring and control helper used by [Resource Manager](https://github.com/BIOcanse/Resource-Manager). It exchanges one JSON request and response per line over standard input and output. The repository contains its source; prebuilt Windows packages are published under [Releases](https://github.com/BIOcanse/Fan-Control-Core/releases).

## Interface

| Operation | Purpose |
| --- | --- |
| `describe` | List detected fans, the selected backend, and supported controls. |
| `read` | Read current fan state. |
| `set` | Request a duty percentage for a fan when `supportsDuty` is true. |
| `boost` | Request full speed for a fan. |
| `auto` | Return a fan to automatic firmware control. |

For example, send `{"id":1,"op":"describe"}` followed by a newline. Replies include the same `id` and an `ok` field. A rejected operation returns `ok:false` and an `error` message; it does not silently substitute a different fan speed.

## Hardware coverage

The Windows source contains WMI backends for ASUS, HP, Lenovo, and Acer, plus a Uniwill WMI/EC backend. Linux uses the kernel's `hwmon` interface. Availability and supported operations depend on the particular firmware and device. Only the Uniwill path has been exercised on physical hardware for this release; the other backends should not be taken as hardware-validated compatibility claims. HID and SMM backends are not implemented.

On normal shutdown, the helper attempts to return fans it has overridden to automatic control. Abrupt termination or a failed firmware command can prevent restoration. Confirm that your device supports the requested operation before using manual fan control.

## Build

Install the .NET 10 SDK, then run:

```powershell
dotnet build src/FanControlCore/FanControlCore.csproj -c Release
```

Some Windows hardware-control paths require administrator privileges. The release package is framework-dependent and requires a compatible .NET runtime.

Protocol research referenced public implementations and documentation including NBFC, NBFC-Linux, LenovoLegionLinux, G-Helper, OmenMon, and the Linux `hwmon` ABI. Thanks to their maintainers for making that work available.

Licensed under [Apache-2.0](LICENSE).
