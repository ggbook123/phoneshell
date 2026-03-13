import type { GroupInfo } from '../protocol/messages.js';
export declare class GroupStore {
    private readonly groupFilePath;
    constructor(baseDirectory: string);
    loadGroup(): GroupInfo | null;
    saveGroup(group: GroupInfo): void;
    clearGroup(): void;
}
