# BLE Client Contract V1

Service `0000ffe0-0000-1000-8000-00805f9b34fb`; notify characteristic FFE1;
write characteristic FFE2. Notifications and writes are byte streams, not
frame boundaries. Frames begin `A5 5A`, use version 1, little-endian fields,
and CRC-16/Modbus over sync through payload. Valid payload lengths are FAST 42,
SLOW 59, CHECKWEIGH 8, request 6..128 and response 8..96. Device info must
report protocol 1, schema 2, register map `0x0104` and all required monitoring
capabilities (`0x000003FF`) before the connection can become READY. Firmware
`0x0510` is the current product baseline, but ordinary monitoring accepts a
different firmware value when the actual protocol contract remains compatible;
strict persistence requires exact `0x0510` identity and evidence bindings.
