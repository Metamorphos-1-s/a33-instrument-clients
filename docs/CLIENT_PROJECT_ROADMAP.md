# A33 Client Project Roadmap

## Stage 5J Firmware 0x050F rebaseline

The current strict Stage 2B contract is Firmware `0x050F`, Map `0x0104`,
Schema `2`, STM32 product commit `b119703`, evidence commit `5236da6`, and
Release ELF SHA-256 `15C8269A...E8D`. New evidence roots are
`Results/pc_stage2b_050f_baseline` and `Results/pc_stage2b_050f_hw`; no current
workflow scans or falls back to 0x050C or earlier Results.

FC03-only baseline workflow `14f7fb91-51e9-434b-bd9d-a6fe78ef3ecf` and strict
Preflight `63e36d23-96f4-4ea4-95e1-f729408f766e` pass against the real TCP
endpoint. Final state is brightness 3, Active hash `B7D78D5B...18EE73`, slot A,
sequence 25, revision 25/25, Mailbox idle and dirty 0. No FC06, FC16, Mailbox
command, APPLY, SAVE, retry or reboot occurred in either capture.

After the 600-second CH579/RS232 concurrency gate, final strict Preflight
`a4661ca3-01e1-4463-a897-d6325430cdc2` passed 30/30 FC03 and all gates. Its
summary SHA-256 is `AB3256C87571B9E01B6A9DDCAC0C811A982115D552F688306E617B551FD3FCBB`.
The new 0x050F evidence roots use a Git `-text` attribute so captured bytes and
their embedded SHA-256 values survive clean checkout unchanged on every OS.

Ordinary monitoring compatibility is protocol based: WPF requires the exact
register map it decodes; WeChat requires BLE Protocol 1, Schema 2, Map 0x0104
and capability mask 0x000003FF. Firmware equality is deliberately reserved for
the strict persistence product baseline. General WPF Flash SAVE remains closed.

The 0x050F two-SAVE/two-physical-power-cycle qualification and final bound
600-second persistence stability are NOT RUN. Current status is **STAGE 5J
SOFTWARE READY; HARDWARE CLOSURE PENDING**.

## Historical Firmware 0x050C rebaseline

Stage 2B now targets the independent Firmware 0x050C baseline under
`Results/pc_stage2b_050c_baseline` and future workflow evidence under
`Results/pc_stage2b_050c_hw`. It is bound to STM32 Production
`2af4abe39ddb3336d91be64fe8c75425c0dbc1aa` and Evidence
`de181c0b3feea020b224915b070dbb2cf5b6219f`. The legacy Firmware 0x050A and
0x050B Results remain historical only and are rejected by the current contract.
The brightness-3 precondition is complete, but the formal two-SAVE/two-reboot
Stage 2B persistence qualification has not started.

Firmware 0x050C fixed TCP persistence qualification is now complete: brightness
`3 -> 4 -> 3` persisted with two SAVE operations and two operator-confirmed
physical power cycles, followed by a 600-second read-only stability PASS and a
zero-connection Complete replay. Independent evidence review is archived in
`docs/PC_STAGE2B_050C_FINAL_HARDWARE_REVIEW.md`. This does not qualify keypad
menu SAVE, BLE/RS232/RS485 persistence, general WPF Flash save, other
configuration persistence, factory reset, calibration, or Stage 2C.

The 0x050C freeze review found and fixed a Persistence Runner omission: its
default persistence evidence root had remained hard-coded to the historical
`pc_stage2b_050b_hw` directory. The Runner now obtains the sole production root
from `Stage2BDeviceContract.PersistenceEvidenceRoot`, with no public evidence
root override and no legacy fallback. The fresh Preflight
`c63ccf3b-89fc-4e31-a412-06640ca13d05` remains preserved as a read-only audit
record and is not reusable for write authorization. The earlier Cycle A
attempt stopped before Workflow creation, Transport creation, or any write;
Stage 2B SAVE and physical-cycle counts remain `0/2`.

## Delivery stages

| Stage | Scope | Current status |
|---|---|---|
| Stage 0 | Protocol contracts, golden vectors and toolchain validation | Complete |
| Stage 1 | PC and WeChat read-only monitoring | PC complete; WeChat Android complete; iOS not run |
| Stage 2A | Runtime operations | PC software and TCP/RS485 evidence substantially complete; RS232 full post-fix runtime validation and part of evidence closeout remain; WeChat runtime operations are not implemented |
| Stage 2B | Safe PC configuration foundation and fixed TCP persistence verification | RAM transaction foundation complete; clean TCP read-only baseline confirmed; fixed brightness persistence hardware workflow not yet run |
| Stage 2C | Product configuration and calibration | Not started |
| Stage 3 | Cross-platform acceptance, packaging, installation and release | Not started |

