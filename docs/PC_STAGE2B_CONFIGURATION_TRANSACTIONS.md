# PC Client Stage 2B Configuration Transactions

Firmware `0x050C` rebaseline work uses the centralized `Stage2BDeviceContract`
and separate `Results/pc_stage2b_050c_baseline` and
`Results/pc_stage2b_050c_hw` roots. Firmware `0x050A` and `0x050B` Results
remain historical and are not valid input for a new workflow.
`capture-persistence-baseline` is a fixed TCP/Unit-ID, FC03-only
bootstrap command and requires explicit read-only confirmation before transport
creation. Formal Stage 2B persistence writes are not part of baseline capture.

## 0x050C Runner root correction

The first 0x050C write-qualification invocation stopped before any Persistence
Workflow, TCP Transport, or command was created because the Runner still
selected the historical `Results/pc_stage2b_050b_hw` path. The correction binds
the production Runner to the centralized
`Stage2BDeviceContract.PersistenceEvidenceRoot`, which resolves to
`Results/pc_stage2b_050c_hw`. There is no public arbitrary root override and no
fallback or cross-root scan. The failed invocation produced no additional
hardware evidence and did not change the fresh Preflight
`c63ccf3b-89fc-4e31-a412-06640ca13d05`. A new fresh Preflight is required before
Cycle A; the final two-SAVE qualification has not started.

The accepted candidate is `a33-stage2b-fw050c-brightness3-20260909`, sourced
from TCP capture workflow `fe1430f4-01a1-4832-9dc7-68379713feb7` using source
commit `b853dc5aa75628a3c2ff32cab003a0b15c7ecd0a`. Its PC JSON Active hash is
`B7D78D5B...18EE73`; the separately calculated STM32 binary register hash is
`4BA7DA26...4DD98`. The preconditioning SAVE and physical power cycle were
1/1; formal Stage 2B SAVE and reboot counts remain zero.

## Final 0x050C qualification and independent review

The fixed TCP qualification subsequently completed on Firmware `0x050C` with
Cycle A `3 -> 4`, Cycle B `4 -> 3`, exactly two SAVE requests (tokens 4 and 5),
two physical power cycles, final 600-second read-only stability PASS, and a
zero-connection Complete replay. The independent offline review corrected the
earlier reversed per-cycle FC03 expectation: raw evidence and summaries agree
on Cycle A `74/74` and Cycle B `73/73`; FC03 quantity is asynchronous and is
not a fixed per-cycle contract. See
`docs/PC_STAGE2B_050C_FINAL_HARDWARE_REVIEW.md` and
`docs/evidence/PC_STAGE2B_050C_FINAL_EVIDENCE_MANIFEST.json` for the complete
file-level audit. Keypad menu SAVE and other non-TCP persistence scopes remain
unverified.

## Current status

```text
PC Client Stage 2B PERSISTENCE SOFTWARE READY FOR STRICT READ-ONLY PREFLIGHT
Real SAVE and reboot persistence validation NOT RUN
```

The clean-baseline recovery Preflight workflow
`242f9c19-349d-4d2d-b4ad-0d45e1c2dda4` is independently reviewed and archived.
It proves that a complete physical power cycle restored Mailbox token/command
to 0/0, ConfigStore to IDLE and dirty 0, revision to 22/22, and retained slot
2, sequence 22, brightness 3 and the authoritative Active 64/64 hash. The
evidence is expired and is an audit record only; it cannot authorize a SAVE or
replace the required fresh Preflight and on-site gate.

## WPF Stage 2B surface

The WPF configuration panel exposes only brightness `0-7` and the verified
Refresh, Validate, Apply RAM and Cancel transaction steps. It does not expose a
general Flash SAVE action. Flash persistence is explicitly described as not
open as a product function; the protected fixed hardware-validation runner is
not presented as an end-user save command.

Configuration commands are persistent command instances with shared state
updates. Writes require a fresh MONITORING snapshot, an idle runtime-operation
path and the correct configuration phase. A validated configuration transaction
blocks runtime operations until Apply RAM or Cancel. `RESULT_UNCERTAIN` locks
the entire configuration session, including Refresh; Disconnect remains the
safe exit. Leaving MONITORING discards the WPF configuration session, so
reconnection requires a new configuration Refresh. The remaining safe fields, general Flash
save UI and calibration are Stage 2C work.

