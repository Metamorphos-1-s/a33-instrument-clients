# A33 Instrument Clients

Client Stage 0 contracts and protocol libraries for the A33 weighing instrument.
The current product baseline is STM32 Firmware `0x050F`, Register Map `0x0104`,
Schema `2`, BLE Protocol `1`: product code commit `b119703cee70b228aa240e7f3477c7dca9946841`
and evidence commit `5236da68341e8feed0c6f6aedbc5ee52cea91f3b`.

This repository contains a native TypeScript WeChat Mini Program core and a
.NET 10/WPF PC skeleton. Hardware communication is intentionally deferred to
Stage 1; protocol tests consume the JSON contracts under `contracts/`.

## Layout

- `contracts/`: machine-readable BLE V1 and Modbus map 0x0104 contracts.
- `apps/wechat-mini/`: TypeScript BLE adapter, stream parser and domain model.
- `apps/pc/`: C# Modbus codec, transport and WPF skeleton.
- `docs/`: architecture and firmware traceability.

## Verification

Run TypeScript tests with Node 20+ after installing the checked-in pnpm lockfile
in `apps/wechat-mini` (`pnpm install --frozen-lockfile && pnpm test`). Build and test the PC solution with
.NET 10 on Windows (`dotnet build apps/pc/A33.Instrument.sln` and
`dotnet test apps/pc/tests/A33.Instrument.Protocol.Tests`). The executed
Stage 0-V gate is recorded in `docs/STAGE0_TOOLCHAIN_VALIDATION.md`; hardware
tests remain outside Stage 0.

The WeChat project uses the validated test AppID in
`apps/wechat-mini/project.config.json`. The DevTools simulator may emit
non-blocking preload advisories for its own `WAAutoService.js` and
`WAServiceMainContext.js` resources; these are documented in the validation
record and are not application errors.

## WeChat Stage 1

The mini program now provides read-only W02 scanning, BLE connection,
FAST/SLOW/CHECKWEIGH monitoring and bounded diagnostics. Only
GET_DEVICE_INFO and GET_ACTIVE_CONFIG are permitted on FFE2. Automated tests
and pending mobile hardware gates are documented in
`docs/WECHAT_STAGE1_BLE_MONITORING.md`.

Security status: the previously exposed AppSecret was rotated on 2026-09-01;
the replacement secret is not stored in this repository.

Xiaomi 15 Android hardware validation passed. A post-fix run exceeded the
600-second gate by fixed-rate telemetry evidence and passed frame, command,
CRC, disconnect, stale-data and application-error criteria. Permission and
Bluetooth recovery, device deduplication and instrument-panel comparison pass.
The Android version was unavailable in phone settings and optional RF metadata
is explicitly marked not recorded. iOS remains not run.

The first Xiaomi 15 Bluetooth-off recovery test exposed an adapter-state
recovery defect. Commit `47824bc` fixes adapter reopen, stale-error clearing
and listener de-duplication; its phone retest passed.

Commit `b65d245` removes BigInt exponentiation that the WeChat runtime lowered
to unsupported `Math.pow`; the post-fix monitor stability retest passed.

Commit `d8ba694` ensures every scan first reopens the adapter, avoiding a
transient `not init` error when permission is revoked. Its first-click denial
path passed on the Xiaomi 15.

The recorded Android RF setup is 5 m through one door, without the instrument
enclosure and using the W02 module's integrated PCB antenna. The phone did not
expose a separate Android version in Settings, so the evidence does not infer
one from HyperOS.

## PC Stage 1

The WPF application now implements read-only Modbus TCP and RTU monitoring with
Map `0x0104` compatibility gating, exact signed-microgram decoding, bounded
diagnostics, stale-data handling and bounded reconnect. Runtime APIs and the UI
expose FC03 only. Software verification is complete; TCP/RS232/RS485 hardware
validation evidence is documented in `docs/PC_STAGE1_MODBUS_MONITORING.md`.

TCP hardware monitoring has passed a 600.040-second read-only run, 10/10
connection cycles and a WPF live-data check. A controlled full power-loss probe
passed bounded fault detection, a new client reconnected cleanly after CH579
restore, and a quick cable-only replug returned the same process to Monitoring.
WPF and instrument-panel comparison passed.

The WPF realtime status decoder now follows the firmware's fixed low-word
layout for `STATUS_FLAGS`, so a stable instrument state is displayed as
`STABLE` instead of being misread as `UNSTABLE`.
COM5 USB-RS232 now passes a 600.087-second read-only run, 10/10 connection
cycles and controlled port-occupancy handling after a CH340 receive-path fix.
Physical unplug/replug recovery passed, and WPF now matches the instrument
panel for weight and stable state. COM5 USB-RS485 also passes a 600.069-second
run and 10/10 connection cycles. PC Client Stage 1 hardware validation is
complete.

PC Stage 2A runtime-operation infrastructure (ZERO/TARE/CLEAR TARE/NET/GROSS)
is complete in the current branch history. TCP and RS485 runtime-operation
evidence exists; RS232 full post-fix runtime validation and part of the Stage
2A evidence closeout remain. WeChat runtime operations are not implemented.

PC Stage 2B provides the safe configuration foundation and a protected fixed
TCP brightness persistence workflow. Brightness RAM apply/cancel and a clean
read-only hardware baseline are verified. General WPF Flash saving is not
exposed. The independent 0x050F FC03-only baseline and strict Preflight pass;
the 0x050F two-SAVE/two-physical-power-cycle qualification is not run.
See `docs/CLIENT_PROJECT_ROADMAP.md` and
`docs/PC_STAGE2B_CONFIGURATION_TRANSACTIONS.md` for the exact boundary.
