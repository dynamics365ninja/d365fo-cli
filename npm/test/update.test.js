'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { parseUpdate, compareVersions, globalPrefix, update } = require(
  path.join(__dirname, '..', 'd365fo-cli', 'launcher', 'update.js'));

test('only an update with known options is handled by the launcher', () => {
  assert.deepStrictEqual(parseUpdate(['update']), { check: false, to: undefined, output: undefined });
  assert.deepStrictEqual(
    parseUpdate(['--profile', 'a', 'update', '--check', '--to=1.2.3', '-o', 'json']),
    { check: true, to: '1.2.3', output: 'json' });
  // Everything else belongs to the binary: its help, and its refusal of what it does not know.
  for (const argv of [[], ['version'], ['search', 'update'], ['update', '--help'], ['update', '--force'],
    ['update', '--to'], ['--profile', 'update']]) {
    assert.strictEqual(parseUpdate(argv), null, argv.join(' '));
  }
});

test('versions are ordered as semantic versions', () => {
  const ordered = ['0.2.0', '0.3.0-dev', '0.3.0-rc.2', '0.3.0-rc.10', '0.3.0', '0.10.0', '1.0.0'];
  for (let i = 1; i < ordered.length; i++) {
    assert.ok(compareVersions(ordered[i - 1], ordered[i]) < 0, `${ordered[i - 1]} < ${ordered[i]}`);
    assert.ok(compareVersions(ordered[i], ordered[i - 1]) > 0);
  }
  assert.strictEqual(compareVersions('0.3.0+abc', '0.3.0+def'), 0);
});

test('only a global installation has a prefix to update', () => {
  const unix = path.join('/usr/local', 'lib', 'node_modules', 'd365fo-cli');
  assert.strictEqual(globalPrefix(unix, { windows: false, exists: () => true }), path.join('/usr/local'));
  assert.strictEqual(globalPrefix(path.join('/project', 'node_modules', 'd365fo-cli'), { windows: false, exists: () => true }), null);

  const prefix = path.join('/prefix');
  const installed = path.join(prefix, 'node_modules', 'd365fo-cli');
  const shim = path.join(prefix, 'd365fo.cmd');
  assert.strictEqual(globalPrefix(installed, { windows: true, exists: (f) => f === shim }), prefix);
  // A project's node_modules has no command shim beside it.
  assert.strictEqual(globalPrefix(installed, { windows: true, exists: () => false }), null);
});

function scenario({ current = '1.0.0', latest = '1.1.0', install = 0, binaries, prefix = '/prefix' } = {}) {
  const modules = fs.mkdtempSync(path.join(os.tmpdir(), 'd365fo-update-test-'));
  const packageDir = path.join(modules, 'd365fo-cli');
  fs.mkdirSync(packageDir);
  fs.writeFileSync(path.join(packageDir, 'package.json'), JSON.stringify({ version: current }));
  const calls = [];
  let installed = current;
  const npm = (args) => {
    calls.push(args.join(' '));
    if (args[0] === 'view') {
      const [, wanted] = args[1].split('@');
      if (latest === null) return { status: 1, stdout: '', stderr: 'ENOTFOUND' };
      return { status: 0, stdout: wanted === '9.9.9' ? '' : `${wanted ?? latest}\n`, stderr: '' };
    }
    if (install === 0) installed = args.at(-1).split('@')[1];
    return { status: install };
  };
  const run = (options = {}) => update({ check: false, ...options }, {
    packageDir, npm, prefix, say: () => {}, platformVersion: () => (binaries === undefined ? installed : binaries),
  });
  return { run, calls, modules, cleanup: () => fs.rmSync(modules, { recursive: true, force: true }) };
}

test('a newer release is installed into the same prefix', (t) => {
  const s = scenario();
  t.after(s.cleanup);
  assert.deepStrictEqual(s.run().data, { current: '1.0.0', target: '1.1.0', updateAvailable: true, updated: true });
  assert.deepStrictEqual(s.calls, ['view d365fo-cli version', 'install -g --prefix /prefix d365fo-cli@1.1.0']);
});

test('files a running process kept npm from removing are reported', (t) => {
  const clean = scenario();
  const held = scenario();
  t.after(() => [clean, held].forEach((s) => s.cleanup()));
  fs.mkdirSync(path.join(held.modules, '.d365fo-cli-Ab12Cd34'));
  assert.strictEqual(clean.run().warnings, undefined);
  const [warning] = held.run().warnings;
  assert.match(warning, /still running the old version/);
  assert.ok(warning.includes(path.join(held.modules, '.d365fo-cli-Ab12Cd34')));
});

test('--check and an up-to-date installation change nothing', (t) => {
  const check = scenario();
  const same = scenario({ latest: '1.0.0' });
  const ahead = scenario({ current: '1.2.0-rc.1' });
  t.after(() => [check, same, ahead].forEach((s) => s.cleanup()));
  assert.strictEqual(check.run({ check: true }).data.updateAvailable, true);
  assert.strictEqual(same.run().data.updated, false);
  assert.strictEqual(ahead.run().data.updateAvailable, false);
  for (const s of [check, same, ahead]) assert.strictEqual(s.calls.length, 1);
});

test('--to moves to an older version, and refuses what is not one', (t) => {
  const s = scenario({ current: '1.1.0' });
  t.after(s.cleanup);
  assert.strictEqual(s.run({ to: 'v1.0.0' }).data.updated, true);
  assert.strictEqual(s.run({ to: '9.9.9' }).error.code, 'VERSION_NOT_FOUND');
  const before = s.calls.length;
  assert.strictEqual(s.run({ to: '1.0.0 & echo' }).error.code, 'BAD_INPUT');
  assert.strictEqual(s.calls.length, before, 'nothing may reach a shell');
});

test('each way an update fails has its own code', (t) => {
  const offline = scenario({ latest: null });
  const local = scenario({ prefix: null });
  const refused = scenario({ install: 1 });
  const partial = scenario({ binaries: null });
  t.after(() => [offline, local, refused, partial].forEach((s) => s.cleanup()));
  assert.strictEqual(offline.run().error.code, 'UPDATE_CHECK_FAILED');
  assert.strictEqual(local.run().error.code, 'NOT_GLOBAL_INSTALL');
  assert.strictEqual(refused.run().error.code, 'UPDATE_FAILED');
  assert.strictEqual(partial.run().error.code, 'UPDATE_INCOMPLETE');
});
