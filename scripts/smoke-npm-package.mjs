// Serves the packed npm packages of this platform from a local registry, then does what a user
// would with the real npm: install, run both commands, find the bridge, install the skills,
// update to a newer version and back.
//
//   node scripts/smoke-npm-package.mjs --version 0.3.0 --tarballs npm-out/tarballs
import assert from 'node:assert';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
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
const PACKAGE = 'd365fo-cli';
const PLATFORM_PACKAGE = `@dynamics365ninja/${PACKAGE}-${target}`;
const names = [PACKAGE, PLATFORM_PACKAGE];
// What npm pack names the tarball of a scoped package: @scope/name -> scope-name-<version>.tgz.
const tarball = (name, number) => `${name.replace(/^@/, '').replace('/', '-')}-${number}.tgz`;
const NEWER = '999.0.0';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'd365fo-npm-smoke-'));
const prefix = path.join(root, 'prefix');
const userconfig = path.join(root, 'npmrc');
fs.writeFileSync(userconfig, '');

// The other platforms are not served: npm must skip those optional dependencies.
const packages = new Map(names.map((name) => [name, new Map()]));
const registry = http.createServer((request, response) => {
  const url = decodeURIComponent(request.url.split('?')[0]).slice(1);
  if (url.startsWith('tarballs/')) {
    const file = [...packages.values()].flatMap((v) => [...v.values()])
      .find((entry) => path.basename(entry.file) === url.slice('tarballs/'.length))?.file;
    if (file) return fs.createReadStream(file).pipe(response);
  }
  const versions = packages.get(url);
  if (!versions) {
    response.writeHead(404, { 'content-type': 'application/json' });
    return response.end('{"error":"Not found"}');
  }
  const base = `http://127.0.0.1:${registry.address().port}`;
  response.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-cache' });
  return response.end(JSON.stringify({
    name: url,
    'dist-tags': { latest: [...versions.keys()].at(-1) },
    versions: Object.fromEntries([...versions].map(([number, entry]) => [number, {
      ...entry.manifest,
      _id: `${url}@${number}`,
      dist: { tarball: `${base}/tarballs/${path.basename(entry.file)}`, integrity: entry.integrity },
    }])),
  }));
});
await new Promise((resolve) => registry.listen(0, '127.0.0.1', resolve));

// A configuration of its own: the check must not read, or depend on, this machine's.
const env = {
  ...process.env,
  D365FO_CONFIG_DIR: path.join(root, 'config'),
  D365FO_BRIDGE_ENABLED: '1',
  npm_config_registry: `http://127.0.0.1:${registry.address().port}/`,
  npm_config_cache: path.join(root, 'cache'),
  npm_config_userconfig: userconfig,
  npm_config_audit: 'false',
  npm_config_fund: 'false',
  npm_config_update_notifier: 'false',
};
delete env.D365FO_BRIDGE_PATH;
delete env.D365FO_PROFILE;

const ok = (message) => console.log(`ok    ${message}`);

// Never synchronous: the registry answers from this process.
function sh(command, cwd = root) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, { shell: true, cwd, env, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (status) => resolve({ status, stdout, stderr }));
  });
}

async function must(command, cwd) {
  const result = await sh(command, cwd);
  assert.strictEqual(result.status, 0, `${command}\n${result.stdout}\n${result.stderr}`);
  return result;
}

function publish(name, file) {
  const staging = fs.mkdtempSync(path.join(root, 'manifest-'));
  // A relative name: GNU tar reads "C:" as a host.
  fs.copyFileSync(file, path.join(staging, 'package.tgz'));
  return must('tar -xzf package.tgz package/package.json', staging).then(() => {
    const manifest = JSON.parse(fs.readFileSync(path.join(staging, 'package', 'package.json'), 'utf8'));
    const integrity = `sha512-${createHash('sha512').update(fs.readFileSync(file)).digest('base64')}`;
    packages.get(name).set(manifest.version, { manifest, file: path.resolve(file), integrity });
  });
}

// The same binaries under a higher version number: what an update moves to.
async function publishNewer() {
  for (const name of names) {
    const staging = fs.mkdtempSync(path.join(root, 'newer-'));
    fs.copyFileSync(packages.get(name).get(version).file, path.join(staging, 'package.tgz'));
    await must('tar -xzf package.tgz', staging);
    const folder = path.join(staging, 'package');
    const file = path.join(folder, 'package.json');
    const manifest = JSON.parse(fs.readFileSync(file, 'utf8'));
    manifest.version = NEWER;
    for (const dependency of Object.keys(manifest.optionalDependencies ?? {})) {
      manifest.optionalDependencies[dependency] = NEWER;
    }
    fs.writeFileSync(file, JSON.stringify(manifest));
    await must(`npm pack --pack-destination "${staging}"`, folder);
    await publish(name, path.join(staging, tarball(name, NEWER)));
  }
}

