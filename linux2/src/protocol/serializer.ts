import type { Message } from './messages.js';

const MESSAGE_TYPES = new Set([
  'device.register', 'device.unregister', 'device.list.request', 'device.list',
  'session.list.request', 'session.list',
  'terminal.open', 'terminal.opened', 'terminal.input', 'terminal.output',
  'terminal.resize', 'terminal.close', 'terminal.closed',
  'control.request', 'control.grant', 'control.force_disconnect',
  'group.join.request', 'group.join.accepted', 'group.join.rejected',
  'group.member.joined', 'group.member.left', 'group.member.list', 'group.kick',
  'mobile.bind.request', 'mobile.bind.accepted', 'mobile.bind.rejected', 'mobile.unbind',
  'auth.request', 'auth.response',
  'panel.login.scan',
  'error',
  'group.server.change.request', 'group.server.change.prepare', 'group.server.change.commit',
  'group.secret.rotate.request', 'group.secret.rotate.done',
  'relay.designate', 'relay.designated',
  'invite.create.request', 'invite.create.response',
  'device.settings.update', 'device.settings.updated',
  'device.kick', 'device.kicked',
  'group.dissolve', 'group.dissolved',
  'panel.disconnected',
]);

export function serialize(message: object): string {
  return JSON.stringify(message);
}

export function getMessageType(json: string): string | null {
  try {
    const parsed = JSON.parse(json);
    if (parsed && typeof parsed.type === 'string') {
      return parsed.type;
    }
  } catch {
    // invalid JSON
  }
  return null;
}

export function deserialize(json: string): Message | null {
  try {
    const parsed = JSON.parse(json);
    if (parsed && typeof parsed.type === 'string' && MESSAGE_TYPES.has(parsed.type)) {
      return parsed as Message;
    }
  } catch {
    // invalid JSON
  }
  return null;
}
