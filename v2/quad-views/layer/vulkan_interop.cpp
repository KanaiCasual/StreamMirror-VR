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

#include "pch.h"

#include "layer.h"
#include <log.h>
#include "vulkan_interop.h"

namespace openxr_api_layer {

    using namespace log;

    struct VulkanInterop::Functions {
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
            return std::any_of(available.cbegin(), available.cend(), [&](const VkExtensionProperties& entry) {
                return strcmp(entry.extensionName, name) == 0;
            });
        }

        const char* const WantedInstance[] = {
            VK_KHR_GET_PHYSICAL_DEVICE_PROPERTIES_2_EXTENSION_NAME, // The GPU's LUID, to make the D3D11 device on it.
            VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME,
            VK_KHR_EXTERNAL_SEMAPHORE_CAPABILITIES_EXTENSION_NAME,
        };
        const char* const WantedDevice[] = {
            VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME,
            VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME, // The shared textures.
            VK_KHR_EXTERNAL_SEMAPHORE_EXTENSION_NAME,
            VK_KHR_EXTERNAL_SEMAPHORE_WIN32_EXTENSION_NAME, // The shared fence.
            VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME,
            VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME,
        };

        std::vector<VkExtensionProperties> InstanceExtensionsOffered() {
            const auto enumerate = LoaderExport<PFN_vkEnumerateInstanceExtensionProperties>("vkEnumerateInstanceExtensionProperties");
            uint32_t count = 0;
            if (!enumerate || enumerate(nullptr, &count, nullptr) != VK_SUCCESS) {
                return {};
            }
            std::vector<VkExtensionProperties> available(count);
            if (enumerate(nullptr, &count, available.data()) != VK_SUCCESS) {
                return {};
            }
            return available;
        }

