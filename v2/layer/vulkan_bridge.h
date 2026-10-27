// VR Gaze Mirror 2.0 - the OpenXR layer: Vulkan games.
//
// The core works in Direct3D 11, so a Vulkan game's eye images are brought across. For each of the game's colour
// swapchains the bridge makes one Direct3D 11 texture, on a D3D11 device of its own on the game's GPU, and imports it
// into the game's Vulkan device through shared memory. At the end of each frame a short copy on the game's own queue
// puts the released image into it. One shared fence orders both ways: Direct3D waits for the copy, and the next copy
// waits until Direct3D has read the last one. The game's own images are only read, never written.
//
// The sharing needs a few Vulkan extensions in the game's instance and device; the layer adds them when the game asks
// the runtime which ones to enable (XR_KHR_vulkan_enable and _enable2).
#pragma once

namespace gaze_mirror::layer {

    // What the bridge needs to know of a swapchain, from its create info.
    struct VulkanSwapchainInfo {
        VkFormat format = VK_FORMAT_UNDEFINED;
        uint32_t width = 0, height = 0, arraySize = 1, sampleCount = 1;
    };

    class VulkanBridge {
      public:
        VulkanBridge();
        VulkanBridge(const VulkanBridge&) = delete;
        VulkanBridge& operator=(const VulkanBridge&) = delete;
        ~VulkanBridge();

        // The extensions the bridge needs, to add to the game's request: the instance ones the Vulkan loader offers,
        // and the device ones (present on every Windows graphics driver).
        static std::vector<const char*> instanceExtensions();
        static std::vector<const char*> deviceExtensions(VkPhysicalDevice physicalDevice = VK_NULL_HANDLE);
        // The Direct3D format a Vulkan colour format is shared as; DXGI_FORMAT_UNKNOWN: not one the bridge handles.
        static DXGI_FORMAT DxgiFormat(VkFormat format);

        bool start(const XrGraphicsBindingVulkanKHR& binding);
        void stop();
        bool active() const { return _device != VK_NULL_HANDLE; }
        ID3D11Device* device() const { return _d3d.Get(); }

        // A colour swapchain of the game: the Direct3D 11 texture that stands for every one of its images, or null.
        ID3D11Texture2D* addSwapchain(XrSwapchain swapchain, const VulkanSwapchainInfo& info, std::vector<VkImage> images);
        void removeSwapchain(XrSwapchain swapchain);
        // Copies the given image of each swapchain into its texture and has the D3D11 device wait for it. False: nothing
        // was copied, and the frame is skipped.
        bool copy(const std::vector<std::pair<XrSwapchain, uint32_t>>& released);
        // After the core has read the textures: the next copy may overwrite them.
        void readDone();

      private:
        struct Shared {
            VulkanSwapchainInfo info;
            std::vector<VkImage> gameImages;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
            VkImage image = VK_NULL_HANDLE;
            VkDeviceMemory memory = VK_NULL_HANDLE;
            bool used = false; // Copied into before: it then comes back from Direct3D's side (the external queue family).
        };
        struct Slot {
            VkCommandBuffer commands = VK_NULL_HANDLE;
            VkFence done = VK_NULL_HANDLE;
        };

        bool loadFunctions(VkInstance instance);
        void waitIdle();

        // The game's Vulkan objects (not ours to destroy) and the functions we call on them.
        VkInstance _instance = VK_NULL_HANDLE;
        VkPhysicalDevice _physicalDevice = VK_NULL_HANDLE;
        VkDevice _device = VK_NULL_HANDLE;
        VkQueue _queue = VK_NULL_HANDLE;
        uint32_t _queueFamily = 0;
        struct Functions;
        std::unique_ptr<Functions> _vk;

        // Ours.
        VkCommandPool _pool = VK_NULL_HANDLE;
        Slot _slots[3];
        uint32_t _nextSlot = 0;
        VkSemaphore _waitForRead = VK_NULL_HANDLE, _signalCopied = VK_NULL_HANDLE; // Both the shared D3D11 fence.
        uint64_t _fenceValue = 0, _copiedValue = 0;
        std::unordered_map<XrSwapchain, Shared> _shared;

        Microsoft::WRL::ComPtr<ID3D11Device5> _d3d;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> _context;
        Microsoft::WRL::ComPtr<ID3D11Fence> _fence;
    };

} // namespace gaze_mirror::layer
