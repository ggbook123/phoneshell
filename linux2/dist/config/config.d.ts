export interface ModuleConfig {
    terminal: boolean;
    relayServer: boolean;
    relayClient: boolean;
    webPanel: boolean;
    aiChat: boolean;
}
export interface TlsConfig {
    enabled: boolean;
    certPath: string;
    keyPath: string;
    caPath: string;
    passphrase: string;
    port: number;
}
export interface AppConfig {
    displayName: string;
    publicHost: string;
    port: number;
    panelPort: number;
    tls: TlsConfig;
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
export declare function loadConfig(args: string[]): AppConfig;
export declare function saveConfig(config: AppConfig): void;
