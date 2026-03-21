import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { v4 as uuidv4 } from 'uuid';
import type { DeviceIdentity } from '../protocol/messages.js';

export class DeviceStore {
  private readonly filePath: string;

  constructor(baseDirectory: string) {
    const dataDir = path.join(baseDirectory, 'data');
    fs.mkdirSync(dataDir, { recursive: true });
    this.filePath = path.join(dataDir, 'device.json');
  }

  loadOrCreate(): DeviceIdentity {
    try {
      if (fs.existsSync(this.filePath)) {
        const json = fs.readFileSync(this.filePath, 'utf-8');
        const identity = JSON.parse(json) as DeviceIdentity;
        if (identity.deviceId) {
          return identity;
        }
      }
    } catch {
      // corrupted file, recreate
    }

    const identity: DeviceIdentity = {
      deviceId: uuidv4().replace(/-/g, ''),
      displayName: os.hostname(),
      createdAt: new Date().toISOString(),
    };

    fs.writeFileSync(this.filePath, JSON.stringify(identity, null, 2), 'utf-8');
    return identity;
  }
}
