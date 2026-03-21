export declare class InviteManager {
    private readonly invites;
    generateInviteCode(): {
        code: string;
        expiresAt: Date;
    };
    /** Validate and consume a one-time invite code. Returns true if valid. */
    consumeInviteCode(code: string): boolean;
    isValidInviteCode(code: string): boolean;
    clearAll(): void;
    private cleanupExpired;
}
