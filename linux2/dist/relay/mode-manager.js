import { EventEmitter } from 'node:events';
/**
 * Mode state machine for device lifecycle:
 *   standalone ──(relay.designate)──→ relay
 *   standalone ──(invite received)──→ client
 *   relay ──────(group.dissolve)───→ standalone
 *   client ─────(kicked/dissolve)──→ standalone
 *   client ─────(server change)────→ relay
 *   relay ──────(server change)────→ client
 */
export class ModeManager extends EventEmitter {
    _mode = 'standalone';
    log = () => { };
    get mode() {
        return this._mode;
    }
    setLogger(fn) {
        this.log = fn;
    }
    /** Initialize mode based on config. Default is standalone. */
    initialize(initialMode) {
        this._mode = initialMode || 'standalone';
        this.log(`Mode initialized: ${this._mode}`);
    }
    /** Transition to relay server mode (when phone designates this device) */
    transitionToRelay() {
        if (this._mode !== 'standalone') {
            this.log(`Cannot transition to relay from ${this._mode}`);
            return false;
        }
        const event = { from: this._mode, to: 'relay' };
        this._mode = 'relay';
        this.log('Mode transition: standalone → relay');
        this.emit('modeChange', event);
        return true;
    }
    /** Transition to relay server mode (server migration target) */
    transitionToRelayFromClient() {
        if (this._mode !== 'client') {
            this.log(`Cannot transition to relay from ${this._mode}`);
            return false;
        }
        const event = { from: this._mode, to: 'relay' };
        this._mode = 'relay';
        this.log('Mode transition: client → relay');
        this.emit('modeChange', event);
        return true;
    }
    /** Transition to client mode (when invited to join a group) */
    transitionToClient(relayUrl, inviteCode) {
        if (this._mode !== 'standalone') {
            this.log(`Cannot transition to client from ${this._mode}`);
            return false;
        }
        const event = { from: this._mode, to: 'client', relayUrl, inviteCode };
        this._mode = 'client';
        this.log(`Mode transition: standalone → client (relay: ${relayUrl})`);
        this.emit('modeChange', event);
        return true;
    }
    /** Transition to client mode (server migration source) */
    transitionToClientFromRelay(relayUrl) {
        if (this._mode !== 'relay') {
            this.log(`Cannot transition to client from ${this._mode}`);
            return false;
        }
        const event = { from: this._mode, to: 'client', relayUrl };
        this._mode = 'client';
        this.log(`Mode transition: relay → client (relay: ${relayUrl})`);
        this.emit('modeChange', event);
        return true;
    }
    /** Return to standalone mode (after being kicked, group dissolved, etc.) */
    transitionToStandalone() {
        if (this._mode === 'standalone') {
            this.log('Already in standalone mode');
            return false;
        }
        const event = { from: this._mode, to: 'standalone' };
        this._mode = 'standalone';
        this.log(`Mode transition: ${event.from} → standalone`);
        this.emit('modeChange', event);
        return true;
    }
    isStandalone() { return this._mode === 'standalone'; }
    isRelay() { return this._mode === 'relay'; }
    isClient() { return this._mode === 'client'; }
}
//# sourceMappingURL=mode-manager.js.map