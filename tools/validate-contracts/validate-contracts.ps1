$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$ble = Get-Content (Join-Path $root 'contracts/ble-v1/constants.json') -Raw | ConvertFrom-Json
$goldenBle = Get-Content (Join-Path $root 'contracts/ble-v1/golden-frames.json') -Raw | ConvertFrom-Json
$goldenModbus = Get-Content (Join-Path $root 'contracts/modbus-v0104/golden-frames.json') -Raw | ConvertFrom-Json
$map = Get-Content (Join-Path $root 'contracts/modbus-v0104/register-map.json') -Raw | ConvertFrom-Json
if ($ble.firmwareVersion -ne '0x0510' -or $ble.schemaVersion -ne 2 -or $ble.registerMap -ne '0x0104' -or
    $ble.persistentFormatVersion -ne 3 -or $ble.slotSchemaVersion -ne 3 -or $ble.slotPayloadLength -ne 281 -or
    $ble.firmwareProductionCommit -ne '785ce21e181fcf00aa371290facec6b7a14e484e' -or
    $ble.firmwareEvidenceCommit -ne '8ef44f5643b83bec1a677b047f7535e5668ce229' -or
    $ble.firmwareReleaseElfSha256 -ne '82E726F5B32A0DE36A5E686F62A937EC4FD9CBB488DB9733E83D2062673EF486') { throw 'BLE compatibility constants mismatch' }
if ($ble.compatibility.monitoringFirmwarePolicy -ne 'protocol-contract' -or
    $ble.compatibility.strictPersistenceFirmware -ne '0x0510' -or
    $ble.compatibility.requiredMonitoringCapabilities -ne '0x000003FF') { throw 'BLE compatibility policy mismatch' }
