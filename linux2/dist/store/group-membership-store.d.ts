export interface GroupMembership {
    groupId: string;
    groupSecret: string;
    relayUrl: string;
    updatedAtUtc: string;
}
export declare class GroupMembershipStore {
    private readonly filePath;
    constructor(baseDirectory: string);
    load(): GroupMembership | null;
    save(membership: GroupMembership): void;
    clear(): void;
}
