# Strict TCP Preflight

- Status: PASS
- Endpoint: `192.168.1.100:502`, Unit 1
- Firmware: `0x050A`; Map: `0x0104`
- Fresh snapshot wait: 1111 ms
- Active configuration: 4 x 16, 64 registers; snapshots equal
- Staging configuration: 4 x 16, 64 registers
- Mailbox: response token 3, result 0, state 0, last command 4; BUSY/Pending false
- Independent FC03: 100/100 successful
- Writes: FC06 0, FC16 0, mailbox writes 0, SAVE 0, total writes 0
- CANCEL/APPLY/SAVE: not run in this read-only task