## Dirty-baseline strict preflight

Strict read-only Preflight workflow
`04639072-339c-4d35-b495-46f76cf03b1e` ran exactly once from client commit
`464bb380041724964ac0d05f87f2a9be5f315262`. Its eight immutable JSON files
are archived under
`Results/pc_stage2b_hw/20260906T194718854Z_04639072-339c-4d35-b495-46f76cf03b1e/`.

The TCP connection, all 26 FC03 requests, identity, freshness, both Active
snapshots, Mailbox, ConfigStore state mirrors, request trace, environment and
evidence hashes passed. Active was stable 64/64, brightness was 3, and the
canonical hash matched the trusted baseline. The only failed gate was
`config_store_clean`: dirty was 1, current revision was 35, and saved revision
was 22. FC06, FC16, Mailbox writes, configuration commands, SAVE, automatic
retry and device reboot were all zero. No write was used to clean the state.

The reported Staging difference at Active address `0x013E` compares different
metadata semantics: Active `0x013E` is Config Schema V2, while the corresponding
Staging offset `0x017E` is validation result. It is preserved and is not treated
as configuration corruption or repaired by a write.

The user subsequently authorized one manual, no-SAVE preconditioning reboot to
discard the unsaved RAM/runtime state. At this archive point that reboot is
pending operator confirmation. It is separate from the future cycle A/B
persistence-validation reboots, does not write Flash, and does not consume the
fixed SAVE budget of 2.

RS232 Stage 2B configuration transactions:

```text
NOT RUN - intentionally excluded from authorized Stage 2B scope
```

The fixed firmware contract is STM32 commit
`71a61249645bff6249286ac801d7f468786cfe85`, firmware `0x050A`, schema `2`,
and Modbus map `0x0104`. No real Preflight, SAVE, or reboot was performed in
the Persistence Evidence Gate software round.

## Trusted Active baseline

The machine-readable authority is
`Results/pc_stage2b_hw/persistence_baseline_manifest.json`. Its baseline ID is
`a33-stage2b-prewrite-brightness3-20260906` and its Active SHA-256 is:

```text
8C2E5BA6BF39436E5DF2956DE7E09A058DDA330C6462073483E4E70CAD1CACEE
```

The hash canonicalization is fixed: serialize the 64 ushort values in address
order as a compact UTF-8 JSON array with no whitespace, calculate SHA-256, and
encode uppercase hexadecimal.

The baseline comes from `tcp_strict_preflight.json`, captured by client commit
`0fb02a7` and committed in `d17eac2`. It completed at
`2026-09-06T06:33:24.0030252Z`, used two identical 4x16 Active reads, and
recorded Unit 1, firmware `0x050A`, and map `0x0104`. The first preserved Stage
2B Mailbox configuration-write attempt is later, in
`tcp_cancel_transaction_trace.json` and commit `c1c330a`; APPLY RAM evidence is
later still in `827614f`. This establishes the selected snapshot as pre-write.

`tcp_active_config_raw_1.json` and `tcp_active_config_raw_2.json` are retained
unchanged as `PRESERVED_NON_AUTHORITATIVE_EVIDENCE`. They used a single
64-register FC03 before the CH579 4x16 limit was enforced, and their zero-filled
tail conflicts with the later segmented pre-write snapshot. Frequency alone
was not used to select the authority.

## Strict Preflight evidence

Every run creates a unique `<UTC>_<workflow-id>` directory and atomically writes:

```text
preflight-summary.json
request-trace.json
active-snapshot-1.json
active-snapshot-2.json
staging-snapshot.json
mailbox-snapshot.json
config-store-snapshot.json
environment.json
```

The summary records the build-time Git commit embedded in the tool assembly,
tool assembly version and SHA-256, firmware commit, baseline and Manifest
hashes, UTC timing, freshness timing, connection/request/error statistics,
individual gates, and final result. `BUILD_FROM_CURRENT_CHECKOUT` is rejected.

