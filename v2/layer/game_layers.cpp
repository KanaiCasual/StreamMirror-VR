#include "pch.h"

#include "../core/log.h"
#include "game_layers.h"

namespace gaze_mirror::layer {

    namespace {
        struct Watch {
            XrInstance instance = XR_NULL_HANDLE;
            PFN_xrGetInstanceProcAddr next = nullptr;
            PFN_xrEndFrame nextEndFrame = nullptr;
            PFN_xrDestroyInstance nextDestroyInstance = nullptr;
            int logFrame = 0;
        };
        Watch* w = nullptr;
        thread_local const GameFrame* t_frame = nullptr;

        void Note(GameFrame& frame, const XrSwapchainSubImage& sub) {
            if (sub.swapchain != XR_NULL_HANDLE) frame.swapchains.push_back(sub.swapchain);
        }

        XrResult XRAPI_CALL OnEndFrame(XrSession session, const XrFrameEndInfo* info) {
            if (!w || !w->nextEndFrame) return XR_ERROR_HANDLE_INVALID;
            if (!info || (info->layerCount > 0 && !info->layers)) return w->nextEndFrame(session, info);
            thread_local GameFrame frame;
            frame.swapchains.clear();
            frame.panels = 0;
            bool world = false;
            for (uint32_t i = 0; i < info->layerCount; i++) {
                const XrCompositionLayerBaseHeader* layer = info->layers[i];
                if (!layer) continue;
                if (layer->type == XR_TYPE_COMPOSITION_LAYER_PROJECTION) {
                    const auto* projection = reinterpret_cast<const XrCompositionLayerProjection*>(layer);
                    for (uint32_t v = 0; v < projection->viewCount && projection->views; v++) Note(frame, projection->views[v].subImage);
                    if (world) frame.panels++;
                    world = true;
                } else if (layer->type == XR_TYPE_COMPOSITION_LAYER_QUAD) {
                    Note(frame, reinterpret_cast<const XrCompositionLayerQuad*>(layer)->subImage);
                    frame.panels++;
                } else if (layer->type == XR_TYPE_COMPOSITION_LAYER_CYLINDER_KHR) {
                    Note(frame, reinterpret_cast<const XrCompositionLayerCylinderKHR*>(layer)->subImage);
                    frame.panels++;
                }
            }
            LogFewTimes(w->logFrame, 1, "game layers: the game's first frame has %u layer(s), %u of them panels", info->layerCount, frame.panels);
            t_frame = &frame;
            const XrResult result = w->nextEndFrame(session, info);
            t_frame = nullptr;
            return result;
        }

        XrResult XRAPI_CALL OnDestroyInstance(XrInstance instance) {
            if (!w || !w->nextDestroyInstance) return XR_ERROR_HANDLE_INVALID;
            const XrResult result = w->nextDestroyInstance(instance);
            if (instance == w->instance) {
                delete w;
                w = nullptr;
            }
            return result;
        }

        XrResult XRAPI_CALL OnGetInstanceProcAddr(XrInstance instance, const char* name, PFN_xrVoidFunction* function) {
            if (!w || !name || !function) return XR_ERROR_HANDLE_INVALID;
            if (strcmp(name, "xrGetInstanceProcAddr") == 0) {
                *function = reinterpret_cast<PFN_xrVoidFunction>(OnGetInstanceProcAddr);
                return XR_SUCCESS;
            }
            if (strcmp(name, "xrEndFrame") == 0 && w->nextEndFrame) {
                *function = reinterpret_cast<PFN_xrVoidFunction>(OnEndFrame);
                return XR_SUCCESS;
            }
            if (strcmp(name, "xrDestroyInstance") == 0 && w->nextDestroyInstance) {
                *function = reinterpret_cast<PFN_xrVoidFunction>(OnDestroyInstance);
                return XR_SUCCESS;
            }
            return w->next(instance, name, function);
        }

        XrResult XRAPI_CALL CreateApiLayerInstance(const XrInstanceCreateInfo* info, const XrApiLayerCreateInfo* layerInfo, XrInstance* instance) {
            if (!info || !instance || !layerInfo || layerInfo->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_CREATE_INFO ||
                !layerInfo->nextInfo || layerInfo->nextInfo->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_NEXT_INFO ||
                strcmp(layerInfo->nextInfo->layerName, GameLayersName) != 0 || !layerInfo->nextInfo->nextGetInstanceProcAddr ||
                !layerInfo->nextInfo->nextCreateApiLayerInstance) {
                Log("game layers: the loader handed over something unexpected - layer not started");
                return XR_ERROR_INITIALIZATION_FAILED;
            }
            XrApiLayerCreateInfo chainLayerInfo = *layerInfo;
            chainLayerInfo.nextInfo = layerInfo->nextInfo->next;
            const XrResult result = layerInfo->nextInfo->nextCreateApiLayerInstance(info, &chainLayerInfo, instance);
            if (XR_FAILED(result)) return result;

            auto watch = std::make_unique<Watch>();
            watch->instance = *instance;
            watch->next = layerInfo->nextInfo->nextGetInstanceProcAddr;
            watch->next(*instance, "xrEndFrame", reinterpret_cast<PFN_xrVoidFunction*>(&watch->nextEndFrame));
            watch->next(*instance, "xrDestroyInstance", reinterpret_cast<PFN_xrVoidFunction*>(&watch->nextDestroyInstance));
            int below = 0;
            for (auto other = layerInfo->nextInfo->next; other; other = other->next) below++;
            Log("game layers: at the top of the OpenXR layer list, %d layer(s) below - the game's own panels can be told from other tools'", below);
            delete w;
            w = watch.release();
            return XR_SUCCESS;
        }
    } // namespace

    const GameFrame* GameFrameNow() {
        return t_frame;
    }

} // namespace gaze_mirror::layer

// The manifest XR_APILAYER_KANAICASUAL_StreamMirror_GameLayers.json names this function in place of the usual one.
extern "C" __declspec(dllexport) XrResult XRAPI_CALL xrNegotiateLoaderApiLayerInterface_GameLayers(const XrNegotiateLoaderInfo* loaderInfo,
                                                                                                    const char* layerName,
                                                                                                    XrNegotiateApiLayerRequest* request) {
    using namespace gaze_mirror::layer;
    gaze_mirror::SetLogName(L"streammirror-layer");
    if (!loaderInfo || !layerName || !request || loaderInfo->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
        loaderInfo->structVersion != XR_LOADER_INFO_STRUCT_VERSION || loaderInfo->structSize != sizeof(XrNegotiateLoaderInfo) ||
        request->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST || request->structVersion != XR_API_LAYER_INFO_STRUCT_VERSION ||
        request->structSize != sizeof(XrNegotiateApiLayerRequest) || strcmp(layerName, GameLayersName) != 0 ||
        loaderInfo->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION ||
        loaderInfo->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION || loaderInfo->minApiVersion > XR_CURRENT_API_VERSION ||
        loaderInfo->maxApiVersion < XR_API_VERSION_1_0) {
        gaze_mirror::Log("game layers: negotiation refused (unexpected structures, name or versions)");
        return XR_ERROR_INITIALIZATION_FAILED;
    }
    request->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    request->layerApiVersion = XR_CURRENT_API_VERSION;
    request->getInstanceProcAddr = reinterpret_cast<PFN_xrGetInstanceProcAddr>(OnGetInstanceProcAddr);
    request->createApiLayerInstance = reinterpret_cast<PFN_xrCreateApiLayerInstance>(CreateApiLayerInstance);
    return XR_SUCCESS;
}
