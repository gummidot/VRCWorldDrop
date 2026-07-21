# Example: synced drop from VRCFury toggles

`WorldDropSynced Example (VRCFury Toggles).prefab` drives a synced drop from **VRCFury Toggle** components (using "Use a global parameter" pointed at `VF_Drop` / `VF_Show`), with the drop's **Drop Param** / **Show Param** set to the same names.

Note that VRCFury Toggle global parameters are synced by default, so this uses a VRCFury Full Controller to force those parameters to be local only.

The example also has **Saved Across Sessions** on, with a third Save toggle wired through **Save Param** (`VF_Save`). Two things make the Save preference actually stick:

- The Save toggle has **Saved** checked (and "Use a global parameter" pointed at `VF_Save`). Without Saved, the preference resets every session.
- The Full Controller's parameters asset declares `VF_Save` as local, saved, and default on, matching the built-in Save toggle's behavior.
