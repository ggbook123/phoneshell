import type { DeviceIdentity } from '../protocol/messages.js';
export declare class DeviceStore {
    private readonly filePath;
    constructor(baseDirectory: string);
    loadOrCreate(): DeviceIdentity;
}
