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
| `temp` | Push temperature samples that a software curve follows. |
| `curve` | Read a fan's current curve and how it is executed (firmware table or software plan). |
| `setcurve` | Apply a temperature/percent curve. `execution` chooses firmware or software execution; when omitted the core default applies, and an unavailable choice is rejected. |

For example, send `{"id":1,"op":"describe"}` followed by a newline. Replies include the same `id` and an `ok` field. A rejected operation returns `ok:false` and an `error` message; it does not silently substitute a different fan speed.

## Explicit redetection

`describe` with `refresh:true` refreshes the selected backend's capabilities without changing fan ownership or settings. If initial discovery found no backend, this explicit request retries the existing discovery route in the same helper. Ordinary descriptions do not retry discovery. Availability, reason and fans in the response reflect the result after redetection. An active backend is retained; redetection never starts another writer or stops an active software plan.

## Hardware coverage

The Windows source contains WMI backends for ASUS, HP, Lenovo, and Acer, plus a Uniwill WMI/EC backend. Linux uses the kernel's `hwmon` interface. Availability and supported operations depend on the particular firmware and device. Only the Uniwill path has been exercised on physical hardware for this release; the other backends should not be taken as hardware-validated compatibility claims. HID and SMM backends are not implemented.

On normal shutdown, the helper attempts to return fans it has overridden to automatic control. Abrupt termination or a failed firmware command can prevent restoration. Confirm that your device supports the requested operation before using manual fan control.

## Build

Install the .NET 10 SDK, then run:

```powershell
dotnet build src/FanControlCore/FanControlCore.csproj -c Release
```

The Windows release package is published as a self-contained single file and does not need a separately installed .NET runtime:

```powershell
dotnet publish src/FanControlCore/FanControlCore.csproj -c Release -r win-x64
```

Some Windows hardware-control paths require administrator privileges.

Protocol research referenced public implementations and documentation including NBFC, NBFC-Linux, LenovoLegionLinux, G-Helper, OmenMon, and the Linux `hwmon` ABI. Thanks to their maintainers for making that work available.

Licensed under [Apache-2.0](LICENSE).

## Firmware curve restoration

Lenovo firmware curve writes require an explicit FANCONTROLCORE_STATE_DIRECTORY supplied by the embedding host. The helper stores the original complete firmware table there, bound to the system UUID and fan ID. Resource Manager supplies its UserData/Control/FanControlCore directory. Missing device identity or unavailable storage prevents a new write.

To cancel a persistent curve, send auto with resetFirmwareCurve set to true. Success includes firmwareCurveReset: true; older helpers do not provide this receipt. Lenovo cancellation restores and verifies the saved table before releasing cooling. Missing or invalid baselines and changed firmware modes or layouts remain failures. Normal shutdown releases temporary overrides and preserves persistent firmware curves. Tables written before baseline support cannot be reconstructed automatically.

Uniwill table changes also require the host-supplied state directory and system UUID. The helper saves each fan's complete original temperature/speed bytes before mutation and the original shared custom-table enable bit. Ordinary auto and shutdown restore a temporary fixed-duty table while retaining a persistent curve. Explicit cancellation restores only the selected fan's original table; the shared bit returns to its original value after the last saved table ownership is released. Failed writes remain eligible for cleanup, failed restoration keeps its baseline for retry, and an unsuccessful first curve does not retain a persistent enable-bit claim. Editing a previously applied curve keeps a temporary copy of that curve until the new edit succeeds. The existing shared software takeover/full-speed mode still affects both fans.

The Uniwill second-fan RPM low byte uses the existing 0x046D reference layout except for the verified MECHREVO JIAOLONG Series with EC project ID 0x1A, where OEM definitions and prior hardware observations identify 0x046B. Model detection is read-only and does not grant write capability. Unknown models or project IDs retain the reference layout; a failed selected register read remains unknown. Injected transport tests cover both layouts and do not access hardware.

These restoration paths have regression coverage with injected EC/WMI transports. Their new restoration behavior has not yet been exercised on physical hardware.

## Restoration failures

A release that returns false or throws an exception remains a failed restoration. The software engine retains the existing restoring entry and retries it on its next tick; the old curve is no longer active. StopAll and the core's RestoreAll continue with the other fans and retain failed ownership for an explicit later retry. Shutdown still attempts restoration, but this does not guarantee recovery from permanent backend faults or abrupt process termination.

## Curve receipts

Curve read and write receipts describe the actual execution with the existing `execution` field. Firmware table receipts may be cached until a write, cancellation or detection reset. Software receipts come from the active core plan and must be read again because a lost temperature feed or manual action may end that plan. An unsuccessful or incomplete receipt cannot confirm application. The regression runner uses only fake backends, never hardware.
