// MIT License
//
// Copyright(c) 2023 Matthieu Bucchianeri
// Copyright(c) 2026 Oliver Kobis (KanaiCasual)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this softwareand associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and /or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright noticeand this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

// A Pixel Shader that puts the focus view over the wide view: the focus view fades out towards its edges, all the
// way to nothing, and is sharpened where it shows. Optionally it marks the zones and the gaze for checking in the
// headset.

cbuffer ConstantBuffer : register(b0) {
    float smoothingArea;     // the width of the fade, as a fraction of the focus view
    bool ignoreAlpha;
    bool isUnpremultipliedAlpha;
    bool debugFocusView;
    float sharpen;           // 0..1
    bool showDiagnostics;
    bool gazeTracked;        // the focus view follows the eyes right now
    float padding;
    float4 gazeMarkers;      // raw gaze xy, filtered gaze xy, in the output's texture coordinates
    float2 outputSize;       // in pixels
};

SamplerState sourceSampler : register(s0);
Texture2D sourceStereoTexture : register(t0);
Texture2D sourceFocusTexture : register(t1);

float4 premultiplyAlpha(float4 color) {
    return float4(color.rgb * color.a, color.a);
}

float4 unpremultiplyAlpha(float4 color) {
    if (color.a != 0) {
        return float4(color.rgb / color.a, color.a);
    } else {
        return 0;
    }
}

// 1 on a ring of the given radius around a point, in output pixels.
float ring(float2 texcoord, float2 center, float radius) {
    return 1 - smoothstep(1.0, 2.0, abs(length((texcoord - center) * outputSize) - radius));
}

// 1 on a cross of the given half length around a point, in output pixels.
float crossMark(float2 texcoord, float2 center, float halfLength) {
    const float2 d = abs((texcoord - center) * outputSize);
    return max((1 - smoothstep(1.0, 2.0, d.y)) * step(d.x, halfLength), (1 - smoothstep(1.0, 2.0, d.x)) * step(d.y, halfLength));
}

float4 main(in float4 position : SV_POSITION, in float2 texcoord : PROJ_COORD0, in float3 projectedFocusCoord : PROJ_COORD1) : SV_TARGET {
    float4 color0 = sourceStereoTexture.Sample(sourceSampler, texcoord);

    const float2 layer1ProjectedCoordNdc = projectedFocusCoord.xy / projectedFocusCoord.z;
    const float2 layer1TexCoord = layer1ProjectedCoordNdc * float2(0.5f, -0.5f) + 0.5f;
    // One output pixel, in the focus view's texture coordinates (taken before any branch).
    const float2 pixelX = ddx(layer1TexCoord);
    const float2 pixelY = ddy(layer1TexCoord);
    // For pixels outside of the focus view, the sampler gives a fully transparent pixel.
    float4 color1 = sourceFocusTexture.SampleLevel(sourceSampler, layer1TexCoord, 0);

    const float isInside = all(abs(layer1ProjectedCoordNdc) < 1);

    // The fade: full strength inside, smoothly down to nothing at the edge.
    float fade = isInside;
    if (smoothingArea > 0) {
        const float2 s = smoothstep(0, smoothingArea, layer1TexCoord) * smoothstep(0, smoothingArea, 1 - layer1TexCoord);
        fade *= s.x * s.y;
    }

    // Sharpening: an unsharp mask on the output's pixel grid (that is the detail the eye gets), held within the
    // contrast of the neighbourhood so that edges get no halo, and fading out with the focus view.
    if (sharpen > 0 && fade > 0) {
        const float3 n = sourceFocusTexture.SampleLevel(sourceSampler, layer1TexCoord - pixelY, 0).rgb;
        const float3 s = sourceFocusTexture.SampleLevel(sourceSampler, layer1TexCoord + pixelY, 0).rgb;
        const float3 w = sourceFocusTexture.SampleLevel(sourceSampler, layer1TexCoord - pixelX, 0).rgb;
        const float3 e = sourceFocusTexture.SampleLevel(sourceSampler, layer1TexCoord + pixelX, 0).rgb;
        const float3 lowest = min(color1.rgb, min(min(n, s), min(w, e)));
        const float3 highest = max(color1.rgb, max(max(n, s), max(w, e)));
        const float3 detail = (color1.rgb - (n + s + w + e) * 0.25) * (sharpen * 1.5 * fade);
        color1.rgb = clamp(color1.rgb + detail, lowest, highest);
    }

    if (ignoreAlpha) {
        color0.a = color1.a = 1;
    }

    if (!isUnpremultipliedAlpha) {
        color0 = unpremultiplyAlpha(color0);
        color1 = unpremultiplyAlpha(color1);
    }

    color1.a *= fade;

    color0 = premultiplyAlpha(color0);
    color1 = premultiplyAlpha(color1);

    float4 color;
    if (!debugFocusView) {
        color = color1 + color0 * (1 - color1.a);
    } else {
        color = color1;
    }

    if (showDiagnostics) {
        // Wide view blue, fade band amber, focus view green; the ring is the gaze as the tracker gives it, the cross
        // the gaze the focus view follows. Red markers: the eyes are not tracked right now.
        const float3 zone = isInside < 0.5 ? float3(0.1, 0.3, 1.0) : (fade < 0.999 ? float3(1.0, 0.6, 0.05) : float3(0.1, 0.9, 0.3));
        color.rgb = lerp(color.rgb, zone * color.a, isInside < 0.5 ? 0.12 : 0.18);
        const float3 marker = gazeTracked ? float3(0.0, 0.95, 1.0) : float3(1.0, 0.1, 0.05);
        color.rgb = lerp(color.rgb, marker * color.a, ring(texcoord, gazeMarkers.xy, 9.0));
        color.rgb = lerp(color.rgb, float3(1.0, 0.95, 0.1) * color.a, crossMark(texcoord, gazeMarkers.zw, 10.0));
    }

    return float4(color.rgb, color0.a);
}
