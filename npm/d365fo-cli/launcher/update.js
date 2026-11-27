'use strict';

// `d365fo update` of an npm installation. It runs here, in Node, because npm replaces the
// platform package and Windows does not let a running d365fo.exe be replaced. The binary
// answers the same command for every other kind of installation (UpdateCommand.cs).

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { PACKAGE, platformPackage } = require('./launch.js');
const VERSION = /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/;

/**
 * The options of `update` when `argv` is an update this launcher runs, otherwise null: any
 * other command, help, and arguments it does not know go to the binary, which prints the help
 * or rejects them.
 */
function parseUpdate(argv) {
  const options = { check: false, to: undefined, output: undefined };
  let command = false;

  for (let i = 0; i < argv.length; i++) {
    const [name, inline] = argv[i].startsWith('--') && argv[i].includes('=')
      ? [argv[i].slice(0, argv[i].indexOf('=')), argv[i].slice(argv[i].indexOf('=') + 1)]
      : [argv[i], undefined];
    const value = () => (inline !== undefined ? inline : argv[++i]);

    // The global options may stand before the command; an update reads no configuration.
    if (name === '--profile') { if (value() === undefined) return null; continue; }
    if (name === '--no-refresh') continue;
    if (!command) {
      if (name !== 'update') return null;
      command = true;
    } else if (name === '--check' && inline === undefined) {
      options.check = true;
    } else if (name === '--to') {
      options.to = value();
      if (options.to === undefined) return null;
    } else if (name === '--output' || name === '-o') {
      options.output = value();
      if (options.output === undefined) return null;
    } else {
      return null;
    }
  }

  return command ? options : null;
}

/** Negative, zero or positive as `a` is older than, equal to or newer than `b`. */
function compareVersions(a, b) {
  const split = (version) => {
    const clean = version.split('+')[0];
    const dash = clean.indexOf('-');
    return [
      (dash < 0 ? clean : clean.slice(0, dash)).split('.').map(Number),
      dash < 0 ? [] : clean.slice(dash + 1).split('.'),
    ];
  };
  const [coreA, preA] = split(a);
  const [coreB, preB] = split(b);
  for (let i = 0; i < 3; i++) {
    if (coreA[i] !== coreB[i]) return coreA[i] - coreB[i];
  }
  // A release is newer than any of its pre-releases.
  if (!preA.length || !preB.length) return preB.length - preA.length;
  for (let i = 0; i < Math.max(preA.length, preB.length); i++) {
    if (preA[i] === undefined) return -1;
    if (preB[i] === undefined) return 1;
    const order = preA[i].localeCompare(preB[i], 'en', { numeric: true });
    if (order !== 0) return order;
  }
  return 0;
}

/**
 * The npm prefix this package is globally installed under, or null for any other place (a
 * project's node_modules, an npx cache), which `npm install -g` would not update.
 */
function globalPrefix(packageDir, { windows = process.platform === 'win32', exists = fs.existsSync } = {}) {
  // A scoped package sits one folder deeper: node_modules/@scope/name.
  const scope = path.dirname(packageDir);
  if (path.basename(scope) !== PACKAGE.split('/')[0]) return null;
  const modules = path.dirname(scope);
  if (path.basename(modules) !== 'node_modules') return null;
  if (windows) {
    const prefix = path.dirname(modules);
    return exists(path.join(prefix, 'd365fo.cmd')) ? prefix : null;
  }
  const lib = path.dirname(modules);
  if (path.basename(lib) !== 'lib') return null;
  const prefix = path.dirname(lib);
  return exists(path.join(prefix, 'bin', 'd365fo')) ? prefix : null;
}

function runNpm(args, { capture = false } = {}) {
  const windows = process.platform === 'win32';
  // npm is a .cmd on Windows, which only a shell starts. Every argument is either fixed, a
  // checked version, or the prefix path, quoted.
  const command = windows ? ['npm', ...args.map((a) => (/\s|[&()^]/.test(a) ? `"${a}"` : a))].join(' ') : 'npm';
  return spawnSync(command, windows ? [] : args, {
    shell: windows,
    cwd: os.homedir(),
    encoding: 'utf8',
    // npm's own report goes to stderr: stdout carries only this command's result.
    stdio: capture ? ['ignore', 'pipe', 'pipe'] : ['ignore', 2, 2],
  });
}

function installedVersion(packageDir, name) {
  try {
    const manifest = require.resolve(`${name}/package.json`, { paths: [packageDir] });
    return JSON.parse(fs.readFileSync(manifest, 'utf8')).version;
  } catch {
    return null;
  }
}

