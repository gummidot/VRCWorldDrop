# Example: synced drop from a plain FX controller

`WorldDropSynced Example (FX Controller).prefab` drives a synced drop from a vanilla Unity FX controller and expression menu, merged onto the avatar with a **VRCFury Full Controller** (its **Drop Param** / **Show Param** point at `FX_Drop` / `FX_Show`).

The example also has **Saved Across Sessions** on, with a Save toggle in the expression menu wired through **Save Param** (`FX_Save`). For the Save preference to stick, the expression parameters asset declares `FX_Save` as **saved** (and local, default on); an unsaved parameter would reset the preference every session. `FX_Save` is also listed in the Full Controller's **Global Parameters** so the name is kept as typed.
