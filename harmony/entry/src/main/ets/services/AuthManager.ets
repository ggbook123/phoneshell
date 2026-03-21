import { hilog } from '@kit.PerformanceAnalysisKit';
import { ConnectionManager } from './ConnectionManager';

const DOMAIN = 0x0000;
const TAG = 'AuthManager';

// Auth request pending item
export class AuthPendingRequest {
  requestId: string = '';
  action: string = '';
  requesterName: string = '';
  description: string = '';
  targetDeviceId: string = '';
  receivedAt: number = 0;
}

// Callback wrapper for ArkTS strict mode
class AuthDialogCallbackWrapper {
  id: number = -1;
  callback: (request: AuthPendingRequest) => void = (_r: AuthPendingRequest) => {};
}

class AuthResolvedCallbackWrapper {
  id: number = -1;
  callback: (requestId: string, approved: boolean) => void = (_r: string, _a: boolean) => {};
}

class AuthManagerClass {
  private pendingQueue: Array<AuthPendingRequest> = [];
  private dialogCallbackWrappers: Array<AuthDialogCallbackWrapper> = [];
  private resolvedCallbackWrappers: Array<AuthResolvedCallbackWrapper> = [];
  private nextCallbackId: number = 1;
  private authListenerId: number = -1;
  private maxPendingAge: number = 300000; // 5 minutes TTL

  // Initialize: register with ConnectionManager to listen for auth.request
  init(): void {
    if (this.authListenerId !== -1) return;
    this.authListenerId = ConnectionManager.addOnAuthRequest(
      (requestId: string, action: string, requesterName: string, description: string, targetDeviceId: string) => {
        this.onAuthRequestReceived(requestId, action, requesterName, description, targetDeviceId);
      }
    );
    hilog.info(DOMAIN, TAG, 'AuthManager initialized');
  }

  dispose(): void {
    if (this.authListenerId !== -1) {
      ConnectionManager.removeOnAuthRequest(this.authListenerId);
      this.authListenerId = -1;
    }
    this.pendingQueue = [];
  }

  // --- Dialog trigger callback: UI registers to show auth dialog ---

  addOnShowDialog(callback: (request: AuthPendingRequest) => void): number {
    let wrapper: AuthDialogCallbackWrapper = new AuthDialogCallbackWrapper();
    wrapper.id = this.nextCallbackId;
    this.nextCallbackId++;
    wrapper.callback = callback;
    this.dialogCallbackWrappers.push(wrapper);
    return wrapper.id;
  }

  removeOnShowDialog(callbackId: number): void {
    for (let i: number = 0; i < this.dialogCallbackWrappers.length; i++) {
      if (this.dialogCallbackWrappers[i].id === callbackId) {
        this.dialogCallbackWrappers.splice(i, 1);
        break;
      }
    }
  }

  // --- Resolved callback: notified when auth request is approved/rejected ---

  addOnResolved(callback: (requestId: string, approved: boolean) => void): number {
    let wrapper: AuthResolvedCallbackWrapper = new AuthResolvedCallbackWrapper();
    wrapper.id = this.nextCallbackId;
    this.nextCallbackId++;
    wrapper.callback = callback;
    this.resolvedCallbackWrappers.push(wrapper);
    return wrapper.id;
  }

  removeOnResolved(callbackId: number): void {
    for (let i: number = 0; i < this.resolvedCallbackWrappers.length; i++) {
      if (this.resolvedCallbackWrappers[i].id === callbackId) {
        this.resolvedCallbackWrappers.splice(i, 1);
        break;
      }
    }
  }

  // --- Approve / Reject ---

  approve(requestId: string): void {
    hilog.info(DOMAIN, TAG, 'approve requestId=%{public}s', requestId);
    ConnectionManager.sendAuthResponse(requestId, true);
    this.removeFromQueue(requestId);
    this.emitResolved(requestId, true);
  }

  reject(requestId: string): void {
    hilog.info(DOMAIN, TAG, 'reject requestId=%{public}s', requestId);
    ConnectionManager.sendAuthResponse(requestId, false);
    this.removeFromQueue(requestId);
    this.emitResolved(requestId, false);
  }

  // --- Pending queue accessors ---

  getPendingCount(): number {
    this.pruneExpired();
    return this.pendingQueue.length;
  }

  getPendingRequests(): Array<AuthPendingRequest> {
    this.pruneExpired();
    return this.pendingQueue.slice();
  }

  getNextPending(): AuthPendingRequest | null {
    this.pruneExpired();
    if (this.pendingQueue.length === 0) return null;
    return this.pendingQueue[0];
  }

  // Show the next pending request dialog (if any)
  showPending(): void {
    const next: AuthPendingRequest | null = this.getNextPending();
    if (next === null) return;
    this.emitShowDialog(next);
  }

  // --- Internal ---

  private onAuthRequestReceived(requestId: string, action: string, requesterName: string, description: string, targetDeviceId: string): void {
    hilog.info(DOMAIN, TAG, 'auth.request received id=%{public}s action=%{public}s from=%{public}s',
      requestId, action, requesterName);

    // Deduplicate
    for (let i: number = 0; i < this.pendingQueue.length; i++) {
      if (this.pendingQueue[i].requestId === requestId) return;
    }

    let request: AuthPendingRequest = new AuthPendingRequest();
    request.requestId = requestId;
    request.action = action;
    request.requesterName = requesterName;
    request.description = description;
    request.targetDeviceId = targetDeviceId;
    request.receivedAt = Date.now();

    this.pendingQueue.push(request);

    // Immediately trigger dialog
    this.emitShowDialog(request);
  }

  private removeFromQueue(requestId: string): void {
    this.pendingQueue = this.pendingQueue.filter((r: AuthPendingRequest) => r.requestId !== requestId);
  }

  private pruneExpired(): void {
    const now: number = Date.now();
    this.pendingQueue = this.pendingQueue.filter((r: AuthPendingRequest) => {
      return (now - r.receivedAt) < this.maxPendingAge;
    });
  }

  private emitShowDialog(request: AuthPendingRequest): void {
    let listeners: Array<AuthDialogCallbackWrapper> = this.dialogCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(request); } catch (_e) { /* ignore */ }
    }
  }

  private emitResolved(requestId: string, approved: boolean): void {
    let listeners: Array<AuthResolvedCallbackWrapper> = this.resolvedCallbackWrappers.slice();
    for (let i: number = 0; i < listeners.length; i++) {
      try { listeners[i].callback(requestId, approved); } catch (_e) { /* ignore */ }
    }
  }
}

// Singleton export
export const AuthManager: AuthManagerClass = new AuthManagerClass();
