# Protocol Traceability

| Client contract | Firmware source | Commit | Test evidence |
| --- | --- | --- | --- |
| BLE common frame and UUIDs | `Docs/BLE_PROTOCOL_V1.md`, `Docs/STAGE5C_A_BLE_TRANSPORT.md` | `b119703cee70b228aa240e7f3477c7dca9946841` | `contracts/ble-v1/golden-frames.json`, TypeScript tests |
| FAST_WEIGHT | `Docs/BLE_PROTOCOL_V1.md`, `Protocol/ble/ble_frame_codec.c` | same | 56-byte reference frame |
| SLOW/CHECKWEIGH | `Docs/BLE_PROTOCOL_V1.md` | same | golden frames and decoder tests |
| BLE commands/config | `Docs/STAGE5C_C_BLE_COMMANDS.md`, `Docs/STAGE5C_D_BLE_CALIBRATION.md` | same | request/response vectors |
| Modbus map 0x0104 | `Protocol/modbus/modbus_register_map.h`, `Tools/stage5b_hw/register_map.py` | same | JSON address-overlap validation |
| FC03/06/16 | `Protocol/modbus/*`, `Tools/stage5b_hw/modbus_frame.py` | same | C# codec tests |

The Firmware `0x0510` hardware/evidence commit is
`5236da68341e8feed0c6f6aedbc5ee52cea91f3b`; its Release ELF SHA-256 is
`15C8269A80962E2CA7329A2623AB69F2286C2E3336373D0B658E2755E2B8DE8D`.
Strict PC persistence binds all three values. Ordinary monitoring does not use
firmware equality as a proxy for protocol compatibility.

PC Stage 1 expands the machine-readable realtime/display/checkweigh field
definitions from `Docs/MODBUS_REGISTER_MAP_V1.md` and
`Protocol/modbus/modbus_register_model.c` at the same pinned commit. Runtime
lookup loads that JSON directly. The FC03 zero-response vector was corrected to
four declared data bytes plus CRC (`... FA 33`); the prior vector had an extra
zero byte inconsistent with its byte count.

The later Stage 5C narrative contains historical hardware notes mentioning map
`0x0102`; the authoritative header and Python constants at the pinned commit
define `MODBUS_REGISTER_MAP_VERSION 0x0104`, which is the client contract here.
