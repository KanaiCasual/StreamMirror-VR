// Offline test of VR Gaze Mirror's quad views layer (built on Quad-Views-Foveated).
//
// Plays both the game and the runtime: loads the layer DLL the way the OpenXR loader does, hands it a fake runtime
// whose swapchain images are real textures, submits quad-view frames whose four views are each filled with one colour,
// and reads the stereo image the layer hands to the "runtime" back: the focus view's colour must sit where the focus
// view lies, the peripheral view's colour around it, and fade out to nothing at the focus view's edge. Halfway through,
// the settings file switches the diagnostics on: the last picture must show them (settings apply while a game runs).
// The case gaze-filter checks the gaze the focus view follows, without a device. The layer's first start copies the
// official layer's settings. Cases where it must stand aside: opengl-declared (the game may pick OpenGL) and
// official-below (Quad-Views-Foveated below it does the work); d3d11-and-d3d12 declares both, as Unreal does, and
// gets quad views. vulkan: a real Vulkan device, its images handed out as colour attachments; the composed picture is
// read back from the runtime's Vulkan image.
//
//   qvf_test.exe <layer dll> <output folder> <case>
//   case: d3d11 | d3d12 | d3d12-array | d3d11-and-d3d12 | vulkan | opengl-declared | official-below | gaze-filter

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <string>
#include <vector>

#define XR_NO_PROTOTYPES
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_GRAPHICS_API_D3D12
#define XR_USE_GRAPHICS_API_VULKAN
#define VK_USE_PLATFORM_WIN32_KHR
#include <vulkan/vulkan.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <loader_interfaces.h>

#include "gaze_filter.h"

using Microsoft::WRL::ComPtr;

namespace {

    constexpr char LayerName[] = "XR_APILAYER_KANAICASUAL_GazeMirror_QuadViews";
    constexpr uint32_t StereoWidth = 2000, StereoHeight = 2100;
    constexpr int ImagesPerSwapchain = 3;
    constexpr int64_t Format = 29;       // DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
    constexpr int64_t FormatVulkan = 43; // VK_FORMAT_R8G8B8A8_SRGB

    int g_failures = 0;
    void Check(bool ok, const char* what) {
        printf("  %s  %s\n", ok ? "ok  " : "FAIL", what);
        if (!ok) g_failures++;
    }

    // ---- the graphics devices -----------------------------------------------------------------------------------
    bool g_d3d12 = false;
    ComPtr<ID3D11Device> g_device11;
    ComPtr<ID3D11DeviceContext> g_context11;
    ComPtr<ID3D12Device> g_device12;
    ComPtr<ID3D12CommandQueue> g_queue12;

