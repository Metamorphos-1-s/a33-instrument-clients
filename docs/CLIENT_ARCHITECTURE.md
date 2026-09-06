# Client Architecture

The WeChat Mini Program discovers a user-selected W02 through the native BLE
API, subscribes to FFE1 notifications and uses FFE2 only for the read-only
GET_DEVICE_INFO and GET_ACTIVE_CONFIG commands in Stage 1. W02 is a transparent
bridge to STM32 USART1. BLE is an auxiliary monitoring path; sequence gaps and
stale data remain visible diagnostics and never become fabricated weight
samples. Stage 1 implementation details are in
`docs/WECHAT_STAGE1_BLE_MONITORING.md`.

BLE V1 sequence diagnostics are domain-aware. FAST, SLOW and CHECKWEIGH share
the firmware telemetry sequence and are the only frames counted by
`sequenceGaps` and `duplicates`. COMMAND_REQUEST and COMMAND_RESPONSE use a
separate command domain and never move the telemetry baseline. Command response
integrity remains enforced independently by frame CRC, payload decoding,
`transaction_id`, `operation`, timeout, byte-identical retry and mismatch
statistics. Connection verification and manual read-only refresh therefore do
not distort telemetry-loss diagnostics.

The Windows client has the same domain model above either Modbus TCP (PC to
CH579 gateway to STM32) or Modbus RTU (PC USART2 RS232/RS485 directly to
STM32). `IModbusTransport` owns bytes and timing, protocol codecs own framing,
and `InstrumentMonitoringService` maps registers to domain values. WPF never builds a
Modbus frame or touches sockets/SerialPort directly.

PC Stage 1 uses `ReadOnlyModbusClient` and `InstrumentMonitoringService` in the
Core layer. The runtime surface exposes FC03 only, validates Map `0x0104`
before decoding, and reads addresses from the shared JSON contract embedded in
the Protocol assembly. Details and verification are in
`docs/PC_STAGE1_MODBUS_MONITORING.md`.

Writes are serialized, bounded by timeout, matched by transaction/request
identity, and retried with identical request bytes where a retry is safe.
Configuration changes are read back; SAVE waits for an explicit completion.

Stage 1 adds production UI and platform transports. Stage 2 adds diagnostics,
configuration and calibration workflows after hardware validation.

The Stage 0-V build and test evidence is maintained in
`docs/STAGE0_TOOLCHAIN_VALIDATION.md`; it intentionally does not claim BLE or
Modbus hardware access.