if ($ble.version -ne 1 -or $ble.messageTypes.FAST_WEIGHT -ne 1 -or $ble.messageTypes.SLOW_STATUS -ne 2 -or $ble.messageTypes.CHECKWEIGH_STATUS -ne 3) { throw 'BLE message constants mismatch' }
$telemetryDomain = @($ble.sequenceDomains.telemetry)
if (($telemetryDomain -join ',') -ne '1,2,3' -or $ble.sequenceDomains.commandRequest[0] -ne 128 -or $ble.sequenceDomains.commandResponse[0] -ne 129) { throw 'BLE sequence-domain contract mismatch' }
$ops = @($ble.operations.PSObject.Properties.Value); if (($ops | Sort-Object | Get-Unique).Count -ne $ops.Count) { throw 'duplicate BLE operation code' }
$results = @($ble.resultCodes.PSObject.Properties.Value); if (($results | Sort-Object | Get-Unique).Count -ne $results.Count) { throw 'duplicate BLE result code' }
function Test-Crc($hex) { $list=[System.Collections.Generic.List[byte]]::new(); for($k=0;$k -lt $hex.Length;$k+=2){$list.Add([Convert]::ToByte($hex.Substring($k,2),16))}; $bytes=$list.ToArray(); if($bytes.Length -lt 14){return $false}; $last=$bytes.Length-1; $lo=$bytes[$last-1]; $hi=$bytes[$last]; $wire=[int]$lo + ([int]$hi * 256); $crc=0xffff; for($i=0;$i -lt ($bytes.Length-2);$i++){ $crc=$crc -bxor $bytes[$i]; for($j=0;$j -lt 8;$j++){ $crc=if(($crc -band 1)-ne 0){($crc -shr 1)-bxor 0xa001}else{$crc -shr 1} } }; return (($crc -band 0xffff) -eq $wire) }
$ranges = @{}
foreach($r in $map){ for($i=0;$i -lt [int]$r.register_count;$i++){ $a=[int]$r.address+$i; if($ranges.ContainsKey($a)){ throw "register overlap at $a" }; $ranges[$a]=$r.name } }
$required = @{
  storage_state_diag = @(0x0030,1); config_dirty = @(0x0032,1);
  current_revision = @(0x0033,2); saved_revision = @(0x0035,2);
  command_mailbox_response = @(0x004c,12); storage_active_slot = @(0x01c1,1);
  storage_active_sequence = @(0x01c2,2); storage_state = @(0x01c4,1)
}
foreach($entry in $required.GetEnumerator()){
  $definition = $map | Where-Object name -eq $entry.Key
  if($null -eq $definition -or [int]$definition.address -ne $entry.Value[0] -or [int]$definition.register_count -ne $entry.Value[1]){
    throw "ConfigStore contract mismatch: $($entry.Key)"
  }
}
$runtimeContract = Get-Content (Join-Path $root 'apps/wechat-mini/miniprogram/core/protocol/device-contract.ts') -Raw
foreach ($requiredSource in @('productFirmwareVersion: 0x0510', 'schemaVersion: 2',
    'persistentFormatVersion: 3', 'slotSchemaVersion: 3', 'slotPayloadLength: 281',
    'registerMapVersion: 0x0104', 'requiredMonitoringCapabilities: 0x000003ff')) {
  if (-not $runtimeContract.Contains($requiredSource)) { throw "WeChat runtime contract mismatch: $requiredSource" }
}
$pcContract = Get-Content (Join-Path $root 'apps/pc/src/A33.Instrument.Core/Stage2BDeviceContract.cs') -Raw
foreach ($requiredSource in @('FirmwareVersion = 0x0510',
    'BaselineRoot = "Results/pc_stage2c_0510_baseline"',
    'PersistenceEvidenceRoot = "Results/pc_stage2c_0510_hw"')) {
  if (-not $pcContract.Contains($requiredSource)) { throw "PC strict contract mismatch: $requiredSource" }
}
$baselinePath = Join-Path $root 'Results/pc_stage2c_0510_baseline/persistence_baseline_manifest.json'
$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
if ($baseline.schema_version -ne 2 -or $baseline.baseline_id -ne 'a33-stage2c-fw0510-brightness3-20260912' -or
    $baseline.firmware_version -ne 0x0510 -or $baseline.register_map -ne 0x0104 -or $baseline.device_schema -ne 2 -or
    $baseline.persistent_format_version -ne 3 -or $baseline.slot_schema_version -ne 3 -or $baseline.slot_payload_length -ne 281 -or
    $baseline.register_count -ne 64 -or $baseline.active_registers.Count -ne 64 -or
    $baseline.original_brightness -ne 3 -or $baseline.active_slot -ne 1 -or $baseline.active_sequence -ne 3 -or
    $baseline.current_revision -ne 3 -or $baseline.saved_revision -ne 3 -or -not $baseline.proven_before_first_stage2b_write -or
    $baseline.stm32_production_commit -ne $ble.firmwareProductionCommit -or
    $baseline.stm32_evidence_commit -ne $ble.firmwareEvidenceCommit -or
    $baseline.stm32_release_elf_sha256 -ne $ble.firmwareReleaseElfSha256) {
  throw 'persistence baseline provenance or shape mismatch'
}
$canonical = ConvertTo-Json @($baseline.active_registers) -Compress
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try { $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical)) }
finally { $sha256.Dispose() }
$baselineHash = ([BitConverter]::ToString($hashBytes)).Replace('-', '')
if ($baselineHash -ne $baseline.active_array_sha256 -or $baselineHash -ne '91D346E87BD112EFAC3B513A8CAFBBDDE9642069A15280DB7565374378BA43E1') {
  throw 'persistence baseline hash mismatch'
}
$binary=[System.Collections.Generic.List[byte]]::new(); foreach($value in $baseline.active_registers){$binary.Add([byte]([int]$value -shr 8));$binary.Add([byte]([int]$value -band 0xff))}
$shaBinary=[System.Security.Cryptography.SHA256]::Create()
try{$binaryHash=([BitConverter]::ToString($shaBinary.ComputeHash($binary.ToArray()))).Replace('-','')}
finally{$shaBinary.Dispose()}
if($binaryHash -ne $baseline.stm32_binary_active_sha256 -or $binaryHash -ne 'F596F7460C911607FA8328A6D4BA5725EA5A0E5B14765D61EE7889A0D62F48A4'){throw 'STM32 binary baseline hash mismatch'}
if ($goldenBle.frames.Count -lt 3 -or $goldenModbus.tcp.mapVersionRequest -ne '0001000000060103000e0001') { throw 'golden vectors incomplete' }
foreach($frame in $goldenBle.frames){if(-not (Test-Crc $frame.hex)){throw "BLE golden CRC invalid: $($frame.name)"}}
Write-Output "Validated $($map.Count) register definitions, no overlaps; BLE/Modbus golden contracts parsed."
