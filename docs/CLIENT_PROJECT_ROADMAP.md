# A33 Client Project Roadmap

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
