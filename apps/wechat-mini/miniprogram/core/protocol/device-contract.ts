import { DeviceInfo } from '../domain/device-model';

export const BLE_DEVICE_CONTRACT = {
  protocolVersion: 1,
  productFirmwareVersion: 0x050f,
  schemaVersion: 2,
  registerMapVersion: 0x0104,
  requiredMonitoringCapabilities: 0x000003ff,
} as const;

export function isMonitoringCompatible(info: DeviceInfo): boolean {
  const contract = BLE_DEVICE_CONTRACT;
  return info.protocolVersion === contract.protocolVersion &&
    info.schemaVersion === contract.schemaVersion &&
    info.registerMapVersion === contract.registerMapVersion &&
    (info.capabilities & contract.requiredMonitoringCapabilities) ===
      contract.requiredMonitoringCapabilities;
}

export function isProductBaseline(info: DeviceInfo): boolean {
  return isMonitoringCompatible(info) &&
    info.firmwareVersion === BLE_DEVICE_CONTRACT.productFirmwareVersion;
}
