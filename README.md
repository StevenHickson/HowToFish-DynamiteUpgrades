# Dynamite Upgrades

Take a stick of dynamite to the **bullet upgrade counter** in the shop and upgrade it:

| Tier | Sold from | Price | Damage | Radius | Force | Fish from underwater blasts |
|---|---|---|---|---|---|---|
| Strong (orange) | Island 3 | $30 | x1.6 | x1.3 | x1.3 | x1.5 |
| Super (blue) | Island 4 | $50 | x3 | x1.75 | x1.8 | x2.5 |
| Mega (purple) | Island 5 | $80 | x5 | x2.2 | x2.4 | x4 |

Like the gun upgrades, each island's counter only goes so far: upgrade once on island 3, up to twice on island 4, and all three times on island 5.

All numbers, prices and colors can be changed in r2modman's config editor (`justresearch.dynamiteupgrades.cfg`).

**Multiplayer:** everyone should install it. The host's settings decide explosion strength and prices.

Upgraded sticks keep their tier when you save and reload, whether they are in your inventory, in your hand, or lying on the ground. Removing the mod is safe: saves load as normal dynamite.

## Building from source

Needs the .NET SDK (8 or newer), How to Fish, and an r2modman profile with BepInEx.

```
dotnet build -c Release
```

This builds the mod, copies it into your r2modman profile's `BepInEx/plugins` folder, and writes a Thunderstore-ready zip to `dist/`. If your game or profile live somewhere else, change `GameDir` and `ProfileDir` at the top of `DynamiteUpgrades.csproj`.
