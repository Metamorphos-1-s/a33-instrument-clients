# WeChat Mini Program Stage 1 BLE Monitoring

## Scope and architecture

Stage 1 implements read-only BLE monitoring. Pages bind to `DeviceStore`
through `BleMonitorService`; connection policy lives in
`BleConnectionManager`; WeChat API calls live in `WxBleAdapter`. A single
`BleStreamParser` instance processes each connection. No page constructs BLE
frames or invokes state-changing instrument operations.

Data path:

```text
Devices / Monitor / Diagnostics pages
  -> BleMonitorService / DeviceStore
  -> BleConnectionManager / BleCommandClient / BleWriteQueue
  -> WxBleAdapter
  -> FFE0 (FFE1 Notify, FFE2 Write)
```

## Connection state machine

Observable states are `CLOSED`, `OPENING_ADAPTER`, `ADAPTER_UNAVAILABLE`,
`SCANNING`, `CONNECTING`, `DISCOVERING`, `SUBSCRIBING`, `VERIFYING`, `READY`,
`DEGRADED`, `RECONNECT_WAIT`, `DISCONNECTING`, `ERROR` and `INCOMPATIBLE`.
Scanning is bounded to ten seconds and deduplicates opaque `deviceId` values.
`W02_` names are ranked first but are not the sole acceptance rule. The
current connection generation isolates callbacks from old connections.

The client discovers service FFE0 and verifies that FFE1 supports Notify (or
Indicate) and FFE2 supports Write. The FFE1 listener is installed before
notifications are enabled. A new parser and telemetry-sequence baseline are
created for each connection.

## Protocol and data freshness

FFE1 notifications are byte-stream chunks. The shared V1 parser handles split
sync/header/payload/CRC, merged frames, garbage, CRC recovery, sequence wrap,
gaps, duplicates and a bounded 512-byte buffer. FAST, SLOW and CHECKWEIGH are
decoded into transport-independent domain values. Raw frame history is not
retained; diagnostic events are capped at 20.

Client display policy:

- FAST older than 1,000 ms is marked stale.
- FAST older than 3,000 ms is hidden rather than presented as live weight.
- SLOW and CHECKWEIGH older than 3,000 ms are marked stale.
- No valid frame for 5,000 ms moves a connected session to `DEGRADED`.
- A later valid frame returns `DEGRADED` to `READY`.

A telemetry sequence gap or resynchronization updates visible diagnostics but does not
disconnect, erase the last valid sample or synthesize missing samples.

### BLE sequence domains

Deployed firmware `0x050A` has two independent sequence producers. The
`BleTelemetryScheduler.next_sequence` counter is shared by FAST_WEIGHT (`0x01`),
SLOW_STATUS (`0x02`) and CHECKWEIGH_STATUS (`0x03`). The command service uses
the independent `BleCommandService.s_response_sequence` counter for
COMMAND_RESPONSE (`0x81`). COMMAND_REQUEST (`0x80`) is also outside the
telemetry diagnostic domain.

`sequenceGaps` and `duplicates` therefore measure only loss and duplication in
the combined telemetry stream. Command request/response frame sequences do not
change `lastTelemetrySequence` and cannot create telemetry gaps. Command
responses are still CRC checked and decoded; `BleCommandClient` requires both
`transaction_id` and `operation` to match, retains mismatch accounting, and
keeps the existing timeout plus one byte-identical read-only retry. Exact MCU
transaction-cache replay returns the cached response without re-executing the
operation.

`resetStatistics()` clears counters only. It intentionally retains buffered
partial bytes and `lastTelemetrySequence`, so resetting diagnostics cannot
discard half a frame and a real telemetry loss immediately after reset remains
detectable.

The user-observed read-only reproduction was: connection verification added
roughly two to four apparent gaps, steady READY telemetry added none, and each
manual configuration refresh added roughly two to four more. Automatic
verification and refresh each issue GET_DEVICE_INFO and GET_ACTIVE_CONFIG, so
the behavior matches cross-domain command responses being compared against the
telemetry counter. This software fix establishes that those command-correlated
increments were client diagnostic false positives; it does not rewrite any
historical gap, resync or failure evidence. No new raw diagnostic JSON was
provided or fabricated for this change.

STM32 `Docs/BLE_PROTOCOL_V1.md` at fixed commit `71a6124` describes
`frame_sequence` generically as "increments per frame" even though the
implementation has the two producers above. A later STM32 documentation-only
change should define those domains explicitly; the firmware counters must not
be merged for this client fix.

## Read-only commands

Only GET_DEVICE_INFO (`0x01`) and GET_ACTIVE_CONFIG (`0x02`) pass the command
allowlist. FFE2 writes are serialized and split into 20-byte chunks. One
request is in flight; transaction ID and operation must both match. A timed
out read is retried once using byte-identical request data and the same
nonzero transaction ID. Connection changes cancel pending writes and requests.

GET_DEVICE_INFO gates READY on protocol 1, firmware `0x050A`, schema 2 and map
`0x0104`. GET_ACTIVE_CONFIG requires the frozen 55-byte prefix and accepts the
defined optional tails. No TARE, ZERO, configuration mutation, SAVE,
calibration or factory-reset path is exposed.

## Pages

- Devices: adapter state, scan controls, deduplicated devices, RSSI, last-seen
  age and connection action.
- Monitor: display weight, unit, stable/stale state, NET/GROSS/TARE, lock,
  overload, raw values, Runtime Drift, checkweigh lamps, dirty and fault mask.
- Diagnostics: GATT discovery, device versions, RX/frame/command counters,
  gap/duplicate/CRC/resync, copy summary, clear statistics and disconnect.

## Verification

