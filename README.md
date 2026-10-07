# MiniMe Puppeteer

A ChilloutVR mod that allows players to use the **Remote Mini Me** (from [MiniMe++](https://github.com/kafeijao/Kafe_CVR_Mods)) as a collaborative puppeteer controller to pose and move other players in real-time.

## Features

- **MiniMe Collaborative Puppeteering**:
  - If a player spawns their MiniMe using `MiniMe++`, remote users who also have this mod can physically reach out to the miniature copy.
  - Grabbing the **Torso / Root** moves and positions the player through world space (with optional momentum throw physics).
  - Grabbing individual **Limbs** (Left/Right Hand, Left/Right Foot, Head, Pelvis) drives the player's avatar in real-time using native `IKSolverVR` targets.
- **Mutual Mod Presence**: Both players must have the mod installed and enabled before any interaction can take place.
- **Granular Permissions & Consent**:
  - `Disabled` / `Friends Only` (default) / `Whitelist Only` / `Anyone`
  - Granular Per-Limb permissions (Root, Arms, Legs, Head, Pelvis)
- **Doll Posing ("Hold Pose on Release")**: Optional toggle to keep limbs posed when let go.
- **Emergency Panic Break**:
  - Press `~` (tilde key) at any time to instantly break out.
  - Rapidly spam Jump (10 times in 5 seconds) to break free in VR.
  - Despawning/closing your local MiniMe automatically terminates all puppeteering immediately.
  - Red "Release Control" button in the BTKUI menu.

## Requirements

- [MelonLoader](https://github.com/LavaGang/MelonLoader) (v0.6+)
- [MiniMe++](https://github.com/kafeijao/Kafe_CVR_Mods)
- [BTKUILib](https://github.com/BTK-Development/BTKUILib) (optional, for in-game menu)

## How to Build

1. Ensure .NET 8 SDK is installed:
   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```
2. Build the project:
   ```powershell
   dotnet build D:\CVRMods\MiniMePuppeteer\MiniMePuppeteer.csproj -c Release
   ```
   The build automatically outputs `MiniMePuppeteer.dll` directly to your `ChilloutVR\Mods\` directory.
