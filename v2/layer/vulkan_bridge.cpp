// StreamMirror VR - the OpenXR layer: Vulkan games (see vulkan_bridge.h).

#include "pch.h"

#include "../core/log.h"
#include "vulkan_bridge.h"

namespace stream_mirror::layer {

    struct VulkanBridge::Functions {
        PFN_vkGetDeviceProcAddr getDeviceProcAddr = nullptr;
        PFN_vkGetPhysicalDeviceProperties2 getPhysicalDeviceProperties2 = nullptr;
        PFN_vkGetDeviceQueue getDeviceQueue = nullptr;
        PFN_vkCreateCommandPool createCommandPool = nullptr;
        PFN_vkDestroyCommandPool destroyCommandPool = nullptr;
        PFN_vkAllocateCommandBuffers allocateCommandBuffers = nullptr;
        PFN_vkBeginCommandBuffer beginCommandBuffer = nullptr;
        PFN_vkEndCommandBuffer endCommandBuffer = nullptr;
        PFN_vkResetCommandBuffer resetCommandBuffer = nullptr;
        PFN_vkCmdPipelineBarrier cmdPipelineBarrier = nullptr;
        PFN_vkCmdCopyImage cmdCopyImage = nullptr;
        PFN_vkCmdResolveImage cmdResolveImage = nullptr;
        PFN_vkQueueSubmit queueSubmit = nullptr;
        PFN_vkCreateFence createFence = nullptr;
        PFN_vkDestroyFence destroyFence = nullptr;
        PFN_vkWaitForFences waitForFences = nullptr;
        PFN_vkResetFences resetFences = nullptr;
        PFN_vkCreateSemaphore createSemaphore = nullptr;
        PFN_vkDestroySemaphore destroySemaphore = nullptr;
        PFN_vkImportSemaphoreWin32HandleKHR importSemaphoreWin32Handle = nullptr;
        PFN_vkCreateImage createImage = nullptr;
        PFN_vkDestroyImage destroyImage = nullptr;
        PFN_vkGetImageMemoryRequirements2 getImageMemoryRequirements2 = nullptr;
        PFN_vkGetMemoryWin32HandlePropertiesKHR getMemoryWin32HandleProperties = nullptr;
        PFN_vkAllocateMemory allocateMemory = nullptr;
        PFN_vkFreeMemory freeMemory = nullptr;
        PFN_vkBindImageMemory bindImageMemory = nullptr;
    };

    namespace {
        // The Vulkan loader the game has loaded (every graphics driver brings it).
        HMODULE VulkanLoader() {
            HMODULE loader = GetModuleHandleW(L"vulkan-1.dll");
            return loader ? loader : LoadLibraryW(L"vulkan-1.dll");
        }

        template <typename Function>
        Function LoaderExport(const char* name) {
            const HMODULE loader = VulkanLoader();
            return loader ? reinterpret_cast<Function>(GetProcAddress(loader, name)) : nullptr;
        }

        bool Has(const std::vector<VkExtensionProperties>& available, const char* name) {
            for (const VkExtensionProperties& entry : available) {
                if (strcmp(entry.extensionName, name) == 0) return true;
            }
            return false;
        }

        const char* const WantedInstance[] = {
            VK_KHR_GET_PHYSICAL_DEVICE_PROPERTIES_2_EXTENSION_NAME, // The GPU's LUID, to make the D3D11 device on it.
            VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME,
            VK_KHR_EXTERNAL_SEMAPHORE_CAPABILITIES_EXTENSION_NAME,
        };
        const char* const WantedDevice[] = {
            VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME,
            VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME, // The shared texture.
            VK_KHR_EXTERNAL_SEMAPHORE_EXTENSION_NAME,
            VK_KHR_EXTERNAL_SEMAPHORE_WIN32_EXTENSION_NAME, // The shared fence.
            VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME,
            VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME,
        };

