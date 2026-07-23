# 1.4.1

- Fix synced world drops showing in the wrong position when you re-drop an object while other players have your avatar culled (thanks to Wolfy527 for reporting).

# 1.4.0

- Add **Saved Across Sessions** for synced world drops to save dropped positions across world rejoins and avatar switches.
- Fix synced world drops and custom menu toggles not working in some setups that combine them with other VRCFury toggles, such as a toggle with an FX Float action or one set to local only (thanks to Wolfy527 for help testing).

# 1.3.0

- Add a Default Shown option to make a synced drop start visible instead of hidden.
- Synced drops now automatically hide the object on upload to prevent them appearing in your upload preview or flashing on when your avatar loads. You only need to disable `Container/Item` manually if you want to hide it in your scene.
- Custom menu support for synced world drops.

# 1.2.0

- Synced world drops use fewer synced params when you sync only a few objects. Base cost now scales with object count (1-2 objects now use 40 -> 24 bits on Slow mode, or 72 -> 40 bits on Fast mode).

# 1.1.0

- Add synced world drops (experimental).
- Now available as a VPM package. Add it through the VCC: https://gummidot.github.io/vpm-listing/

# 1.0.0

Initial release.
