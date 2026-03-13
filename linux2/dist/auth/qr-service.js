import QRCode from 'qrcode';
export function buildGroupBindPayload(serverWsUrl, groupId, groupSecret, serverDeviceId) {
    const server = encodeURIComponent(serverWsUrl);
    const gid = encodeURIComponent(groupId);
    const secret = encodeURIComponent(groupSecret);
    const nonce = crypto.randomUUID().replace(/-/g, '');
    if (serverDeviceId) {
        const deviceId = encodeURIComponent(serverDeviceId);
        return `phoneshell://bind?server=${server}&groupId=${gid}&groupSecret=${secret}&serverDeviceId=${deviceId}&nonce=${nonce}`;
    }
    return `phoneshell://bind?server=${server}&groupId=${gid}&groupSecret=${secret}&nonce=${nonce}`;
}
export function buildPanelLoginPayload(serverWsUrl, groupId, requestId) {
    const server = encodeURIComponent(serverWsUrl);
    const gid = encodeURIComponent(groupId);
    const rid = encodeURIComponent(requestId);
    const nonce = crypto.randomUUID().replace(/-/g, '');
    return `phoneshell://login?server=${server}&groupId=${gid}&requestId=${rid}&nonce=${nonce}`;
}
export async function generateQrPng(payload, pixelsPerModule = 6) {
    return QRCode.toBuffer(payload, {
        type: 'png',
        margin: 1,
        scale: pixelsPerModule,
        errorCorrectionLevel: 'M',
    });
}
//# sourceMappingURL=qr-service.js.map