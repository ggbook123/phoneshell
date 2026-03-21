export type PanelLoginState = 'awaiting_scan' | 'awaiting_mobile' | 'awaiting_approval' | 'approved' | 'rejected' | 'expired';
export interface PanelLoginSession {
    requestId: string;
    token: string;
    status: PanelLoginState;
    createdAtUtc: string;
    expiresAtUtc: Date;
    requesterAddress?: string;
    message?: string;
    serverUrl?: string;
    loginQrPayload?: string;
}
export interface PanelAccessToken {
    token: string;
    expiresAtUtc: Date;
}
export declare class TokenManager {
    private readonly loginSessions;
    private readonly accessTokens;
    private readonly loginTtlMs;
    private readonly tokenTtlMs;
    generateToken(): string;
    /** Constant-time comparison for token verification */
    tokensEqual(a: string, b: string): boolean;
    createLoginSession(requesterAddress?: string, serverUrl?: string): PanelLoginSession;
    getLoginSession(requestId: string): PanelLoginSession | undefined;
    approveLogin(session: PanelLoginSession): void;
    rejectLogin(session: PanelLoginSession, message: string): void;
    isPanelTokenValid(token: string): boolean;
    clearLoginSessions(): void;
    clearAll(): void;
    /** Get all login sessions (for dispatching pending logins) */
    getAllLoginSessions(): PanelLoginSession[];
    private cleanupExpired;
}
