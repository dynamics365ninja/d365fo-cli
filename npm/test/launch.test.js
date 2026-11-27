'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { spawnSync } = require('node:child_process');
const path = require('node:path');

const launcher = path.join(__dirname, '..', 'd365fo-cli', 'launcher', 'launch.js');
const { PLATFORMS, LaunchError, executablePath } = require(launcher);

const resolveInto = (root) => (request) => path.join(root, request);

test('each tool resolves into its folder of the platform package', () => {
  const resolve = resolveInto('/modules');
  assert.strictEqual(
    executablePath('d365fo', { platform: 'win32', arch: 'x64', resolve }),
    path.join('/modules', 'd365fo-cli-win32-x64', 'bin', 'd365fo.exe'));
  assert.strictEqual(
    executablePath('d365fo-cli-mcp', { platform: 'darwin', arch: 'arm64', resolve }),
    path.join('/modules', 'd365fo-cli-darwin-arm64', 'mcp', 'd365fo-cli-mcp'));
});

test('an unsupported platform is named with the supported ones', () => {
  assert.throws(
    () => executablePath('d365fo', { platform: 'linux', arch: 'arm64' }),
    (error) => error instanceof LaunchError && error.message.includes('linux-arm64')
      && PLATFORMS.every((target) => error.message.includes(target)));
});

test('a missing platform package says how to reinstall', () => {
  const resolve = () => { throw new Error('MODULE_NOT_FOUND'); };
  assert.throws(
    () => executablePath('d365fo', { platform: 'linux', arch: 'x64', resolve }),
    (error) => error instanceof LaunchError && error.message.includes('d365fo-cli-linux-x64')
      && error.message.includes('--omit=optional'));
});

// Node stands in for the native binary: what matters is what crosses the launcher.
function throughLauncher(script, args, input) {
  const wrapper = `require(${JSON.stringify(launcher)}).launch(process.execPath, ['-e', ${JSON.stringify(script)}, '--', ...process.argv.slice(1)])`;
  return spawnSync(process.execPath, ['-e', wrapper, '--', ...args], { input, encoding: 'utf8' });
}

test('arguments arrive as they were given', () => {
  const args = ['a b', 'line1\nline2', '"quoted"', '%PATH%', 'a&b|c<d>e^f', '', 'Název', '--flag=x y'];
  const result = throughLauncher('process.stdout.write(JSON.stringify(process.argv.slice(1)))', args);
  assert.strictEqual(result.status, 0, result.stderr);
  assert.deepStrictEqual(JSON.parse(result.stdout), args);
});

test('standard input reaches the child and its exit code comes back', () => {
  const result = throughLauncher(
    'process.stdin.pipe(process.stdout); process.stdin.on("end", () => { process.exitCode = 7; })',
    [], 'hello');
  assert.strictEqual(result.stdout, 'hello');
  assert.strictEqual(result.status, 7);
});

test('a binary that cannot start fails with its path', () => {
  const wrapper = `require(${JSON.stringify(launcher)}).launch('/no/such/d365fo', [])`;
  const result = spawnSync(process.execPath, ['-e', wrapper], { encoding: 'utf8' });
  assert.strictEqual(result.status, 1);
  assert.match(result.stderr, /Could not start \/no\/such\/d365fo/);
});
