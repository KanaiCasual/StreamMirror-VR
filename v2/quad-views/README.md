# VR Gaze Mirror's quad views layer

`XR_APILAYER_NOVENDOR_gaze_mirror_quad_views`: quad views and eye-tracked foveated rendering for OpenXR games, built on
[Quad-Views-Foveated](https://github.com/mbucchia/Quad-Views-Foveated) 1.1.4 by Matthieu Bucchianeri (MIT, see
`LICENSE`; the code it uses is listed in `THIRD_PARTY`). He does not make or support this build.

What it changes against upstream 1.1.4:

- **Direct3D 12** games, through D3D11on12 on the game's own queue (`layer.cpp`, `m_on12Device`).
- **Stands aside** (`StandAsideReason`, decided in `framework\dispatch.cpp` when the game makes its instance) when the
  game may render with Vulkan or OpenGL, with neither Direct3D 11 nor 12, with Direct3D 12 where D3D11on12 fails, or
  when another `*quad_views_foveated` layer is below it. Standing aside, it hands the quad views request on where a
  layer or runtime below offers it.
- **Its own name and folder**: settings and log in `%LOCALAPPDATA%\GazeMirror\QuadViews`; the first start copies the
  official layer's `settings.cfg`.
- **Blend** (`ProjectionPS.hlsl`): the focus view fades out to nothing at its edge; sharpening is an unsharp mask on
  the output's pixel grid, held within the local contrast, inside the blend (the CAS pass is gone).
- **Gaze** (`gaze_filter.h`): deadzone (`gaze_deadzone_degrees`, 0.5), smoothing (`gaze_smoothing_ms`, 8), and an
  ease back to the middle after the blink hold.
- **Focus angles per display time** for every game (upstream: DCS only).
- **Live settings**: a change notification on the settings folder, looked at once a frame; image sizes and Turbo stay
  as the game started.
- **Diagnostics** (`show_diagnostics=1`): zones tinted, the tracker's gaze and the followed gaze marked.

Build: `v2\Get-External.ps1` once (fmt, the OpenXR SDK headers, XrUtility and WIL at upstream's versions, into
`v2\external\qvf-deps`), then `v2\Build-Dev.ps1`, which builds `layer\QuadViewsLayer.vcxproj` and runs `test\build.cmd`
(the offline test: a fake runtime and game, D3D11 and D3D12, the stand-aside cases and the gaze filter).
`framework\dispatch.gen.*` are upstream's generated dispatch code, kept as files: a function the layer starts to
intercept has to be added there by hand.