        VkImageMemoryBarrier Barrier(VkImage image, uint32_t layers, VkImageLayout from, VkImageLayout to, VkAccessFlags srcAccess,
                                     VkAccessFlags dstAccess, uint32_t srcFamily = VK_QUEUE_FAMILY_IGNORED, uint32_t dstFamily = VK_QUEUE_FAMILY_IGNORED) {
            VkImageMemoryBarrier barrier{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
            barrier.srcAccessMask = srcAccess;
            barrier.dstAccessMask = dstAccess;
            barrier.oldLayout = from;
            barrier.newLayout = to;
            barrier.srcQueueFamilyIndex = srcFamily;
            barrier.dstQueueFamilyIndex = dstFamily;
            barrier.image = image;
            barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, layers};
            return barrier;
        }
    } // namespace

    VulkanBridge::VulkanBridge() = default;

    VulkanBridge::~VulkanBridge() {
        stop();
    }

    std::vector<const char*> VulkanBridge::instanceExtensions() {
        std::vector<const char*> wanted;
        const auto enumerate = LoaderExport<PFN_vkEnumerateInstanceExtensionProperties>("vkEnumerateInstanceExtensionProperties");
        uint32_t count = 0;
        if (!enumerate || enumerate(nullptr, &count, nullptr) != VK_SUCCESS) return wanted;
        std::vector<VkExtensionProperties> available(count);
        if (enumerate(nullptr, &count, available.data()) != VK_SUCCESS) return wanted;
        for (const char* name : WantedInstance) {
            if (Has(available, name)) wanted.push_back(name);
        }
        return wanted;
    }

    std::vector<const char*> VulkanBridge::deviceExtensions(VkPhysicalDevice physicalDevice) {
        std::vector<const char*> wanted(std::begin(WantedDevice), std::end(WantedDevice));
        if (physicalDevice == VK_NULL_HANDLE) return wanted; // Not known yet: every Windows driver has these.
        const auto enumerate = LoaderExport<PFN_vkEnumerateDeviceExtensionProperties>("vkEnumerateDeviceExtensionProperties");
        uint32_t count = 0;
        if (!enumerate || enumerate(physicalDevice, nullptr, &count, nullptr) != VK_SUCCESS) return wanted;
        std::vector<VkExtensionProperties> available(count);
        if (enumerate(physicalDevice, nullptr, &count, available.data()) != VK_SUCCESS) return wanted;
        wanted.erase(std::remove_if(wanted.begin(), wanted.end(), [&](const char* name) { return !Has(available, name); }), wanted.end());
        return wanted;
    }

    DXGI_FORMAT VulkanBridge::DxgiFormat(VkFormat format) {
        switch (format) {
        case VK_FORMAT_R8G8B8A8_UNORM: return DXGI_FORMAT_R8G8B8A8_UNORM;
        case VK_FORMAT_R8G8B8A8_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        case VK_FORMAT_B8G8R8A8_UNORM: return DXGI_FORMAT_B8G8R8A8_UNORM;
        case VK_FORMAT_B8G8R8A8_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        case VK_FORMAT_A2B10G10R10_UNORM_PACK32: return DXGI_FORMAT_R10G10B10A2_UNORM;
        case VK_FORMAT_R16G16B16A16_SFLOAT: return DXGI_FORMAT_R16G16B16A16_FLOAT;
        case VK_FORMAT_B10G11R11_UFLOAT_PACK32: return DXGI_FORMAT_R11G11B10_FLOAT;
        default: return DXGI_FORMAT_UNKNOWN;
        }
    }

