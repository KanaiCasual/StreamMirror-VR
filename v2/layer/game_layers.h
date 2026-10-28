// StreamMirror VR - OpenXR layer: the game's own layers. The mirror layer's DLL is a second, small layer as well, with a
// manifest of its own, that sits at the top of the OpenXR layer list, right under the game. It notes which pictures the
// game itself hands to xrEndFrame and passes the frame on untouched. The mirror layer, at the bottom of the list, can then
// tell the game's own panels from the ones other tools lay over the game on the way down.
#pragma once

namespace stream_mirror::layer {

    constexpr const char* GameLayersName = "XR_APILAYER_KANAICASUAL_StreamMirror_GameLayers";

    // What the game sent with the frame that is on its way down this thread.
    struct GameFrame {
        std::vector<XrSwapchain> swapchains; // Every picture of every layer the game sent.
        uint32_t panels = 0;                 // Its flat and curved panels, and projection layers after its first (its world).
    };

    // The game's frame, while its xrEndFrame is on its way down; null when the watcher is not in the chain, or the frame
    // came down another way (a layer between that hands frames to a thread of its own).
    const GameFrame* GameFrameNow();

} // namespace stream_mirror::layer
