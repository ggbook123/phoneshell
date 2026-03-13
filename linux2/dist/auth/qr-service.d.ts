export declare function buildGroupBindPayload(serverWsUrl: string, groupId: string, groupSecret: string, serverDeviceId?: string): string;
export declare function buildPanelLoginPayload(serverWsUrl: string, groupId: string, requestId: string): string;
export declare function generateQrPng(payload: string, pixelsPerModule?: number): Promise<Buffer>;
