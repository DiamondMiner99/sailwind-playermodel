# Sailwind Player Model

Adds a third-person player model to Sailwind, a character customization screen, and a pause menu that
other mods can add buttons to.

## Features

- Player model visible in third person. It walks, crouches and turns with your view. Not shown in first
  person, except from the chest down while you sit.
- A third person camera that follows you, on land as well as aboard. V switches between first person and
  following you. The camera key (C) works as in the base game: aboard it switches between first person and
  the game's view of the whole ship, and from the follow view it goes to the ship view. With
  CameraKeyFollows on, C goes from first person to following you, then aboard to the ship view, then back to
  first person. On a gamepad the camera button goes through the follow view that way until FollowButton is
  set. The scroll wheel moves the camera closer or farther while your hands are empty and nothing else has
  taken the mouse, and your body turns with the camera, so you walk, strafe and back up from where it looks.
  H freezes which way you face and where you look, so the camera orbits around you while you walk from the
  frozen facing. Up and down in both views follow the game's Invert Mouse setting. You can eat, drink and
  smoke in both views with bottles, mugs, food and the pipe. An item you tip to pour, such as a soup pot or
  a bucket, is left to first person, because these views judge the tip against the way you were looking when
  the view opened. While swimming in the follow view, Crouch dives and Jump brings you back up.
- Both hands are posed on what you are using: the ship's wheel, winches, the anchor winch, the bilge pump,
  sail pushers and mooring ropes. A Shipyard Expansion tiller is held at its end with one hand. Held items are
  held by their handles and edges and follow what the game does with them: drinking, eating, smoking, reading,
  sighting, sweeping, and turning an item with the scroll wheel.
- While Held Tool Mode is ItemInHand (the default) and Interactions are enabled, a barrel you drink from goes
  back to being carried in both hands when you stop drinking, in first person too. It is handled the same as a
  barrel you have just picked up, so it cannot go into a crate or onto a shelf.
- Sitting: right-click a chair that is set down, or press X while looking at a rail, ledge, crate or bench
  at seat height, or at the floor. Sitting down has to be started in first person: sit first and then switch
  the view, and the seat stays. Your legs dangle when the floor is out of reach, and pressing X again on a
  rail swings them to the other side. Spars like the bowsprit and the crosstrees are straddled when you look
  along them and sat on sideways when you look across them; X switches between the two. On the floor you sit
  with your legs out, cross-legged, with one knee up, or hugging your knees, picked at random; X changes to
  the next one. You can look around and use held items while seated, and in first person you see your own
  body from the chest down. A seat has to be on the boat you are aboard, or ashore when you are ashore. Any
  movement key or jump stands you back up where you sat down from. The ship's wheel, a rope winch, the
  anchor winch and the bilge pump are worked from the seat when their handles are within reach of where you
  sit: your body turns and leans toward them and you keep the seat. Handles farther off stand you up to
  them, and so does a sail pusher, which you push while walking along, and a wheel or winch held with the
  mouse (the game's steer with mouse and winches with mouse settings). If the boat heels or the crate you
  sit on slides far enough to carry one of those four out of reach while you work it, you stand up to it. A
  Shipyard Expansion tiller and another mod's control, such as HMS Leopard's oars or the Realistic Skies
  telescope, are worked from a seat with no reach test, so sit within arm's reach of one or your hands will
  not be on it. The movement keys work whatever control you hold from the seat instead of walking you, so
  click to let go of it before you stand up. You stay seated while you use the chart table or a market, and
  while you pass out or sleep in a tavern room. Don't sit on a lit stove for too long.
- The model lies down in the game's beds.
- Character screen: head, hair, eyebrows, facial hair, hat, torso, hips, legs, and colors for skin, hair,
  cloth, trim, leather and metal. Uses the game's NPC parts. Opened from the pause menu. It is drawn larger on
  screens taller than 1080 pixels, and the UIScale setting makes it larger or smaller. In a window too small for
  it, it shrinks to fit.
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
itself, since those versions include their own player model and pause menu. The co-op version is read from
BepInEx's plugin cache, so this only works while `EnableAssemblyCache` is on in the `[Caching]` section of
`BepInEx/config/BepInEx.cfg`, which is the default. With the cache off, this mod loads alongside an older
co-op and you get two player models and two pause menus.

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

Shipyard Expansion's tiller is held at its end with one hand, standing or seated.

These controls do not have a pose yet, and the model stands or sits normally while you use them: HMS Leopard's
oars, gun carriages, cannons, bell and cutter; the Realistic Skies observatory telescope and ladder; the
Shipyard Expansion ladder.

## Config

Options are in `BepInEx/config/com.diamondminer99.playermodel.cfg` or the F1 menu if you have the
[BepInEx Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

- **Pose**: crouch depth, torso lean, foot height
- **Held Tool**: how held items are shown, elbow direction and how fast the arms move onto a grip
- **Interactions**: on/off, how far the model steps in to reach a control, hand spacing on wheels and cranks
- **Item Poses**: where items are carried, read and brought to the mouth, and how far the wrist bends
- **Seating**: on/off, the sit key, the (seated) label, how long sitting down takes, seated view height, whether
  lit stoves burn, and whether your body shows in first person while seated and where it fades out
- **Camera**: on/off for the camera that follows you, how far away it starts, its key (FollowKey, V) and gamepad
  button (FollowButton), whether the camera key goes through it (CameraKeyFollows), and the look lock (LookLockKey,
  H, with LookLockButton for a gamepad and LookFollowsCamera for the state the view opens in)
- **Appearance**: your character, as `slot=variant` pairs. The Character screen writes this
- **Menu**: pause menu button size, and the size of the Character screen, the seat hints and the (seated) label
  (UIScale)

## For mod developers

Add `[BepInDependency(SailwindPlayerModel.Plugin.PluginGuid)]` to your plugin class so this mod loads first.
Its settings, including the saved character that `PlayerModel.LocalAppearance` reads, are loaded in its Awake.

### Posing the player model

`SailwindPlayerModel.PlayerModel`:

```csharp
bool IsBodyAvailable { get; }          // false until the world has loaded and the model is built
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

`Register` can be called at any time, including from your plugin's Awake. A button registered after the menu
is built is added to it right away. Registering the same `Id` again replaces the button, and
`ModPauseMenu.Unregister(id)` removes it.

`Label` and `Visible` are called every frame while the menu is open. Hidden buttons are removed from the
column and the rest are re-spaced. Built-in button orders: Resume 0, Character 300, Settings 400, Recover 500,
Quit 900. `ModPauseMenu.SetVisible(id, func)` changes when a button is shown, built-ins included.
`ModPauseMenu.CloneScroll` creates an extra parchment panel next to the menu.

Sailwind Co-op adds its buttons and crew list this way.

## License

MIT