## Stage 2B boundary

Stage 2B owns the guarded PC configuration transaction core, Mailbox token and
uncertainty rules, complete 64-register Active/Staging handling, ConfigStore
public-state verification, atomic recovery journal, evidence-bound Strict
Preflight, and one fixed TCP brightness persistence qualification workflow:
brightness `3 -> 4 -> 3`, at most two SAVE requests, each sent once, with two
operator-controlled physical reboots and final 64/64 restoration.

The archived clean Preflight proves that a physical power cycle restored the
device to brightness 3, dirty 0, revision 22/22, slot 2 and sequence 22 while
the Active configuration matched the authority. It does not prove either SAVE
cycle and cannot substitute for a fresh evidence-bound Preflight at write time.

General WPF Flash persistence, productized editing of the remaining safe
fields, mobile configuration, calibration, factory reset, communication/Slave
ID settings and raw register access belong to Stage 2C. BLE, RS232 and RS485
persistence are not claimed by the fixed Stage 2B TCP workflow.

Stage 3 owns distribution and release acceptance. No earlier stage completion
is a product release declaration.

Before the fixed Stage 2B hardware workflow begins, the software scope is
frozen with no general WPF Flash action, evidence-bound current-tool identity,
zero automatic reconnect for persistence sessions, per-session Modbus request
traces, and a fixed final 600-second read-only stability gate. That gate locks
slot, sequence and revisions to the Cycle B second-reboot evidence, rejects any
communication error, reconnect or write, and records every complete Active
sample. A Complete fast return remains offline and is allowed only after the
full journal, baseline, tool identity, hashes, session trace, environment and
stability evidence chain is validated. The real SAVE and two-reboot workflow
has not been run.

The real-transport readiness layer adds an explicit asynchronous Fresh Snapshot
barrier before any persistence Start/Resume and a persistence-only strict error
latch. Background polling and all configuration writes share one exclusion
gate; each write rechecks strict health while holding it. Request trace,
communication diagnostics, strict latch, reconnect and connection generation
jointly determine session cleanliness. Normal WPF monitoring keeps its existing
Degraded recovery behavior.

Strict monitor errors are atomically published before the shared request gate
is released, and each failed request contributes exactly one Diagnostics
error. Manual reboot permission is emitted only after the Waiting session
trace, environment and summary are written and validated. Recovery validates
that prior session offline before opening a Transport.

The reboot decision is based only on evidence reopened from disk. Waiting and
Complete share the same FC16 payload parser and Cycle validator for journal
budgets, SAVE tokens and the fixed Cycle A/B brightness values.

Final stability evidence must cover the beginning, middle and tail of the full
600-second UTC window without reversed timestamps or sampling gaps. Complete
replay remains a zero-connection operation and validates the full journal,
Preflight, final stability, summary, trace, environment and hash chain. After
Stage 2B hardware closure, Validation/Evidence should be frozen as a laboratory
tool. Any physical assembly separation from product Core is deferred for Stage
2C together with productized general configuration, calibration and additional
interfaces.

Formal Stage 2B completion is deliberately limited to exactly three ordered,
clean sessions: Cycle A, Cycle B and final stability. Their command shapes and
aggregate two-SAVE budget must match the journal and traces. Missing, duplicate,
extra or conflicting sessions are preserved for independent review and are not
accepted by the Complete fast path.

The initial formal TCP persistence attempt exposed a repeatable gateway
blackout: immediate Mailbox FC03 after a successful SAVE FC16 returned Modbus
exception `0x0B`, while later public-state inspection confirmed the save. That
workflow is preserved as `RESULT_UNCERTAIN`, and the device has been restored
and power-cycle verified at the authoritative brightness-3 baseline. The
software now applies a fixed one-second SAVE response quiet period while
holding the shared request gate; SAVE remains single-attempt and any later
error still fails the session. A new independent qualification is required.

A subsequent attempt confirmed the delayed SAVE path but found that planned
strict shutdown could cancel an in-flight FC03 and manufacture a Timeout. The
strict stop path now drains the shared request gate before cancellation, while
real timeouts remain latched. The affected workflow is preserved and cannot be
resumed; another independent qualification is required after device recovery.
