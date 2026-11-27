// Assembles the npm packages of a release from published platform folders.
//
//   node scripts/build-npm-packages.mjs --version 0.3.0 --out npm-out [--pack] <rid>=<folder> ...
//
// <folder> holds what scripts/publish-release-layout.sh writes. One package per given platform
// goes to <out>/<name>, plus the `d365fo-cli` launcher package, which depends on all of them as
// optionalDependencies so that npm installs only the one that fits. --pack also writes the
// tarballs to <out>/tarballs. See docs/RELEASING.md.
import { execSync } from 'node:child_process';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const launcherDir = path.join(repo, 'npm', 'd365fo-cli');
const { PLATFORMS, TOOLS, platformPackage } = createRequire(import.meta.url)(
  path.join(launcherDir, 'launcher', 'launch.js'));

const RID_TARGETS = {
  'win-x64': 'win32-x64',
  'linux-x64': 'linux-x64',
  'osx-x64': 'darwin-x64',
  'osx-arm64': 'darwin-arm64',
};

function fail(message) {
  console.error(message);
  process.exit(1);
}

const { values, positionals } = parseArgs({
  options: { version: { type: 'string' }, out: { type: 'string' }, pack: { type: 'boolean' } },
  allowPositionals: true,
});
const { version, out } = values;
if (!version || !/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version)) fail('--version must be a semantic version.');
if (!out) fail('--out is required.');
if (positionals.length === 0) fail('Name at least one platform as <rid>=<folder>.');
if (fs.existsSync(out) && fs.readdirSync(out).length > 0) fail(`${out} is not empty.`);

const manifest = JSON.parse(fs.readFileSync(path.join(launcherDir, 'package.json'), 'utf8'));
const shared = {
  license: manifest.license,
  repository: manifest.repository,
  homepage: manifest.homepage,
  bugs: manifest.bugs,
};

const writeJson = (file, value) => fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`);
// npm packs a LICENSE file found beside package.json without being told to.
const addLicense = (folder) => fs.copyFileSync(path.join(repo, 'LICENSE'), path.join(folder, 'LICENSE'));
// Debug symbols and XML documentation are in the release archives; nothing runs them.
const shipped = (source) => !/\.pdb$|[\\/]D365FO\.[^\\/]*\.xml$/i.test(source);

const built = [];
for (const argument of positionals) {
  const separator = argument.indexOf('=');
  const rid = argument.slice(0, separator);
  const source = argument.slice(separator + 1);
  const target = RID_TARGETS[rid];
  if (separator < 0 || !target || !PLATFORMS.includes(target)) {
    fail(`'${argument}' is not <rid>=<folder> with a rid of ${Object.keys(RID_TARGETS).join(', ')}.`);
  }

  const [os, cpu] = target.split('-');
  const name = platformPackage(target);
  const destination = path.join(out, name);
  const folders = Object.values(TOOLS);
  if (os === 'win32') folders.push('D365FO.Bridge');

  for (const folder of folders) {
    const from = path.join(source, folder);
    if (!fs.existsSync(from)) fail(`${rid}: ${from} does not exist.`);
    fs.cpSync(from, path.join(destination, folder), { recursive: true, filter: shipped });
  }

  const suffix = os === 'win32' ? '.exe' : '';
  const executables = Object.entries(TOOLS).map(([tool, folder]) => path.join(folder, tool + suffix));
  if (os === 'win32') executables.push(path.join('D365FO.Bridge', 'D365FO.Bridge.exe'));
  for (const executable of executables) {
    const file = path.join(destination, executable);
    if (!fs.existsSync(file)) fail(`${rid}: ${executable} is missing from ${source}.`);
    fs.chmodSync(file, 0o755);
  }

  writeJson(path.join(destination, 'package.json'), {
    name,
    version,
    description: `The ${target} binaries of d365fo-cli. Install d365fo-cli instead.`,
    ...shared,
    os: [os],
    cpu: [cpu],
    files: folders,
    preferUnplugged: true,
  });
  fs.writeFileSync(path.join(destination, 'README.md'),
    `# ${name}\n\nThe ${target} binaries of [d365fo-cli](https://www.npmjs.com/package/d365fo-cli). `
    + 'Install that package; npm adds this one on a matching system.\n');
  addLicense(destination);
  built.push(name);
}

const launcher = path.join(out, manifest.name);
fs.cpSync(launcherDir, launcher, { recursive: true });
addLicense(launcher);
const { private: _, ...published } = manifest;
writeJson(path.join(launcher, 'package.json'), {
  ...published,
  version,
  optionalDependencies: Object.fromEntries(PLATFORMS.map((target) => [platformPackage(target), version])),
});
built.push(manifest.name);

if (values.pack) {
  const tarballs = path.resolve(out, 'tarballs');
  fs.mkdirSync(tarballs, { recursive: true });
  for (const name of built) {
    const [info] = JSON.parse(execSync(`npm pack --json --pack-destination "${tarballs}"`, {
      cwd: path.join(out, name),
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
    }));
    const megabytes = (bytes) => (bytes / 1024 / 1024).toFixed(1);
    console.log(`${info.filename}: ${megabytes(info.size)} MB packed, ${megabytes(info.unpackedSize)} MB unpacked, ${info.entryCount} files`);
  }
} else {
  console.log(built.join('\n'));
}
