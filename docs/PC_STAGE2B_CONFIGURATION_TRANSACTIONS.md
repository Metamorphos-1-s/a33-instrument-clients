# PC Client Stage 2B Configuration Transactions

## Current Status

This branch contains the safe configuration transaction core. Hardware
configuration validation is not run. The repository's actual Stage 2A record
has RS232 skipped, so Stage 2B remains software-only and hardware-pending.

Baseline:

- Client Stage 2A: `b61dabd`
- STM32 fixed firmware: `71a6124`
- Map: `0x0104`

## Scope Implemented

`ConfigurationTransactionService` provides a serialized refresh/edit/apply
state model with immutable active snapshots, field-level differences, local
range checks, device token/command matching, and `RESULT_UNCERTAIN` handling
after a write may have been sent. The service reads the complete active range
`0x0100-0x013F` and exposes only a conservative non-calibration whitelist:
brightness, startup auto-zero enable, and profile filter/stability fields.

The device transaction command IDs are taken from the fixed firmware mailbox:
BEGIN `9`, VALIDATE `10`, APPLY RAM `11`, CANCEL `12`, SAVE `13`. Calibration,
factory reset, communication parameters, Slave ID, and unknown fields are not
exposed. SAVE is never automatic.

The brightness field is active register `0x0116`, staging register `0x0156`,
one unsigned register, and the fixed firmware range is `0-7` (not `0-255`).
This range is enforced by `ConfigEdit_SetIntegerField` and
`ConfigEdit_Validate` in STM32 commit `71a6124`.

## Hardware Gate

No configuration write, Apply RAM, SAVE, communication-parameter switch, or
configuration hardware evidence has been executed in this branch. TCP and
RS485 Stage 2A runtime evidence remains separate. RS232 was explicitly skipped
by the user and is not treated as PASS or WAIVED here.

## Stage 2B-P TCP preflight

The current TCP device at `192.168.1.100:502`, Unit 1, reports Map `0x0104`
and firmware `0x050A`. Two complete active-configuration reads of 64 registers
matched exactly. The active brightness is register `0x0116`, value `3`; its
staging counterpart is `0x0156`, value `3`. The fixed firmware validates this
unsigned field in range `0-7`, so the selected temporary value is `4` and the
restore value is `3`. The mailbox response range `0x004C-0x0057` was all zero,
with no BUSY or pending transaction. A separate read-only run completed 100/100
FC03 cycles with zero timeout, MBAP, Unit, exception or bad-frame errors. See
`Results/pc_stage2b_hw/tcp_preflight_identity.json` and the other `tcp_*`
preflight files. No FC16 or SAVE was sent.

## Verification

The Stage 2B core compiles with the existing PC solution. Configuration contract
unit coverage checks that only safe non-calibration fields are exposed and that
out-of-range values are rejected by metadata. Hardware validation remains
pending explicit authorization, original-config snapshot, safe test field, and
rollback confirmation.
