// Installs the packed npm packages of this platform into a throwaway prefix and runs what a
// user would: the two commands and, on Windows, the bridge lookup.
//
//   node scripts/smoke-npm-package.mjs --version 0.3.0 --tarballs npm-out/tarballs
import assert from 'node:assert';
import { execSync, spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: { version: { type: 'string' }, tarballs: { type: 'string' } } });
const { version, tarballs } = values;
if (!version || !tarballs) {
  console.error('Usage: smoke-npm-package.mjs --version <version> --tarballs <folder>');
  process.exit(1);
}

const windows = process.platform === 'win32';
const target = `${process.platform}-${process.arch}`;
const packed = ['d365fo-cli', `d365fo-cli-${target}`]
  .map((name) => path.resolve(tarballs, `${name}-${version}.tgz`));
packed.forEach((file) => assert.ok(fs.existsSync(file), `${file} does not exist`));

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'd365fo-npm-smoke-'));
const prefix = path.join(root, 'prefix');
// A configuration of its own: the check must not read, or depend on, this machine's.
const env = { ...process.env, D365FO_CONFIG_DIR: path.join(root, 'config'), D365FO_BRIDGE_ENABLED: '1' };
delete env.D365FO_BRIDGE_PATH;
delete env.D365FO_PROFILE;

const ok = (message) => console.log(`ok    ${message}`);

try {
  // --omit=optional: the platform package comes from its tarball, and the others do not exist
  // in any registry yet.
  execSync(`npm install -g --prefix "${prefix}" --omit=optional --no-audit --no-fund ${packed.map((f) => `"${f}"`).join(' ')}`,
    { stdio: 'inherit' });

  const modules = windows ? path.join(prefix, 'node_modules') : path.join(prefix, 'lib', 'node_modules');
  const shim = (name) => (windows ? path.join(prefix, `${name}.cmd`) : path.join(prefix, 'bin', name));
  const cli = (args) => spawnSync(`"${shim('d365fo')}" ${args}`, { shell: true, encoding: 'utf8', env, cwd: root });

  const reported = cli('--version');
  assert.strictEqual(reported.status, 0, reported.stderr);
  assert.ok(reported.stdout.trim() === version || reported.stdout.trim().startsWith(`${version}+`),
    `d365fo --version reports '${reported.stdout.trim()}', expected ${version}`);
  ok(`d365fo --version -> ${reported.stdout.trim()}`);

  const unknown = cli('no-such-command');
  assert.notStrictEqual(unknown.status, 0, 'an unknown command must fail through the launcher');
  ok(`d365fo no-such-command -> exit ${unknown.status}`);

  const serverVersion = await mcpServerVersion(path.join(modules, 'd365fo-cli', 'launcher', 'd365fo-cli-mcp.js'));
  assert.ok(serverVersion === version || serverVersion.startsWith(`${version}+`),
    `the MCP server reports '${serverVersion}', expected ${version}`);
  ok(`d365fo-cli-mcp initialize -> ${serverVersion}`);

  if (windows) {
    const doctor = JSON.parse(cli('doctor --output json').stdout);
    const bridge = doctor.data.checks.find((check) => check.name === 'bridge.executable');
    const expected = path.join(modules, `d365fo-cli-${target}`, 'D365FO.Bridge', 'D365FO.Bridge.exe');
    assert.ok(bridge?.ok, `doctor does not find the bridge: ${JSON.stringify(bridge)}`);
    // .native expands the 8.3 names a Windows temp folder is often given in.
    const real = (file) => fs.realpathSync.native(file).toLowerCase();
    assert.strictEqual(real(bridge.detail), real(expected));
    ok(`doctor finds the packaged bridge`);
  }
  console.log('\nthe installed npm packages work.');
} finally {
  fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
}

// Keeps stdin open until the reply arrives, then closes it and expects the server to leave.
function mcpServerVersion(launcher) {
  return new Promise((resolve, reject) => {
    const server = spawn(process.execPath, [launcher], { env, cwd: root, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    let found;
    const timer = setTimeout(() => {
      server.kill();
      reject(new Error(`the MCP server did not answer and exit within 60 s\n${stderr}`));
    }, 60_000);
    server.stdout.on('data', (chunk) => {
      stdout += chunk;
      for (const line of stdout.split('\n').slice(0, -1)) {
        const info = JSON.parse(line).result?.serverInfo;
        if (info && !found) {
          found = info.version;
          server.stdin.end();
        }
      }
    });
    server.stderr.on('data', (chunk) => { stderr += chunk; });
    server.on('error', reject);
    server.on('exit', () => {
      clearTimeout(timer);
      if (found) resolve(found);
      else reject(new Error(`the MCP server exited without answering\n${stderr}`));
    });
    server.stdin.write(`${JSON.stringify({
      jsonrpc: '2.0',
      id: 1,
      method: 'initialize',
      params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'smoke', version: '1' } },
    })}\n`);
  });
}
