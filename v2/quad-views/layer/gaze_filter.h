// MIT License
//
// Copyright(c) 2026 Oliver Kobis (KanaiCasual)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and /or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

#pragma once

#include <algorithm>
#include <cmath>
#include <optional>

#include <openxr/openxr.h>

namespace openxr_api_layer {

    // The gaze the focus view follows. It moves only once the eyes have moved more than the deadzone (tracker jitter
    // leaves it alone), and gets there over a short smoothing. While the eyes are not tracked it eases back to straight
    // ahead instead of jumping there, and stays there until they are tracked again.
    struct GazeFilter {
        float smoothingMs{8.f};
        float deadzoneDegrees{0.5f};
        std::optional<XrVector3f> followed; // unit vector in view space
        XrVector3f target{};

        // gaze: the tracker's gaze when isValid, and the gaze to follow on return. elapsedMs: since the last call.
        // Returns false while there has never been a gaze to follow.
        bool update(bool isValid, XrVector3f& gaze, float elapsedMs) {
            if (isValid) {
                if (!followed) {
                    followed = target = gaze;
                } else if (std::acos(std::clamp(Dot(gaze, target), -1.f, 1.f)) > deadzoneDegrees * 3.14159265f / 180.f) {
                    target = gaze;
                }
                Follow(smoothingMs, elapsedMs);
            } else if (followed) {
                target = {0.f, 0.f, -1.f};
                Follow(std::max(smoothingMs, 150.f), elapsedMs);
            } else {
                return false;
            }
            gaze = *followed;
            return true;
        }

      private:
        static float Dot(const XrVector3f& a, const XrVector3f& b) {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        void Follow(float timeConstantMs, float elapsedMs) {
            const float k = timeConstantMs > 0 ? 1.f - std::exp(-std::max(elapsedMs, 0.f) / timeConstantMs) : 1.f;
            XrVector3f v{followed->x + (target.x - followed->x) * k,
                         followed->y + (target.y - followed->y) * k,
                         followed->z + (target.z - followed->z) * k};
            const float length = std::sqrt(Dot(v, v));
            if (length > 1e-6f) {
                v = {v.x / length, v.y / length, v.z / length};
            }
            followed = v;
        }
    };

} // namespace openxr_api_layer
