# Vendored pcl2-patch core

Upstream: https://github.com/ShiraiFyuya/pcl2-patch
Commit: abaa77cc0f3ea34852d845f9b0126560f5d64e18
Copyright (c) 2026 ShiraiFyuya. License: GPL-3.0, see LICENSE.

Only the managed patch engine (Ctx.cs, Patches.cs and its project) is included.
The original GUI, EXE replacement coordinator and PCL-CE bundle patcher are not included.
This tool restricts application to the verified official 2.99.0.1 build.

Local changes:
- Ctx.FromAssembly wraps the unified coordinator's in-memory Cecil assembly without taking ownership.
- Patches.ProtectUpdates exposes the two update-protection patches independently.
- ModpackInstall rewires every branch/handler reference to its removed terminal throw,
  preventing a dangling IL target when the compiler shared that tail between cases.

The combined PCL.Link.Patcher is distributed under GPL-3.0, with corresponding source.
The separate PCL UI integration and third-party networking binaries retain their respective licensing requirements.
