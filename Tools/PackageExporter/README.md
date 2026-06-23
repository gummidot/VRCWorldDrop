# Package Exporter

Builds `VRCWorldDrop` `.unitypackage` files straight from disk, without opening the
Unity Editor. Used both for local testing and by the release workflow
([.github/workflows/release.yml](../../.github/workflows/release.yml)).

A `.unitypackage` is just a gzipped tar where every asset lives in a folder named
after its GUID, holding `asset` (the file), `asset.meta` (its `.meta`), and
`pathname` (where Unity drops it on import).

## Usage

The npm project lives at the repo root, so run from there:

```sh
npm install          # first time only (or `npm ci` from the committed lockfile)

npm run export       # -> VRCWorldDrop_v<version>.unitypackage      (Assets/ layout)
npm run export:vpm   # -> VRCWorldDrop_v<version>_VPM.unitypackage  (Packages/ layout)
npm run export:zip   # -> VRCWorldDrop_v<version>.zip               (the Assets unitypackage, zipped)
npm run export:all   # all of the above
npm run zip          # zip an already-exported unitypackage
```

Output `.unitypackage` and `.zip` files land in the repo root (gitignored).

## What it does

- **Source:** `Assets/VRCWorldDrop/` only. Sibling `Assets/VRCWorldDrop_Dev/` and
  `_Experimental/` folders are never included (out of scope by path).
- **Excludes:** `Synced/_Temp/` (build-time scratch regenerated every build by
  `WdsBuildHook`). Adjust the `EXCLUDE` list in [export.mjs](export.mjs).
- **Version:** read from the VPM manifest at `Assets/VRCWorldDrop/package.json`
  (the single source of truth). The repo-root `package.json` is the npm tooling
  manifest, not the shipped one.

### Two layouts

| Command            | Files ship to                            | For                                          |
| ------------------ | ---------------------------------------- | -------------------------------------------- |
| `export`           | `Assets/VRCWorldDrop/`                   | Classic `.unitypackage` import into `Assets/` |
| `export:vpm`       | `Packages/com.gummidot.vrc-world-drop/`  | Manual drop into a VPM project's `Packages/`  |

Both layouts include `package.json` (it lives inside the package folder, so it is
just walked in like any other asset).

> Note: the VPM `.unitypackage` only places files in the `Packages/` folder; it does
> not register the package with VCC/ALCOM. For managed VPM installs, use the `.zip`
> the release workflow publishes (the canonical VPM artifact) plus the
> [VCC listing](https://gummidot.github.io/vpm-listing/).
>
> The Synced variant's C# is wrapped in asmdefs (`VRCWorldDrop.Synced.Runtime` /
> `.Editor` at `Assets/VRCWorldDrop/Synced/{Runtime,Editor}/`), so it compiles under
> `Packages/` too. This is required: Unity only compiles loose scripts under `Assets/`.