        VkImageMemoryBarrier Barrier(VkImage image,
                                     uint32_t layers,
                                     VkImageLayout from,
                                     VkImageLayout to,
                                     VkAccessFlags srcAccess,
                                     VkAccessFlags dstAccess,
                                     uint32_t srcFamily = VK_QUEUE_FAMILY_IGNORED,
                                     uint32_t dstFamily = VK_QUEUE_FAMILY_IGNORED) {
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

    VulkanInterop::VulkanInterop() = default;

    VulkanInterop::~VulkanInterop() {
        stop();
    }

    std::vector<const char*> VulkanInterop::instanceExtensions() {
        const std::vector<VkExtensionProperties> available = InstanceExtensionsOffered();
        std::vector<const char*> wanted;
        for (const char* name : WantedInstance) {
            if (Has(available, name)) {
                wanted.push_back(name);
            }
        }
        return wanted;
    }

    std::vector<const char*> VulkanInterop::deviceExtensions(VkPhysicalDevice physicalDevice) {
        std::vector<const char*> wanted(std::begin(WantedDevice), std::end(WantedDevice));
        if (physicalDevice == VK_NULL_HANDLE) {
            return wanted; // Not known yet: every Windows driver has these.
        }
        const auto enumerate = LoaderExport<PFN_vkEnumerateDeviceExtensionProperties>("vkEnumerateDeviceExtensionProperties");
        uint32_t count = 0;
        if (!enumerate || enumerate(physicalDevice, nullptr, &count, nullptr) != VK_SUCCESS) {
            return wanted;
        }
        std::vector<VkExtensionProperties> available(count);
        if (enumerate(physicalDevice, nullptr, &count, available.data()) != VK_SUCCESS) {
            return wanted;
        }
        wanted.erase(std::remove_if(wanted.begin(), wanted.end(), [&](const char* name) { return !Has(available, name); }),
                     wanted.end());
        return wanted;
    }

    bool VulkanInterop::Available() {
        const std::vector<VkExtensionProperties> available = InstanceExtensionsOffered();
        return std::all_of(std::cbegin(WantedInstance), std::cend(WantedInstance), [&](const char* name) {
            return Has(available, name);
        });
    }

    DXGI_FORMAT VulkanInterop::DxgiFormat(int64_t format) {
        switch (static_cast<VkFormat>(format)) {
        case VK_FORMAT_R8G8B8A8_UNORM:
            return DXGI_FORMAT_R8G8B8A8_UNORM;
        case VK_FORMAT_R8G8B8A8_SRGB:
            return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        case VK_FORMAT_B8G8R8A8_UNORM:
            return DXGI_FORMAT_B8G8R8A8_UNORM;
        case VK_FORMAT_B8G8R8A8_SRGB:
            return DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
        case VK_FORMAT_A2B10G10R10_UNORM_PACK32:
            return DXGI_FORMAT_R10G10B10A2_UNORM;
        case VK_FORMAT_R16G16B16A16_SFLOAT:
            return DXGI_FORMAT_R16G16B16A16_FLOAT;
        case VK_FORMAT_B10G11R11_UFLOAT_PACK32:
            return DXGI_FORMAT_R11G11B10_FLOAT;
        default:
            return DXGI_FORMAT_UNKNOWN;
        }
    }

    bool VulkanInterop::loadFunctions() {
        const auto getInstanceProcAddr = LoaderExport<PFN_vkGetInstanceProcAddr>("vkGetInstanceProcAddr");
        if (!getInstanceProcAddr) {
            return false;
        }
        auto vk = std::make_unique<Functions>();
        vk->getDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(getInstanceProcAddr(m_instance, "vkGetDeviceProcAddr"));
        vk->getPhysicalDeviceProperties2 = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(
            getInstanceProcAddr(m_instance, "vkGetPhysicalDeviceProperties2"));
        if (!vk->getPhysicalDeviceProperties2) {
            vk->getPhysicalDeviceProperties2 = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(
                getInstanceProcAddr(m_instance, "vkGetPhysicalDeviceProperties2KHR"));
        }
        if (!vk->getDeviceProcAddr) {
            return false;
        }
        bool complete = true;
        const auto device = [&](const char* name, const char* alias = nullptr) {
            PFN_vkVoidFunction function = vk->getDeviceProcAddr(m_device, name);
            if (!function && alias) {
                function = vk->getDeviceProcAddr(m_device, alias);
            }
            if (!function) {
                Log(fmt::format("Vulkan: the game's device lacks {}\n", name));
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
        vk->getImageMemoryRequirements2 = reinterpret_cast<PFN_vkGetImageMemoryRequirements2>(
            device("vkGetImageMemoryRequirements2", "vkGetImageMemoryRequirements2KHR"));
        if (!complete) {
            return false;
        }
        m_vk = std::move(vk);
        return true;
    }

    bool VulkanInterop::start(const XrGraphicsBindingVulkanKHR& binding) {
        stop();
        m_instance = binding.instance;
        m_physicalDevice = binding.physicalDevice;
        m_device = binding.device;
        m_queueFamily = binding.queueFamilyIndex;
        const auto fail = [this](const char* why, long code = 0) {
            ErrorLog(fmt::format("Vulkan: {} ({:#x}): no quad views composition in this game\n", why, code));
            stop();
            return false;
        };
        if (!m_device || !m_physicalDevice || !loadFunctions()) {
            return fail("the game's Vulkan device cannot be shared (an extension is missing)");
        }

        // Our Direct3D 11 device goes on the game's GPU, which Vulkan names by its LUID.
        ComPtr<IDXGIAdapter1> adapter;
        if (m_vk->getPhysicalDeviceProperties2) {
            VkPhysicalDeviceIDProperties id{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES};
            VkPhysicalDeviceProperties2 properties{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2, &id};
            m_vk->getPhysicalDeviceProperties2(m_physicalDevice, &properties);
            ComPtr<IDXGIFactory1> factory;
            if (id.deviceLUIDValid && SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(factory.ReleaseAndGetAddressOf())))) {
                ComPtr<IDXGIAdapter1> candidate;
                for (UINT i = 0; factory->EnumAdapters1(i, candidate.ReleaseAndGetAddressOf()) != DXGI_ERROR_NOT_FOUND; i++) {
                    DXGI_ADAPTER_DESC1 desc{};
                    if (SUCCEEDED(candidate->GetDesc1(&desc)) && memcmp(&desc.AdapterLuid, id.deviceLUID, sizeof(LUID)) == 0) {
                        adapter = candidate;
                        break;
                    }
                }
            }
            Log(fmt::format("Vulkan: the game renders on \"{}\"{}\n",
                            properties.properties.deviceName,
                            adapter ? "" : "; its adapter was not found, the default one is used"));
        }
        const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        HRESULT hr = D3D11CreateDevice(adapter.Get(),
                                       adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
                                       nullptr,
                                       D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                       levels,
                                       ARRAYSIZE(levels),
                                       D3D11_SDK_VERSION,
                                       device.ReleaseAndGetAddressOf(),
                                       nullptr,
                                       context.ReleaseAndGetAddressOf());
        if (FAILED(hr) || FAILED(device.As(&m_d3d)) || FAILED(context.As(&m_context))) {
            return fail("no Direct3D 11 device on the game's GPU", hr);
        }

        m_vk->getDeviceQueue(m_device, binding.queueFamilyIndex, binding.queueIndex, &m_queue);
        VkCommandPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
        poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT | VK_COMMAND_POOL_CREATE_TRANSIENT_BIT;
        poolInfo.queueFamilyIndex = m_queueFamily;
        if (!m_queue || m_vk->createCommandPool(m_device, &poolInfo, nullptr, &m_pool) != VK_SUCCESS) {
            return fail("no command pool on the game's queue");
        }
        for (Slot& slot : m_slots) {
            VkCommandBufferAllocateInfo allocate{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
            allocate.commandPool = m_pool;
            allocate.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            allocate.commandBufferCount = 1;
            VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
            fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
            if (m_vk->allocateCommandBuffers(m_device, &allocate, &slot.commands) != VK_SUCCESS ||
                m_vk->createFence(m_device, &fenceInfo, nullptr, &slot.done) != VK_SUCCESS) {
                return fail("no command buffers");
            }
        }

        // The fence both sides share.
        hr = m_d3d->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(m_fence.ReleaseAndGetAddressOf()));
        HANDLE shared = nullptr;
        if (FAILED(hr) || FAILED(hr = m_fence->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &shared))) {
            return fail("no shared fence", hr);
        }
        bool imported = true;
        for (VkSemaphore* semaphore : {&m_wait, &m_signal}) {
            VkSemaphoreCreateInfo semaphoreInfo{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
            VkImportSemaphoreWin32HandleInfoKHR import{VK_STRUCTURE_TYPE_IMPORT_SEMAPHORE_WIN32_HANDLE_INFO_KHR};
            import.handleType = VK_EXTERNAL_SEMAPHORE_HANDLE_TYPE_D3D12_FENCE_BIT;
            import.handle = shared;
            imported = imported && m_vk->createSemaphore(m_device, &semaphoreInfo, nullptr, semaphore) == VK_SUCCESS &&
                       (import.semaphore = *semaphore, m_vk->importSemaphoreWin32Handle(m_device, &import) == VK_SUCCESS);
        }
        CloseHandle(shared); // Importing does not take the handle over.
        if (!imported) {
            return fail("the shared fence could not be imported into Vulkan");
        }
        m_value = 0;
        Log(fmt::format("Vulkan: sharing with Direct3D 11 is ready (queue family {}, queue {})\n",
                        binding.queueFamilyIndex,
                        binding.queueIndex));
        return true;
    }

    void VulkanInterop::waitIdle() {
        if (!m_vk || !m_device) {
            return;
        }
        for (Slot& slot : m_slots) {
            if (slot.done) {
                m_vk->waitForFences(m_device, 1, &slot.done, VK_TRUE, 2'000'000'000ull);
            }
        }
    }

    void VulkanInterop::stop() {
        if (m_vk && m_device) {
            waitIdle();
            for (auto& entry : m_shared) {
                if (entry.second.image) {
                    m_vk->destroyImage(m_device, entry.second.image, nullptr);
                }
                if (entry.second.memory) {
                    m_vk->freeMemory(m_device, entry.second.memory, nullptr);
                }
            }
            for (Slot& slot : m_slots) {
                if (slot.done) {
                    m_vk->destroyFence(m_device, slot.done, nullptr);
                }
                slot = {};
            }
            if (m_pool) {
                m_vk->destroyCommandPool(m_device, m_pool, nullptr); // Frees its command buffers.
            }
            if (m_wait) {
                m_vk->destroySemaphore(m_device, m_wait, nullptr);
            }
            if (m_signal) {
                m_vk->destroySemaphore(m_device, m_signal, nullptr);
            }
        }
        m_shared.clear();
        m_pool = VK_NULL_HANDLE;
        m_wait = m_signal = VK_NULL_HANDLE;
        if (m_context) {
            m_context->ClearState();
            m_context->Flush();
        }
        m_fence.Reset();
        m_context.Reset();
        m_d3d.Reset();
        m_vk.reset();
        m_queue = VK_NULL_HANDLE;
        m_device = VK_NULL_HANDLE;
        m_physicalDevice = VK_NULL_HANDLE;
        m_instance = VK_NULL_HANDLE;
    }

    ID3D11Texture2D* VulkanInterop::addSwapchain(XrSwapchain swapchain,
                                                 const XrSwapchainCreateInfo& info,
                                                 std::vector<VkImage> images) {
        if (!active()) {
            return nullptr;
        }
        removeSwapchain(swapchain);
        const DXGI_FORMAT format = DxgiFormat(info.format);
        if (format == DXGI_FORMAT_UNKNOWN || info.width == 0 || info.height == 0) {
            ErrorLog(fmt::format("Vulkan: swapchain format {} cannot be shared with Direct3D\n", info.format));
            return nullptr;
        }
        Shared shared;
        shared.info = info;
        shared.images = std::move(images);

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
        HRESULT hr = m_d3d->CreateTexture2D(&desc, nullptr, shared.texture.ReleaseAndGetAddressOf());
        ComPtr<IDXGIResource1> resource;
        HANDLE handle = nullptr;
        if (FAILED(hr) || FAILED(hr = shared.texture.As(&resource)) ||
            FAILED(hr = resource->CreateSharedHandle(
                       nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle))) {
            ErrorLog(fmt::format("Vulkan: no shared texture {}x{} ({:#x})\n", info.width, info.height, (uint32_t)hr));
            return nullptr;
        }

        // The same memory as a Vulkan image the copies can write into and read from.
        VkExternalMemoryImageCreateInfo external{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO};
        external.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
        VkImageCreateInfo imageInfo{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO, &external};
        imageInfo.imageType = VK_IMAGE_TYPE_2D;
        imageInfo.format = static_cast<VkFormat>(info.format);
        imageInfo.extent = {info.width, info.height, 1};
        imageInfo.mipLevels = 1;
        imageInfo.arrayLayers = desc.ArraySize;
        imageInfo.samples = VK_SAMPLE_COUNT_1_BIT;
        imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
        imageInfo.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
        imageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        bool ok = m_vk->createImage(m_device, &imageInfo, nullptr, &shared.image) == VK_SUCCESS;
        if (ok) {
            VkMemoryDedicatedRequirements dedicated{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_REQUIREMENTS};
            VkMemoryRequirements2 requirements{VK_STRUCTURE_TYPE_MEMORY_REQUIREMENTS_2, &dedicated};
            VkImageMemoryRequirementsInfo2 requirementsInfo{VK_STRUCTURE_TYPE_IMAGE_MEMORY_REQUIREMENTS_INFO_2};
            requirementsInfo.image = shared.image;
            m_vk->getImageMemoryRequirements2(m_device, &requirementsInfo, &requirements);
            VkMemoryWin32HandlePropertiesKHR handleProperties{VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR};
            m_vk->getMemoryWin32HandleProperties(m_device, VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT, handle, &handleProperties);
            uint32_t types = requirements.memoryRequirements.memoryTypeBits;
            if (handleProperties.memoryTypeBits & types) {
                types &= handleProperties.memoryTypeBits;
            }
            uint32_t typeIndex = 0;
            while (typeIndex < 32 && !(types & (1u << typeIndex))) {
                typeIndex++;
            }
            VkMemoryDedicatedAllocateInfo dedicatedAllocate{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO};
            dedicatedAllocate.image = shared.image;
            VkImportMemoryWin32HandleInfoKHR import{VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR, &dedicatedAllocate};
            import.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
            import.handle = handle;
            VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, &import};
            allocate.allocationSize = requirements.memoryRequirements.size;
            allocate.memoryTypeIndex = typeIndex;
            ok = typeIndex < 32 && m_vk->allocateMemory(m_device, &allocate, nullptr, &shared.memory) == VK_SUCCESS &&
                 m_vk->bindImageMemory(m_device, shared.image, shared.memory, 0) == VK_SUCCESS;
        }
        CloseHandle(handle); // Importing does not take the handle over.
        if (!ok) {
            if (shared.image) {
                m_vk->destroyImage(m_device, shared.image, nullptr);
            }
            if (shared.memory) {
                m_vk->freeMemory(m_device, shared.memory, nullptr);
            }
            ErrorLog("Vulkan: the shared texture could not be imported into Vulkan\n");
            return nullptr;
        }
        ID3D11Texture2D* texture = shared.texture.Get();
        Log(fmt::format("Vulkan: swapchain {}x{}x{}, format {}, {} sample(s), {} image(s): shared with Direct3D\n",
                        info.width,
                        info.height,
                        desc.ArraySize,
                        info.format,
                        info.sampleCount,
                        shared.images.size()));
        m_shared[swapchain] = std::move(shared);
        return texture;
    }

    void VulkanInterop::removeSwapchain(XrSwapchain swapchain) {
        const auto found = m_shared.find(swapchain);
        if (found == m_shared.end()) {
            return;
        }
        waitIdle(); // Our copies may still use the image.
        if (found->second.image) {
            m_vk->destroyImage(m_device, found->second.image, nullptr);
        }
        if (found->second.memory) {
            m_vk->freeMemory(m_device, found->second.memory, nullptr);
        }
        m_context->ClearState();
        m_context->Flush();
        m_shared.erase(found);
    }

    VulkanInterop::Slot* VulkanInterop::beginCommands() {
        Slot& slot = m_slots[m_nextSlot];
        if (m_vk->waitForFences(m_device, 1, &slot.done, VK_TRUE, 1'000'000'000ull) != VK_SUCCESS) {
            ErrorLog("Vulkan: an earlier copy has not finished after a second\n");
            return nullptr;
        }
        m_vk->resetCommandBuffer(slot.commands, 0);
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        m_vk->beginCommandBuffer(slot.commands, &begin);
        return &slot;
    }

    bool VulkanInterop::submit(Slot& slot) {
        m_vk->endCommandBuffer(slot.commands);
        const uint64_t waitValue = m_value;
        const uint64_t signalValue = m_value + 1;
        VkD3D12FenceSubmitInfoKHR fenceValues{VK_STRUCTURE_TYPE_D3D12_FENCE_SUBMIT_INFO_KHR};
        fenceValues.waitSemaphoreValuesCount = 1;
        fenceValues.pWaitSemaphoreValues = &waitValue;
        fenceValues.signalSemaphoreValuesCount = 1;
        fenceValues.pSignalSemaphoreValues = &signalValue;
        const VkPipelineStageFlags waitStage = VK_PIPELINE_STAGE_TRANSFER_BIT;
        VkSubmitInfo submitInfo{VK_STRUCTURE_TYPE_SUBMIT_INFO, &fenceValues};
        submitInfo.waitSemaphoreCount = 1;
        submitInfo.pWaitSemaphores = &m_wait;
        submitInfo.pWaitDstStageMask = &waitStage;
        submitInfo.commandBufferCount = 1;
        submitInfo.pCommandBuffers = &slot.commands;
        submitInfo.signalSemaphoreCount = 1;
        submitInfo.pSignalSemaphores = &m_signal;
        m_vk->resetFences(m_device, 1, &slot.done);
        const VkResult result = m_vk->queueSubmit(m_queue, 1, &submitInfo, slot.done);
        if (result != VK_SUCCESS) {
            ErrorLog(fmt::format("Vulkan: a copy could not be submitted ({})\n", (int)result));
            // Leave the slot usable: a fence that was reset but never submitted would block forever.
            VkFenceCreateInfo fenceInfo{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
            fenceInfo.flags = VK_FENCE_CREATE_SIGNALED_BIT;
            m_vk->destroyFence(m_device, slot.done, nullptr);
            m_vk->createFence(m_device, &fenceInfo, nullptr, &slot.done);
            return false;
        }
        m_nextSlot = (m_nextSlot + 1) % ARRAYSIZE(m_slots);
        m_value = signalValue;
        return true;
    }

    bool VulkanInterop::copyIn(const std::vector<std::pair<XrSwapchain, uint32_t>>& images, uint64_t frame) {
        if (!active()) {
            return false;
        }
        Slot* slot = beginCommands();
        if (!slot) {
            return false;
        }
        int copies = 0;
        for (const auto& [swapchain, index] : images) {
            const auto found = m_shared.find(swapchain);
            if (found == m_shared.end() || index >= found->second.images.size() || !found->second.images[index]) {
                continue;
            }
            Shared& shared = found->second;
            if (shared.copiedFrame == frame && shared.copiedIndex == index) {
                continue; // Both eyes in one swapchain: copied once.
            }
            const VkImage game = shared.images[index];
            const uint32_t layers = std::max(shared.info.arraySize, 1u);

            // The game hands its images back as colour attachments (the OpenXR rule for Vulkan); ours comes back from
            // Direct3D's side after the first time.
            VkImageMemoryBarrier before[2] = {
                Barrier(game,
                        layers,
                        VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                        VK_ACCESS_TRANSFER_READ_BIT),
                shared.used ? Barrier(shared.image,
                                      layers,
                                      VK_IMAGE_LAYOUT_GENERAL,
                                      VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                      0,
                                      VK_ACCESS_TRANSFER_WRITE_BIT,
                                      VK_QUEUE_FAMILY_EXTERNAL,
                                      m_queueFamily)
                            : Barrier(shared.image,
                                      layers,
                                      VK_IMAGE_LAYOUT_UNDEFINED,
                                      VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                      0,
                                      VK_ACCESS_TRANSFER_WRITE_BIT),
            };
            m_vk->cmdPipelineBarrier(slot->commands,
                                     VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                                     VK_PIPELINE_STAGE_TRANSFER_BIT,
                                     0,
                                     0,
                                     nullptr,
                                     0,
                                     nullptr,
                                     2,
                                     before);
            const VkImageSubresourceLayers subresource{VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, layers};
            if (shared.info.sampleCount > 1) {
                VkImageResolve region{subresource, {0, 0, 0}, subresource, {0, 0, 0}, {shared.info.width, shared.info.height, 1}};
                m_vk->cmdResolveImage(slot->commands,
                                      game,
                                      VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                      shared.image,
                                      VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                      1,
                                      &region);
            } else {
                VkImageCopy region{subresource, {0, 0, 0}, subresource, {0, 0, 0}, {shared.info.width, shared.info.height, 1}};
                m_vk->cmdCopyImage(slot->commands,
                                   game,
                                   VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                                   shared.image,
                                   VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                                   1,
                                   &region);
            }
            // The game's image goes back as it came; ours goes over to Direct3D.
            VkImageMemoryBarrier after[2] = {
                Barrier(game,
                        layers,
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                        VK_ACCESS_TRANSFER_READ_BIT,
                        VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_SHADER_READ_BIT),
                Barrier(shared.image,
                        layers,
                        VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                        VK_IMAGE_LAYOUT_GENERAL,
                        VK_ACCESS_TRANSFER_WRITE_BIT,
                        0,
                        m_queueFamily,
                        VK_QUEUE_FAMILY_EXTERNAL),
            };
            m_vk->cmdPipelineBarrier(slot->commands,
                                     VK_PIPELINE_STAGE_TRANSFER_BIT,
                                     VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                                     0,
                                     0,
                                     nullptr,
                                     0,
                                     nullptr,
                                     2,
                                     after);
            shared.used = true;
            shared.copiedFrame = frame;
            shared.copiedIndex = index;
            copies++;
        }
        if (copies == 0) {
            m_vk->endCommandBuffer(slot->commands);
            return true; // Nothing new to copy: what the textures hold is this frame's.
        }

        // Direct3D is done with what it did before, the copies go after that, and Direct3D's next work after them.
        m_context->Signal(m_fence.Get(), ++m_value);
        m_context->Flush();
        if (!submit(*slot)) {
            return false;
        }
        m_context->Wait(m_fence.Get(), m_value);
        return true;
    }

    bool VulkanInterop::copyOut(XrSwapchain swapchain, uint32_t index) {
        if (!active()) {
            return false;
        }
        const auto found = m_shared.find(swapchain);
        if (found == m_shared.end() || index >= found->second.images.size() || !found->second.images[index]) {
            return false;
        }
        Shared& shared = found->second;
        Slot* slot = beginCommands();
        if (!slot) {
            return false;
        }
        const VkImage runtime = shared.images[index];
        const uint32_t layers = std::max(shared.info.arraySize, 1u);

        // Ours comes from Direct3D's side (the first time from nowhere: Direct3D drew into it all the same, and keeping
        // what it holds needs a layout that is not "undefined" - the external family's general one is what Direct3D
        // leaves). The runtime's image is a colour attachment between acquire and release.
        VkImageMemoryBarrier before[2] = {
            Barrier(shared.image,
                    layers,
                    VK_IMAGE_LAYOUT_GENERAL,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    0,
                    VK_ACCESS_TRANSFER_READ_BIT,
                    VK_QUEUE_FAMILY_EXTERNAL,
                    m_queueFamily),
            Barrier(runtime,
                    layers,
                    VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                    VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    0,
                    VK_ACCESS_TRANSFER_WRITE_BIT),
        };
        m_vk->cmdPipelineBarrier(slot->commands,
                                 VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                                 VK_PIPELINE_STAGE_TRANSFER_BIT,
                                 0,
                                 0,
                                 nullptr,
                                 0,
                                 nullptr,
                                 2,
                                 before);
        const VkImageSubresourceLayers subresource{VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, layers};
        VkImageCopy region{subresource, {0, 0, 0}, subresource, {0, 0, 0}, {shared.info.width, shared.info.height, 1}};
        m_vk->cmdCopyImage(slot->commands,
                           shared.image,
                           VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                           runtime,
                           VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                           1,
                           &region);
        VkImageMemoryBarrier after[2] = {
            Barrier(shared.image,
                    layers,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    VK_IMAGE_LAYOUT_GENERAL,
                    VK_ACCESS_TRANSFER_READ_BIT,
                    0,
                    m_queueFamily,
                    VK_QUEUE_FAMILY_EXTERNAL),
            Barrier(runtime,
                    layers,
                    VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                    VK_ACCESS_TRANSFER_WRITE_BIT,
                    VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_SHADER_READ_BIT),
        };
        m_vk->cmdPipelineBarrier(slot->commands,
                                 VK_PIPELINE_STAGE_TRANSFER_BIT,
                                 VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                                 0,
                                 0,
                                 nullptr,
                                 0,
                                 nullptr,
                                 2,
                                 after);
        shared.used = true;

        // After Direct3D's drawing; Direct3D's next drawing after the copy.
        m_context->Signal(m_fence.Get(), ++m_value);
        m_context->Flush();
        if (!submit(*slot)) {
            return false;
        }
        m_context->Wait(m_fence.Get(), m_value);
        m_context->Flush();
        return true;
    }

} // namespace openxr_api_layer
