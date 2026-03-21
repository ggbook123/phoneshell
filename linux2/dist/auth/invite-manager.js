import crypto from 'node:crypto';
const INVITE_TTL_MS = 5 * 60 * 1000; // 5 minutes
const CODE_LENGTH = 8;
export class InviteManager {
    invites = new Map();
    generateInviteCode() {
        this.cleanupExpired();
        const bytes = crypto.randomBytes(CODE_LENGTH);
        const code = bytes.toString('base64url').slice(0, CODE_LENGTH);
        const now = new Date();
        const expiresAt = new Date(now.getTime() + INVITE_TTL_MS);
        this.invites.set(code, { code, createdAt: now, expiresAt });
        return { code, expiresAt };
    }
    /** Validate and consume a one-time invite code. Returns true if valid. */
    consumeInviteCode(code) {
        this.cleanupExpired();
        const entry = this.invites.get(code);
        if (!entry)
            return false;
        if (new Date() > entry.expiresAt) {
            this.invites.delete(code);
            return false;
        }
        // One-time use: delete after consumption
        this.invites.delete(code);
        return true;
    }
    isValidInviteCode(code) {
        const entry = this.invites.get(code);
        if (!entry)
            return false;
        return new Date() <= entry.expiresAt;
    }
    clearAll() {
        this.invites.clear();
    }
    cleanupExpired() {
        const now = new Date();
        for (const [key, entry] of this.invites) {
            if (now > entry.expiresAt) {
                this.invites.delete(key);
            }
        }
    }
}
//# sourceMappingURL=invite-manager.js.map