// A running d365fo process does not stop npm on Windows: npm moves the package aside, installs
// the new one, and cannot delete the folder the process still holds. The folder it moves it to
// is `.<name>-<hash>` beside the package, inside the scope folder.
function leftBehind(packageDir) {
  const scope = path.dirname(packageDir);
  try {
    return fs.readdirSync(scope)
      .filter((name) => name.startsWith(`.${path.basename(packageDir)}-`))
      .map((name) => path.join(scope, name));
  } catch {
    return [];
  }
}

/** Runs the update and returns the result envelope the CLI uses. */
function update(options, {
  packageDir = path.dirname(__dirname),
  npm = runNpm,
  prefix = globalPrefix(packageDir),
  platformVersion = () => installedVersion(packageDir, platformPackage(`${process.platform}-${process.arch}`)),
  say = (message) => process.stderr.write(`${message}\n`),
} = {}) {
  const fail = (code, message, hint) => ({ ok: false, error: { code, message, hint } });
  const current = JSON.parse(fs.readFileSync(path.join(packageDir, 'package.json'), 'utf8')).version;

  const requested = options.to === undefined ? undefined : options.to.replace(/^v/, '');
  if (requested !== undefined && !VERSION.test(requested)) {
    return fail('BAD_INPUT', `'${options.to}' is not a version.`, 'Pass --to 1.2.3.');
  }

  const view = npm(['view', requested ? `${PACKAGE}@${requested}` : PACKAGE, 'version'], { capture: true });
  const target = view.status === 0 ? view.stdout.trim() : '';
  if (!VERSION.test(target)) {
    // npm answers an unknown version with E404 or, in some releases, with nothing.
    return requested && (view.status === 0 || /E404|No match found/.test(view.stderr || ''))
      ? fail('VERSION_NOT_FOUND', `${PACKAGE}@${requested} is not published.`, `List them with: npm view ${PACKAGE} versions`)
      : fail('UPDATE_CHECK_FAILED', `npm could not look up ${PACKAGE}: ${(view.stderr || '').trim() || 'no answer'}`,
        'Check the network and the registry npm is configured to use.');
  }

  const order = compareVersions(target, current);
  // Without --to, an installation ahead of `latest` (a pre-release) is not moved back.
  const available = requested ? order !== 0 : order > 0;
  const data = { current, target, updateAvailable: available, updated: false };
  if (options.check || !available) return { ok: true, data };

  if (!prefix) {
    return fail('NOT_GLOBAL_INSTALL', `${packageDir} is not a global npm installation.`,
      `Update it where it was installed, for example: npm install ${PACKAGE}@${target}`);
  }

  say(`Updating ${PACKAGE} ${current} -> ${target}`);
  const locked = 'Stop what runs from the package — `d365fo daemon stop`, editors running d365fo-cli-mcp — and run the update again.';
  const install = npm(['install', '-g', '--prefix', prefix, `${PACKAGE}@${target}`]);
  if (install.status !== 0) {
    return fail('UPDATE_FAILED', `npm install exited with ${install.status}.`, locked);
  }

  // npm treats a platform package it could not install as optional and still succeeds.
  const binaries = platformVersion();
  if (binaries !== target) {
    return fail('UPDATE_INCOMPLETE',
      `${PACKAGE} is ${target}, but its binaries are ${binaries ?? 'missing'}.`,
      `${locked} Or: npm install -g ${PACKAGE}@${target}`);
  }

  const result = { ok: true, data: { ...data, updated: true } };
  const leftovers = leftBehind(packageDir);
  if (leftovers.length > 0) {
    result.warnings = [
      'A d365fo process started before the update is still running the old version: run '
      + '`d365fo daemon stop` and restart the editors that use d365fo-cli-mcp. Then delete what npm '
      + `could not remove: ${leftovers.join(', ')}`,
    ];
  }
  return result;
}

function print(result, output) {
  const json = output ? output.toLowerCase() === 'json' : !(process.stdout.isTTY && process.stderr.isTTY);
  if (json) {
    process.stdout.write(`${JSON.stringify(result)}\n`);
  } else if (!result.ok) {
    process.stderr.write(`${result.error.message}\n${result.error.hint}\n`);
  } else {
    const { current, target, updated, updateAvailable } = result.data;
    process.stdout.write(updated ? `Updated ${current} -> ${target}.\n`
      : updateAvailable ? `${target} is available (installed: ${current}). Run: d365fo update\n`
        : `${current} is up to date.\n`);
    for (const warning of result.warnings ?? []) process.stderr.write(`warning: ${warning}\n`);
  }
}

function runUpdate(options) {
  // A working directory inside the package would keep npm from replacing it on Windows.
  process.chdir(os.homedir());
  const result = update(options);
  print(result, options.output);
  process.exitCode = result.ok ? 0 : 1;
}

module.exports = { parseUpdate, compareVersions, globalPrefix, update, runUpdate };
