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
    panelPort: number;
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
