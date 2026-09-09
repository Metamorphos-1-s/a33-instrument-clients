# PC Stage 2B Firmware 0x050C independent hardware review

## Result

The previous review rejection was caused by an over-specified and reversed
per-cycle FC03 expectation. The raw evidence and its summaries agree on the
actual asynchronous polling counts: Cycle A is 74/74 and Cycle B is 73/73.
This is not a hardware inconsistency and no hardware run was repeated.

```text
PC CLIENT STAGE 2B FIRMWARE 0x050C INDEPENDENT EVIDENCE REVIEW PASS
PREVIOUS REVIEW FAILURE WAS AN OVER-SPECIFIED FC03 EXPECTATION
```

## Identities

- Hardware execution tool commit: `5bf8f3c878be626a14f5658256bc34234221ad36`
- Tool version/SHA-256: `1.0.0.0` / `539A38FE45A7FA84DEFD067370B29869F47F33E9F40ED82509907892A647CC8F`
- STM32 Production: `2af4abe39ddb3336d91be64fe8c75425c0dbc1aa`
- STM32 Evidence: `de181c0b3feea020b224915b070dbb2cf5b6219f`
- STM32 Release ELF SHA-256: `895999B7547935827FC64DFF70EE5F4DF7B00E1FD413706E1BFDBEFAD5925E44`
- Firmware/Map/Schema: `0x050C` / `0x0104` / `2`
- Baseline: `a33-stage2b-fw050c-brightness3-20260909`
- Baseline Manifest SHA-256: `CE1D5FDAD5B752CFFCD1107891DB9CCB04A252668B409DC8729B3B361F0EA189`

## Method and raw evidence

The project Validator was run offline: C# `304/304` passed, including the
complete persistence evidence and completion validators. An independent
Python standard-library parser then decoded each raw Modbus ADU, checked
success/error fields, recomputed trace hashes, and derived function/address,
quantity, Mailbox command, token, and Staging values. It agreed with every
summary field. The parser was a one-off external audit script and was not
written into the repository or any raw evidence directory.

Preflight `4797b910-44db-4811-9fc6-8c96be9a7720` is 26/26 FC03, read-only,
and its eight-file tree is 20,218 bytes with stable SHA-256
`6B6081095A8084DC96CE484970012690CFD147BC049A046C32D243571128FFFB`.

Persistence Workflow `07ccb9db-8d8b-4083-bd22-c0e9a0ee6d6f` contains exactly
three formal sessions. Its eleven-file tree is 5,282,570 bytes with stable
SHA-256 `4F7126D1768209A41D8EBD57C33ED1F01EA6FFF7610BAFDA71B55792A99DD07C`.
The raw journal SHA-256 is
`39C5FE0DCEFD85DA05DA794DDFBF48A6908DCD814F51AACD931A10F6D47AD2B8`.

## Cycle evidence

| Session phase | FC03 | FC16 | Staging | Mailbox commands | SAVE token |
|---|---:|---:|---|---|---:|
| `WaitingForFirstReboot` (Cycle A) | 74/74/0 | 5/5/0 | `0x0156=4` | BEGIN, VALIDATE, APPLY, SAVE | 4 |
| `WaitingForSecondReboot` (Cycle B) | 73/73/0 | 5/5/0 | `0x0156=3` | BEGIN, VALIDATE, APPLY, SAVE | 5 |
| `COMPLETE_STABILITY_PASS` (Final) | 8682/8682 | 0 | none | none | none |

Both Cycle summaries and raw traces match exactly. Each Cycle has one
Staging write, four Mailbox writes, one BEGIN, VALIDATE, APPLY and SAVE, zero
CANCEL/FC06, and no unknown or failed request. Token 4 and token 5 are present
in the corresponding raw Mailbox FC16 payloads, journal, and CycleEvidence.

The state transitions are 3/19/A -> 4/20/B after Cycle A and
4/20/B -> 3/21/A after Cycle B. Both manual physical power-cycle events
occurred only after their respective Waiting evidence was written and
validated. Final Active returned to the authoritative 64/64 baseline with
brightness 3, dirty 0, revision 21/21, slot A, sequence 21, idle Mailbox and
IDLE ConfigStore.

## Final stability and Complete replay

`final-stability.json` reports PASS for `601.104088` seconds, 474 samples,
and a maximum sample gap of 2000 ms. Every sample retained the complete Active
configuration, both authoritative hashes, brightness 3, dirty 0, revision
21/21, slot A, sequence 21, idle Mailbox and IDLE ConfigStore. The Final
session has one connection generation (`1 -> 1`), 8682/8682 FC03, zero writes,
zero automatic reconnects, and clean error counters.

The Complete replay returned through the existing Complete journal path before
Transport construction. No fourth session or new evidence file was created;
the zero-connection conclusion is established by the Runner code path, the
unchanged three-session file set, and unchanged raw hashes.

## Archive and boundaries

Derived archive manifest: `Docs/evidence/PC_STAGE2B_050C_FINAL_EVIDENCE_MANIFEST.json`
(SHA-256 `34520E04E4F6F3A69229C58BFB9C81847C83D2450B55653757732F2F0683F8DF`).
The manifest explicitly excludes itself and is outside both raw Workflow
directories. It lists every raw file, length and SHA-256.

The original 0x050A/0x050B Results and all raw 0x050C evidence were not
modified, supplemented, renamed or deleted. This review performed zero real
hardware operations: no TCP, FC03, FC06, FC16, Mailbox, SAVE, reset, ST-Link,
BLE, RS232, RS485 or physical power-cycle operation.

The following remain outside this qualification: real keypad menu SAVE, BLE
persistence, RS232/RS485 persistence, general WPF Flash save, other
configuration persistence, factory reset, calibration and Stage 2C.

`main` remains unchanged and Stage 2B has not been merged to `main`.
