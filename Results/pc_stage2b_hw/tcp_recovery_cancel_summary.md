# TCP Recovery CANCEL

- Recovery CANCEL: PASS
- Token: `0x0002` (exactly one request)
- Precondition: active brightness 3, staging brightness 4, mailbox token 1/result 0/last command 9
- Response: token 2, result 0, last command 12
- Active configuration: 64/64 unchanged
- Staging brightness after: observed 4
- Mailbox after: not BUSY/Pending
- Final strict preflight: PASS, 100/100 FC03, all protocol errors 0
- BEGIN/VALIDATE/APPLY/SAVE: 0 in recovery path
- Original failed CANCEL evidence remains preserved; corrected full CANCEL rerun is a separate task.
