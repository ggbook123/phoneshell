import crypto from 'node:crypto';

export type PanelLoginState =
  | 'awaiting_scan'
  | 'awaiting_mobile'
  | 'awaiting_approval'
  | 'approved'
  | 'rejected'
  | 'expired';

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

export class TokenManager {
  private readonly loginSessions = new Map<string, PanelLoginSession>();
  private readonly accessTokens = new Map<string, PanelAccessToken>();
  private readonly loginTtlMs = 10 * 60 * 1000; // 10 minutes
  private readonly tokenTtlMs = 365 * 24 * 60 * 60 * 1000; // 365 days

  generateToken(): string {
    return crypto.randomBytes(32).toString('base64url');
  }

  /** Constant-time comparison for token verification */
  tokensEqual(a: string, b: string): boolean {
    const aBuf = Buffer.from(a, 'utf-8');
    const bBuf = Buffer.from(b, 'utf-8');
    if (aBuf.length !== bBuf.length) return false;
    return crypto.timingSafeEqual(aBuf, bBuf);
  }

  createLoginSession(requesterAddress?: string, serverUrl?: string): PanelLoginSession {
    this.cleanupExpired();

    const requestId = crypto.randomUUID().replace(/-/g, '');
    const token = this.generateToken();
    const now = new Date();

    const session: PanelLoginSession = {
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

  getLoginSession(requestId: string): PanelLoginSession | undefined {
    this.cleanupExpired();
    const session = this.loginSessions.get(requestId);
    if (session && session.status !== 'approved' && new Date() > session.expiresAtUtc) {
      session.status = 'expired';
      session.message = 'Request expired.';
    }
    return session;
  }

  approveLogin(session: PanelLoginSession): void {
    session.status = 'approved';
    session.message = 'Approved.';
    this.accessTokens.set(session.token, {
      token: session.token,
      expiresAtUtc: new Date(Date.now() + this.tokenTtlMs),
    });
  }

  rejectLogin(session: PanelLoginSession, message: string): void {
    session.status = 'rejected';
    session.message = message;
  }

  isPanelTokenValid(token: string): boolean {
    if (!token) return false;
    const entry = this.accessTokens.get(token);
    if (!entry) return false;
    if (entry.expiresAtUtc <= new Date()) {
      this.accessTokens.delete(token);
      return false;
    }
    return true;
  }

  clearLoginSessions(): void {
    this.loginSessions.clear();
  }

  clearAll(): void {
    this.loginSessions.clear();
    this.accessTokens.clear();
  }

  /** Get all login sessions (for dispatching pending logins) */
  getAllLoginSessions(): PanelLoginSession[] {
    return Array.from(this.loginSessions.values());
  }

  private cleanupExpired(): void {
    const now = new Date();
    for (const [key, entry] of this.accessTokens) {
      if (entry.expiresAtUtc <= now) this.accessTokens.delete(key);
    }
    for (const [, session] of this.loginSessions) {
      if (session.status === 'approved') continue;
      if (session.expiresAtUtc <= now) {
        session.status = 'expired';
        session.message ??= 'Request expired.';
      }
    }
  }
}