try {
  for (const name of names) {
    const file = path.resolve(tarballs, tarball(name, version));
    assert.ok(fs.existsSync(file), `${file} does not exist`);
    await publish(name, file);
  }

  await must(`npm install -g --prefix "${prefix}" ${PACKAGE}`);
  ok(`npm install -g ${PACKAGE}`);

  const modules = windows ? path.join(prefix, 'node_modules') : path.join(prefix, 'lib', 'node_modules');
  const packageDir = path.join(modules, PACKAGE);
  const shim = windows ? path.join(prefix, 'd365fo.cmd') : path.join(prefix, 'bin', 'd365fo');
  const cli = (args) => sh(`"${shim}" ${args}`);
  const binaries = () => path.join(packageDir, 'node_modules', ...PLATFORM_PACKAGE.split('/'));
  const installed = (folder) => JSON.parse(fs.readFileSync(path.join(folder, 'package.json'), 'utf8')).version;

  const runs = async () => {
    const reported = await cli('--version');
    assert.strictEqual(reported.status, 0, reported.stderr);
    assert.ok(reported.stdout.trim() === version || reported.stdout.trim().startsWith(`${version}+`),
      `d365fo --version reports '${reported.stdout.trim()}', expected ${version}`);
    return reported.stdout.trim();
  };
  ok(`d365fo --version -> ${await runs()}`);

  const unknown = await cli('no-such-command');
  assert.notStrictEqual(unknown.status, 0, 'an unknown command must fail through the launcher');
  ok(`d365fo no-such-command -> exit ${unknown.status}`);

  const serverVersion = await mcpServerVersion(path.join(packageDir, 'launcher', 'd365fo-cli-mcp.js'));
  assert.ok(serverVersion === version || serverVersion.startsWith(`${version}+`),
    `the MCP server reports '${serverVersion}', expected ${version}`);
  ok(`d365fo-cli-mcp initialize -> ${serverVersion}`);

  const doctor = JSON.parse((await cli('doctor --output json')).stdout);
  const check = (name) => doctor.data.checks.find((c) => c.name === name);
  assert.match(check('install').detail, /^npm: /);
  ok('doctor recognises the npm installation');
  if (windows) {
    const bridge = check('bridge.executable');
    assert.ok(bridge?.ok, `doctor does not find the bridge: ${JSON.stringify(bridge)}`);
    // .native expands the 8.3 names a Windows temp folder is often given in.
    const real = (file) => fs.realpathSync.native(file).toLowerCase();
    assert.strictEqual(real(bridge.detail), real(path.join(binaries(), 'D365FO.Bridge', 'D365FO.Bridge.exe')));
    ok('doctor finds the packaged bridge');
  }

  const repository = path.join(root, 'xpp repository');
  fs.mkdirSync(repository);
  const skills = await cli(`skills install "${repository}" --for all --output json`);
  assert.strictEqual(skills.status, 0, skills.stdout + skills.stderr);
  for (const folder of ['.claude/skills/d365fo-cli/SKILL.md', '.github/skills/d365fo-cli/SKILL.md']) {
    assert.ok(fs.existsSync(path.join(repository, folder)), `${folder} was not written`);
  }
  ok('d365fo skills install writes both layouts');

  const update = async (args, status = 0) => {
    const result = await cli(`update ${args} --output json`);
    assert.strictEqual(result.status, status, `update ${args}\n${result.stdout}\n${result.stderr}`);
    return JSON.parse(result.stdout);
  };
  assert.strictEqual((await update('')).data.updateAvailable, false);
  ok('d365fo update with nothing newer changes nothing');

  await publishNewer();
  assert.deepStrictEqual((await update('--check')).data,
    { current: version, target: NEWER, updateAvailable: true, updated: false });
  assert.strictEqual(installed(packageDir), version);
  ok('d365fo update --check reports the newer version');

  // With a server running, as an editor would keep one: the update must still succeed.
  const holder = spawn(path.join(binaries(), 'mcp', `d365fo-cli-mcp${windows ? '.exe' : ''}`), [],
    { env, cwd: root, stdio: ['pipe', 'ignore', 'ignore'] });
  const left = new Promise((resolve) => holder.on('exit', resolve));
  await new Promise((resolve) => setTimeout(resolve, 3000));
  assert.strictEqual(holder.exitCode, null, 'the server must be running during the update');
  const updated = await update('').finally(() => holder.kill());
  await left;
  assert.strictEqual(updated.data.updated, true);
  if (windows) assert.match(updated.warnings?.[0] ?? '', /still running the old version/);
  assert.strictEqual(installed(packageDir), NEWER);
  assert.strictEqual(installed(binaries()), NEWER);
  await runs();
  ok(`d365fo update -> ${NEWER} while the MCP server runs, and the new one runs`);

  assert.strictEqual((await update(`--to ${version}`)).data.updated, true);
  assert.strictEqual(installed(binaries()), version);
  await runs();
  ok(`d365fo update --to ${version} goes back`);

  assert.strictEqual((await update('--to 5.5.5', 1)).error.code, 'VERSION_NOT_FOUND');
  ok('d365fo update --to an unpublished version is refused');

  console.log('\nthe installed npm packages work.');
} finally {
  registry.close();
  registry.closeAllConnections();
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
