# PC Client Stage 2B Configuration Transactions

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
