# PC Client Stage 2B Configuration Transactions

## Current status

```text
PC Client Stage 2B RAM TRANSACTIONS CODE COMPLETE
PC Client Stage 2B PERSISTENCE SOFTWARE READY FOR READ-ONLY PREFLIGHT
Real SAVE and reboot persistence validation NOT RUN
```

RS232 Stage 2B configuration transactions:

```text
NOT RUN - intentionally excluded from authorized Stage 2B scope
```

The fixed firmware contract is STM32 commit
`71a61249645bff6249286ac801d7f468786cfe85`, firmware `0x050A`, schema `2`,
and Modbus map `0x0104`. No real SAVE or device reboot was performed while
closing the persistence software path.

## Fixed workflow

The only persistence workflow exposed by the hardware-validation tool is the
fixed brightness sequence below. Register addresses, values, SAVE count, and
reboot behavior are not command-line options.

1. Read identity, Mailbox, ConfigStore, and the complete 64-register active configuration.
2. Require active brightness `3` at `0x0116`.
3. BEGIN, stage brightness `4` at `0x0156`, VALIDATE, and APPLY RAM.
4. Reserve one SAVE in the atomic journal, then send command `13` exactly once.
5. Confirm persistence from the public register invariants and stop at `WAITING_FOR_FIRST_REBOOT`.
6. After a manual reboot, read identity and the device's actual Mailbox token before any write, then verify brightness `4`.
7. Repeat the guarded cycle for brightness `3` and stop at `WAITING_FOR_SECOND_REBOOT`.
8. After the second manual reboot, read the Mailbox token again and verify the final active configuration matches the original snapshot 64/64.

The immutable SAVE budget is `2`. SAVE is never retried, and the software never
reboots a device. A new non-zero token is allocated for every BEGIN, VALIDATE,
APPLY, CANCEL, and SAVE command. Token `0` is skipped, tokens do not repeat in a
process, SAVE tokens do not repeat across recovered processes, and token state
is rebuilt from the device after each reboot.

## Public ConfigStore contract

| Field | Address | Width |
|---|---:|---:|
| ConfigStore state mirror 1 | `0x0030` | 1 |
| `config_dirty` | `0x0032` | 1 |
| current revision | `0x0033-0x0034` | 2 |
| saved revision | `0x0035-0x0036` | 2 |
| Mailbox response | `0x004C-0x0057` | 12 |
| storage schema | `0x01C0` | 1 |
| active slot | `0x01C1` | 1 |
| active sequence | `0x01C2-0x01C3` | 2 |
| ConfigStore state mirror 2 | `0x01C4` | 1 |

Multi-register revisions and sequence values use the device's configured
Modbus word order. Slot values are `0` none, `1` A, and `2` B. A changed SAVE
increments the sequence using the firmware wrap rule and writes the inactive
slot. ConfigStore states are:

| Value | State |
|---:|---|
| 0 | IDLE |
| 1 | PREPARE |
| 2 | ERASE_PAGE_0 |
| 3 | ERASE_PAGE_1 |
| 4 | PROGRAM_BODY |
| 5 | VERIFY_BODY |
| 6 | PROGRAM_COMMIT |
| 7 | VERIFY_FINAL |
| 8 | COMPLETE |
| 9 | ERROR |

Unknown state values and disagreeing mirrors are never successful. Firmware
operation result, operation revision, and last error exist internally but are
not exposed by Modbus `0x0104`; the client must not fabricate them or claim a
specific internal failure cause.

## SAVE completion

Mailbox `ACCEPTED` means only that the deferred SAVE request was accepted. It
is not Flash confirmation. `SAVE_CONFIRMED` requires all of the following:

- Both state mirrors are known and equal.
- The observed terminal state is IDLE or COMPLETE, allowing for a transient COMPLETE that polling may miss.
- `config_dirty` is zero.
- Current revision equals saved revision and the pre-SAVE current revision.
- Active sequence advances according to the firmware rule.
- Active slot changes according to the firmware A/B rule.
- The complete active configuration matches the current cycle expectation 64/64.
- No ERROR, unknown state, or contradictory public field was observed.

Seeing only ACCEPTED, IDLE, dirty clear, or equal revisions is insufficient.
ConfigStore ERROR is a definite failure, but the detailed internal cause is not
visible. A timeout without the complete invariant set is `RESULT_UNCERTAIN`.

## RESULT_UNCERTAIN

Ambiguous SAVE transmission, response loss, unrecoverable communication loss,
poll timeout, state-mirror disagreement, contradictory state, journal/device
conflict, ambiguous APPLY/CANCEL, or any condition that could cause duplicate
Flash writes locks the workflow in `RESULT_UNCERTAIN`.

The reserved SAVE remains consumed. The client does not resend SAVE, reboot,
continue the next cycle, or automatically perform the restore SAVE. Only
read-only diagnosis and manual disposition are allowed.

## Journal and recovery

Each authorized run creates a unique directory:

```text
Results/pc_stage2b_hw/<UTC timestamp>_<workflow GUID>/
```

The schema-versioned journal contains the original and expected 64-register
snapshots, cycle and phase, fixed budget, reservations, SAVE tokens, Mailbox
tokens around both reboots, ConfigStore snapshots, revision/slot/sequence
evidence, results, uncertainty reason, final comparison, and append-only event
history. Updates use a same-directory temporary file, complete flush, and rename
without deleting the old journal first.

A damaged, incomplete, incompatible, or contradictory journal cannot authorize
a write. When a process exits after reserving a SAVE, recovery never resends it:
public registers must prove completion, otherwise the result is uncertain.

## Strict read-only preflight

Strict preflight uses only FC03 and reports counts from the actual request
trace. It validates firmware, schema, map, Unit ID, a fresh realtime snapshot,
two stable active snapshots, the staging block, complete Mailbox response,
both ConfigStore mirrors, dirty, revisions, slot, sequence, and all protocol
error counters. Any write count, identity mismatch, BUSY/Pending state,
ConfigStore inconsistency, changing active configuration, or communication error
fails the gate. Every run uses a new evidence directory and cannot overwrite the
31 existing Stage 2B evidence files.
