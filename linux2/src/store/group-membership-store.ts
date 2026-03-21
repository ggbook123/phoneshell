import fs from 'node:fs';
import path from 'node:path';

export interface GroupMembership {
  groupId: string;
  groupSecret: string;
  relayUrl: string;
  updatedAtUtc: string;
}

export class GroupMembershipStore {
  private readonly filePath: string;

  constructor(baseDirectory: string) {
    const dataDir = path.join(baseDirectory, 'data');
    fs.mkdirSync(dataDir, { recursive: true });
    this.filePath = path.join(dataDir, 'group-membership.json');
  }

  load(): GroupMembership | null {
    try {
      if (fs.existsSync(this.filePath)) {
        const json = fs.readFileSync(this.filePath, 'utf-8');
        const membership = JSON.parse(json) as GroupMembership;
        if (membership.groupId && membership.groupSecret && membership.relayUrl) {
          return membership;
        }
      }
    } catch {
      // ignore corrupted file
    }
    return null;
  }

  save(membership: GroupMembership): void {
    const payload: GroupMembership = {
      groupId: membership.groupId,
      groupSecret: membership.groupSecret,
      relayUrl: membership.relayUrl,
      updatedAtUtc: membership.updatedAtUtc || new Date().toISOString(),
    };
    fs.writeFileSync(this.filePath, JSON.stringify(payload, null, 2), { encoding: 'utf-8', mode: 0o600 });
  }

  clear(): void {
    try {
      if (fs.existsSync(this.filePath)) {
        fs.unlinkSync(this.filePath);
      }
    } catch {
      // ignore
    }
  }
}
