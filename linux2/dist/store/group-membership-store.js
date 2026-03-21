import fs from 'node:fs';
import path from 'node:path';
export class GroupMembershipStore {
    filePath;
    constructor(baseDirectory) {
        const dataDir = path.join(baseDirectory, 'data');
        fs.mkdirSync(dataDir, { recursive: true });
        this.filePath = path.join(dataDir, 'group-membership.json');
    }
    load() {
        try {
            if (fs.existsSync(this.filePath)) {
                const json = fs.readFileSync(this.filePath, 'utf-8');
                const membership = JSON.parse(json);
                if (membership.groupId && membership.groupSecret && membership.relayUrl) {
                    return membership;
                }
            }
        }
        catch {
            // ignore corrupted file
        }
        return null;
    }
    save(membership) {
        const payload = {
            groupId: membership.groupId,
            groupSecret: membership.groupSecret,
            relayUrl: membership.relayUrl,
            updatedAtUtc: membership.updatedAtUtc || new Date().toISOString(),
        };
        fs.writeFileSync(this.filePath, JSON.stringify(payload, null, 2), { encoding: 'utf-8', mode: 0o600 });
    }
    clear() {
        try {
            if (fs.existsSync(this.filePath)) {
                fs.unlinkSync(this.filePath);
            }
        }
        catch {
            // ignore
        }
    }
}
//# sourceMappingURL=group-membership-store.js.map