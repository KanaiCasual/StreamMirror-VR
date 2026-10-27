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

namespace openxr_api_layer {

    // Vulkan games: the composition stays Direct3D 11, on a device of our own on the game's GPU. Every colour swapchain
    // (the game's, and the full view ones the layer makes) gets one Direct3D 11 texture that stands for all its images,
    // shared with the game's Vulkan device. Before the composition the game's images are copied into theirs, after it
    // the result is copied out into the runtime's image - both on the game's queue, ordered with Direct3D by one shared
    // fence whose value only ever goes up: each step waits for the one before.
    class VulkanInterop {
      public:
        VulkanInterop();
        VulkanInterop(const VulkanInterop&) = delete;
        VulkanInterop& operator=(const VulkanInterop&) = delete;
        ~VulkanInterop();

        // The extensions the sharing needs, to add to the game's request: the instance ones the Vulkan loader offers,
        // and the device ones (every Windows graphics driver has them).
        static std::vector<const char*> instanceExtensions();
        static std::vector<const char*> deviceExtensions(VkPhysicalDevice physicalDevice = VK_NULL_HANDLE);
        // Whether this PC can share between Vulkan and Direct3D at all (asked before a game gets quad views).
        static bool Available();
        // The Direct3D format a Vulkan colour format is shared as; DXGI_FORMAT_UNKNOWN: not one that is handled.
        static DXGI_FORMAT DxgiFormat(int64_t format);

        bool start(const XrGraphicsBindingVulkanKHR& binding);
        void stop();
        bool active() const {
            return m_device != VK_NULL_HANDLE;
        }
        ID3D11Device* device() const {
            return m_d3d.Get();
        }

        // A colour swapchain: the texture that stands for every one of its images, or null.
        ID3D11Texture2D* addSwapchain(XrSwapchain swapchain, const XrSwapchainCreateInfo& info, std::vector<VkImage> images);
        void removeSwapchain(XrSwapchain swapchain);
        // The game's images into their textures (each swapchain once a frame), then Direct3D waits for that.
        bool copyIn(const std::vector<std::pair<XrSwapchain, uint32_t>>& images, uint64_t frame);
        // After Direct3D has drawn into a swapchain's texture: into the runtime's image, after Direct3D is done.
        bool copyOut(XrSwapchain swapchain, uint32_t index);

      private:
        struct Shared {
            XrSwapchainCreateInfo info{};
            std::vector<VkImage> images;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
            VkImage image = VK_NULL_HANDLE;
            VkDeviceMemory memory = VK_NULL_HANDLE;
            bool used = false; // Through Vulkan before: it comes back from Direct3D's side (the external queue family).
            uint64_t copiedFrame = UINT64_MAX;
            uint32_t copiedIndex = UINT32_MAX;
        };
        struct Slot {
            VkCommandBuffer commands = VK_NULL_HANDLE;
            VkFence done = VK_NULL_HANDLE;
        };
        struct Functions;

        bool loadFunctions();
        void waitIdle();
        Slot* beginCommands();
        // Submits the slot's commands after the shared fence reaches the current value; they set it one higher.
        bool submit(Slot& slot);

        VkInstance m_instance = VK_NULL_HANDLE;
        VkPhysicalDevice m_physicalDevice = VK_NULL_HANDLE;
        VkDevice m_device = VK_NULL_HANDLE;
        VkQueue m_queue = VK_NULL_HANDLE;
        uint32_t m_queueFamily = 0;
        std::unique_ptr<Functions> m_vk;

        VkCommandPool m_pool = VK_NULL_HANDLE;
        Slot m_slots[4];
        uint32_t m_nextSlot = 0;
        VkSemaphore m_wait = VK_NULL_HANDLE, m_signal = VK_NULL_HANDLE; // Both the shared fence.
        uint64_t m_value = 0;
        std::map<XrSwapchain, Shared> m_shared;

        Microsoft::WRL::ComPtr<ID3D11Device5> m_d3d;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> m_context;
        Microsoft::WRL::ComPtr<ID3D11Fence> m_fence;
    };

} // namespace openxr_api_layer
