# Sailwind Player Model

Adds a third-person player model to Sailwind, a character customization screen, and a pause menu that
other mods can add buttons to.

## Features

- Player model visible in the boat camera. It walks, crouches, turns with your view, and holds your held
  item. Not shown in first person.
- Character screen: head, hair, eyebrows, facial hair, hat, torso, hips, legs. Uses the game's NPC parts.
  Opened from the pause menu.
- Pause menu: Esc opens a menu with Resume, Character, Settings, Recover Boat and Quit Game. Settings
  opens the normal settings screen and Recover Boat opens the game's boat recovery screen.
- The model is cloned from the game's NPCs at runtime, so there are no asset files.

## Installation

Requires BepInEx 5 (x64).

Download the .zip from the latest [release](https://github.com/DiamondMiner99/sailwind-playermodel/releases)
and extract it into your Sailwind folder. The folder structure should look like:

```
BepInEx\
  plugins\
    SailwindPlayerModel\
      SailwindPlayerModel.dll
```

If you use Sailwind Co-op, you need co-op 0.4.0 or later. On older co-op versions this mod disables
itself, since those versions include their own player model and pause menu.

## Config

Options are in `BepInEx/config/com.diamondminer99.playermodel.cfg` or the F1 menu if you have the
[BepInEx Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

- **Pose**: crouch depth, torso lean, foot height
- **Held Tool**: position and rotation of a held item in the hand
- **Appearance**: your character, as `slot=variant` pairs. The Character screen writes this
- **Menu**: pause menu button size

## For mod developers

### Posing the player model

`SailwindPlayerModel.PlayerModel`:

```csharp
bool IsBodyAvailable { get; }          // false in menus and while loading
Transform Root { get; }
Transform GetBone(string name);        // Spine_01, UpperLeg_L, Shoulder_R, Head, ...
void ForceVisible(bool on);            // show the model outside the boat camera
IDisposable ClaimPose(string owner, int priority, PoseParts parts, Action<SyntyBody> write);
```

The model has no Animator. All animation is done by writing bone transforms in LateUpdate.

`ClaimPose` lets a mod take over part of the skeleton. `parts` is a flags enum (`Legs`, `Spine`, `LeftArm`,
`RightArm`, `Body`). While a claim is held, the built-in animation skips those parts, and the `write`
callback is called each LateUpdate after the built-in animation, in ascending priority order. Dispose the
returned object to release the claim. Claims and releases are written to the log with the owner name.

Check `IsBodyAvailable` before using the other members.

### Adding a pause menu button

```csharp
ModPauseMenu.Register(new ModPauseMenu.Entry {
    Id = "mymod_button",
    Order = 250,
    Label = () => "My Button",
    Visible = () => true,
    OnClick = () => DoSomething(),
});
```

`Label` and `Visible` are called every frame while the menu is open. Hidden buttons are removed from the
column and the rest are re-spaced. Built-in button orders: Resume 0, Character 300, Settings 400, Recover 500,
Quit 900. `ModPauseMenu.SetVisible(id, func)` changes when a button is shown, built-ins included.
`ModPauseMenu.CloneScroll` creates an extra parchment panel next to the menu.

Sailwind Co-op adds its buttons and crew list this way.

## License

MIT
