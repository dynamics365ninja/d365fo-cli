'use strict';

// Starts a native d365fo binary from the platform package npm installed beside this one.
// Nothing else happens here: arguments, standard streams and the exit code pass through.

const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

// "<process.platform>-<process.arch>" of every build a release publishes.
// scripts/build-npm-packages.mjs reads this list, so there is one.
const PLATFORMS = ['win32-x64', 'linux-x64', 'darwin-x64', 'darwin-arm64'];

// Tool name -> folder of the platform package that holds it (the release archive layout).
const TOOLS = { 'd365fo': 'bin', 'd365fo-cli-mcp': 'mcp' };

class LaunchError extends Error {}

function platformPackage(target) {
  return `d365fo-cli-${target}`;
}

function executablePath(tool, options = {}) {
  const { platform = process.platform, arch = process.arch, resolve = require.resolve } = options;
  const target = `${platform}-${arch}`;
  if (!PLATFORMS.includes(target)) {
    throw new LaunchError(`d365fo-cli has no build for ${target}. Supported: ${PLATFORMS.join(', ')}.`);
  }

  const name = platformPackage(target);
  let manifest;
  try {
    manifest = resolve(`${name}/package.json`);
  } catch {
    throw new LaunchError(
      `The package ${name}, which holds the binaries, is not installed. `
      + 'Reinstall without --omit=optional: npm install -g d365fo-cli');
  }

  const file = platform === 'win32' ? `${tool}.exe` : tool;
  return path.join(path.dirname(manifest), TOOLS[tool], file);
}

function ensureExecutable(file) {
  try {
    fs.accessSync(file, fs.constants.X_OK);
  } catch {
    // An archive step that drops the mode leaves a binary npm installed but cannot run.
    // Without the right to change it, spawn reports the failure.
    try { fs.chmodSync(file, 0o755); } catch { /* reported by spawn */ }
  }
}

function launch(file, args) {
  const windows = process.platform === 'win32';
  if (!windows) ensureExecutable(file);

  const child = spawn(file, args, { stdio: 'inherit' });

  // The launcher must outlive the child to report its exit code. On Windows, Ctrl+C reaches
  // every process of the console by itself and child.kill() would terminate the child before it
  // could shut down, so the signal is only absorbed there.
  const handlers = ['SIGINT', 'SIGTERM', 'SIGHUP'].map((signal) => {
    const handler = windows ? () => {} : () => child.kill(signal);
    process.on(signal, handler);
    return [signal, handler];
  });
  const detach = () => handlers.forEach(([signal, handler]) => process.removeListener(signal, handler));

  child.on('error', (error) => {
    detach();
    process.stderr.write(`Could not start ${file}: ${error.message}\n`);
    process.exitCode = 1;
  });

  child.on('exit', (code, signal) => {
    detach();
    if (signal) process.kill(process.pid, signal);
    else process.exitCode = code;
  });

  return child;
}

function run(tool) {
  let file;
  try {
    file = executablePath(tool);
  } catch (error) {
    if (!(error instanceof LaunchError)) throw error;
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
    return;
  }
  launch(file, process.argv.slice(2));
}

module.exports = { PLATFORMS, TOOLS, LaunchError, platformPackage, executablePath, launch, run };
