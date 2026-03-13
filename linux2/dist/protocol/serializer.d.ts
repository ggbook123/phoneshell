import type { Message } from './messages.js';
export declare function serialize(message: object): string;
export declare function getMessageType(json: string): string | null;
export declare function deserialize(json: string): Message | null;
