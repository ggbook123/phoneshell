import fs from 'node:fs';
import path from 'node:path';
export class GroupStore {
    groupFilePath;
    constructor(baseDirectory) {
        const dataDir = path.join(baseDirectory, 'data');
        fs.mkdirSync(dataDir, { recursive: true });
        this.groupFilePath = path.join(dataDir, 'group.json');
    }
    loadGroup() {
        try {
            if (fs.existsSync(this.groupFilePath)) {
                const json = fs.readFileSync(this.groupFilePath, 'utf-8');
                const group = JSON.parse(json);
                if (group.groupId)
                    return group;
            }
        }
        catch {
            // corrupted file
        }
        return null;
    }
    saveGroup(group) {
        fs.writeFileSync(this.groupFilePath, JSON.stringify(group, null, 2), 'utf-8');
    }
    clearGroup() {
        try {
            if (fs.existsSync(this.groupFilePath)) {
                fs.unlinkSync(this.groupFilePath);
            }
        }
        catch {
            // ignore
        }
    }
}
//# sourceMappingURL=group-store.js.map