    bool VulkanBridge::loadFunctions(VkInstance instance) {
        const auto getInstanceProcAddr = LoaderExport<PFN_vkGetInstanceProcAddr>("vkGetInstanceProcAddr");
        if (!getInstanceProcAddr) return false;
        auto vk = std::make_unique<Functions>();
        vk->getDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(getInstanceProcAddr(instance, "vkGetDeviceProcAddr"));
        vk->getPhysicalDeviceProperties2 = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(getInstanceProcAddr(instance, "vkGetPhysicalDeviceProperties2"));
        if (!vk->getPhysicalDeviceProperties2) {
            vk->getPhysicalDeviceProperties2 = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(getInstanceProcAddr(instance, "vkGetPhysicalDeviceProperties2KHR"));
        }
        if (!vk->getDeviceProcAddr) return false;
        bool complete = true;
        const auto device = [&](const char* name, const char* alias = nullptr) {
            PFN_vkVoidFunction function = vk->getDeviceProcAddr(_device, name);
            if (!function && alias) function = vk->getDeviceProcAddr(_device, alias);
            if (!function) {
                Log("vulkan: the game's device lacks %s", name);
                complete = false;
            }
            return function;
        };
#define VK_DEVICE(member, name) vk->member = reinterpret_cast<PFN_##name>(device(#name))
        VK_DEVICE(getDeviceQueue, vkGetDeviceQueue);
        VK_DEVICE(createCommandPool, vkCreateCommandPool);
        VK_DEVICE(destroyCommandPool, vkDestroyCommandPool);
        VK_DEVICE(allocateCommandBuffers, vkAllocateCommandBuffers);
        VK_DEVICE(beginCommandBuffer, vkBeginCommandBuffer);
        VK_DEVICE(endCommandBuffer, vkEndCommandBuffer);
        VK_DEVICE(resetCommandBuffer, vkResetCommandBuffer);
        VK_DEVICE(cmdPipelineBarrier, vkCmdPipelineBarrier);
        VK_DEVICE(cmdCopyImage, vkCmdCopyImage);
        VK_DEVICE(cmdResolveImage, vkCmdResolveImage);
        VK_DEVICE(queueSubmit, vkQueueSubmit);
        VK_DEVICE(createFence, vkCreateFence);
        VK_DEVICE(destroyFence, vkDestroyFence);
        VK_DEVICE(waitForFences, vkWaitForFences);
        VK_DEVICE(resetFences, vkResetFences);
        VK_DEVICE(createSemaphore, vkCreateSemaphore);
        VK_DEVICE(destroySemaphore, vkDestroySemaphore);
        VK_DEVICE(importSemaphoreWin32Handle, vkImportSemaphoreWin32HandleKHR);
        VK_DEVICE(createImage, vkCreateImage);
        VK_DEVICE(destroyImage, vkDestroyImage);
        VK_DEVICE(getMemoryWin32HandleProperties, vkGetMemoryWin32HandlePropertiesKHR);
        VK_DEVICE(allocateMemory, vkAllocateMemory);
        VK_DEVICE(freeMemory, vkFreeMemory);
        VK_DEVICE(bindImageMemory, vkBindImageMemory);
#undef VK_DEVICE
        vk->getImageMemoryRequirements2 = reinterpret_cast<PFN_vkGetImageMemoryRequirements2>(device("vkGetImageMemoryRequirements2", "vkGetImageMemoryRequirements2KHR"));
        if (!complete) return false;
        _vk = std::move(vk);
        return true;
    }

