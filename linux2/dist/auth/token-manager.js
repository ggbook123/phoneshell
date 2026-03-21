import crypto from 'node:crypto';
export class TokenManager {
    loginSessions = new Map();
    accessTokens = new Map();
    loginTtlMs = 10 * 60 * 1000; // 10 minutes
    tokenTtlMs = 365 * 24 * 60 * 60 * 1000; // 365 days
    generateToken() {
        return crypto.randomBytes(32).toString('base64url');
    }
    /** Constant-time comparison for token verification */
    tokensEqual(a, b) {
        const aBuf = Buffer.from(a, 'utf-8');
        const bBuf = Buffer.from(b, 'utf-8');
        if (aBuf.length !== bBuf.length)
            return false;
        return crypto.timingSafeEqual(aBuf, bBuf);
    }
    createLoginSession(requesterAddress, serverUrl) {
        this.cleanupExpired();
        const requestId = crypto.randomUUID().replace(/-/g, '');
        const token = this.generateToken();
        const now = new Date();
        const session = {
            requestId,
            token,
            status: 'awaiting_scan',
            createdAtUtc: now.toISOString(),
            expiresAtUtc: new Date(now.getTime() + this.loginTtlMs),
            requesterAddress,
            serverUrl,
        };
        this.loginSessions.set(requestId, session);
        return session;
    }
    getLoginSession(requestId) {
        this.cleanupExpired();
        const session = this.loginSessions.get(requestId);
        if (session && session.status !== 'approved' && new Date() > session.expiresAtUtc) {
            session.status = 'expired';
            session.message = 'Request expired.';
        }
        return session;
    }
    approveLogin(session) {
        session.status = 'approved';
        session.message = 'Approved.';
        this.accessTokens.set(session.token, {
            token: session.token,
            expiresAtUtc: new Date(Date.now() + this.tokenTtlMs),
        });
    }
    rejectLogin(session, message) {
        session.status = 'rejected';
        session.message = message;
    }
    isPanelTokenValid(token) {
        if (!token)
            return false;
        const entry = this.accessTokens.get(token);
        if (!entry)
            return false;
        if (entry.expiresAtUtc <= new Date()) {
            this.accessTokens.delete(token);
            return false;
        }
        return true;
    }
    clearLoginSessions() {
        this.loginSessions.clear();
    }
    clearAll() {
        this.loginSessions.clear();
        this.accessTokens.clear();
    }
    /** Get all login sessions (for dispatching pending logins) */
    getAllLoginSessions() {
        return Array.from(this.loginSessions.values());
    }
    cleanupExpired() {
        const now = new Date();
        for (const [key, entry] of this.accessTokens) {
            if (entry.expiresAtUtc <= now)
                this.accessTokens.delete(key);
        }
        for (const [key, session] of this.loginSessions) {
            if (session.status === 'approved')
                continue;
            if (session.expiresAtUtc <= now) {
                // Remove expired sessions to prevent unbounded Map growth
                this.loginSessions.delete(key);
            }
        }
    }
}
//# sourceMappingURL=token-manager.js.map