import type { AppConfig } from '../config/config.js';
export declare function createApp(config: AppConfig): {
    start: () => void;
    stop: () => void;
};
