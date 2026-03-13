export declare function buildGroupBindPayload(serverWsUrl: string, groupId: string, groupSecret: string, serverDeviceId?: string): string;
export declare function buildPanelLoginPayload(serverWsUrl: string, groupId: string, requestId: string): string;
/** QR payload for standalone mode: phone scans to connect directly to this device */
export declare function buildStandalonePayload(httpUrl: string, wsUrl: string, deviceId: string, displayName: string): string;
export declare function generateQrPng(payload: string, pixelsPerModule?: number): Promise<Buffer>;
