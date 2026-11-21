// StreamMirror VR - core: the producer side of the shared format (protocol/stream_mirror_protocol.h).
// Owns the shared block and the slot textures; tells the readers when a frame is ready; knows whether anybody wants
// one at all (a few memory reads per frame, and never a timer).
#pragma once

#include <d3d11.h>
#include <wrl/client.h>

#include "../protocol/stream_mirror_protocol.h"

namespace stream_mirror {

    class Publisher {
      public:
        ~Publisher();

        // Claims the shared block. Somebody else alive there keeps it, except: an OpenXR game's layer takes it from the
        // SteamVR helper unless yieldToOpenVR (the picture is to come through SteamVR), and takeOver takes it from anybody.
        bool start(ID3D11Device* device, LONG producerKind, const char* program, const char* application, bool yieldToOpenVR = false, bool takeOver = false);
        void stop();
        // The process that publishes right now, if it is alive and not this one; 0 otherwise. A look at the shared
        // block, nothing more: for a producer that stands by and wants to know whether it is worth trying.
        struct Elsewhere {
            LONG pid = 0;  // 0: nobody else.
            LONG kind = 0;
            LONG gazeSupport = 0;
            LONGLONG frameNumber = 0;
        };
        static Elsewhere LiveProducerElsewhere();
        bool started() const {
            return _frames != nullptr;
        }

        // Is there anybody to make a picture for?
        bool wanted();
        // A reader is there and alive right now: its heartbeat only, no asking (no side effects, unlike wanted()).
        bool readerLive() const;

        // A slot to draw the next frame into, of this size (textures are made on demand): never the published one, one
        // still waiting for the GPU or one a reader holds, and of the rest the one drawn longest ago. Null when there is
        // none - then skip the frame rather than wait.
        struct Slot {
            int index = -1;
            ID3D11Texture2D* texture = nullptr;
            ID3D11RenderTargetView* target = nullptr;
        };
        Slot acquire(uint32_t width, uint32_t height);

        // The frame in that slot is recorded: sends it to the GPU. The readers hear of it only once the GPU has drawn it
        // (publishFinished). They read on devices of their own, and nothing orders their reading after our drawing - for
        // a Direct3D 12 game, whose queue our drawing goes down, not even Windows - so a reader told at once could show
        // what the slot held before: a picture a few frames old.
        void submit(const Slot& slot, int eye, bool gazeValid, float gazeU, float gazeV);
        // Tells the readers of the newest submitted frame the GPU has drawn, if there is one. Once a producer frame; it
        // only looks, never waits.
        void publishFinished();
        // What the producer knows about the headset's eye tracking (Frames::gazeSupport).
        void setGazeSupport(LONG support) {
            if (_frames) _frames->gazeSupport = support;
        }

        // Another producer took the block over (an OpenXR game beats the OpenVR helper).
        bool lost() const {
            return _frames && _frames->producerPid != static_cast<LONG>(GetCurrentProcessId());
        }
        // The game changed (OpenVR helper): tell the readers.
        void rename(const char* program, const char* application);

        uint32_t width() const {
            return _width;
        }
        uint32_t height() const {
            return _height;
        }

      private:
        struct SlotState {
            Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
            Microsoft::WRL::ComPtr<ID3D11RenderTargetView> target;
            Microsoft::WRL::ComPtr<ID3D11Query> drawn; // Ends after the slot's drawing; done = drawn on the GPU.
            uint64_t order = 0;                          // When it was last drawn into (0: never).
        };
        // A drawn frame waiting for the GPU, with what the readers are told along with it.
        struct Waiting {
            int slot = -1;
            int eye = 0;
            bool gazeValid = false;
            float gazeU = 0.f, gazeV = 0.f;
        };
        static constexpr int WaitingMax = 2; // With the published slot that leaves one of the four for a reader.
        void resize(uint32_t width, uint32_t height);
        bool waiting(int slot) const;
        void publish(const Waiting& frame);

        Microsoft::WRL::ComPtr<ID3D11Device> _device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> _immediate;
        SlotState _slots[SlotCount];
        Waiting _waiting[WaitingMax];
        int _waitingCount = 0;
        uint64_t _drawn = 0;
        uint32_t _width = 0, _height = 0;
        HANDLE _mapping = nullptr;
        Frames* _frames = nullptr;
        HANDLE _readerEvents[ReaderCount] = {};
        HANDLE _producerWake = nullptr;
        HANDLE _producerChanged = nullptr;
        ULONGLONG _lastPing[ReaderCount] = {};
        int _unansweredPings[ReaderCount] = {};
        int _logProblem = 0;
        int _logNoSlot = 0;
    };

} // namespace stream_mirror