Preflight summary schema 2 binds `environment.json` as an independently useful
run artifact. The environment file has its own schema and records the workflow
ID, exact UTC start and completion times, full 40-character build commit, tool
assembly version and SHA-256, OS, framework, process architecture, and machine
name. Successful and failed Fake/real runs construct it from the same immutable
run values used by the summary, and it remains one of the atomically written,
hashed eight evidence files.

Evidence validation deserializes `environment.json`; a matching file hash is
not sufficient. Workflow ID, UTC offsets and ordering, exact start/end times,
duration, current/full build commit, tool version, tool hash, and non-empty
platform fields must agree with the summary and current client. A missing,
damaged, expired, tampered, non-UTC, or semantically conflicting environment
file rejects persistence before the monitoring or Transport factory is called.
The persistence runner also compares the summary/environment tool version and
SHA-256 with its currently executing HardwareValidation assembly.

Each request trace entry records sequence, UTC start/end, duration, function,
address, count, request and response hex, returned register count, success,
exception category/code, and read purpose. FC03 attempted/succeeded/failed are
derived from the trace. Connection attempts, successes, failures, disconnects,
and automatic retries are separate. Planned freshness reads are not retries.
There is no connection or request retry loop.

All strict reads are limited to 16 registers. Freshness first reads the initial
sample sequence, then reads the realtime block as `16 + 16 + 2` while requiring
the surrounding sequence to be stable and different from the initial value.
The wait interval and deadline are fixed and use an injectable clock.

PASS requires exact identity; advancing fresh sequence; two complete, equal
Active snapshots; both Active snapshots and hashes equal to the Manifest;
brightness 3; complete Staging plus an explicit difference list; idle Mailbox;
known, equal, IDLE ConfigStore mirrors; dirty 0; equal current/saved revision;
valid slot; zero communication errors; zero writes, SAVE, reboot, and automatic
retry. Staging is reported but is not automatically cleaned.

## Preflight binding and on-site gate

Starting persistence requires `--preflight-workflow-id`; no baseline path,
timeout relaxation, bypass, force, address, value, or SAVE-count option exists.
Before creating a Transport, the tool locates exactly one evidence directory,
requires all eight files, verifies their hashes, reparses the request trace and
snapshots, requires PASS from the current client commit, and checks the fixed
device contract, endpoint, zero-write counters, zero retries, baseline hash,
and a maximum age of 15 minutes.

Immediately before the first BEGIN, `ConfigurationPersistenceService` performs
a second read-only on-site gate. It requires exact identity, idle Mailbox, two
stable ConfigStore samples, IDLE/clean/equal revisions, valid slot, Active
64/64 and baseline hash, brightness 3, and slot/sequence/revision equality with
the bound Preflight. Failure occurs before a Journal write cycle, BEGIN,
Staging write, APPLY, or SAVE.

## ConfigStore sampling and SAVE completion

The public contract is state mirror 1 at `0x0030`, dirty at `0x0032`, current
revision at `0x0033-0x0034`, saved revision at `0x0035-0x0036`, Mailbox response
at `0x004C-0x0057`, schema/slot/sequence/state at `0x01C0-0x01C4`. Multiword
values use the configured Modbus word order. Internal operation result,
operation revision, and last error are not exposed and are never fabricated.

The diagnostic and storage blocks require separate FC03 reads. A single mirror
disagreement therefore triggers bounded read-only resampling. Persistent
disagreement, unknown state, or timeout becomes `RESULT_UNCERTAIN`. SAVE
confirmation requires two consecutive equal terminal samples which both meet
the complete dirty/revision/slot/sequence/Active 64/64 invariant. Mailbox
ACCEPTED alone is never Flash confirmation.

## Journal schema 3 and recovery

Schema 3 binds baseline ID/hash/Manifest hash, Preflight workflow/summary hash,
client and firmware commits. It stores separate immutable cycle A and B evidence:
APPLY before/after Active, SAVE-before state, timestamped poll observations,
SAVE confirmation, all Mailbox tokens, SAVE reservation time, request-may-have-
been-sent flag, and reboot identity/Mailbox/ConfigStore/Active/hash evidence.
Cycle B cannot exist before verified cycle A reboot evidence.

