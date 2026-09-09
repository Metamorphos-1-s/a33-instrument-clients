# PC Stage 2B Firmware 0x050B candidate baseline

The Firmware 0x050B baseline was captured on 2026-09-09 after one explicitly
authorized keypad preconditioning SAVE changed only Active register `0x0116`
from brightness 4 to 3. The operator then performed one complete physical power
cycle. These are preconditioning operations, not either of the two future
Stage 2B qualification SAVE/reboot cycles.

Post-reboot COM5 and TCP read-only evidence agreed on Firmware `0x050B`, Map
`0x0104`, Schema 2, brightness 3, dirty 0, revision 19/19, slot A and sequence
19. STM32 Flash slot A is committed and CRC-valid with battery divider
47000/10000 ohm. No Modbus FC06, FC16, Mailbox command or Stage 2B SAVE was
issued during capture.

The TCP capture workflow is `58827d39-3b1d-4eb6-b054-f0619673ec47`, generated
by client commit `17280de1a0da85fabfc061095679e3695554bf87`. Its 17 requests
were successful FC03 operations. The two Active rounds were each four reads of
16 registers and matched exactly.

- PC compact-JSON Active SHA-256:
  `B7D78D5BD4A6DE0BE2C0DA201C167A0178608FCFF49F6297664C87C79018EE73`
- STM32 big-endian register-byte SHA-256:
  `4BA7DA269DECB38D631ED8076A4FE90B7EF70AA04123CF15D5662B7DBBD4DD98`

The hashes differ because the PC contract hashes the UTF-8 compact JSON array,
while STM32 evidence hashes two big-endian bytes per register. Neither hash may
substitute for the other, for an evidence-file hash, for the ELF hash, or for a
Flash-region hash.

STM32 binding:

- Production source: `a3cc744a6cae85d670008fe6a1bf96eae63bd2a7`
- Evidence HEAD: `34cd363d30d6b0f271f6f9b9ba73c8bd91262dc6`
- Release ELF SHA-256:
  `F6607DE318CE03925C27D8F4B8AA98020F3FC016EF8221229BBEEA8BE16C6FAC`
- Post-SAVE/reboot config-region SHA-256:
  `27A057A6973DFB3E6AE7EBF16B4FC722820340A1A975CC325EEBF8D278D32F16`
- Active slot A SHA-256:
  `92D374C25CFDB51530EC2EAD009E5E03EEEDABFC9F99ACE969A4D5CD36FACE37`

The old `Results/pc_stage2b_hw` tree remains Firmware 0x050A historical evidence
and is not a recovery or baseline input for the new default workflow. At task
start and after capture it contained the same 142 files; the external inventory
SHA-256 was
`E1EA362D3D5EB74166F0FD4F7102BEBF0F69B6247EF546EFF7849A87248E1DD7`.

This baseline proves it predates formal Stage 2B writes through an FC03-only
trace, zero Mailbox commands, and explicit Stage 2B SAVE/reboot counters of zero.
It does not authorize the future two-SAVE persistence qualification.
