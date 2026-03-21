import { EventEmitter } from 'node:events';
export type DeviceMode = 'standalone' | 'relay' | 'client';
export interface ModeChangeEvent {
    from: DeviceMode;
    to: DeviceMode;
    relayUrl?: string;
    inviteCode?: string;
}
type LogFn = (msg: string) => void;
/**
 * Mode state machine for device lifecycle:
 *   standalone ──(relay.designate)──→ relay
 *   standalone ──(invite received)──→ client
 *   relay ──────(group.dissolve)───→ standalone
 *   client ─────(kicked/dissolve)──→ standalone
 *   client ─────(server change)────→ relay
 *   relay ──────(server change)────→ client
 */
export declare class ModeManager extends EventEmitter {
    private _mode;
    private log;
    get mode(): DeviceMode;
    setLogger(fn: LogFn): void;
    /** Initialize mode based on config. Default is standalone. */
    initialize(initialMode?: DeviceMode): void;
    /** Transition to relay server mode (when phone designates this device) */
    transitionToRelay(): boolean;
    /** Transition to relay server mode (server migration target) */
    transitionToRelayFromClient(): boolean;
    /** Transition to client mode (when invited to join a group) */
    transitionToClient(relayUrl: string, inviteCode: string): boolean;
    /** Transition to client mode (server migration source) */
    transitionToClientFromRelay(relayUrl: string): boolean;
    /** Return to standalone mode (after being kicked, group dissolved, etc.) */
    transitionToStandalone(): boolean;
    isStandalone(): boolean;
    isRelay(): boolean;
    isClient(): boolean;
}
export {};