Validation rejects invalid GUIDs, unknown enums, wrong fixed values, bad hashes,
Phase/Cycle/authorization-stage contradictions, budget/Token contradictions,
waiting-flag contradictions, missing cycle evidence, event-time reversal,
workflow-directory mismatch, and incomplete completion evidence. Syntax-valid
but logically contradictory JSON cannot authorize a connection or write.

Each SAVE budget is reserved atomically before the only send attempt. Recovery
never retransmits a reserved SAVE. `RESULT_UNCERTAIN` and explicit failure use a
locked authorization stage and cannot continue automatically.

Each persistence-runner process session also atomically records a unique
session summary, environment and shared Modbus client trace in the workflow
directory. FC03, FC06, FC16, Staging writes and Mailbox command IDs 9-13 are
derived from the actual trace, including success/failure and typed Timeout,
CRC, MBAP, TID, Unit, Modbus exception, bad-frame and transport errors. The
persistence connection sets automatic reconnect attempts to zero; SAVE itself
has no retry path.

The persistence runner enables a HardwareValidation-only strict monitoring
mode. `StartMonitoringAsync` starts an asynchronous loop and does not imply
that its first TCP response has completed, so the runner waits on an explicit
current-generation Fresh Snapshot barrier before creating or resuming a
persistence cycle. The barrier is cancellation-aware, bounded, event-driven,
requires map `0x0104`, Monitoring state and a non-stale snapshot, and fails on
disconnect, generation change or a latched error. A synchronous Fake can hide
this first-frame race; the acceptance tests therefore use a genuinely
asynchronous, externally released first FC03 and prove that no journal or
write exists before readiness.

Strict sessions share one exclusion gate between background polling and
configuration commands. Every Mailbox write (BEGIN, VALIDATE, APPLY, CANCEL
and SAVE) and every Staging write rechecks session health after acquiring that
gate and before entering the Transport. Timeout, CRC, MBAP, TID, Unit, Modbus
exception, bad frame, transport, decode or monitor-loop error, stale snapshot,
unexpected disconnect, generation change or reconnect permanently latches the
first UTC error. Later successful reads cannot clear it. Normal WPF monitoring
does not enable this mode and retains its existing Degraded/reconnect behavior.

Strict background errors are published while polling still owns the shared
gate. Diagnostics, the first-error latch, Faulted state and Fresh Snapshot
waiter notification therefore become visible before a queued write can enter.
A failed Modbus request is counted once by the request observer; the outer
monitor path records only decode, slow-poll, event-handler or other internal
failures that have no failed request trace.

Session cleanliness is not inferred from one counter source. The atomic
summary binds actual request trace statistics, a serializable
`CommunicationDiagnostics` snapshot, the strict latch, reconnect count,
connection generation and connection/disconnection counts. Write sessions
record their legitimate commands; the final read-only session additionally
requires all three error views clean, continuous generation, zero reconnect,
all FC03 successful, and every write/command count zero.

Entering a Waiting phase does not authorize a reboot by itself. The runner
first stops the session and atomically writes its trace, environment and
summary, then validates connection 1/1/0/1, generation continuity, zero
reconnect, clean trace/Diagnostics/latch, successful requests and the exact
Cycle command shape. Only then does it print `MANUAL_REBOOT_REQUIRED` and
return 20. Failure prints `DO_NOT_REBOOT`, returns 24 and preserves all
evidence. Recovery validates the preceding Waiting session before creating a
Transport, so it cannot automatically cross an unclean session.

The decision validator accepts the journal path rather than an in-memory
journal. It reopens and fully validates the atomic journal, summary, trace and
environment, then parses each FC16 request payload. The four Mailbox commands
must be BEGIN/VALIDATE/APPLY/SAVE, the SAVE token must match the corresponding
Cycle evidence and journal budget, and the sole Staging payload must write
brightness 4 or 3 to `0x0156` for Cycle A or B respectively. Complete uses the
same Cycle validator, so Waiting and final replay cannot drift apart.

