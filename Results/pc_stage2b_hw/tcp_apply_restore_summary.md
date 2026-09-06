# TCP Non-Persistent Apply and Restore

- Status: PASS
- Application: brightness `3 -> 4`, Apply RAM confirmed
- Observation: 10 seconds completed
- Restoration: brightness `4 -> 3`, Apply RAM confirmed
- Tokens: `6,7,8,9,10,11` (distinct)
- Only active register `0x0116` changed during application; final 64-register snapshot matched the original
- CANCEL: 0; SAVE: 0; duplicate tokens: 0; unauthorized writes: 0
- Final strict preflight: PASS, 100/100 FC03, all protocol errors 0
- SAVE, reboot, RS485 and RS232 configuration validation were not run.
