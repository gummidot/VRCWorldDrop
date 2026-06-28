# Example: synced drop from VRCFury toggles

`WorldDropSynced Example (VRCFury Toggles).prefab` drives a synced drop from two **VRCFury Toggle** components (using "Use a global parameter" pointed at `VF_Drop` / `VF_Show`), with the drop's **Drop Param** / **Show Param** set to the same names.

Note that VRCFury Toggle global parameters are synced by default, so this uses a VRCFury Full Controller to force those parameters to be local only.