After reboot 1, before cycle B writes, the client requires Mailbox token 0,
IDLE/clean ConfigStore, and exact slot/sequence/revision continuity with cycle A
SAVE confirmation plus the brightness-4 Active snapshot. After reboot 2 it
requires the same metadata continuity with cycle B, then exact baseline 64/64,
baseline SHA-256, and brightness 3. Either mismatch becomes
`RESULT_UNCERTAIN`; no subsequent SAVE is sent.

After the second reboot produces a complete 64/64 restoration, the same
read-only connection runs the fixed final stability service for at least 600
seconds at one-second intervals. Every sample requires the authoritative
Active 64-register array and hash, brightness 3, idle Mailbox, and IDLE/clean,
known and consistent ConfigStore. Slot, active sequence, current revision,
saved revision and required Mailbox fields are locked to the Cycle B second
reboot evidence; later values that merely remain internally consistent still
fail. Any trace error, failed read, automatic reconnect, connection failure,
write function or configuration command vetoes PASS.

`final-stability.json` and its session trace, environment and summary are
atomic and cannot be overwritten. The Complete fast path opens no transport,
but returns PASS only after validating the journal and current workflow,
baseline, client commit, tool version/SHA, every evidence hash, cross-file
phase/time/identity bindings, trace semantics, and all final read-only zero
error/zero-write counters. Missing, damaged, tampered or conflicting evidence
fails offline. The real SAVE and two-reboot workflow has not been run.

The 600-second validator also requires UTC start/completion/sample timestamps,
duration agreement, a first sample within two seconds, no reversed sample and
no interval over two seconds, at least 600 seconds of samples, and a final
sample within two seconds of completion. This covers the beginning, middle and
tail rather than accepting a short sample set with a later completion time.
Persistence environment evidence uses the same schema, UTC, duration, full
commit, current tool version/SHA and non-empty OS/framework/architecture/
machine validation routine as Preflight evidence.

HardwareValidation remains a bounded laboratory acceptance tool and is not a
WPF product feature. After Stage 2B hardware closure it should be frozen;
Stage 2C may evaluate physically separating Validation/Evidence code from the
product Core while implementing general configuration, calibration and other
interfaces.

A Complete PASS requires exactly three ordered formal sessions: Cycle A ending
at `WaitingForFirstReboot`, Cycle B ending at `WaitingForSecondReboot`, and the
final `COMPLETE_STABILITY_PASS` session. Each Cycle has one BEGIN, one
brightness Staging write, one VALIDATE, one APPLY and one SAVE; CANCEL and FC06
are zero. The final session permits only FC03. The offline validator binds all
three IDs, files, hashes, environments, traces and tool identities, and
requires exactly two aggregate SAVE commands. Missing, extra or recovery
sessions require independent review and cannot become formal PASS.

### SAVE response quiet period

The first formal hardware qualification attempt proved that the TCP gateway
can return Modbus exception `0x0B` when Mailbox FC03 is issued immediately
after an acknowledged SAVE FC16. Later read-only inspection proved the same
SAVE completed successfully. The affected workflow remains `RESULT_UNCERTAIN`
and is not eligible for PASS; the device was separately restored to the
authoritative brightness-3 baseline and verified after a physical power cycle.

Command 13 now keeps the shared command gate for a fixed one-second quiet
period after its single FC16 response before reading Mailbox. This prevents
both the transaction and background monitor from issuing FC03 during the
observed Flash blackout. The delay is not configurable through CLI. Any error
after the quiet period still locks the strict session, consumes the already
reserved SAVE budget and cannot trigger a retry.

The next hardware attempt confirmed SAVE and public ConfigStore completion but
exposed a planned-shutdown race: canceling the monitor while an FC03 was in
flight recorded a false Timeout and correctly caused `DO_NOT_REBOOT`. Strict
shutdown now acquires the shared request gate first, lets any in-flight poll
finish, then cancels the idle monitor loop. A real request timeout still
latches before the gate becomes available; only planned-stop cancellation is
avoided. Non-strict WPF stop behavior is unchanged.
