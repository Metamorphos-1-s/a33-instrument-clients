# Corrected TCP CANCEL Rerun

- Status: PASS
- Preflight: PASS before and after
- Token sequence: BEGIN `3`, VALIDATE `4`, CANCEL `5`
- Staging write: exactly one write to `0x0156=4`
- Active brightness: remained `3`
- Active configuration: 64/64 unchanged
- Mailbox after: response token `5`, result `0`, last command `12`, not BUSY/Pending
- APPLY: 0; SAVE: 0; duplicate mailbox tokens: 0
- Final strict FC03: 100/100, all protocol errors 0
- Original failed CANCEL and recovery CANCEL evidence remain preserved.
