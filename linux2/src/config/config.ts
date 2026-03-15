import fs from 'node:fs';
import path from 'node:path';

export interface ModuleConfig {
  terminal: boolean;
  relayServer: boolean;
  relayClient: boolean;
  webPanel: boolean;
  aiChat: boolean;
}

export interface AppConfig {
  displayName: string;
  publicHost: string;
  port: number;
  relayUrl: string;
  relayAuthToken: string;
  groupSecret: string;
  defaultCols: number;
  defaultRows: number;
  modules: ModuleConfig;
  configPath?: string;
  baseDirectory: string;
  mode: 'standalone' | 'server' | 'client';
}

function defaultConfig(): AppConfig {
  return {
    displayName: '',
    publicHost: '',
    port: 19090,
    relayUrl: '',
    relayAuthToken: '',
    groupSecret: '',
    defaultCols: 120,
    defaultRows: 30,
    modules: {
      terminal: true,
      relayServer: false,
      relayClient: true,
      webPanel: true,
      aiChat: false,
    },
    baseDirectory: '/etc/phoneshell',
    mode: 'standalone',
  };
}

function loadConfigFile(filePath: string): Partial<AppConfig> {
  try {
    if (fs.existsSync(filePath)) {
      const json = fs.readFileSync(filePath, 'utf-8');
      return JSON.parse(json);
    }
  } catch {
    // ignore
  }
  return {};
}

function applyEnvVars(config: AppConfig): void {
  const env = (key: string) => {
    const v = process.env[key];
    return v?.trim() || undefined;
  };

  const name = env('PHONESHELL_NAME');
  if (name) config.displayName = name;

  const relayUrl = env('PHONESHELL_RELAY_URL');
  if (relayUrl) config.relayUrl = relayUrl;

  const relayToken = env('PHONESHELL_RELAY_TOKEN');
  if (relayToken) config.relayAuthToken = relayToken;

  const groupSecret = env('PHONESHELL_GROUP_SECRET');
  if (groupSecret) config.groupSecret = groupSecret;

  const publicHost = env('PHONESHELL_PUBLIC_HOST');
  if (publicHost) config.publicHost = publicHost;

  const portValue = env('PHONESHELL_PORT');
  if (portValue) {
    const port = parseInt(portValue, 10);
    if (port >= 1 && port <= 65535) config.port = port;
  }

  const modeValue = env('PHONESHELL_MODE')?.toLowerCase();
  if (modeValue === 'server' || modeValue === 'relay-server') {
    config.modules.relayServer = true;
    config.modules.relayClient = false;
  } else if (modeValue === 'client' || modeValue === 'relay-client') {
    config.modules.relayServer = false;
    config.modules.relayClient = true;
  }
}

function applyCliArgs(config: AppConfig, args: string[]): string | undefined {
  let configPath: string | undefined;

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    switch (arg) {
      case '--config':
        if (i + 1 < args.length) configPath = args[++i];
        break;
      case '--name':
        if (i + 1 < args.length) config.displayName = args[++i];
        break;
      case '--port':
        if (i + 1 < args.length) {
          const port = parseInt(args[++i], 10);
          if (port >= 1 && port <= 65535) config.port = port;
        }
        break;
      case '--relay':
        if (i + 1 < args.length) config.relayUrl = args[++i];
        break;
      case '--relay-token':
        if (i + 1 < args.length) config.relayAuthToken = args[++i];
        break;
      case '--group-secret':
        if (i + 1 < args.length) config.groupSecret = args[++i];
        break;
      case '--public-host':
        if (i + 1 < args.length) config.publicHost = args[++i];
        break;
      case '--mode': {
        if (i + 1 < args.length) {
          const mode = args[++i].toLowerCase();
          if (mode === 'server' || mode === 'relay-server' || mode === 'relay') {
            config.modules.relayServer = true;
            config.modules.relayClient = false;
            config.mode = 'server';
          } else if (mode === 'client' || mode === 'relay-client') {
            config.modules.relayServer = false;
            config.modules.relayClient = true;
            config.mode = 'client';
          } else if (mode === 'standalone') {
            config.modules.relayServer = true;
            config.modules.relayClient = false;
            config.mode = 'standalone';
          }
        }
        break;
      }
      case '--enable-terminal': config.modules.terminal = true; break;
      case '--disable-terminal': config.modules.terminal = false; break;
      case '--enable-relay-server': config.modules.relayServer = true; break;
      case '--disable-relay-server': config.modules.relayServer = false; break;
      case '--enable-relay-client': config.modules.relayClient = true; break;
      case '--disable-relay-client': config.modules.relayClient = false; break;
      case '--enable-web-panel': config.modules.webPanel = true; break;
      case '--disable-web-panel': config.modules.webPanel = false; break;
      case '--enable-ai-chat': config.modules.aiChat = true; break;
      case '--disable-ai-chat': config.modules.aiChat = false; break;
    }
  }

  return configPath;
}

