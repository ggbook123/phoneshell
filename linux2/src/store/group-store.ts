import fs from 'node:fs';
import path from 'node:path';
import type { GroupInfo } from '../protocol/messages.js';

export class GroupStore {
  private readonly groupFilePath: string;

  constructor(baseDirectory: string) {
    const dataDir = path.join(baseDirectory, 'data');
    fs.mkdirSync(dataDir, { recursive: true });
    this.groupFilePath = path.join(dataDir, 'group.json');
  }

  loadGroup(): GroupInfo | null {
    try {
      if (fs.existsSync(this.groupFilePath)) {
        const json = fs.readFileSync(this.groupFilePath, 'utf-8');
        const group = JSON.parse(json) as GroupInfo;
        if (group.groupId) return group;
      }
    } catch {
      // corrupted file
    }
    return null;
  }

  saveGroup(group: GroupInfo): void {
    fs.writeFileSync(this.groupFilePath, JSON.stringify(group, null, 2), 'utf-8');
  }

  clearGroup(): void {
    try {
      if (fs.existsSync(this.groupFilePath)) {
        fs.unlinkSync(this.groupFilePath);
      }
    } catch {
      // ignore
    }
  }
}
