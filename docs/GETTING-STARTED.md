# Getting started with VR Gaze Mirror

This guide takes you from the download to a framed, steady picture of your headset view in OBS, with the gaze ring if
your headset tracks your eyes. It follows the order you set things up in. Every option is explained in full in the
[README](../README.md#the-settings-app-page-by-page); this guide only names what you need on the way.

Plan about 15 minutes, with your game installed and the headset at hand.

**Contents**

1. [Install](#1-install)
2. [Check the Status page](#2-check-the-status-page)
3. [Get the picture into OBS (or Discord)](#3-get-the-picture-into-obs-or-discord)
4. [Take a picture of your own view](#4-take-a-picture-of-your-own-view)
5. [Frame it](#5-frame-it)
6. [Calm it down](#6-calm-it-down)
7. [The gaze ring](#7-the-gaze-ring)
8. [Save it for your game](#8-save-it-for-your-game)
9. [Before you go live](#9-before-you-go-live)
10. [Which setup is yours?](#10-which-setup-is-yours)
11. [When something looks wrong](#11-when-something-looks-wrong)

---

## 1. Install

1. **Close your VR game and OBS.** The installer replaces files they keep open.
2. Download `VR-Gaze-Mirror-<version>.msi` from the [Releases page](https://github.com/KanaiCasual/VR-Gaze-Mirror/releases/latest) and run it. Windows
   SmartScreen may warn, because the files carry no code signature yet: **More info > Run anyway**.
3. Start **VR Gaze Mirror** from the Start menu.

Quad views (foveated rendering: sharp where you look, cheaper elsewhere) come with it, switched off. The **Quad Views**
page switches them on; the mirror works without them.

The app opens with a short tour of its pages. Take it once; you can start it again from the About page.

## 2. Check the Status page

The **Status** page lists the mirror's parts, each with a coloured dot:

| Part | What it does | You need it for |
|---|---|---|
| **Mirror layer** | takes the picture inside OpenXR games | DCS World, Assetto Corsa EVO, VAIL and other OpenXR games |
| **OBS plugin** | the "Gaze Mirror" source in OBS | recording or streaming with OBS |
| **SteamVR helper** | takes the picture from SteamVR | VRChat, Half-Life: Alyx and other SteamVR games |

Green means ready. Grey means not needed on your PC: the helper, for example, is grey when SteamVR is not installed.
A red or orange dot says what to do about it in its own line.

The line at the very top tells you, while you play, which game the mirror shows and whether your eye gaze arrives.
Keep that line in mind: it answers most "why is nothing happening" questions.

## 3. Get the picture into OBS (or Discord)

Pick one way. You can use both at once.

**OBS (recommended).** It reads the picture straight from the graphics card and costs the least.

1. In OBS: **Sources > + > Gaze Mirror**.
2. Start your VR game. The picture appears once the game is in VR.
3. Select the source and press **Ctrl+F** (Transform > Fit to screen) once. It keeps fitting from then on, whatever you
   do with the crop box.

**Discord or another program that captures windows.** On the app's **Mirror** page, press **Open mirror window** in
the row above the picture. The window sits behind your other windows, where capture still sees it. In Discord:
**Share Your Screen > Applications > VR Gaze Mirror - Mirror**.

At this point the picture is one eye's full view, uncropped. The next steps frame it.

## 4. Take a picture of your own view

The crop box is set on a still picture of the eye image, on the **Mirror** page. The first time a game runs, the page
takes one by itself. A picture taken while you sit and look the way you play is better, because the framing is meant
for that:

1. With your game running in VR, press **Capture from headset** on the Mirror page. A banner says what to do.
2. Put the headset on, sit the way you play, look straight ahead.
3. Press **Pause** (the default key). A sound confirms it, and the capture switches itself off.

Would rather not reach for a key? The small arrow next to the button sets a countdown instead (5 to 30 seconds).

The app keeps this picture, through restarts too, until you press **Refresh picture** or capture again. It never
replaces a saved picture by itself.

## 5. Frame it

On the Mirror page, the blue box on the picture is what your viewers get.

1. Tick **Crop the picture** and pick the shape: **16:9** for a normal stream.
2. **Measure where your view ends** (worth it once per headset): press **Measure in headset** on the **View edges**
   card. In the headset, the view goes black and a white block flashes at one side. Push it out with the arrow keys
   until it is gone from your view, press Enter, and do the next side. The picture then shows a dashed box: the part
   your eye can really see.
3. Press **Crop to view edges**. The box becomes the largest box of your shape inside that area: no black lens corners,
   no wasted picture.
4. Drag the box to move it, drag a corner or roll the mouse wheel to resize. With the **Free** shape, drag a side to
   change only the width or only the height. **Lock** keeps a stray click from moving
   it.

## 6. Calm it down

Your head moves all the time, and a raw headset view on stream is restless. Open these sections beside the picture:

- **Steady the picture**: takes small head movement and tracking jitter out of the picture. Start with it on and the
  defaults. **Tilt > Level** keeps the horizon level when you lean, which flight and racing viewers appreciate.
- **Follow my gaze (up / down)**: a 16:9 box covers about half the height of your view, so a look down at your knee
  board or up at the canopy leaves the frame. With this on, the box glides up or down after your eyes. It needs eye
  tracking.

The **Picture** card sets what goes out: which eye, the **Picture size** (the resolution OBS gets: pick the one you
record or stream at, such as 1920 x 1080 for 1080p; more only costs more) and the **Picture rate**.

## 7. The gaze ring

The ring shows your viewers where you look. You see none of it in the headset. Skip this part if your headset has no
eye tracking, or switch the ring off on the **Ring** page.

1. **Does gaze arrive?** Put the headset on with your game running and look at the Status page's top line. "gaze
   arriving" means you are set. If it says no gaze, turn on eye tracking in your headset's software (Pimax Play, Varjo
   Base, Virtual Desktop and the like) and calibrate it there.
2. **Pick a look** on the **Ring** page: a preset is enough for most people. The preview shows each change at once.
3. **Check where it lands.** On the **Gaze** page, under **Where the ring lands**, switch on **Show a marker inside
   the headset**. A small reticle appears in your view at the ring's spot. Look at something small; if the reticle
   sits beside it, nudge it with the two sliders. Switch the marker off when you are done.

No eye tracking in the headset itself? The **Gaze from** setting on the Gaze page lists the fallbacks: raw gaze over
OSC (SRanibro and similar), VRChat's avatar eyes, or **Fixed centre** for a ring that marks straight ahead.

## 8. Save it for your game

At the top of the Mirror page, **Save as...** stores this framing as a profile (**New** starts one from the default
framing instead). Type your game's program file under **For game** (for example `DCS.exe`), and the mirror switches to
that profile by itself whenever that game starts. Make one per game that needs a different framing.

Changes you make afterwards show on the stream at once, but the profile keeps them only when you press **Save**. An
orange line under the bar tells you something is unsaved, and **Revert** goes back. That way trying something out never
spoils a profile by accident. Prefer every change saved as you go? Tick **Save edits automatically**; **Revert** then
takes you back to how the profile was when you opened it. And on any page, **Ctrl+Z** undoes your last change there.

## 9. Before you go live

- [ ] The Status page shows the game's name and, with eye tracking, "gaze arriving".
- [ ] The OBS source fills the canvas (Ctrl+F done) and moves smoothly.
- [ ] The ring sits where you look (marker checked, then switched **off**).
- [ ] Nothing you don't want on stream: flat panels (the game's own, and tools' such as OpenKneeboard) come along
      by default; untick **Show panels** on the Picture card to keep them off.

## 10. Which setup is yours?

**Native OpenXR runtime** (Pimax Play's OpenXR, Varjo Base, Windows Mixed Reality) with an OpenXR game: the mirror
layer does everything. Nothing to choose.

**OpenXR game running through SteamVR's OpenXR runtime** (common with Index, Vive, Quest via Steam Link): the
**Capture** choice on the Picture card decides. **SteamVR capture** takes the picture SteamVR shows you, overlays
included (OVR Toolkit, fpsVR, the dashboard). **OpenXR capture** takes the game alone, plus its panels and tool panels such
as OpenKneeboard.

**SteamVR game** (VRChat, Half-Life: Alyx, Pavlov): the SteamVR helper mirrors it. It starts and stops with SteamVR by
itself.

**Quad views / foveated rendering** (Varjo headsets, Pimax with quad views on, Quad-Views-Foveated): the mirror puts
the sharp middle and the wider view back together, the way the headset shows them. Nothing to set.

**Using other OpenXR tools** (OpenKneeboard, OpenXR Toolkit, motion compensation): the **Layers** page lists every
OpenXR layer in the order games load them, with a switch for each. If the order breaks a rule the app knows, it says so
and **Fix order** puts it right.

## 11. When something looks wrong

| You see | Look at |
|---|---|
| Black source in OBS | Is the game in VR? The Status top line names the game the mirror shows. An OpenXR game on OpenGL cannot be mirrored yet. |
| The crop does nothing in OBS | Press Ctrl+F once on the source. |
| No ring | Status top line: does gaze arrive? Is the ring on (Ring page)? |
| Ring beside what you look at | Gaze page > Where the ring lands, with the marker. |
| The headset stays black in a game | Switch off the other OpenXR layers on the Layers page one by one, to find the one that game does not like. |
| Anything else | About page > **Logs**. Send the three log files with a [bug report](https://github.com/KanaiCasual/VR-Gaze-Mirror/issues). |

More cases are in the [README's troubleshooting section](../README.md#troubleshooting).
