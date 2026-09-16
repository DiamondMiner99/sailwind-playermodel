# Sailwind Player Model

Adds a third-person player model to Sailwind, a character customization screen, and a pause menu that
other mods can add buttons to.

## Features

- Player model visible in third person. It walks, crouches and turns with your view. Not shown in first
  person.
- A third person camera that follows you, on land as well as aboard. The camera key (C) goes from first person
  to following you, then aboard to the game's view of the whole ship, then back to first person. The scroll
  wheel moves it closer or farther, and walking turns you the way the camera looks.
- Both hands are posed on what you are using: the ship's wheel, winches, the anchor winch, the bilge pump,
  sail pushers and mooring ropes. Held items are held by their handles and edges and follow what the game
  does with them: drinking, eating, smoking, reading, sighting, sweeping, and turning an item with the scroll
  wheel.
- Sitting: right-click a chair that is set down, or press X while looking at a rail, ledge, crate or bench at
  seat height, or at the floor. Your legs dangle when the floor is out of reach, and pressing X again on a rail
  swings them to the other side. Spars like the bowsprit and the crosstrees are straddled when you look along
  them and sat on sideways when you look across them; X switches between the two. On the floor you sit with your
  legs out, cross-legged, with one knee up, or hugging your knees, picked at random; X changes to the next one.
  You can look around and use held items while seated, and in first person you see your own body from the chest
  down. Any movement key or jump stands you back up where you sat down from. Don't sit on a lit stove for too long.
- The model lies down in the game's beds.
- Character screen: head, hair, eyebrows, facial hair, hat, torso, hips, legs, and colors for skin, hair,
  cloth, trim, leather and metal. Uses the game's NPC parts. Opened from the pause menu.
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

## Other mods

Items from these mods have their own poses:

- **Realistic Skies**: the quintant is sighted through its telescope like the quadrant, and lowered while you
  inspect its reading. The planisphere, almanac and celestial atlas are held up facing you. The star chart is
  held like the game's charts.
- **Climate**: the barometer, hygrometer and thermometer are held up facing you.

Items from these mods use the pose of the game item they are built on:

- **KemyFurnitureMod**: furniture is carried like a crate, sized from each piece.
- **Postal Expansion**: letters and express mail are held like the game's mail.
- **Better Lanterns and Lights**: lanterns are held by the ring like the game's lanterns.

These controls do not have a pose yet, and the model stands normally while you use them: HMS Leopard's
oars, gun carriages, cannons, bell and cutter; the Realistic Skies observatory telescope and ladder; the
Shipyard Expansion ladder.

## Config

Options are in `BepInEx/config/com.diamondminer99.playermodel.cfg` or the F1 menu if you have the
[BepInEx Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

- **Pose**: crouch depth, torso lean, foot height
- **Held Tool**: elbow direction and how fast the arms move onto a grip
- **Interactions**: on/off, how far the model steps in to reach a control, hand spacing on wheels and cranks
- **Item Poses**: where items are carried, read and brought to the mouth, and how far the wrist bends
- **Seating**: on/off, the sit key, the (seated) label, how long sitting down takes, seated view height, whether
  lit stoves burn, and whether your body shows in first person while seated and where it fades out
- **Camera**: on/off for the camera that follows you, and how far away it starts
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
