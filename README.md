# Doprez.Stride.SteamAudio

[![NuGet](https://img.shields.io/nuget/v/Doprez.Stride.SteamAudio)](https://www.nuget.org/packages/Doprez.Stride.SteamAudio)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Steam Audio integration for the [Stride](https://www.stride3d.net/) game engine. Provides physics-based spatial audio with HRTF binaural rendering, occlusion, transmission, reflections, and pathing — powered by [Valve's Steam Audio](https://valvesoftware.github.io/steam-audio/).

> **Note:** Audio sources must be **raw PCM float32** files (`.raw`). Add them as a raw asset in Stride, not as an audio asset.

## Quick Start

1. Install the NuGet package:
   ```
   dotnet add package Doprez.Stride.SteamAudio
   ```
2. Add a **raw PCM audio file** to your project as a raw asset.
3. Add a **`SteamAudioListener`** component to your camera/player entity.
4. Add a **`SteamAudioEmitter`** component to any entity that should produce sound and assign the raw file.
5. *(Optional)* Add a **`SteamAudioScene`** component to a root entity to enable occlusion, reflections, and other physics-based effects.

## Components

### SteamAudioListener

Attach to the entity that represents the player's ears (typically the camera). No configuration properties — position and orientation are read from the entity transform.

### SteamAudioEmitter

The main sound-source component. Attach to any entity that should emit audio.

| Property | Type | Default | Description |
|---|---|---|---|
| `RawFileSource` | `UrlReference` | — | Raw PCM float32 audio asset (required) |
| `SampleRate` | `int` | `44100` | Sample rate of the raw audio file |
| `FrameSize` | `int` | `4096` | Number of samples per processing frame |
| `Volume` | `float` | `1.0` | Playback volume |

#### Direct Sound Simulation

| Property | Type | Default | Description |
|---|---|---|---|
| `EnableOcclusion` | `bool` | `false` | Sound blocked by geometry (requires `SteamAudioScene`) |
| `EnableTransmission` | `bool` | `false` | Sound passing through walls, filtered by material |
| `TransmissionType` | `TransmissionType` | `FrequencyDependent` | Frequency-dependent or frequency-independent transmission |
| `EnableAirAbsorption` | `bool` | `false` | High frequencies attenuate faster over distance |
| `EnableDirectivity` | `bool` | `false` | Directional emission pattern (megaphone effect) |
| `DirectivityDipoleWeight` | `float` | `0.5` | 0 = omnidirectional, 1 = full dipole |
| `DirectivityDipolePower` | `float` | `2.0` | Sharpness of the directivity pattern |

#### Occlusion & Transmission

| Property | Type | Default | Description |
|---|---|---|---|
| `OcclusionType` | `OcclusionType` | `Volumetric` | `Raycast` (fast) or `Volumetric` (softer transitions) |
| `NumOcclusionSamples` | `int` | `64` | Sample count for volumetric occlusion (higher = smoother) |
| `OcclusionRadius` | `float` | `1.0` | Radius for volumetric occlusion |
| `NumTransmissionRays` | `int` | `16` | Ray count for transmission calculation |

#### HRTF

| Property | Type | Default | Description |
|---|---|---|---|
| `HrtfInterpolation` | `HrtfInterpolation` | `Bilinear` | `Bilinear` (smooth) or `Nearest` (fast) |
| `SofaFilePath` | `string` | `""` | Path to a custom SOFA HRTF file; empty = built-in default |
| `HrtfNormType` | `HrtfNormType` | `None` | HRTF normalization (`None` or `RMS`) |

#### Reflections & Pathing

| Property | Type | Default | Description |
|---|---|---|---|
| `EnableReflections` | `bool` | `false` | Early reflections + late reverb from scene geometry |
| `ReflectionAmbisonicsOrder` | `int` | `1` | Ambisonics order (0 = mono, 1 = 4ch, 2 = 9ch) |
| `EnablePathing` | `bool` | `false` | Sound travels around obstacles via shortest path |
| `PathingOrder` | `int` | `1` | Ambisonics order for pathing spatialization |

### SteamAudioScene

Scene-level component that configures the acoustic scene geometry and simulation. Place on a **single** entity (e.g., a manager/root entity) to enable physics-based audio features.

| Property | Type | Default | Description |
|---|---|---|---|
| `GeometrySource` | `GeometrySourceMode` | `PhysicsColliders` | Where to get geometry: `PhysicsColliders`, `ModelMeshes`, or `Custom` |
| `MaxOcclusionSamples` | `int` | `64` | Max occlusion samples per source |
| `NumRays` | `int` | `4096` | Reflection rays (higher = more accurate, more CPU) |
| `NumBounces` | `int` | `4` | Bounces per reflection ray |
| `NumDiffuseSamples` | `int` | `32` | Diffuse samples per bounce |
| `ReflectionDuration` | `float` | `1.0` | Max impulse response duration (seconds) |
| `AmbisonicsOrder` | `int` | `1` | Max ambisonics order for reflection IRs |
| `MaxSources` | `int` | `32` | Max simultaneous simulated sources |
| `NumThreads` | `int` | `0` | Simulation threads (0 = auto) |
| `IrradianceMinDistance` | `float` | `1.0` | Min distance (meters) for irradiance calculation |

### SteamAudioMaterial

Per-entity acoustic material. Attach to any entity with geometry to control how sound interacts with its surfaces. If absent, a default hard-surface material is used.

| Property | Type | Default | Description |
|---|---|---|---|
| `AbsorptionLow` | `float` | `0.10` | Low-frequency absorption (0 = reflective, 1 = absorptive) |
| `AbsorptionMid` | `float` | `0.20` | Mid-frequency absorption |
| `AbsorptionHigh` | `float` | `0.30` | High-frequency absorption |
| `Scattering` | `float` | `0.05` | Scattering (0 = specular, 1 = fully diffuse) |
| `TransmissionLow` | `float` | `0.10` | Low-frequency transmission (0 = blocks, 1 = transmits) |
| `TransmissionMid` | `float` | `0.05` | Mid-frequency transmission |
| `TransmissionHigh` | `float` | `0.03` | High-frequency transmission |

### SteamAudioDebugOverlay

A `SyncScript` that renders real-time diagnostics on screen (scene geometry stats, simulator status, per-emitter occlusion/transmission values). Attach to any entity and toggle with the `Enabled` property.

## Geometry Providers

The `SteamAudioScene` component extracts geometry for the acoustic simulation. Three modes are available via `GeometrySource`:

| Mode | Description |
|---|---|
| `PhysicsColliders` | Automatically extracts geometry from `StaticColliderComponent` shapes (Box, Sphere, Capsule, Cylinder). No extra setup needed. |
| `ModelMeshes` | Extracts geometry from `ModelComponent` meshes. Requires a custom implementation of `ModelGeometryProviderBase` with GPU buffer access. |
| `Custom` | Register your own `ISteamAudioGeometryProvider` via the `SteamAudioSceneProcessor.RegisterProvider()` method. |

### Custom Geometry Provider

Implement `ISteamAudioGeometryProvider` to supply geometry from any source:

```csharp
public interface ISteamAudioGeometryProvider
{
    bool CanProvide(Entity entity);
    SteamAudioMeshData? ExtractMesh(Entity entity);
}
```

## Architecture

The library is driven by three entity processors that run each frame:

| Processor | Order | Role |
|---|---|---|
| `SteamAudioSceneProcessor` | 79999 | Builds & manages the acoustic scene geometry |
| `SteamAudioListenerProcessor` | 80000 | Tracks the listener entity |
| `SteamAudioProcessor` | 80001 | Drives the simulation, applies DSP effects, streams audio via OpenAL |

Audio is rendered through **OpenAL Soft** (via Silk.NET) with float32 support. The Steam Audio C API is accessed through the [Doprez.SteamAudio.NET](https://github.com/Doprez/SteamAudio.NET) bindings.

## Dependencies

- [Stride](https://www.stride3d.net/) 4.3+
- [Silk.NET.OpenAL](https://github.com/dotnet/Silk.NET) — OpenAL audio backend
- [Doprez.SteamAudio.NET](https://github.com/Doprez/SteamAudio.NET) — C# bindings + native `phonon.dll`

## License

[MIT](LICENSE)