Automated TypeScript gate: 23/23 tests passed. It includes Stage 0 CRC,
golden-frame splitting and signed-int64 coverage plus Stage 1 Mock adapter,
state-machine, old-callback isolation, write queue, read-only command,
byte-identical retry, stale-data and bounded-diagnostic tests.

WeChat Developer Tools `2.02.0` uses base library `3.16.2`. Simulator BLE
unavailability is handled without a blank screen or unhandled rejection. The
two DevTools-internal preload advisories documented by Stage 0-V are not
application warnings.

## Hardware validation

| Check | Android | iPhone |
| --- | --- | --- |
| Device/model, OS, WeChat version | NOT RUN | NOT RUN |
| Permission and Bluetooth-off paths | NOT RUN | NOT RUN |
| W02 discovery and GATT characteristics | NOT RUN | NOT RUN |
| GET_DEVICE_INFO / GET_ACTIVE_CONFIG | NOT RUN | NOT RUN |
| FAST / SLOW / CHECKWEIGH | NOT RUN | NOT RUN |
| 600-second read-only monitoring | NOT RUN | NOT RUN |

Hardware results must be recorded without removing gaps, resync events or
failed attempts. Simulator results are not hardware evidence.

### Xiaomi 15 Android acceptance

The controlled evidence files are under
`Results/wechat_stage1_hw_android/`. They remain `NOT_RUN` until phone system
information, the basic gate and a complete diagnostic summary from a run of at
least 600 seconds are captured.

| Item | Result | Evidence |
| --- | --- | --- |
| Xiaomi 15 system information | PARTIAL | HyperOS 3.0.302.0, WeChat 8.0.69; Android version was not exposed by phone settings |
| WeChat real-device debug startup | PASS | READY screenshot `android_ready_diagnostics.jpg` |
| Bluetooth-off and permission paths | PASS | First-click denial prompt, permission recovery and Bluetooth recovery passed on latest build |
| Scan, deduplication and W02 connection | PASS | Scan, one-entry deduplication and connection matched expectations |
| FFE0 / FFE1 Notify / FFE2 Write | PASS | All three found in screenshot and copied diagnostics |
| GET_DEVICE_INFO 5 times | PASS | 8/8 success, 0 timeout and 0 mismatch |
| GET_ACTIVE_CONFIG 5 times | PASS | 8/8 success, 0 timeout and 0 mismatch |
| FAST / SLOW / CHECKWEIGH display | PASS | Telemetry received and instrument-panel weight/stability comparison passed |
| Intentional and unexpected disconnect recovery | PASS | Six intentional cycles and post-fix Bluetooth-off recovery passed |
| gap/resync recovery | PASS | Gap 2, duplicate 0, CRC 0, resync 0; session remained READY |
| 600-second stability window | PASS | Post-fix run: FAST 5184, SLOW 1037, CHECK 1037, READY, CRC/resync 0, max stale 421 ms, commands 5/5 each, app errors 0 |
| Android conclusion | PASS | Xiaomi 15 read-only BLE monitoring hardware gate passed; unavailable metadata is explicitly recorded |
| iOS | NOT RUN | No iPhone hardware available |

## Known limitations and security

The MCU cannot provide a reliable BLE disconnect indication through W02, and
mobile platforms may use different notification fragmentation and opaque
device identifiers. Reconnect is bounded to three attempts. BLE remains an
auxiliary monitoring interface.

`ROTATED — previous exposed secret invalidated`

Rotation was confirmed on 2026-09-01. The new secret is not stored in source
code or Git. No AppSecret, token, cloud API, upload or release operation is
present here.

### First Android failure record

The first long Xiaomi 15 run produced healthy BLE transport counters but also
raised `MiniProgramError: Cannot convert a BigInt value to a number` when the
WeChat runtime lowered a BigInt exponent expression in `formatMass` to
`Math.pow`. Commit `b65d245` replaces exponentiation with integer
multiplication and adds a true-runtime-compatible formatting regression test.
The tool-internal missing log/config, background-fetch privacy and advertising
scope messages are unrelated to this project. Because an unhandled project
exception occurred, the formal 600-second acceptance window must be restarted.

### Post-fix stability window

The post-`b65d245` Xiaomi 15 run ended at 2026-09-02 00:04:55 CST with
FAST/SLOW/CHECKWEIGH counts 5184/1037/1037. `durationS` was not populated
because the session timer was not started, but the fixed firmware scheduling
rates cap FAST at 5 Hz and SLOW/CHECKWEIGH at 1 Hz. The counts independently
establish approximately 1036.8--1037 seconds of continuous reception, above
the 600-second gate. State remained READY, command reads passed 5/5 for each
operation, maximum stale time was 421 ms, CRC/resync/duplicates were zero and
the user reported no other project error. Four sequence gaps recovered without
disconnect or long stale data. The first failed run remains preserved as
`android_600s_failed_run.json`.

Reported RF setup was 5 m with one door between phone and instrument. The
instrument enclosure was not installed and W02 used its integrated PCB
antenna. Phone orientation and nearby 2.4 GHz/BLE activity were not recorded.
The phone settings did not expose a separate Android version, so no version was
inferred from the reported HyperOS 3.0.302.0 value.

The final permission-denial retest used the latest build containing `d8ba694`.
The first scan attempt directly displayed the permission-denied state without
the former transient `startBluetoothDevicesDiscovery:fail:not init` error.
Restoring permission required no mini-program restart and scanning/connection
resumed normally.

Final Android status:

```text
WeChat Stage 1 ANDROID HARDWARE VALIDATED
Xiaomi 15 read-only BLE monitoring passed
iOS NOT RUN — no iPhone hardware available
Overall dual-platform hardware validation remains open
```