    // Runs one command list on the game's queue and waits for it.
    template <typename Fill> void Run12(Fill fill) {
        ComPtr<ID3D12CommandAllocator> allocator;
        ComPtr<ID3D12GraphicsCommandList> list;
        g_device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator));
        g_device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list));
        fill(list.Get());
        list->Close();
        ID3D12CommandList* lists[] = {list.Get()};
        g_queue12->ExecuteCommandLists(1, lists);
        ComPtr<ID3D12Fence> fence;
        g_device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
        g_queue12->Signal(fence.Get(), 1);
        HANDLE done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        fence->SetEventOnCompletion(1, done);
        WaitForSingleObject(done, 10000);
        CloseHandle(done);
    }

    DXGI_FORMAT Typeless(int64_t format) {
        switch (format) {
        case DXGI_FORMAT_R8G8B8A8_UNORM:
        case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
            return DXGI_FORMAT_R8G8B8A8_TYPELESS;
        case DXGI_FORMAT_B8G8R8A8_UNORM:
        case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
            return DXGI_FORMAT_B8G8R8A8_TYPELESS;
        default:
            return static_cast<DXGI_FORMAT>(format);
        }
    }

    // ---- the Vulkan game: its own instance and device, and the one queue it renders on ---------------------------
    bool g_vulkan = false;
    struct Vulkan {
        PFN_vkGetInstanceProcAddr gipa = nullptr;
        VkInstance instance = VK_NULL_HANDLE;
        VkPhysicalDevice physical = VK_NULL_HANDLE;
        VkDevice device = VK_NULL_HANDLE;
        VkQueue queue = VK_NULL_HANDLE;
        uint32_t family = 0;
        VkCommandPool pool = VK_NULL_HANDLE;

        template <typename Function> Function Fn(const char* name) { return reinterpret_cast<Function>(gipa(instance, name)); }

        uint32_t MemoryType(uint32_t bits, VkMemoryPropertyFlags wanted) {
            VkPhysicalDeviceMemoryProperties properties{};
            Fn<PFN_vkGetPhysicalDeviceMemoryProperties>("vkGetPhysicalDeviceMemoryProperties")(physical, &properties);
            for (uint32_t i = 0; i < properties.memoryTypeCount; i++) {
                if ((bits & (1u << i)) && (properties.memoryTypes[i].propertyFlags & wanted) == wanted) return i;
            }
            return UINT32_MAX;
        }

        static std::vector<std::string> Split(const std::string& list) {
            std::vector<std::string> names;
            for (size_t at = 0; at < list.size();) {
                const size_t end = std::min(list.find(' ', at), list.size());
                if (end > at) names.push_back(list.substr(at, end - at));
                at = end + 1;
            }
            return names;
        }

        bool make(const std::string& instanceExtensions, const std::string& deviceExtensions) {
            const HMODULE loader = LoadLibraryW(L"vulkan-1.dll");
            gipa = loader ? reinterpret_cast<PFN_vkGetInstanceProcAddr>(GetProcAddress(loader, "vkGetInstanceProcAddr")) : nullptr;
            if (!gipa) return false;
            const std::vector<std::string> instanceNames = Split(instanceExtensions), deviceNames = Split(deviceExtensions);
            std::vector<const char*> names;
            for (const std::string& name : instanceNames) names.push_back(name.c_str());
            VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
            app.pApplicationName = "Layer test";
            app.apiVersion = VK_API_VERSION_1_1;
            VkInstanceCreateInfo instanceInfo{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
            instanceInfo.pApplicationInfo = &app;
            instanceInfo.enabledExtensionCount = uint32_t(names.size());
            instanceInfo.ppEnabledExtensionNames = names.data();
            if (Fn<PFN_vkCreateInstance>("vkCreateInstance")(&instanceInfo, nullptr, &instance) != VK_SUCCESS) return false;
            uint32_t count = 1;
            Fn<PFN_vkEnumeratePhysicalDevices>("vkEnumeratePhysicalDevices")(instance, &count, &physical);
            if (!physical) return false;
            uint32_t families = 0;
            Fn<PFN_vkGetPhysicalDeviceQueueFamilyProperties>("vkGetPhysicalDeviceQueueFamilyProperties")(physical, &families, nullptr);
            std::vector<VkQueueFamilyProperties> familyList(families);
            Fn<PFN_vkGetPhysicalDeviceQueueFamilyProperties>("vkGetPhysicalDeviceQueueFamilyProperties")(physical, &families, familyList.data());
            while (family < families && !(familyList[family].queueFlags & VK_QUEUE_GRAPHICS_BIT)) family++;
            if (family == families) return false;
            const float priority = 1.f;
            VkDeviceQueueCreateInfo queueInfo{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
            queueInfo.queueFamilyIndex = family;
            queueInfo.queueCount = 1;
            queueInfo.pQueuePriorities = &priority;
            names.clear();
            for (const std::string& name : deviceNames) names.push_back(name.c_str());
            VkDeviceCreateInfo deviceInfo{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
            deviceInfo.queueCreateInfoCount = 1;
            deviceInfo.pQueueCreateInfos = &queueInfo;
            deviceInfo.enabledExtensionCount = uint32_t(names.size());
            deviceInfo.ppEnabledExtensionNames = names.data();
            if (Fn<PFN_vkCreateDevice>("vkCreateDevice")(physical, &deviceInfo, nullptr, &device) != VK_SUCCESS) return false;
            Fn<PFN_vkGetDeviceQueue>("vkGetDeviceQueue")(device, family, 0, &queue);
            VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
            poolInfo.queueFamilyIndex = family;
            return Fn<PFN_vkCreateCommandPool>("vkCreateCommandPool")(device, &poolInfo, nullptr, &pool) == VK_SUCCESS;
        }

        // Records into one command buffer, runs it on the game's queue and waits for it.
        template <typename Record> void Run(Record record) {
            VkCommandBufferAllocateInfo allocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
            allocate.commandPool = pool;
            allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            allocate.commandBufferCount = 1;
            VkCommandBuffer commands = VK_NULL_HANDLE;
            Fn<PFN_vkAllocateCommandBuffers>("vkAllocateCommandBuffers")(device, &allocate, &commands);
            VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
            Fn<PFN_vkBeginCommandBuffer>("vkBeginCommandBuffer")(commands, &begin);
            record(commands);
            Fn<PFN_vkEndCommandBuffer>("vkEndCommandBuffer")(commands);
            VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
            submit.commandBufferCount = 1;
            submit.pCommandBuffers = &commands;
            Fn<PFN_vkQueueSubmit>("vkQueueSubmit")(queue, 1, &submit, VK_NULL_HANDLE);
            Fn<PFN_vkQueueWaitIdle>("vkQueueWaitIdle")(queue);
            Fn<PFN_vkFreeCommandBuffers>("vkFreeCommandBuffers")(device, pool, 1, &commands);
        }

        void Barrier(VkCommandBuffer commands, VkImage image, uint32_t layers, VkImageLayout from, VkImageLayout to) {
            VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
            barrier.srcQueueFamilyIndex = barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            barrier.image = image;
            barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, layers};
            barrier.oldLayout = from;
            barrier.newLayout = to;
            barrier.srcAccessMask = VK_ACCESS_MEMORY_WRITE_BIT;
            barrier.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
            Fn<PFN_vkCmdPipelineBarrier>("vkCmdPipelineBarrier")(commands, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0,
                                                                 nullptr, 0, nullptr, 1, &barrier);
        }

        void destroy() {
            if (device) {
                Fn<PFN_vkDeviceWaitIdle>("vkDeviceWaitIdle")(device);
                if (pool) Fn<PFN_vkDestroyCommandPool>("vkDestroyCommandPool")(device, pool, nullptr);
                Fn<PFN_vkDestroyDevice>("vkDestroyDevice")(device, nullptr);
            }
            if (instance) Fn<PFN_vkDestroyInstance>("vkDestroyInstance")(instance, nullptr);
            device = VK_NULL_HANDLE;
            instance = VK_NULL_HANDLE;
        }
    } g_vk;

    // ---- the fake runtime ---------------------------------------------------------------------------------------
    struct FakeSwapchain {
        XrSwapchainCreateInfo info{};
        std::vector<ComPtr<ID3D11Texture2D>> images11;
        std::vector<ComPtr<ID3D12Resource>> images12;
        std::vector<VkImage> imagesVk;
        std::vector<VkDeviceMemory> memoriesVk;
        std::vector<ULONG> references; // Of each image, when it was made.
        uint32_t next = 0;
        int acquired = -1, released = -1;
        bool destroyed = false;
    };
    std::map<uint64_t, FakeSwapchain> g_swapchains;
    float g_fill[4][4]{}; // The colour of each slice of the swapchain made next (linear).
    std::vector<XrCompositionLayerProjectionView> g_submitted;
    uint32_t g_submittedLayers = 0;
    XrTime g_time = 1000000;

    FakeSwapchain& Of(XrSwapchain swapchain) {
        return g_swapchains[reinterpret_cast<uint64_t>(swapchain)];
    }

    ULONG References(IUnknown* object) {
        object->AddRef();
        return object->Release();
    }

    void Fill(FakeSwapchain& swapchain, uint32_t image) {
        if (g_vulkan) {
            // Cleared slice by slice, then handed out as a colour attachment (the OpenXR rule for Vulkan).
            const VkImage target = swapchain.imagesVk[image];
            g_vk.Run([&](VkCommandBuffer commands) {
                g_vk.Barrier(commands, target, swapchain.info.arraySize, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
                for (uint32_t slice = 0; slice < swapchain.info.arraySize; slice++) {
                    VkClearColorValue color{};
                    memcpy(color.float32, g_fill[slice], sizeof(color.float32));
                    const VkImageSubresourceRange range{VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, slice, 1};
                    g_vk.Fn<PFN_vkCmdClearColorImage>("vkCmdClearColorImage")(commands, target, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &color, 1, &range);
                }
                g_vk.Barrier(commands, target, swapchain.info.arraySize, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
            });
            return;
        }
        for (uint32_t slice = 0; slice < swapchain.info.arraySize; slice++) {
            if (g_d3d12) {
                ComPtr<ID3D12DescriptorHeap> heap;
                D3D12_DESCRIPTOR_HEAP_DESC heapDesc{D3D12_DESCRIPTOR_HEAP_TYPE_RTV, 1};
                g_device12->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&heap));
                D3D12_RENDER_TARGET_VIEW_DESC view{};
                view.Format = static_cast<DXGI_FORMAT>(swapchain.info.format);
                view.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2DARRAY;
                view.Texture2DArray.FirstArraySlice = slice;
                view.Texture2DArray.ArraySize = 1;
                const auto handle = heap->GetCPUDescriptorHandleForHeapStart();
                g_device12->CreateRenderTargetView(swapchain.images12[image].Get(), &view, handle);
                Run12([&](ID3D12GraphicsCommandList* list) { list->ClearRenderTargetView(handle, g_fill[slice], 0, nullptr); });
            } else {
                D3D11_RENDER_TARGET_VIEW_DESC view{};
                view.Format = static_cast<DXGI_FORMAT>(swapchain.info.format);
                view.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2DARRAY;
                view.Texture2DArray.FirstArraySlice = slice;
                view.Texture2DArray.ArraySize = 1;
                ComPtr<ID3D11RenderTargetView> target;
                g_device11->CreateRenderTargetView(swapchain.images11[image].Get(), &view, &target);
                g_context11->ClearRenderTargetView(target.Get(), g_fill[slice]);
            }
        }
    }

    XrResult XRAPI_CALL StubSuccess() { return XR_SUCCESS; }
    template <typename Handle> XrResult MakeHandle(Handle* handle) {
        static uint64_t next = 0x1000;
        *handle = reinterpret_cast<Handle>(static_cast<uintptr_t>(next++));
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubGetInstanceProperties(XrInstance, XrInstanceProperties* properties) {
        strcpy_s(properties->runtimeName, "Fake runtime");
        properties->runtimeVersion = XR_MAKE_VERSION(1, 2, 3);
        return XR_SUCCESS;
    }
    // What the layers and runtime below offer and get asked for.
    bool g_belowOffersQuadViews = false;
    std::vector<std::string> g_passedDown;

    XrResult XRAPI_CALL StubEnumerateExtensions(const char*, uint32_t capacity, uint32_t* count, XrExtensionProperties* properties) {
        *count = g_belowOffersQuadViews ? 1 : 0;
        if (g_belowOffersQuadViews && capacity && properties) strcpy_s(properties[0].extensionName, XR_VARJO_QUAD_VIEWS_EXTENSION_NAME);
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubGetSystem(XrInstance, const XrSystemGetInfo*, XrSystemId* system) {
        *system = 1;
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubGetSystemProperties(XrInstance, XrSystemId, XrSystemProperties* properties) {
        strcpy_s(properties->systemName, "Fake headset");
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubEnumerateViewConfigurations(XrInstance, XrSystemId, uint32_t capacity, uint32_t* count, XrViewConfigurationType* types) {
        *count = 1;
        if (capacity && types) types[0] = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubEnumerateViewConfigurationViews(XrInstance, XrSystemId, XrViewConfigurationType, uint32_t capacity, uint32_t* count,
                                                            XrViewConfigurationView* views) {
        *count = 2;
        for (uint32_t i = 0; i < 2 && i < capacity; i++) {
            views[i].recommendedImageRectWidth = StereoWidth;
            views[i].recommendedImageRectHeight = StereoHeight;
            views[i].maxImageRectWidth = views[i].maxImageRectHeight = 8192;
            views[i].recommendedSwapchainSampleCount = views[i].maxSwapchainSampleCount = 1;
        }
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubCreateSession(XrInstance, const XrSessionCreateInfo*, XrSession* session) { return MakeHandle(session); }
    XrResult XRAPI_CALL StubCreateReferenceSpace(XrSession, const XrReferenceSpaceCreateInfo*, XrSpace* space) { return MakeHandle(space); }
    XrResult XRAPI_CALL StubCreateSwapchain(XrSession, const XrSwapchainCreateInfo* info, XrSwapchain* swapchain) {
        MakeHandle(swapchain);
        FakeSwapchain& made = Of(*swapchain);
        made.info = *info;
        for (int i = 0; i < ImagesPerSwapchain; i++) {
            if (g_vulkan) {
                VkImageCreateInfo imageInfo{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
                imageInfo.imageType = VK_IMAGE_TYPE_2D;
                imageInfo.format = static_cast<VkFormat>(info->format);
                imageInfo.extent = {info->width, info->height, 1};
                imageInfo.mipLevels = 1;
                imageInfo.arrayLayers = info->arraySize;
                imageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
                imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
                imageInfo.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                                  VK_IMAGE_USAGE_TRANSFER_DST_BIT;
                VkImage image = VK_NULL_HANDLE;
                VkDeviceMemory memory = VK_NULL_HANDLE;
                if (g_vk.Fn<PFN_vkCreateImage>("vkCreateImage")(g_vk.device, &imageInfo, nullptr, &image) != VK_SUCCESS) return XR_ERROR_RUNTIME_FAILURE;
                VkMemoryRequirements needs{};
                g_vk.Fn<PFN_vkGetImageMemoryRequirements>("vkGetImageMemoryRequirements")(g_vk.device, image, &needs);
                VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
                allocate.allocationSize = needs.size;
                allocate.memoryTypeIndex = g_vk.MemoryType(needs.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
                if (g_vk.Fn<PFN_vkAllocateMemory>("vkAllocateMemory")(g_vk.device, &allocate, nullptr, &memory) != VK_SUCCESS) return XR_ERROR_RUNTIME_FAILURE;
                g_vk.Fn<PFN_vkBindImageMemory>("vkBindImageMemory")(g_vk.device, image, memory, 0);
                made.imagesVk.push_back(image);
                made.memoriesVk.push_back(memory);
            } else if (g_d3d12) {
                D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_DEFAULT};
                D3D12_RESOURCE_DESC desc{};
                desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
                desc.Width = info->width;
                desc.Height = info->height;
                desc.DepthOrArraySize = uint16_t(info->arraySize);
                desc.MipLevels = 1;
                desc.Format = Typeless(info->format);
                desc.SampleDesc.Count = 1;
                desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
                ComPtr<ID3D12Resource> image;
                // The state a D3D12 runtime hands its colour images out in.
                if (FAILED(g_device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr,
                                                               IID_PPV_ARGS(&image)))) {
                    return XR_ERROR_RUNTIME_FAILURE;
                }
                made.images12.push_back(image);
                image.Reset();
                made.references.push_back(References(made.images12.back().Get()));
            } else {
                D3D11_TEXTURE2D_DESC desc{};
                desc.Width = info->width;
                desc.Height = info->height;
                desc.MipLevels = 1;
                desc.ArraySize = info->arraySize;
                desc.Format = Typeless(info->format);
                desc.SampleDesc.Count = 1;
                desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
                ComPtr<ID3D11Texture2D> image;
                if (FAILED(g_device11->CreateTexture2D(&desc, nullptr, &image))) return XR_ERROR_RUNTIME_FAILURE;
                made.images11.push_back(image);
                image.Reset();
                made.references.push_back(References(made.images11.back().Get()));
            }
            Fill(made, uint32_t(i));
        }
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubDestroySwapchain(XrSwapchain swapchain) {
        Of(swapchain).destroyed = true;
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubEnumerateSwapchainImages(XrSwapchain swapchain, uint32_t capacity, uint32_t* count, XrSwapchainImageBaseHeader* images) {
        FakeSwapchain& from = Of(swapchain);
        *count = ImagesPerSwapchain;
        if (capacity == 0 || !images) return XR_SUCCESS;
        for (uint32_t i = 0; i < *count && i < capacity; i++) {
            if (g_vulkan) {
                if (images->type != XR_TYPE_SWAPCHAIN_IMAGE_VULKAN_KHR) return XR_ERROR_VALIDATION_FAILURE;
                reinterpret_cast<XrSwapchainImageVulkanKHR*>(images)[i].image = from.imagesVk[i];
            } else if (g_d3d12) {
                if (images->type != XR_TYPE_SWAPCHAIN_IMAGE_D3D12_KHR) return XR_ERROR_VALIDATION_FAILURE;
                reinterpret_cast<XrSwapchainImageD3D12KHR*>(images)[i].texture = from.images12[i].Get();
            } else {
                if (images->type != XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR) return XR_ERROR_VALIDATION_FAILURE;
                reinterpret_cast<XrSwapchainImageD3D11KHR*>(images)[i].texture = from.images11[i].Get();
            }
        }
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubAcquireSwapchainImage(XrSwapchain swapchain, const XrSwapchainImageAcquireInfo*, uint32_t* index) {
        FakeSwapchain& from = Of(swapchain);
        if (from.acquired >= 0) return XR_ERROR_CALL_ORDER_INVALID;
        *index = from.next++ % ImagesPerSwapchain;
        from.acquired = int(*index);
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubReleaseSwapchainImage(XrSwapchain swapchain, const XrSwapchainImageReleaseInfo*) {
        FakeSwapchain& from = Of(swapchain);
        if (from.acquired < 0) return XR_ERROR_CALL_ORDER_INVALID;
        from.released = from.acquired;
        from.acquired = -1;
        return XR_SUCCESS;
    }
    XrFovf EyeFov(uint32_t eye) {
        // Canted a little, as headsets are: more outward than inward.
        const float outward = 0.8726646f, inward = 0.6981317f, up = 0.7853982f, down = 0.8290314f; // 50, 40, 45, 47.5 degrees.
        return eye == 0 ? XrFovf{-outward, inward, up, -down} : XrFovf{-inward, outward, up, -down};
    }
    XrResult XRAPI_CALL StubLocateViews(XrSession, const XrViewLocateInfo*, XrViewState* state, uint32_t capacity, uint32_t* count, XrView* views) {
        *count = 2;
        if (state) state->viewStateFlags = XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT |
                                           XR_VIEW_STATE_ORIENTATION_TRACKED_BIT | XR_VIEW_STATE_POSITION_TRACKED_BIT;
        for (uint32_t i = 0; i < 2 && i < capacity; i++) {
            views[i].pose = {{0, 0, 0, 1}, {i == 0 ? -0.032f : 0.032f, 0, 0}};
            views[i].fov = EyeFov(i);
        }
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubWaitFrame(XrSession, const XrFrameWaitInfo*, XrFrameState* state) {
        g_time += 11111111;
        state->predictedDisplayTime = g_time;
        state->predictedDisplayPeriod = 11111111;
        state->shouldRender = XR_TRUE;
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubEndFrame(XrSession, const XrFrameEndInfo* info) {
        g_submitted.clear();
        g_submittedLayers = info->layerCount;
        for (uint32_t i = 0; i < info->layerCount; i++) {
            if (!info->layers[i] || info->layers[i]->type != XR_TYPE_COMPOSITION_LAYER_PROJECTION) continue;
            const auto* projection = reinterpret_cast<const XrCompositionLayerProjection*>(info->layers[i]);
            for (uint32_t view = 0; view < projection->viewCount; view++) g_submitted.push_back(projection->views[view]);
        }
        return XR_SUCCESS;
    }
    // XR_KHR_vulkan_enable: the runtime itself needs no Vulkan extensions; the layer adds its own.
    XrResult XRAPI_CALL StubVulkanExtensions(XrInstance, XrSystemId, uint32_t capacity, uint32_t* count, char* buffer) {
        *count = 1;
        if (capacity && buffer) buffer[0] = 0;
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubStringToPath(XrInstance, const char*, XrPath* path) {
        *path = 1;
        return XR_SUCCESS;
    }

    XrResult XRAPI_CALL StubGetInstanceProcAddr(XrInstance, const char* name, PFN_xrVoidFunction* function) {
        static const std::map<std::string, PFN_xrVoidFunction> table = {
            {"xrGetInstanceProperties", reinterpret_cast<PFN_xrVoidFunction>(StubGetInstanceProperties)},
            {"xrEnumerateInstanceExtensionProperties", reinterpret_cast<PFN_xrVoidFunction>(StubEnumerateExtensions)},
            {"xrGetSystem", reinterpret_cast<PFN_xrVoidFunction>(StubGetSystem)},
            {"xrGetSystemProperties", reinterpret_cast<PFN_xrVoidFunction>(StubGetSystemProperties)},
            {"xrEnumerateViewConfigurations", reinterpret_cast<PFN_xrVoidFunction>(StubEnumerateViewConfigurations)},
            {"xrEnumerateViewConfigurationViews", reinterpret_cast<PFN_xrVoidFunction>(StubEnumerateViewConfigurationViews)},
            {"xrCreateSession", reinterpret_cast<PFN_xrVoidFunction>(StubCreateSession)},
            {"xrCreateReferenceSpace", reinterpret_cast<PFN_xrVoidFunction>(StubCreateReferenceSpace)},
            {"xrCreateSwapchain", reinterpret_cast<PFN_xrVoidFunction>(StubCreateSwapchain)},
            {"xrDestroySwapchain", reinterpret_cast<PFN_xrVoidFunction>(StubDestroySwapchain)},
            {"xrEnumerateSwapchainImages", reinterpret_cast<PFN_xrVoidFunction>(StubEnumerateSwapchainImages)},
            {"xrAcquireSwapchainImage", reinterpret_cast<PFN_xrVoidFunction>(StubAcquireSwapchainImage)},
            {"xrReleaseSwapchainImage", reinterpret_cast<PFN_xrVoidFunction>(StubReleaseSwapchainImage)},
            {"xrLocateViews", reinterpret_cast<PFN_xrVoidFunction>(StubLocateViews)},
            {"xrWaitFrame", reinterpret_cast<PFN_xrVoidFunction>(StubWaitFrame)},
            {"xrEndFrame", reinterpret_cast<PFN_xrVoidFunction>(StubEndFrame)},
            {"xrStringToPath", reinterpret_cast<PFN_xrVoidFunction>(StubStringToPath)},
            {"xrGetVulkanInstanceExtensionsKHR", reinterpret_cast<PFN_xrVoidFunction>(StubVulkanExtensions)},
            {"xrGetVulkanDeviceExtensionsKHR", reinterpret_cast<PFN_xrVoidFunction>(StubVulkanExtensions)},
        };
        const auto found = table.find(name);
        *function = found != table.end() ? found->second : reinterpret_cast<PFN_xrVoidFunction>(StubSuccess);
        return XR_SUCCESS;
    }
    XrResult XRAPI_CALL StubCreateApiLayerInstance(const XrInstanceCreateInfo* info, const XrApiLayerCreateInfo*, XrInstance* instance) {
        g_passedDown.assign(info->enabledExtensionNames, info->enabledExtensionNames + info->enabledExtensionCount);
        return MakeHandle(instance);
    }

    bool PassedDown(const char* extension) {
        return std::find(g_passedDown.begin(), g_passedDown.end(), std::string(extension)) != g_passedDown.end();
    }

    std::string ReadText(const std::string& path) {
        std::string text;
        FILE* file = nullptr;
        if (fopen_s(&file, path.c_str(), "rb") != 0) return text;
        char buffer[4096];
        size_t got;
        while ((got = fread(buffer, 1, sizeof(buffer), file)) > 0) text.append(buffer, got);
        fclose(file);
        return text;
    }

    void WriteText(const std::string& path, const char* text) {
        FILE* file = nullptr;
        if (fopen_s(&file, path.c_str(), "wb") == 0) {
            fputs(text, file);
            fclose(file);
        }
    }

    // ---- reading pictures back ----------------------------------------------------------------------------------
    struct Picture {
        uint32_t width = 0, height = 0;
        std::vector<uint8_t> pixels; // RGBA, as stored (sRGB-encoded).
        const uint8_t* at(float u, float v) const {
            const uint32_t x = std::min(width - 1, uint32_t(u * width)), y = std::min(height - 1, uint32_t(v * height));
            return &pixels[(size_t(y) * width + x) * 4];
        }
    };

    Picture Read(FakeSwapchain& swapchain, int image) {
        Picture picture;
        picture.width = swapchain.info.width;
        picture.height = swapchain.info.height;
        picture.pixels.resize(size_t(picture.width) * picture.height * 4);
        if (g_vulkan) {
            // Copied into a buffer the CPU can read, on the game's queue - after the layer's own copy into the image.
            VkBufferCreateInfo bufferInfo{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
            bufferInfo.size = picture.pixels.size();
            bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
            VkBuffer buffer = VK_NULL_HANDLE;
            VkDeviceMemory memory = VK_NULL_HANDLE;
            g_vk.Fn<PFN_vkCreateBuffer>("vkCreateBuffer")(g_vk.device, &bufferInfo, nullptr, &buffer);
            VkMemoryRequirements needs{};
            g_vk.Fn<PFN_vkGetBufferMemoryRequirements>("vkGetBufferMemoryRequirements")(g_vk.device, buffer, &needs);
            VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
            allocate.allocationSize = needs.size;
            allocate.memoryTypeIndex = g_vk.MemoryType(needs.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
            g_vk.Fn<PFN_vkAllocateMemory>("vkAllocateMemory")(g_vk.device, &allocate, nullptr, &memory);
            g_vk.Fn<PFN_vkBindBufferMemory>("vkBindBufferMemory")(g_vk.device, buffer, memory, 0);
            const VkImage source = swapchain.imagesVk[image];
            g_vk.Run([&](VkCommandBuffer commands) {
                g_vk.Barrier(commands, source, 1, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
                VkBufferImageCopy region{};
                region.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
                region.imageExtent = {picture.width, picture.height, 1};
                g_vk.Fn<PFN_vkCmdCopyImageToBuffer>("vkCmdCopyImageToBuffer")(commands, source, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, buffer, 1, &region);
                g_vk.Barrier(commands, source, 1, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
            });
            void* mapped = nullptr;
            g_vk.Fn<PFN_vkMapMemory>("vkMapMemory")(g_vk.device, memory, 0, VK_WHOLE_SIZE, 0, &mapped);
            if (mapped) memcpy(picture.pixels.data(), mapped, picture.pixels.size());
            g_vk.Fn<PFN_vkUnmapMemory>("vkUnmapMemory")(g_vk.device, memory);
            g_vk.Fn<PFN_vkDestroyBuffer>("vkDestroyBuffer")(g_vk.device, buffer, nullptr);
            g_vk.Fn<PFN_vkFreeMemory>("vkFreeMemory")(g_vk.device, memory, nullptr);
            if (!mapped) return {};
            return picture;
        }
        if (g_d3d12) {
            ID3D12Resource* texture = swapchain.images12[image].Get();
            const D3D12_RESOURCE_DESC desc = texture->GetDesc();
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
            UINT64 total = 0;
            g_device12->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, nullptr, nullptr, &total);
            D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_READBACK};
            D3D12_RESOURCE_DESC bufferDesc{};
            bufferDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            bufferDesc.Width = total;
            bufferDesc.Height = 1;
            bufferDesc.DepthOrArraySize = 1;
            bufferDesc.MipLevels = 1;
            bufferDesc.SampleDesc.Count = 1;
            bufferDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            ComPtr<ID3D12Resource> buffer;
            if (FAILED(g_device12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &bufferDesc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                           IID_PPV_ARGS(&buffer)))) {
                return {};
            }
            Run12([&](ID3D12GraphicsCommandList* list) {
                // The image must have come back in the render-target state: the debug layer objects here if it did not.
                D3D12_RESOURCE_BARRIER barrier{};
                barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
                barrier.Transition.pResource = texture;
                barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
                barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
                barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
                list->ResourceBarrier(1, &barrier);
                D3D12_TEXTURE_COPY_LOCATION from{};
                from.pResource = texture;
                from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                D3D12_TEXTURE_COPY_LOCATION to{};
                to.pResource = buffer.Get();
                to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                to.PlacedFootprint = footprint;
                list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
                std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
                list->ResourceBarrier(1, &barrier);
            });
            uint8_t* mapped = nullptr;
            if (FAILED(buffer->Map(0, nullptr, reinterpret_cast<void**>(&mapped)))) return {};
            for (uint32_t y = 0; y < picture.height; y++) {
                memcpy(&picture.pixels[size_t(y) * picture.width * 4], mapped + footprint.Offset + size_t(y) * footprint.Footprint.RowPitch,
                       size_t(picture.width) * 4);
            }
            buffer->Unmap(0, nullptr);
        } else {
            D3D11_TEXTURE2D_DESC desc;
            swapchain.images11[image]->GetDesc(&desc);
            desc.Format = static_cast<DXGI_FORMAT>(swapchain.info.format);
            desc.Usage = D3D11_USAGE_STAGING;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> staging;
            if (FAILED(g_device11->CreateTexture2D(&desc, nullptr, &staging))) return {};
            g_context11->CopyResource(staging.Get(), swapchain.images11[image].Get());
            D3D11_MAPPED_SUBRESOURCE mapped;
            if (FAILED(g_context11->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return {};
            for (uint32_t y = 0; y < picture.height; y++) {
                memcpy(&picture.pixels[size_t(y) * picture.width * 4], static_cast<uint8_t*>(mapped.pData) + size_t(y) * mapped.RowPitch,
                       size_t(picture.width) * 4);
            }
            g_context11->Unmap(staging.Get(), 0);
        }
        return picture;
    }

    bool SavePng(const std::wstring& path, const Picture& picture) {
        std::vector<uint8_t> bgra(picture.pixels);
        for (size_t i = 0; i + 3 < bgra.size(); i += 4) std::swap(bgra[i], bgra[i + 2]);
        ComPtr<IWICImagingFactory> factory;
        ComPtr<IWICStream> stream;
        ComPtr<IWICBitmapEncoder> encoder;
        ComPtr<IWICBitmapFrameEncode> frame;
        WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
        return SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))) &&
               SUCCEEDED(factory->CreateStream(&stream)) && SUCCEEDED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE)) &&
               SUCCEEDED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)) &&
               SUCCEEDED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache)) && SUCCEEDED(encoder->CreateNewFrame(&frame, nullptr)) &&
               SUCCEEDED(frame->Initialize(nullptr)) && SUCCEEDED(frame->SetSize(picture.width, picture.height)) &&
               SUCCEEDED(frame->SetPixelFormat(&format)) &&
               SUCCEEDED(frame->WritePixels(picture.height, picture.width * 4, UINT(bgra.size()), bgra.data())) && SUCCEEDED(frame->Commit()) &&
               SUCCEEDED(encoder->Commit());
    }

    bool Near(const uint8_t* pixel, int r, int g, int b) {
        return abs(pixel[0] - r) <= 6 && abs(pixel[1] - g) <= 6 && abs(pixel[2] - b) <= 6;
    }

    float Degrees(const XrVector3f& a, const XrVector3f& b) {
        return acosf(std::min(1.f, a.x * b.x + a.y * b.y + a.z * b.z)) * 180.f / 3.14159265f;
    }

    XrVector3f Gaze(float yawDegrees) {
        const float a = yawDegrees * 3.14159265f / 180.f;
        return {sinf(a), 0.f, -cosf(a)};
    }

    int TestGazeFilter() {
        openxr_api_layer::GazeFilter filter;
        filter.smoothingMs = 8.f;
        filter.deadzoneDegrees = 0.5f;
        XrVector3f gaze{};
        Check(!filter.update(false, gaze, 11.f), "no gaze to follow before the eyes were ever tracked");
        gaze = Gaze(10.f);
        Check(filter.update(true, gaze, 0.f) && Degrees(gaze, Gaze(10.f)) < 0.01f, "the first gaze is followed at once");
        gaze = Gaze(10.3f);
        filter.update(true, gaze, 11.f);
        Check(Degrees(gaze, Gaze(10.f)) < 0.01f, "a move inside the deadzone leaves the focus view where it is");
        gaze = Gaze(20.f);
        filter.update(true, gaze, 4.f);
        const float partWay = Degrees(gaze, Gaze(10.f));
        printf("  note  after 4 ms of a 10 degree move: %.2f degrees\n", partWay);
        Check(partWay > 2.f && partWay < 6.f, "a larger move is followed over the smoothing");
        gaze = Gaze(20.f);
        filter.update(true, gaze, 50.f);
        Check(Degrees(gaze, Gaze(20.f)) < 0.05f, "and arrives");
        filter.update(false, gaze, 16.f);
        const float easing = Degrees(gaze, Gaze(20.f));
        printf("  note  16 ms without tracking: %.2f degrees towards the middle\n", easing);
        Check(easing > 0.5f && easing < 5.f, "without tracking the gaze eases back towards the middle, not jumping");
        for (int i = 0; i < 100; i++) filter.update(false, gaze, 16.f);
        Check(Degrees(gaze, Gaze(0.f)) < 0.05f, "and gets there");
        gaze = Gaze(-15.f);
        filter.update(true, gaze, 100.f);
        Check(Degrees(gaze, Gaze(-15.f)) < 0.05f, "tracking again, it follows the eyes again");
        filter.smoothingMs = 0.f;
        gaze = Gaze(5.f);
        filter.update(true, gaze, 0.f);
        Check(Degrees(gaze, Gaze(5.f)) < 0.01f, "with no smoothing it is there at once");
        printf("%d failure(s)\n", g_failures);
        return g_failures ? 1 : 0;
    }

    template <typename Function> Function Get(PFN_xrGetInstanceProcAddr getter, XrInstance instance, const char* name) {
        PFN_xrVoidFunction function = nullptr;
        getter(instance, name, &function);
        return reinterpret_cast<Function>(function);
    }

    // The errors the Direct3D 12 debug layer has collected (when it is installed).
    int DebugErrors() {
        ComPtr<ID3D12InfoQueue> queue;
        if (!g_device12 || FAILED(g_device12.As(&queue))) return -1;
        int errors = 0;
        const UINT64 count = queue->GetNumStoredMessages();
        for (UINT64 i = 0; i < count; i++) {
            SIZE_T size = 0;
            queue->GetMessage(i, nullptr, &size);
            std::vector<uint8_t> storage(size);
            auto* message = reinterpret_cast<D3D12_MESSAGE*>(storage.data());
            if (FAILED(queue->GetMessage(i, message, &size))) continue;
            // D3D11on12 asks of every resource it wraps whether Direct3D 11 made it, and the debug layer reports the "no"
            // as an error. It is the expected answer for a Direct3D 12 runtime's images.
            if (strstr(message->pDescription, "ReflectSharedProperties")) continue;
            if (message->Severity == D3D12_MESSAGE_SEVERITY_ERROR || message->Severity == D3D12_MESSAGE_SEVERITY_CORRUPTION) {
                if (errors < 12) printf("    D3D12: %s\n", message->pDescription);
                errors++;
            }
        }
        queue->ClearStoredMessages();
        return errors;
    }

} // namespace

int main(int argc, char** argv) {
    if (argc < 4) {
        printf("usage: qvf_test.exe <layer dll> <output folder> <d3d11|d3d12|d3d12-array>\n");
        return 2;
    }
    const std::string folder = argv[2], name = argv[3];
    if (name == "gaze-filter") {
        printf("case %s\n", name.c_str());
        return TestGazeFilter();
    }
    g_d3d12 = name.rfind("d3d12", 0) == 0;
    const bool arrays = name.find("array") != std::string::npos;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    CreateDirectoryA(folder.c_str(), nullptr);

    // The layer keeps its log and the user's settings under %LOCALAPPDATA%: the test gets a folder of its own.
    SetEnvironmentVariableA("LOCALAPPDATA", folder.c_str());
    _putenv_s("LOCALAPPDATA", folder.c_str());
    // The official layer's settings, for the first start to copy; the layer's own folder starts empty.
    const std::string settings = folder + "\\GazeMirror\\QuadViews\\settings.cfg";
    DeleteFileA(settings.c_str());
    CreateDirectoryA((folder + "\\Quad-Views-Foveated").c_str(), nullptr);
    WriteText(folder + "\\Quad-Views-Foveated\\settings.cfg", "# the official layer's file\nsharpen_focus_view=0.3\n");
    const bool standsAside = name == "opengl-declared" || name == "official-below";
    g_vulkan = name == "vulkan";
    g_belowOffersQuadViews = name == "official-below";

    printf("case %s\n", name.c_str());
    bool debugLayer = false;
    if (g_d3d12) {
        ComPtr<ID3D12Debug> debug;
        if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)))) {
            debug->EnableDebugLayer();
            debugLayer = true;
        }
        printf("  note  the Direct3D 12 debug layer is %s\n", debugLayer ? "on" : "not installed: resource states are not checked");
        if (SUCCEEDED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_device12)))) {
            D3D12_COMMAND_QUEUE_DESC queueDesc{};
            queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            g_device12->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&g_queue12));
        }
        if (!g_device12 || !g_queue12) {
            printf("no Direct3D 12 device\n");
            return 2;
        }
    } else if (!g_vulkan) { // The Vulkan game makes its device once the layer has said which extensions it needs.
        D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &g_device11, nullptr, &g_context11);
        if (!g_device11) {
            printf("no Direct3D 11 device\n");
            return 2;
        }
    }

    const HMODULE layer = LoadLibraryA(argv[1]);
    if (!layer) {
        printf("the layer could not be loaded (%lu)\n", GetLastError());
        return 2;
    }
    const auto negotiate = reinterpret_cast<XrResult(XRAPI_CALL*)(const XrNegotiateLoaderInfo*, const char*, XrNegotiateApiLayerRequest*)>(
        GetProcAddress(layer, "xrNegotiateLoaderApiLayerInterface"));
    XrNegotiateLoaderInfo loaderInfo{XR_LOADER_INTERFACE_STRUCT_LOADER_INFO, XR_LOADER_INFO_STRUCT_VERSION, sizeof(XrNegotiateLoaderInfo)};
    loaderInfo.minInterfaceVersion = 1;
    loaderInfo.maxInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    loaderInfo.minApiVersion = XR_MAKE_VERSION(1, 0, 0);
    loaderInfo.maxApiVersion = XR_MAKE_VERSION(1, 0x3ff, 0xfff);
    XrNegotiateApiLayerRequest request{XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST, XR_API_LAYER_INFO_STRUCT_VERSION, sizeof(XrNegotiateApiLayerRequest)};
    Check(negotiate && XR_SUCCEEDED(negotiate(&loaderInfo, LayerName, &request)), "the layer accepts the loader");
    if (!request.getInstanceProcAddr || !request.createApiLayerInstance) return 1;

    // ---- the instance
    std::vector<const char*> extensions = {g_vulkan  ? XR_KHR_VULKAN_ENABLE_EXTENSION_NAME
                                           : g_d3d12 ? XR_KHR_D3D12_ENABLE_EXTENSION_NAME
                                                     : XR_KHR_D3D11_ENABLE_EXTENSION_NAME,
                                           XR_VARJO_QUAD_VIEWS_EXTENSION_NAME};
    if (name == "d3d11-and-d3d12") extensions.push_back(XR_KHR_D3D12_ENABLE_EXTENSION_NAME);
    if (name == "opengl-declared") extensions.push_back("XR_KHR_opengl_enable");
    XrInstanceCreateInfo instanceInfo{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(instanceInfo.applicationInfo.applicationName, "Layer test");
    instanceInfo.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    instanceInfo.enabledExtensionCount = uint32_t(extensions.size());
    instanceInfo.enabledExtensionNames = extensions.data();
    XrApiLayerNextInfo nextInfo{XR_LOADER_INTERFACE_STRUCT_API_LAYER_NEXT_INFO, XR_API_LAYER_NEXT_INFO_STRUCT_VERSION, sizeof(XrApiLayerNextInfo)};
    strcpy_s(nextInfo.layerName, LayerName);
    nextInfo.nextGetInstanceProcAddr = StubGetInstanceProcAddr;
    nextInfo.nextCreateApiLayerInstance = StubCreateApiLayerInstance;
    // official-below: the official Quad-Views-Foveated sits under this layer (the stub plays it and the runtime).
    XrApiLayerNextInfo officialInfo = nextInfo;
    strcpy_s(officialInfo.layerName, "XR_APILAYER_MBUCCHIA_quad_views_foveated");
    if (name == "official-below") nextInfo.next = &officialInfo;
    XrApiLayerCreateInfo layerInfo{XR_LOADER_INTERFACE_STRUCT_API_LAYER_CREATE_INFO, XR_API_LAYER_CREATE_INFO_STRUCT_VERSION, sizeof(XrApiLayerCreateInfo)};
    layerInfo.nextInfo = &nextInfo;
    XrInstance instance = XR_NULL_HANDLE;
    Check(XR_SUCCEEDED(request.createApiLayerInstance(&instanceInfo, &layerInfo, &instance)), "the instance is made");

    const auto getter = request.getInstanceProcAddr;
    const auto getSystem = Get<PFN_xrGetSystem>(getter, instance, "xrGetSystem");
    const auto enumerateTypes = Get<PFN_xrEnumerateViewConfigurations>(getter, instance, "xrEnumerateViewConfigurations");
    const auto enumerateViews = Get<PFN_xrEnumerateViewConfigurationViews>(getter, instance, "xrEnumerateViewConfigurationViews");
    const auto createSession = Get<PFN_xrCreateSession>(getter, instance, "xrCreateSession");
    const auto destroySession = Get<PFN_xrDestroySession>(getter, instance, "xrDestroySession");
    const auto beginSession = Get<PFN_xrBeginSession>(getter, instance, "xrBeginSession");
    const auto createSwapchain = Get<PFN_xrCreateSwapchain>(getter, instance, "xrCreateSwapchain");
    const auto destroySwapchain = Get<PFN_xrDestroySwapchain>(getter, instance, "xrDestroySwapchain");
    const auto acquire = Get<PFN_xrAcquireSwapchainImage>(getter, instance, "xrAcquireSwapchainImage");
    const auto wait = Get<PFN_xrWaitSwapchainImage>(getter, instance, "xrWaitSwapchainImage");
    const auto release = Get<PFN_xrReleaseSwapchainImage>(getter, instance, "xrReleaseSwapchainImage");
    const auto locateViews = Get<PFN_xrLocateViews>(getter, instance, "xrLocateViews");
    const auto waitFrame = Get<PFN_xrWaitFrame>(getter, instance, "xrWaitFrame");
    const auto beginFrame = Get<PFN_xrBeginFrame>(getter, instance, "xrBeginFrame");
    const auto endFrame = Get<PFN_xrEndFrame>(getter, instance, "xrEndFrame");
    const auto createSpace = Get<PFN_xrCreateReferenceSpace>(getter, instance, "xrCreateReferenceSpace");

    // ---- the system and its views
    XrSystemGetInfo systemInfo{XR_TYPE_SYSTEM_GET_INFO};
    systemInfo.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId system = 0;
    Check(XR_SUCCEEDED(getSystem(instance, &systemInfo, &system)), "the system is found");
    XrViewConfigurationType types[4]{};
    uint32_t typeCount = 0;
    enumerateTypes(instance, system, 4, &typeCount, types);
    if (standsAside) {
        Check(typeCount == 1 && types[0] == XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, "the layer stands aside: it offers no quad views");
        if (name == "official-below") {
            Check(PassedDown(XR_VARJO_QUAD_VIEWS_EXTENSION_NAME), "the game's quad views request goes on to the official layer below");
        } else {
            Check(!PassedDown(XR_VARJO_QUAD_VIEWS_EXTENSION_NAME), "the game's quad views request stops here (nothing below offers it)");
        }
        printf("%d failure(s)\n", g_failures);
        return g_failures ? 1 : 0;
    }
    Check(typeCount == 2 && types[0] == XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO, "quad views are offered to the game");
    Check(!PassedDown(XR_VARJO_QUAD_VIEWS_EXTENSION_NAME), "the game's quad views request stops at the layer");
    const std::string imported = ReadText(settings);
    Check(imported.find("# Copied from") != std::string::npos && imported.find("sharpen_focus_view=0.3") != std::string::npos,
          "the first start copies the official layer's settings");

    // ---- the Vulkan game's device, with the extensions the layer asks for (XR_KHR_vulkan_enable's two questions)
    XrGraphicsBindingVulkanKHR bindingVk{XR_TYPE_GRAPHICS_BINDING_VULKAN_KHR};
    if (g_vulkan) {
        const auto ask = [&](const char* function) {
            PFN_xrGetVulkanInstanceExtensionsKHR question = Get<PFN_xrGetVulkanInstanceExtensionsKHR>(getter, instance, function);
            uint32_t count = 0;
            if (!question || XR_FAILED(question(instance, system, 0, &count, nullptr))) return std::string();
            std::string list(count, '\0');
            question(instance, system, count, &count, list.data());
            list.resize(strnlen(list.c_str(), list.size()));
            return list;
        };
        const std::string instanceExtensions = ask("xrGetVulkanInstanceExtensionsKHR");
        const std::string deviceExtensions = ask("xrGetVulkanDeviceExtensionsKHR");
        printf("  note  Vulkan extensions asked for: instance \"%s\", device \"%s\"\n", instanceExtensions.c_str(), deviceExtensions.c_str());
        Check(deviceExtensions.find("VK_KHR_external_memory_win32") != std::string::npos &&
                  deviceExtensions.find("VK_KHR_external_semaphore_win32") != std::string::npos,
              "the layer adds the sharing extensions to the game's Vulkan device");
        if (!g_vk.make(instanceExtensions, deviceExtensions)) {
            printf("no Vulkan device\n");
            return 2;
        }
        bindingVk.instance = g_vk.instance;
        bindingVk.physicalDevice = g_vk.physical;
        bindingVk.device = g_vk.device;
        bindingVk.queueFamilyIndex = g_vk.family;
        bindingVk.queueIndex = 0;
    }

    // ---- the session
    XrGraphicsBindingD3D11KHR binding11{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    binding11.device = g_device11.Get();
    XrGraphicsBindingD3D12KHR binding12{XR_TYPE_GRAPHICS_BINDING_D3D12_KHR};
    binding12.device = g_device12.Get();
    binding12.queue = g_queue12.Get();
    XrSessionCreateInfo sessionInfo{XR_TYPE_SESSION_CREATE_INFO, g_vulkan  ? static_cast<const void*>(&bindingVk)
                                                                 : g_d3d12 ? static_cast<const void*>(&binding12)
                                                                           : static_cast<const void*>(&binding11)};
    sessionInfo.systemId = system;
    XrSession session = XR_NULL_HANDLE;
    const ULONG deviceReferencesBefore = g_d3d12 ? References(g_device12.Get()) : 0;
    const ULONG queueReferencesBefore = g_d3d12 ? References(g_queue12.Get()) : 0;
    Check(XR_SUCCEEDED(createSession(instance, &sessionInfo, &session)), "the session is made");
    const ULONG deviceReferences = g_d3d12 ? References(g_device12.Get()) : 0;
    XrSessionBeginInfo beginInfo{XR_TYPE_SESSION_BEGIN_INFO};
    beginInfo.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO;
    const XrResult begun = beginSession(session, &beginInfo);
    Check(XR_SUCCEEDED(begun), "the session begins with quad views");
    if (XR_FAILED(begun)) {
        printf("%d failure(s)\n", g_failures);
        return 1;
    }

    XrViewConfigurationView sizes[4]{{XR_TYPE_VIEW_CONFIGURATION_VIEW}, {XR_TYPE_VIEW_CONFIGURATION_VIEW}, {XR_TYPE_VIEW_CONFIGURATION_VIEW}, {XR_TYPE_VIEW_CONFIGURATION_VIEW}};
    uint32_t sizeCount = 0;
    Check(XR_SUCCEEDED(enumerateViews(instance, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO, 4, &sizeCount, sizes)) && sizeCount == 4,
          "four views are described");
    printf("  note  wide view %ux%u, focus view %ux%u\n", sizes[0].recommendedImageRectWidth, sizes[0].recommendedImageRectHeight,
           sizes[2].recommendedImageRectWidth, sizes[2].recommendedImageRectHeight);

    XrReferenceSpaceCreateInfo spaceInfo{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    spaceInfo.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    spaceInfo.poseInReferenceSpace.orientation.w = 1;
    XrSpace space = XR_NULL_HANDLE;
    createSpace(session, &spaceInfo, &space);

    // ---- the game's swapchains: the wide views blue (left) and green (right), the focus views red and yellow.
    // One swapchain per view, or (arrays) one for the wide pair and one for the focus pair, an eye per slice.
    const float colors[4][4] = {{0, 0, 1, 1}, {0, 1, 0, 1}, {1, 0, 0, 1}, {1, 1, 0, 1}};
    XrSwapchain swapchains[4]{};
    uint32_t slices[4]{};
    for (int view = 0; view < 4; view++) {
        if (arrays && (view % 2) == 1) {
            swapchains[view] = swapchains[view - 1];
            slices[view] = 1;
            continue;
        }
        XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        info.format = g_vulkan ? FormatVulkan : Format;
        info.sampleCount = 1;
        info.width = sizes[view].recommendedImageRectWidth;
        info.height = sizes[view].recommendedImageRectHeight;
        info.faceCount = 1;
        info.arraySize = arrays ? 2 : 1;
        info.mipCount = 1;
        memcpy(g_fill[0], colors[view], sizeof(g_fill[0]));
        if (arrays) memcpy(g_fill[1], colors[view + 1], sizeof(g_fill[1]));
        Check(XR_SUCCEEDED(createSwapchain(session, &info, &swapchains[view])), "a swapchain is made");
    }
    memset(g_fill, 0, sizeof(g_fill)); // The layer's own swapchains start black.
    if (g_vulkan) {
        bool readable = true;
        for (int view = 0; view < 4; view++) readable = readable && (Of(swapchains[view]).info.usageFlags & XR_SWAPCHAIN_USAGE_TRANSFER_SRC_BIT);
        Check(readable, "the layer has the game's Vulkan swapchains made readable for its copy");
    }

    // ---- frames
    XrView views[4]{{XR_TYPE_VIEW}, {XR_TYPE_VIEW}, {XR_TYPE_VIEW}, {XR_TYPE_VIEW}};
    bool framesOk = true;

    // ---- the picture the runtime was given: without the diagnostics (frame 2), then with them (the last frame)
    const auto checkPicture = [&](bool diagnostics) {
    if (g_submitted.size() == 2) {
        for (int eye = 0; eye < 2; eye++) {
            FakeSwapchain& output = Of(g_submitted[eye].subImage.swapchain);
            Check(output.released >= 0 && output.acquired < 0, "the output image was handed back to the runtime");
            if (output.released < 0) continue;
            const Picture picture = Read(output, output.released);
            if (picture.pixels.empty()) {
                Check(false, "the output image can be read");
                continue;
            }
            SavePng(std::wstring(folder.begin(), folder.end()) + L"\\" + std::wstring(name.begin(), name.end()) + std::wstring(diagnostics ? L"-diagnostics" : L"") + (eye == 0 ? L"-left.png" : L"-right.png"), picture);
            printf("  note  %s eye: output %ux%u\n", eye == 0 ? "left" : "right", picture.width, picture.height);

            // Where the focus view lies in the full view, by the tangents of their angles.
            const XrFovf full = g_submitted[eye].fov, focus = views[2 + eye].fov;
            const float u = (tanf((focus.angleLeft + focus.angleRight) / 2) - tanf(full.angleLeft)) / (tanf(full.angleRight) - tanf(full.angleLeft));
            const float v = (tanf(full.angleUp) - tanf((focus.angleUp + focus.angleDown) / 2)) / (tanf(full.angleUp) - tanf(full.angleDown));
            if (diagnostics) {
                // The wide view is tinted blue (the right eye's is green: blue shows up).
                if (eye == 1) {
                    const uint8_t* tinted = picture.at(0.03f, 0.03f);
                    printf("  note  the wide view with the diagnostics: %d,%d,%d\n", tinted[0], tinted[1], tinted[2]);
                    Check(tinted[2] > 60 && tinted[1] > 200, "settings changed while the game runs take effect (the diagnostics show)");
                }
                continue;
            }
            const uint8_t* center = picture.at(u, v);
            const uint8_t* corner = picture.at(0.03f, 0.03f);
            const uint8_t* otherCorner = picture.at(0.97f, 0.97f);
            printf("  note  focus centre at %.3f, %.3f: %d,%d,%d   corners: %d,%d,%d and %d,%d,%d\n", u, v, center[0], center[1], center[2], corner[0],
                   corner[1], corner[2], otherCorner[0], otherCorner[1], otherCorner[2]);
            Check(eye == 0 ? Near(center, 255, 0, 0) : Near(center, 255, 255, 0), "the focus view's colour is in the middle of the focus area");
            Check(eye == 0 ? Near(corner, 0, 0, 255) : Near(corner, 0, 255, 0), "the wide view's colour is in the corner");
            Check(eye == 0 ? Near(otherCorner, 0, 0, 255) : Near(otherCorner, 0, 255, 0), "and in the opposite corner");

            // Just inside the focus view's left edge (1% of its width), the focus view has faded out to nearly nothing.
            const float left = (tanf(focus.angleLeft) - tanf(full.angleLeft)) / (tanf(full.angleRight) - tanf(full.angleLeft));
            const float width = (tanf(focus.angleRight) - tanf(focus.angleLeft)) / (tanf(full.angleRight) - tanf(full.angleLeft));
            const uint8_t* edge = picture.at(left + 0.01f * width, v);
            printf("  note  just inside the focus view's edge: %d,%d,%d\n", edge[0], edge[1], edge[2]);
            Check(edge[0] < 64, "the focus view fades out to nothing at its edge");
        }
    }
    };

    for (int frame = 0; frame < 7; frame++) {
        if (frame == 3) {
            // Switch the diagnostics on while the "game" runs.
            FILE* file = nullptr;
            if (fopen_s(&file, settings.c_str(), "w") == 0) {
                fputs("show_diagnostics=1\n", file);
                fclose(file);
            }
        }
        XrFrameState frameState{XR_TYPE_FRAME_STATE};
        framesOk = framesOk && XR_SUCCEEDED(waitFrame(session, nullptr, &frameState));
        framesOk = framesOk && XR_SUCCEEDED(beginFrame(session, nullptr));
        XrViewLocateInfo locateInfo{XR_TYPE_VIEW_LOCATE_INFO};
        locateInfo.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO;
        locateInfo.displayTime = frameState.predictedDisplayTime;
        locateInfo.space = space;
        XrViewState viewState{XR_TYPE_VIEW_STATE};
        uint32_t viewCount = 0;
        framesOk = framesOk && XR_SUCCEEDED(locateViews(session, &locateInfo, &viewState, 4, &viewCount, views)) && viewCount == 4;
        for (int view = 0; view < 4; view++) {
            if (arrays && (view % 2) == 1) continue;
            uint32_t index = 0;
            XrSwapchainImageWaitInfo waitInfo{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
            waitInfo.timeout = XR_INFINITE_DURATION;
            framesOk = framesOk && XR_SUCCEEDED(acquire(swapchains[view], nullptr, &index));
            framesOk = framesOk && XR_SUCCEEDED(wait(swapchains[view], &waitInfo));
            framesOk = framesOk && XR_SUCCEEDED(release(swapchains[view], nullptr));
        }
        XrCompositionLayerProjectionView projected[4]{};
        for (int view = 0; view < 4; view++) {
            projected[view].type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
            projected[view].pose = views[view].pose;
            projected[view].fov = views[view].fov;
            projected[view].subImage.swapchain = swapchains[view];
            projected[view].subImage.imageArrayIndex = slices[view];
            projected[view].subImage.imageRect.extent = {int32_t(sizes[view].recommendedImageRectWidth), int32_t(sizes[view].recommendedImageRectHeight)};
        }
        XrCompositionLayerProjection projection{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
        projection.space = space;
        projection.viewCount = 4;
        projection.views = projected;
        const XrCompositionLayerBaseHeader* layers[] = {reinterpret_cast<const XrCompositionLayerBaseHeader*>(&projection)};
        XrFrameEndInfo endInfo{XR_TYPE_FRAME_END_INFO};
        endInfo.displayTime = frameState.predictedDisplayTime;
        endInfo.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        endInfo.layerCount = 1;
        endInfo.layers = layers;
        const XrResult ended = endFrame(session, &endInfo);
        if (XR_FAILED(ended)) printf("    frame %d: xrEndFrame gave %d\n", frame, int(ended));
        framesOk = framesOk && XR_SUCCEEDED(ended);
        framesOk = framesOk && g_submittedLayers == 1 && g_submitted.size() == 2;
        if (frame == 2) checkPicture(false);
    }
    Check(framesOk, "seven frames go through, each handed to the runtime as one stereo layer");

    checkPicture(true);

    // ---- the end: nothing of the runtime's may be held on to
    bool destroyed = true;
    for (int view = 0; view < 4; view++) {
        if (arrays && (view % 2) == 1) continue;
        destroyed = destroyed && XR_SUCCEEDED(destroySwapchain(swapchains[view]));
    }
    Check(destroyed, "the swapchains are destroyed");
    Check(XR_SUCCEEDED(destroySession(session)), "the session is destroyed");
    bool allGone = true, released = true;
    for (auto& entry : g_swapchains) {
        FakeSwapchain& swapchain = entry.second;
        allGone = allGone && swapchain.destroyed;
        for (size_t i = 0; i < swapchain.references.size(); i++) {
            IUnknown* image = g_d3d12 ? static_cast<IUnknown*>(swapchain.images12[i].Get()) : static_cast<IUnknown*>(swapchain.images11[i].Get());
            if (References(image) != swapchain.references[i]) {
                printf("    an image of a %ux%u swapchain: %lu references, %lu when it was made\n", swapchain.info.width, swapchain.info.height,
                       References(image), swapchain.references[i]);
                released = false;
            }
        }
    }
    Check(allGone, "the layer destroyed its own swapchains too");
    if (g_vulkan) {
        bool writable = true;
        for (auto& entry : g_swapchains) {
            const FakeSwapchain& made = entry.second;
            if (made.info.width == StereoWidth && made.info.height == StereoHeight) {
                writable = writable && (made.info.usageFlags & XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT);
            }
            for (size_t i = 0; i < made.imagesVk.size(); i++) {
                g_vk.Fn<PFN_vkDestroyImage>("vkDestroyImage")(g_vk.device, made.imagesVk[i], nullptr);
                g_vk.Fn<PFN_vkFreeMemory>("vkFreeMemory")(g_vk.device, made.memoriesVk[i], nullptr);
            }
        }
        Check(writable, "the layer's full view swapchains are written by a copy (made for it)");
        g_vk.destroy(); // After the session: the layer has let go of the game's device by now.
    }
    Check(released, "no reference to the runtime's images is kept");
    if (g_d3d12) {
        printf("  note  references to the game's device: %lu before the session, %lu with it, now %lu\n", deviceReferencesBefore, deviceReferences,
               References(g_device12.Get()));
        Check(References(g_queue12.Get()) == queueReferencesBefore, "the game's queue is let go of");
        const int errors = DebugErrors();
        if (debugLayer) Check(errors == 0, "the Direct3D 12 debug layer has no complaint");
    }

    printf("%d failure(s)\n", g_failures);
    return g_failures ? 1 : 0;
}
