import { loadConfig } from './config/config.js';
import { createApp } from './server/app.js';

const config = loadConfig(process.argv.slice(2));

if (process.argv.includes('--help') || process.argv.includes('-h')) {
  console.log(`
PhoneShell Linux Server (Node.js)

Usage: node dist/index.js [options]

Options:
  --config <path>         Config file path (default: /etc/phoneshell/config.json)
  --name <name>           Device display name
  --port <port>           Listen port (default: 19090)
  --mode <server|client>  Operating mode
  --group-secret <secret> Group secret for authentication
  --public-host <host>    Public hostname (for NAT traversal)
  --relay <url>           Relay server URL (client mode)
  --relay-token <token>   Relay auth token (deprecated, use --group-secret)
  --enable-terminal       Enable terminal module
  --disable-terminal      Disable terminal module
  --enable-relay-server   Enable relay server module
  --disable-relay-server  Disable relay server module
  --enable-web-panel      Enable web panel module
  --disable-web-panel     Disable web panel module

Environment Variables:
  PHONESHELL_NAME, PHONESHELL_PORT, PHONESHELL_MODE,
  PHONESHELL_GROUP_SECRET, PHONESHELL_PUBLIC_HOST,
  PHONESHELL_RELAY_URL, PHONESHELL_RELAY_TOKEN
`);
  process.exit(0);
}

const app = createApp(config);

// Signal handling
process.on('SIGINT', () => {
  app.stop();
  process.exit(0);
});
process.on('SIGTERM', () => {
  app.stop();
  process.exit(0);
});

app.start();
