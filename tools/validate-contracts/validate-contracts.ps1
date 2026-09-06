$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$ble = Get-Content (Join-Path $root 'contracts/ble-v1/constants.json') -Raw | ConvertFrom-Json
$goldenBle = Get-Content (Join-Path $root 'contracts/ble-v1/golden-frames.json') -Raw | ConvertFrom-Json
$goldenModbus = Get-Content (Join-Path $root 'contracts/modbus-v0104/golden-frames.json') -Raw | ConvertFrom-Json
$map = Get-Content (Join-Path $root 'contracts/modbus-v0104/register-map.json') -Raw | ConvertFrom-Json
if ($ble.firmwareVersion -ne '0x050A' -or $ble.schemaVersion -ne 2 -or $ble.registerMap -ne '0x0104') { throw 'BLE compatibility constants mismatch' }
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
$baselinePath = Join-Path $root 'Results/pc_stage2b_hw/persistence_baseline_manifest.json'
$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
if ($baseline.schema_version -ne 1 -or $baseline.baseline_id -ne 'a33-stage2b-prewrite-brightness3-20260906' -or
    $baseline.source_evidence_file -ne 'Results/pc_stage2b_hw/tcp_strict_preflight.json' -or
    $baseline.register_count -ne 64 -or $baseline.active_registers.Count -ne 64 -or
    $baseline.original_brightness -ne 3 -or -not $baseline.proven_before_first_stage2b_write) {
  throw 'persistence baseline provenance or shape mismatch'
}
$canonical = ConvertTo-Json @($baseline.active_registers) -Compress
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try { $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical)) }
finally { $sha256.Dispose() }
$baselineHash = ([BitConverter]::ToString($hashBytes)).Replace('-', '')
if ($baselineHash -ne $baseline.active_array_sha256 -or $baselineHash -ne '8C2E5BA6BF39436E5DF2956DE7E09A058DDA330C6462073483E4E70CAD1CACEE') {
  throw 'persistence baseline hash mismatch'
}
if ($goldenBle.frames.Count -lt 3 -or $goldenModbus.tcp.mapVersionRequest -ne '0001000000060103000e0001') { throw 'golden vectors incomplete' }
foreach($frame in $goldenBle.frames){if(-not (Test-Crc $frame.hex)){throw "BLE golden CRC invalid: $($frame.name)"}}
Write-Output "Validated $($map.Count) register definitions, no overlaps; BLE/Modbus golden contracts parsed."
