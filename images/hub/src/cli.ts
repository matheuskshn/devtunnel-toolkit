import { createConnection } from 'node:net';
import { createInterface } from 'node:readline';
import { Manager } from './manager.js';
import { loadConfig } from './config.js';
import { errorCode } from './model.js';

process.umask(0o077);
const args = process.argv.slice(2);
const runDir = process.env.HUB_RUN_DIR ?? '/run/hub';
const dataDir = process.env.HUB_DATA_DIR ?? '/data';
if (args[0] === 'serve') {
  let manager: Manager | undefined;
  try {
    if (process.env.HUB_LOCKED !== '1') throw new Error('Use the hub wrapper to acquire the storage lock');
    manager = new Manager(await loadConfig(), dataDir, runDir);
    let closing = false;
    const shutdown = () => {
      if (closing) return; closing = true;
      const deadline = setTimeout(() => process.exit(1), 20000);
      void manager!.close().then(() => { clearTimeout(deadline); process.exit(0); }, () => process.exit(1));
    };
    process.on('SIGTERM', shutdown); process.on('SIGINT', shutdown);
    await manager.open();
  } catch (e) {
    process.stderr.write(`${errorCode(e)}\n`);
    await manager?.close().catch(() => {}); process.exitCode = 1;
  }
} else if (args[0] === 'health') {
  try {
    const config = await loadConfig();
    const res = await fetch(`http://127.0.0.1:${config.healthPort}/ready`, { signal: AbortSignal.timeout(2000) });
    process.exitCode = res.ok ? 0 : 1;
  } catch { process.exitCode = 1; }
} else if (args[0] === 'session') {
  const socket = createConnection(`${runDir}/manager.sock`);
  socket.once('connect', () => socket.write(JSON.stringify(args) + '\n'));
  socket.on('error', () => { process.stderr.write('MANAGER_UNAVAILABLE\n'); process.exitCode = 1; });
  let response = false;
  const lines = createInterface({ input: socket });
  lines.on('line', line => {
    try {
      const message = JSON.parse(line);
      if (message.output) process.stdout.write(message.output);
      if ('result' in message) { response = true; process.stdout.write(JSON.stringify(message.result, null, 2) + '\n'); }
      if (message.error) { response = true; process.stderr.write(`${message.error}\n`); process.exitCode = 1; }
    } catch { process.exitCode = 1; socket.destroy(); }
  });
  socket.on('close', () => { if (!response) process.exitCode = 1; });
} else {
  process.stdout.write('Usage: hub serve | health | session list\n' +
    '       hub session add ID [--provider microsoft|github] [--tunnel-name NAME]\n' +
    '       hub session login|start|stop|status|logout|remove ID\n');
  if (args.length && !['help','--help','-h'].includes(args[0])) process.exitCode = 1;
}