/** Auto-detect: if no explicit server/client mode and no relay URL, default to server mode. */
function autoDetectMode(config: AppConfig): void {
  if (!config.modules.relayServer && !config.relayUrl) {
    config.modules.relayServer = true;
    config.modules.relayClient = false;
  }
}

export function loadConfig(args: string[]): AppConfig {
  const config = defaultConfig();

  // 1. CLI args first pass — extract --config path
  const configPath = applyCliArgs(config, args);

  // 2. Load config file (if specified or default /etc/phoneshell/config.json)
  const resolvedConfigPath = configPath || '/etc/phoneshell/config.json';
  const fileConfig = loadConfigFile(resolvedConfigPath);
  if (fileConfig.displayName !== undefined) config.displayName = fileConfig.displayName;
  if (fileConfig.publicHost !== undefined) config.publicHost = fileConfig.publicHost;
  if (fileConfig.port !== undefined) config.port = fileConfig.port;
  if (fileConfig.relayUrl !== undefined) config.relayUrl = fileConfig.relayUrl;
  if (fileConfig.relayAuthToken !== undefined) config.relayAuthToken = fileConfig.relayAuthToken;
  if (fileConfig.groupSecret !== undefined) config.groupSecret = fileConfig.groupSecret;
  if (fileConfig.defaultCols !== undefined) config.defaultCols = fileConfig.defaultCols;
  if (fileConfig.defaultRows !== undefined) config.defaultRows = fileConfig.defaultRows;
  if (fileConfig.baseDirectory !== undefined) config.baseDirectory = fileConfig.baseDirectory;
  if ((fileConfig as Record<string, unknown>).mode !== undefined) config.mode = (fileConfig as Record<string, unknown>).mode as AppConfig['mode'];
  if (fileConfig.modules) {
    Object.assign(config.modules, fileConfig.modules);
  }

  // 3. Environment variables (override file config)
  applyEnvVars(config);

  // 4. CLI args second pass (override everything)
  applyCliArgs(config, args);

  // 5. Auto-detect mode
  autoDetectMode(config);

  // 6. Normalize
  config.displayName = config.displayName.trim();
  config.publicHost = config.publicHost.trim();
  config.relayAuthToken = config.relayAuthToken.trim();
  config.groupSecret = config.groupSecret.trim();
  config.relayUrl = config.relayUrl.trim();
  config.configPath = fs.existsSync(resolvedConfigPath) ? resolvedConfigPath : configPath;

  return config;
}

export function saveConfig(config: AppConfig): void {
  if (!config.configPath) return;
  try {
    const dir = path.dirname(config.configPath);
    fs.mkdirSync(dir, { recursive: true });
    
    // Exclude sensitive and runtime-only fields
    const { configPath: _, baseDirectory: __, groupSecret: ___, relayAuthToken: ____, ...rest } = config;
    
    // Save config without sensitive data
    const configData = JSON.stringify(rest, null, 2);
    fs.writeFileSync(config.configPath, configData, { encoding: 'utf-8', mode: 0o600 });
    
    console.log(`[config] Configuration saved (sensitive fields excluded)`);
  } catch (err) {
    console.error(`[config] Failed to save configuration: ${(err as Error).message}`);
  }
}