    bool VulkanBridge::start(const XrGraphicsBindingVulkanKHR& binding) {
        stop();
        _instance = binding.instance;
        _physicalDevice = binding.physicalDevice;
        _device = binding.device;
        _queueFamily = binding.queueFamilyIndex;
        const auto fail = [this](const char* why, long code = 0) {
            Log("vulkan: %s (%ld) - no mirror for this game", why, code);
            stop();
            return false;
        };
        if (!_device || !_physicalDevice || !loadFunctions(_instance)) return fail("the game's Vulkan device cannot be shared (an extension is missing)");

        // Our Direct3D 11 device goes on the game's GPU, which Vulkan names by its LUID.
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        if (_vk->getPhysicalDeviceProperties2) {
            VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
            VkPhysicalDeviceProperties2 properties{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &id};
            _vk->getPhysicalDeviceProperties2(_physicalDevice, &properties);
            Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
            if (id.deviceLUIDValid && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
                Microsoft::WRL::ComPtr<IDXGIAdapter1> candidate;
                for (UINT i = 0; factory->EnumAdapters1(i, &candidate) != DXGI_ERROR_NOT_FOUND; i++) {
                    DXGI_ADAPTER_DESC1 desc{};
                    if (SUCCEEDED(candidate->GetDesc1(&desc)) && memcmp(&desc.AdapterLuid, id.deviceLUID, sizeof(LUID)) == 0) {
                        adapter = candidate;
                        break;
                    }
                }
            }
            Log("vulkan: the game renders on \"%s\"%s", properties.properties.deviceName, adapter ? "" : " - its adapter was not found, the default one is used");
        }
        const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        Microsoft::WRL::ComPtr<ID3D11Device> device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
        HRESULT made = D3D11CreateDevice(adapter.Get(), adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                         levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, nullptr, &context);
        if (FAILED(made) || FAILED(device.As(&_d3d)) || FAILED(context.As(&_context))) return fail("no Direct3D 11 device on the game's GPU", made);

        _vk->getDeviceQueue(_device, binding.queueFamilyIndex, binding.queueIndex, &_queue);
        VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT | VK_COMMAND_POOL_CREATE_TRANSIENT_BIT;
        poolInfo.queueFamilyIndex = _queueFamily;
        if (!_queue || _vk->createCommandPool(_device, &poolInfo, nullptr, &_pool) != VK_SUCCESS) return fail("no command pool on the game's queue");
        for (Slot& slot : _slots) {
            VkCommandBufferAllocateInfo allocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
            allocate.commandPool = _pool;
            allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            allocate.commandBufferCount = 1;
            VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
            fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
            if (_vk->allocateCommandBuffers(_device, &allocate, &slot.commands) != VK_SUCCESS ||
                _vk->createFence(_device, &fenceInfo, nullptr, &slot.done) != VK_SUCCESS) {
                return fail("no command buffers");
            }
        }

        // The fence both sides share: Vulkan signals "copied", Direct3D signals "read".
        made = _d3d->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&_fence));
        HANDLE shared = nullptr;
        if (FAILED(made) || FAILED(made = _fence->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &shared))) return fail("no shared fence", made);
        bool imported = true;
        for (VkSemaphore* semaphore : {&_waitForRead, &_signalCopied}) {
            VkSemaphoreCreateInfo semaphoreInfo{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
            VkImportSemaphoreWin32HandleInfoKHR import{VK_STRUCTURE_TYPE_IMPORT_SEMAPHORE_WIN32_HANDLE_INFO_KHR};
            import.handleType = VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_D3D12_FENCE_BIT;
            import.handle = shared;
            imported = imported && _vk->createSemaphore(_device, &semaphoreInfo, nullptr, semaphore) == VK_SUCCESS &&
                       (import.semaphore = *semaphore, _vk->importSemaphoreWin32Handle(_device, &import) == VK_SUCCESS);
        }
        CloseHandle(shared); // Importing does not take the handle over.
        if (!imported) return fail("the shared fence could not be imported into Vulkan");
        _fenceValue = _copiedValue = 0;
        Log("vulkan: ready (queue family %u, queue %u)", binding.queueFamilyIndex, binding.queueIndex);
        return true;
    }

    void VulkanBridge::waitIdle() {
        if (!_vk || !_device) return;
        for (Slot& slot : _slots) {
            if (slot.done) _vk->waitForFences(_device, 1, &slot.done, VK_TRUE, 2'000'000'000ull);
        }
    }

    void VulkanBridge::stop() {
        if (_vk && _device) {
            waitIdle();
            for (auto& entry : _shared) {
                if (entry.second.image) _vk->destroyImage(_device, entry.second.image, nullptr);
                if (entry.second.memory) _vk->freeMemory(_device, entry.second.memory, nullptr);
            }
            for (Slot& slot : _slots) {
                if (slot.done) _vk->destroyFence(_device, slot.done, nullptr);
                slot = {};
            }
            if (_pool) _vk->destroyCommandPool(_device, _pool, nullptr); // Frees its command buffers.
            if (_waitForRead) _vk->destroySemaphore(_device, _waitForRead, nullptr);
            if (_signalCopied) _vk->destroySemaphore(_device, _signalCopied, nullptr);
        }
        _shared.clear();
        _pool = VK_NULL_HANDLE;
        _waitForRead = _signalCopied = VK_NULL_HANDLE;
        if (_context) {
            _context->ClearState();
            _context->Flush();
        }
        _fence.Reset();
        _context.Reset();
        _d3d.Reset();
        _vk.reset();
        _queue = VK_NULL_HANDLE;
        _device = VK_NULL_HANDLE;
        _physicalDevice = VK_NULL_HANDLE;
        _instance = VK_NULL_HANDLE;
    }

    ID3D11Texture2D* VulkanBridge::addSwapchain(XrSwapchain swapchain, const VulkanSwapchainInfo& info, std::vector<VkImage> images) {
        if (!active()) return nullptr;
        removeSwapchain(swapchain);
        const DXGI_FORMAT format = DxgiFormat(info.format);
        if (format == DXGI_FORMAT_UNKNOWN || info.width == 0 || info.height == 0) {
            Log("vulkan: swapchain format %d is not shared - that swapchain is not mirrored", int(info.format));
            return nullptr;
        }
        Shared shared;
        shared.info = info;
        shared.gameImages = std::move(images);

        // The texture, made by Direct3D and shared by an NT handle (what Vulkan imports as a D3D11 texture).
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = info.width;
        desc.Height = info.height;
        desc.MipLevels = 1;
        desc.ArraySize = std::max(info.arraySize, 1u);
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        HRESULT made = _d3d->CreateTexture2D(&desc, nullptr, &shared.texture);
        Microsoft::WRL::ComPtr<IDXGIResource1> resource;
        HANDLE handle = nullptr;
        if (FAILED(made) || FAILED(made = shared.texture.As(&resource)) ||
            FAILED(made = resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle))) {
            Log("vulkan: no shared texture %u x %u (0x%08lx) - that swapchain is not mirrored", info.width, info.height, static_cast<unsigned long>(made));
            return nullptr;
        }

        // The same memory as a Vulkan image the copy can write into.
        VkExternalMemoryImageCreateInfo external{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO};
        external.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
        VkImageCreateInfo imageInfo{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO, &external};
        imageInfo.imageType = VK_IMAGE_TYPE_2D;
        imageInfo.format = info.format;
        imageInfo.extent = {info.width, info.height, 1};
        imageInfo.mipLevels = 1;
        imageInfo.arrayLayers = desc.ArraySize;
        imageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
        imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
        imageInfo.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT; // (source: the aids)
        imageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        bool ok = _vk->createImage(_device, &imageInfo, nullptr, &shared.image) == VK_SUCCESS;
        if (ok) {
            VkMemoryDedicatedRequirements dedicated{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_REQUIREMENTS};
            VkMemoryRequirements2 requirements{VK_STRUCTURE_TYPE_MEMORY_REQUIREMENTS_2, &dedicated};
            VkImageMemoryRequirementsInfo2 requirementsInfo{VK_STRUCTURE_TYPE_IMAGE_MEMORY_REQUIREMENTS_INFO_2};
            requirementsInfo.image = shared.image;
            _vk->getImageMemoryRequirements2(_device, &requirementsInfo, &requirements);
            VkMemoryWin32HandlePropertiesKHR handleProperties{VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR};
            _vk->getMemoryWin32HandleProperties(_device, VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT, handle, &handleProperties);
            uint32_t types = requirements.memoryRequirements.memoryTypeBits;
            if (handleProperties.memoryTypeBits & types) types &= handleProperties.memoryTypeBits;
            uint32_t typeIndex = 0;
            while (typeIndex < 32 && !(types & (1u << typeIndex))) typeIndex++;

            VkMemoryDedicatedAllocateInfo dedicatedAllocate{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
            dedicatedAllocate.image = shared.image;
            VkImportMemoryWin32HandleInfoKHR import{VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR, &dedicatedAllocate};
            import.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
            import.handle = handle;
            VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, &import};
            allocate.allocationSize = requirements.memoryRequirements.size;
            allocate.memoryTypeIndex = typeIndex;
            ok = typeIndex < 32 && _vk->allocateMemory(_device, &allocate, nullptr, &shared.memory) == VK_SUCCESS &&
                 _vk->bindImageMemory(_device, shared.image, shared.memory, 0) == VK_SUCCESS;
        }
        CloseHandle(handle); // Importing does not take the handle over.
        if (!ok) {
            if (shared.image) _vk->destroyImage(_device, shared.image, nullptr);
            if (shared.memory) _vk->freeMemory(_device, shared.memory, nullptr);
            Log("vulkan: the shared texture could not be imported - that swapchain is not mirrored");
            return nullptr;
        }
        ID3D11Texture2D* texture = shared.texture.Get();
        Log("vulkan: swapchain %u x %u x %u, format %d, %u samples, %zu images: shared", info.width, info.height, desc.ArraySize, int(info.format),
            info.sampleCount, shared.gameImages.size());
        _shared[swapchain] = std::move(shared);
        return texture;
    }

    void VulkanBridge::removeSwapchain(XrSwapchain swapchain) {
        const auto found = _shared.find(swapchain);
        if (found == _shared.end()) return;
        waitIdle(); // Our copies may still use the image.
        if (found->second.image) _vk->destroyImage(_device, found->second.image, nullptr);
        if (found->second.memory) _vk->freeMemory(_device, found->second.memory, nullptr);
        _context->ClearState();
        _context->Flush();
        _shared.erase(found);
    }

    VulkanBridge::Slot* VulkanBridge::beginCommands() {
        Slot& slot = _slots[_nextSlot];
        if (_vk->waitForFences(_device, 1, &slot.done, VK_TRUE, 1'000'000'000ull) != VK_SUCCESS) {
            Log("vulkan: an earlier copy has not finished after a second - frame skipped");
            return nullptr;
        }
        _vk->resetCommandBuffer(slot.commands, 0);
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        _vk->beginCommandBuffer(slot.commands, &begin);
        return &slot;
    }

    bool VulkanBridge::submit(Slot& slot, uint64_t waitValue, uint64_t signalValue) {
        _vk->endCommandBuffer(slot.commands);
        VkD3D12FenceSubmitInfoKHR fenceValues{VK_STRUCTURE_TYPE_D3D12_FENCE_SUBMIT_INFO_KHR};
        fenceValues.waitSemaphoreValuesCount = 1;
        fenceValues.pWaitSemaphoreValues = &waitValue;
        fenceValues.signalSemaphoreValuesCount = 1;
        fenceValues.pSignalSemaphoreValues = &signalValue;
        const VkPipelineStageFlags waitStage = VK_PIPELINE_STAGE_TRANSFER_BIT;
        VkSubmitInfo info{VK_STRUCTURE_TYPE_SUBMIT_INFO, &fenceValues};
        info.waitSemaphoreCount = 1;
        info.pWaitSemaphores = &_waitForRead;
        info.pWaitDstStageMask = &waitStage;
        info.commandBufferCount = 1;
        info.pCommandBuffers = &slot.commands;
        info.signalSemaphoreCount = 1;
        info.pSignalSemaphores = &_signalCopied;
        _vk->resetFences(_device, 1, &slot.done);
        const VkResult result = _vk->queueSubmit(_queue, 1, &info, slot.done);
        if (result != VK_SUCCESS) {
            Log("vulkan: a copy could not be submitted (%d) - frame skipped", int(result));
            // Leave the slot usable: a fence that was reset but never submitted would block forever.
            VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
            fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
            _vk->destroyFence(_device, slot.done, nullptr);
            _vk->createFence(_device, &fenceInfo, nullptr, &slot.done);
            return false;
        }
        _nextSlot = (_nextSlot + 1) % ARRAYSIZE(_slots);
        return true;
    }

    bool VulkanBridge::copy(const std::vector<std::pair<XrSwapchain, uint32_t>>& released) {
        if (!active()) return false;
        Slot* const begun = beginCommands();
        if (!begun) return false;
        Slot& slot = *begun;

        int copies = 0;
        std::vector<XrSwapchain> done;
        for (const auto& [swapchain, index] : released) {
            if (std::find(done.begin(), done.end(), swapchain) != done.end()) continue;
            done.push_back(swapchain);
            const auto found = _shared.find(swapchain);
            if (found == _shared.end() || index >= found->second.gameImages.size() || !found->second.gameImages[index]) continue;
            Shared& shared = found->second;
            const VkImage game = shared.gameImages[index];
            const uint32_t layers = std::max(shared.info.arraySize, 1u);

            // The game hands its images back as colour attachments (the OpenXR rule for Vulkan); ours comes back from
            // Direct3D's side after the first time.
            VkImageMemoryBarrier before[2] = {
                Barrier(game, layers, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                        VK_ACCESS_TRANSFER_READ_BIT),
                shared.used ? Barrier(shared.image, layers, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
                                      VK_QUEUE_FAMILY_EXTERNAL, _queueFamily)
                            : Barrier(shared.image, layers, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT),
            };
            _vk->cmdPipelineBarrier(slot.commands, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                                    VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 2, before);
            const VkImageSubresourceLayers subresource{VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, layers};
            if (shared.info.sampleCount > 1) {
                VkImageResolve region{subresource, {0, 0, 0}, subresource, {0, 0, 0}, {shared.info.width, shared.info.height, 1}};
                _vk->cmdResolveImage(slot.commands, game, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, shared.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
            } else {
                VkImageCopy region{subresource, {0, 0, 0}, subresource, {0, 0, 0}, {shared.info.width, shared.info.height, 1}};
                _vk->cmdCopyImage(slot.commands, game, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, shared.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
            }
            // The game's image goes back as it came; ours goes over to Direct3D.
            VkImageMemoryBarrier after[2] = {
                Barrier(game, layers, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_ACCESS_TRANSFER_READ_BIT,
                        VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_SHADER_READ_BIT),
                Barrier(shared.image, layers, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT, 0, _queueFamily,
                        VK_QUEUE_FAMILY_EXTERNAL),
            };
            _vk->cmdPipelineBarrier(slot.commands, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 2, after);
            shared.used = true;
            copies++;
        }
        if (copies == 0) {
            _vk->endCommandBuffer(slot.commands); // Never submitted: the slot's fence stays signalled.
            return false;
        }

        // Wait until Direct3D has read the last copy, copy, then tell Direct3D.
        const uint64_t signalValue = _fenceValue + 1;
        if (!submit(slot, _fenceValue, signalValue)) return false;
        _copiedValue = signalValue;
        _context->Wait(_fence.Get(), signalValue);
        return true;
    }

    bool VulkanBridge::copyOut(XrSwapchain swapchain, uint32_t index) {
        if (!active()) return false;
        const auto found = _shared.find(swapchain);
        if (found == _shared.end() || index >= found->second.gameImages.size() || !found->second.gameImages[index]) return false;
        Shared& shared = found->second;
        Slot* const begun = beginCommands();
        if (!begun) return false;
        Slot& slot = *begun;
        const VkImage runtime = shared.gameImages[index];
        const uint32_t layers = std::max(shared.info.arraySize, 1u);

        // Ours comes from Direct3D's side - the first time too: Direct3D drew into it, and keeping what it holds needs a
        // layout that is not "undefined" (the external family's general one is what Direct3D leaves). The runtime's image
        // is a colour attachment between its acquire and release.
        VkImageMemoryBarrier before[2] = {
            Barrier(shared.image, layers, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, 0, VK_ACCESS_TRANSFER_READ_BIT,
                    VK_QUEUE_FAMILY_EXTERNAL, _queueFamily),
            Barrier(runtime, layers, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT),
        };
        _vk->cmdPipelineBarrier(slot.commands, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 2, before);
        const VkImageSubresourceLayers subresource{VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, layers};
        VkImageCopy region{subresource, {0, 0, 0}, subresource, {0, 0, 0}, {shared.info.width, shared.info.height, 1}};
        _vk->cmdCopyImage(slot.commands, shared.image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, runtime, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
        VkImageMemoryBarrier after[2] = {
            Barrier(shared.image, layers, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_READ_BIT, 0, _queueFamily,
                    VK_QUEUE_FAMILY_EXTERNAL),
            Barrier(runtime, layers, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT,
                    VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_SHADER_READ_BIT),
        };
        _vk->cmdPipelineBarrier(slot.commands, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 2, after);
        shared.used = true;

        // After Direct3D's drawing; Direct3D's next drawing after the copy. One fence for both directions: every value
        // is a new, higher one, so the mirror's copies (copy/readDone) go on from where this leaves it.
        const uint64_t drawn = std::max(_fenceValue, _copiedValue) + 1;
        _context->Signal(_fence.Get(), drawn);
        _context->Flush();
        _fenceValue = _copiedValue = drawn;
        if (!submit(slot, drawn, drawn + 1)) return false;
        _fenceValue = _copiedValue = drawn + 1;
        _context->Wait(_fence.Get(), drawn + 1);
        _context->Flush();
        return true;
    }

    void VulkanBridge::readDone() {
        if (!active() || _copiedValue <= _fenceValue) return;
        _fenceValue = _copiedValue + 1;
        _context->Signal(_fence.Get(), _fenceValue);
        _context->Flush();
    }

} // namespace stream_mirror::layer
