import QRCode from 'qrcode';

export function buildGroupBindPayload(
  serverWsUrl: string,
  groupId: string,
  groupSecret: string,
  serverDeviceId?: string,
): string {
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

export function buildPanelLoginPayload(
  serverWsUrl: string,
  groupId: string,
  requestId: string,
): string {
  const server = encodeURIComponent(serverWsUrl);
  const gid = encodeURIComponent(groupId);
  const rid = encodeURIComponent(requestId);
  const nonce = crypto.randomUUID().replace(/-/g, '');
  return `phoneshell://login?server=${server}&groupId=${gid}&requestId=${rid}&nonce=${nonce}`;
}

/** QR payload for standalone mode: phone scans to connect directly to this device */
export function buildStandalonePayload(
  httpUrl: string,
  wsUrl: string,
  deviceId: string,
  displayName: string,
): string {
  const http = encodeURIComponent(httpUrl);
  const ws = encodeURIComponent(wsUrl);
  const did = encodeURIComponent(deviceId);
  const name = encodeURIComponent(displayName);
  const nonce = crypto.randomUUID().replace(/-/g, '');
  return `phoneshell://connect?http=${http}&ws=${ws}&deviceId=${did}&displayName=${name}&nonce=${nonce}`;
}

export async function generateQrPng(payload: string, pixelsPerModule = 6): Promise<Buffer> {
  return QRCode.toBuffer(payload, {
    type: 'png',
    margin: 1,
    scale: pixelsPerModule,
    errorCorrectionLevel: 'M',
  });
}
