# Odyssey Gravship Battery Launch

A RimWorld 1.6 + Odyssey Harmony add-on that makes gravships depend on electrical power and a separate engine-preparation phase before launch.

## Current behavior

### Static ship power requirements

The following gravship parts now require normal grid power whenever they are switched on:

- grav core: 100 W;
- pilot console: 100 W;
- small thruster: 10 W;
- large thruster: 50 W.

Unpowered thrusters do not count as active thrusters for flight calculations.

### Prepare for launch

The pilot console now uses a two-step launch flow:

1. **Prepare for launch**
2. **Launch**

`Prepare for launch` starts grav-engine spool-up. No pilot or copilot is required for this stage.

During spool-up:

- the grav engine plays the warmup animation;
- spool-up takes **2 in-game hours**;
- spool-up consumes continuous extra power from the grav core's power net;
- the grav engine inspect string shows spool progress;
- periodic messages report progress in percent;
- confirming **Prepare for launch** shows a warning because this action immediately escalates local threats.

### Spool-up power cost

The total spool energy cost is based on connected gravship size:

- `15 Wd * connected_gravship_tile_count`

Because spool-up takes 2 in-game hours, the extra continuous spool draw is:

- `180 W * connected_gravship_tile_count`

While the engine is fully prepared, it continues consuming the **full spool power draw** so keeping a ship launch-ready is intentionally expensive.

If power fails at any point during spool-up or while the engine is fully prepared:

- spool-up/prepared state is canceled immediately;
- all invested spool energy is lost;
- the player must prepare again from 0%.

### Threat response on spool-up

Starting **Prepare for launch** is noisy and dangerous by design. After the confirmation dialog, spool-up will:

- force any hostile raiders already present on the map into an immediate assault;
- wake any dormant mech-cluster units on the map;
- push awakened mechanoids into an immediate assault as well.

This warning is shown in a confirmation dialog before spool-up begins.

## Launch flow

Once the engine reaches 100% spool, the normal Odyssey launch path becomes available again.

At that point, clicking **Launch** uses Odyssey's normal flow:

1. crew assignment dialog;
2. colonists/animals/robots gather as usual;
3. target selection stays in Odyssey's normal place;
4. final launch confirmation;
5. takeoff.

This mod no longer tries to pause and later resume the gravship ritual. The engine-preparation phase is separate, and the actual launch remains standard Odyssey behavior.

## Inspect text

The grav engine and pilot console display launch-readiness state in inspect text, including whether the ship is:

- idle;
- spooling;
- fully prepared;
- canceled because power was lost.

## Power-net rules

- spool-up uses the grav core's own power net;
- if the grav core is not powered, spool-up cannot begin;
- if the pilot console is not powered, launch cannot be started;
- if a thruster loses power, it behaves like an unusable thruster and does not contribute to flight capability.

## Build

```bash
 dotnet build Source/OdysseyGravshipBatteryLaunch.csproj -c Release
```

Assembly output target:

```text
1.6/Assemblies/
```

## Notes

- The project uses `Lib.Harmony`, `Krafs.Rimworld.Ref`, and `Krafs.Publicizer`.
- The current source reflects the reduced baseline power values listed above.
- Manual on/off toggles for individual gravship parts are **not** implemented in this version.
- Before publishing, change the `author` and `packageId` values in `About/About.xml`.

## Code tour

- `Source/ModBootstrap.cs`: Harmony entry point.
- `Source/Utility/GravshipBatteryUtility.cs`: shared gravship, power-net, spool-state, and helper logic.
- `Source/LaunchWarmup/*`: spool/preparation state handling and warmup visuals.
- `Source/Jobs/JobDriver_GravshipSpoolUp.cs`: older warmup job support still present in the source tree; the current gameplay flow is the prepare-then-launch model.
- `Patches/ThingDef_Power.xml`: XML patch that gives gravship parts static power consumption.

The C# source includes extensive comments aimed at a C# developer who knows RimWorld gameplay but is new to RimWorld modding.
