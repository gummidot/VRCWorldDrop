import { readFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

// Wraps the exported .unitypackage in a .zip, for distribution channels that
// serve a single downloadable archive rather than a bare .unitypackage.
//
// This is NOT the VPM/VCC zip. The VCC zip is the flat package-folder layout
// (package.json at the zip root) and is built by the release workflow. This zip
// just contains the .unitypackage a user imports into Assets/.
//
// Run `npm run export` first (or use `npm run export:zip` to do both).

const __dirname = dirname(fileURLToPath(import.meta.url));
// Tools/PackageExporter -> repo root
const rootDir = resolve(__dirname, '..', '..');

// Version comes from the VPM manifest, which lives inside the package folder.
const pkg = JSON.parse(readFileSync(resolve(rootDir, 'Assets/VRCWorldDrop/package.json'), 'utf8'));
const version = pkg.version;

const name = `VRCWorldDrop_v${version}.unitypackage`;
const zipName = name.replace(/\.unitypackage$/, '.zip');

if (!existsSync(resolve(rootDir, name))) {
  console.error(`${name} not found. Run \`npm run export\` first.`);
  process.exit(1);
}

if (process.platform === 'win32') {
  execFileSync('powershell', [
    '-NoProfile', '-Command',
    `Compress-Archive -Path '${name}' -DestinationPath '${zipName}' -Force`,
  ], { cwd: rootDir, stdio: 'inherit' });
} else {
  // -j: junk paths, so the zip holds the .unitypackage with no leading dirs.
  execFileSync('zip', ['-j', zipName, name], { cwd: rootDir, stdio: 'inherit' });
}

console.log(`Done: ${zipName}`);
