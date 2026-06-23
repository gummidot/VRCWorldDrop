import { readFileSync, readdirSync, existsSync, createWriteStream } from 'node:fs';
import { resolve, dirname, relative, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createGzip } from 'node:zlib';
import { pack } from 'tar-stream';

// Builds a VRCWorldDrop .unitypackage straight from disk, no Unity Editor needed.
// A .unitypackage is just a gzipped tar where every asset lives in a folder named
// after its GUID, holding: asset (the file), asset.meta (the .meta), pathname
// (where Unity drops it on import).
//
// Usage:
//   node export.mjs            -> Assets/VRCWorldDrop/...        (legacy/import-into-Assets)
//   node export.mjs --vpm      -> Packages/<pkg>/...             (VPM package layout)

const __dirname = dirname(fileURLToPath(import.meta.url));
// Tools/PackageExporter -> repo root
const rootDir = resolve(__dirname, '..', '..');

// --- Config -----------------------------------------------------------------

const PACKAGE_NAME = 'com.gummidot.vrc-world-drop';

// Source folder on disk (relative to repo root) and the path it ships to.
const SOURCE_DIR = join('Assets', 'VRCWorldDrop');
const ASSETS_PREFIX = 'Assets/VRCWorldDrop';
const VPM_PREFIX = `Packages/${PACKAGE_NAME}`;

// Paths to drop, relative to SOURCE_DIR (POSIX separators). Matches the dir
// itself or anything under it.
const EXCLUDE = [
  'Synced/_Temp', // build-time scratch, regenerated every build by WdsBuildHook
];

// --- Args -------------------------------------------------------------------

const args = process.argv.slice(2);
const target = args.includes('--vpm') || args.includes('--target=packages') ? 'packages' : 'assets';

// --- Derived ----------------------------------------------------------------

const sourceDir = resolve(rootDir, SOURCE_DIR);

// Version comes from the VPM manifest, which lives inside the package folder.
const pkg = JSON.parse(readFileSync(resolve(sourceDir, 'package.json'), 'utf8'));
const version = pkg.version;
const pathPrefix = target === 'packages' ? VPM_PREFIX : ASSETS_PREFIX;
const outputName =
  target === 'packages'
    ? `VRCWorldDrop_v${version}_VPM.unitypackage`
    : `VRCWorldDrop_v${version}.unitypackage`;
const outputFile = resolve(rootDir, outputName);

// --- Helpers ----------------------------------------------------------------

function shouldExclude(relPath) {
  const normalized = relPath.replace(/\\/g, '/');
  return EXCLUDE.some((ex) => normalized === ex || normalized.startsWith(ex + '/'));
}

function extractGuid(metaPath) {
  const content = readFileSync(metaPath, 'utf8');
  const match = content.match(/^guid:\s*([0-9a-f]{32})\s*$/m);
  if (!match) {
    throw new Error(`No GUID found in ${metaPath}`);
  }
  return match[1];
}

function walkDir(dir) {
  const entries = [];
  for (const item of readdirSync(dir, { withFileTypes: true })) {
    const fullPath = join(dir, item.name);
    const relToSource = relative(sourceDir, fullPath).replace(/\\/g, '/');

    if (item.name.endsWith('.meta')) continue; // handled alongside its asset
    if (shouldExclude(relToSource)) continue;

    if (item.isDirectory()) {
      // Don't emit folder entries; Unity recreates folders from asset pathnames.
      entries.push(...walkDir(fullPath));
    } else {
      entries.push({ fullPath, unityPath: `${pathPrefix}/${relToSource}` });
    }
  }
  return entries;
}

function addAsset(archive, { guid, fileContent, metaContent, unityPath }) {
  archive.entry({ name: `${guid}/`, type: 'directory', mode: 0o777 });
  archive.entry({ name: `${guid}/asset`, mode: 0o777 }, fileContent);
  archive.entry({ name: `${guid}/asset.meta`, mode: 0o777 }, metaContent);
  archive.entry({ name: `${guid}/pathname`, mode: 0o777 }, unityPath);
}

// --- Build ------------------------------------------------------------------

async function buildPackage() {
  console.log(`Building VRCWorldDrop v${version} (${target} layout)`);
  console.log(`  Source:  ${SOURCE_DIR}`);
  console.log(`  Ships to: ${pathPrefix}/`);
  console.log(`  Output:  ${relative(rootDir, outputFile)}`);
  console.log(`  Excludes: ${EXCLUDE.join(', ') || '(none)'}`);
  console.log('');

  if (!existsSync(sourceDir)) {
    throw new Error(`Source folder not found: ${sourceDir}`);
  }

  const archive = pack();
  const output = createWriteStream(outputFile);
  archive.pipe(createGzip()).pipe(output);

  let count = 0;
  let skipped = 0;

  for (const entry of walkDir(sourceDir)) {
    const metaPath = entry.fullPath + '.meta';
    if (!existsSync(metaPath)) {
      console.warn(`  SKIP (no .meta): ${entry.unityPath}`);
      skipped++;
      continue;
    }
    addAsset(archive, {
      guid: extractGuid(metaPath),
      fileContent: readFileSync(entry.fullPath),
      metaContent: readFileSync(metaPath),
      unityPath: entry.unityPath,
    });
    count++;
  }

  archive.finalize();
  await new Promise((res, rej) => {
    output.on('finish', res);
    output.on('error', rej);
  });

  console.log(`Packed ${count} assets${skipped ? ` (${skipped} skipped)` : ''}`);
  console.log(`Done: ${relative(rootDir, outputFile)}`);
}

buildPackage().catch((err) => {
  console.error(err);
  process.exit(1);
});
