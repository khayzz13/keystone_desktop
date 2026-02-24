// VulkanGpuContext — Per-window Vulkan GPU state implementing IWindowGpuContext.
// Each window gets its own VkSwapchain + GRVkBackendContext + Skia GRContext.
// Equivalent of WindowGpuContext.cs (Metal).

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Keystone.Core.Rendering;
using SkiaSharp;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;
using VkFence = Silk.NET.Vulkan.Fence;
using VkImage = Silk.NET.Vulkan.Image;

namespace Keystone.Core.Graphics.Skia.Vulkan;

public class VulkanGpuContext : IWindowGpuContext
{
    readonly VulkanSkiaWindow _shared;
    readonly SurfaceKHR _surface;
    readonly KhrSwapchain _khrSwapchain;
    readonly SwapchainKHR _swapchain;
    readonly VkImage[] _swapchainImages;
    readonly GRContext _grContext;

    SKSurface? _skiaSurface;
    GRBackendRenderTarget? _backendRT;
    uint _currentImageIndex;
    bool _disposed;

    // Semaphores for frame synchronization
    readonly VkSemaphore _imageAvailable;
    readonly VkSemaphore _renderFinished;
    readonly VkFence _inFlightFence;

    public GRContext GRContext => _grContext;

    VulkanGpuContext(VulkanSkiaWindow shared, SurfaceKHR surface,
        KhrSwapchain khrSwapchain, SwapchainKHR swapchain, VkImage[] images,
        GRContext grContext, VkSemaphore imageAvailable, VkSemaphore renderFinished, VkFence fence)
    {
        _shared = shared;
        _surface = surface;
        _khrSwapchain = khrSwapchain;
        _swapchain = swapchain;
        _swapchainImages = images;
        _grContext = grContext;
        _imageAvailable = imageAvailable;
        _renderFinished = renderFinished;
        _inFlightFence = fence;
    }

    public static unsafe VulkanGpuContext Create(VulkanSkiaWindow shared, IntPtr gdkSurface)
    {
        var vk = shared.Vk;

        // Create VkSurfaceKHR from the GDK surface
        // This is platform-specific — X11 uses VkXlibSurfaceCreateInfoKHR,
        // Wayland uses VkWaylandSurfaceCreateInfoKHR.
        // For now, we'll support X11 as the primary path.
        var surface = CreateVkSurface(vk, shared.VkInstance, gdkSurface);

        // Get swapchain extension
        if (!vk.TryGetDeviceExtension(shared.VkInstance, shared.VkDevice, out KhrSwapchain khrSwapchain))
            throw new InvalidOperationException("VK_KHR_swapchain extension not available");

        // Query surface capabilities
        shared.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(
            shared.PhysicalDevice, surface, out var caps);

        var imageCount = Math.Max(caps.MinImageCount, 3); // triple-buffered
        if (caps.MaxImageCount > 0) imageCount = Math.Min(imageCount, caps.MaxImageCount);

        // Create swapchain
        var swapchainInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = Format.B8G8R8A8Unorm,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = caps.CurrentExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr, // VSync
            Clipped = true,
        };

        SwapchainKHR swapchain;
        var result = khrSwapchain.CreateSwapchain(shared.VkDevice, &swapchainInfo, null, &swapchain);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create swapchain: {result}");

        // Get swapchain images
        uint swapImageCount = 0;
        khrSwapchain.GetSwapchainImages(shared.VkDevice, swapchain, &swapImageCount, null);
        var images = new VkImage[swapImageCount];
        fixed (VkImage* pImages = images)
            khrSwapchain.GetSwapchainImages(shared.VkDevice, swapchain, &swapImageCount, pImages);

        // Create Skia GRContext for Vulkan
        var backendContext = new GRVkBackendContext
        {
            VkInstance = shared.VkInstance.Handle,
            VkPhysicalDevice = shared.PhysicalDevice.Handle,
            VkDevice = shared.VkDevice.Handle,
            VkQueue = shared.GraphicsQueue.Handle,
            GraphicsQueueIndex = shared.GraphicsQueueFamily,
        };

        // Set up Vulkan function pointers for Skia
        backendContext.GetProcedureAddress = (name, instance, device) =>
        {
            if (device != IntPtr.Zero)
                return vk.GetDeviceProcAddr(new Device(device), name);
            return vk.GetInstanceProcAddr(new Instance(instance), name);
        };

        var grContext = GRContext.CreateVulkan(backendContext)
            ?? throw new InvalidOperationException("Failed to create Vulkan GRContext");
        grContext.SetResourceCacheLimit(64 * 1024 * 1024); // 64MB per window

        // Create sync objects
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        VkSemaphore imageAvailable, renderFinished;
        VkFence fence;
        vk.CreateSemaphore(shared.VkDevice, &semaphoreInfo, null, &imageAvailable);
        vk.CreateSemaphore(shared.VkDevice, &semaphoreInfo, null, &renderFinished);
        vk.CreateFence(shared.VkDevice, &fenceInfo, null, &fence);

        return new VulkanGpuContext(shared, surface, khrSwapchain, swapchain, images,
            grContext, imageAvailable, renderFinished, fence);
    }

    private static unsafe SurfaceKHR CreateVkSurface(Vk vk, Instance instance, IntPtr gdkSurface)
    {
        // TODO: Detect X11 vs Wayland from GDK surface type and create appropriate surface.
        // For now, this is a placeholder — the actual implementation requires
        // GDK interop to get the X11 Display/Window or Wayland display/surface.
        throw new NotImplementedException(
            "VkSurfaceKHR creation requires GDK surface type detection (X11/Wayland). " +
            "Implement when testing on Linux.");
    }

    // === IWindowGpuContext ===

    public void WarmUpShaders()
    {
        using var surface = SKSurface.Create(_grContext, false, new SKImageInfo(64, 64));
        if (surface == null) return;
        var c = surface.Canvas;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };

        c.DrawRect(0, 0, 32, 32, paint);
        c.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, 16, 16), 4), paint);

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        c.DrawLine(0, 0, 32, 32, paint);
        paint.Style = SKPaintStyle.Fill;

        using var bmp = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var img = SKImage.FromBitmap(bmp);
        c.DrawImage(img, new SKRect(0, 0, 4, 4));

        using var font = new SKFont(SKTypeface.Default, 14);
        c.DrawText("0", 0, 14, font, paint);

        c.Flush();
        _grContext.Flush();
    }

    public unsafe SKCanvas? BeginFrame(object gpuSurface, int width, int height)
    {
        if (_disposed) return null;

        var vk = _shared.Vk;
        var device = _shared.VkDevice;

        // Wait for previous frame's fence
        var fence = _inFlightFence;
        vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue);
        vk.ResetFences(device, 1, &fence);

        // Acquire next swapchain image
        uint imageIndex = 0;
        var result = _khrSwapchain.AcquireNextImage(device, _swapchain, ulong.MaxValue,
            _imageAvailable, default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
        {
            // Swapchain needs recreation — skip this frame
            return null;
        }

        _currentImageIndex = imageIndex;
        DisposeFrameResources();

        // Create Skia backend render target from swapchain image
        var vkImageInfo = new GRVkImageInfo
        {
            Image = _swapchainImages[imageIndex].Handle,
            ImageTiling = (uint)ImageTiling.Optimal,
            ImageLayout = (uint)ImageLayout.Undefined,
            Format = (uint)Format.B8G8R8A8Unorm,
            LevelCount = 1,
            CurrentQueueFamily = _shared.GraphicsQueueFamily,
        };

        _backendRT = new GRBackendRenderTarget(width, height, vkImageInfo);
        _skiaSurface = SKSurface.Create(_grContext, _backendRT,
            GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);

        if (_skiaSurface == null)
        {
            _backendRT.Dispose();
            _backendRT = null;
            return null;
        }

        return _skiaSurface.Canvas;
    }

    public unsafe void FinishAndPresent()
    {
        if (_skiaSurface == null) return;

        _skiaSurface.Canvas.Flush();
        _grContext.Flush();
        _grContext.Submit(synchronous: true);

        // Present
        var swapchain = _swapchain;
        var imageIndex = _currentImageIndex;
        var renderFinished = _renderFinished;

        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        _khrSwapchain.QueuePresent(_shared.GraphicsQueue, &presentInfo);

        DisposeFrameResources();
        _grContext.PurgeUnlockedResources(false);
    }

    public void SetDrawableSize(object gpuSurface, uint width, uint height)
    {
        // Vulkan swapchain recreation on resize would go here.
        // For now, the swapchain is created at initial size.
        // Full implementation would destroy + recreate swapchain with new extent.
    }

    public void ForceFullPurge()
    {
        _grContext.Flush();
        _grContext.Submit(synchronous: true);
        _grContext.SetResourceCacheLimit(0);
        _grContext.PurgeUnlockedResources(true);
        _grContext.SetResourceCacheLimit(64 * 1024 * 1024);
    }

    public (int count, long bytes) GetCacheStats()
    {
        _grContext.GetResourceCacheUsage(out var count, out var bytes);
        return (count, bytes);
    }

    // === IGpuContext ===

    object IGpuContext.Device => _shared.VkDevice;
    object IGpuContext.Queue => _shared.GraphicsQueue;
    object IGpuContext.GraphicsContext => _grContext;

    object? IGpuContext.ImportTexture(IntPtr textureHandle, int width, int height)
    {
        // Vulkan texture import — create GRVkImageInfo from handle
        var vkImageInfo = new GRVkImageInfo
        {
            Image = (ulong)textureHandle,
            ImageTiling = (uint)ImageTiling.Optimal,
            ImageLayout = (uint)ImageLayout.ShaderReadOnlyOptimal,
            Format = (uint)Format.R8G8B8A8Unorm,
            LevelCount = 1,
            CurrentQueueFamily = _shared.GraphicsQueueFamily,
        };
        var backendTexture = new GRBackendTexture(width, height, vkImageInfo);
        return SKImage.FromTexture(_grContext, backendTexture,
            GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Premul);
    }

    void DisposeFrameResources()
    {
        _skiaSurface?.Dispose();
        _backendRT?.Dispose();
        _skiaSurface = null;
        _backendRT = null;
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeFrameResources();
        _grContext.Flush();
        _grContext.Submit(synchronous: true);
        _grContext.SetResourceCacheLimit(0);
        _grContext.PurgeUnlockedResources(false);
        _grContext.Dispose();

        var device = _shared.VkDevice;
        var vk = _shared.Vk;
        var imgAvail = _imageAvailable;
        var renderDone = _renderFinished;
        var fence = _inFlightFence;
        vk.DestroySemaphore(device, imgAvail, null);
        vk.DestroySemaphore(device, renderDone, null);
        vk.DestroyFence(device, fence, null);

        var swapchain = _swapchain;
        _khrSwapchain.DestroySwapchain(device, swapchain, null);

        var surface = _surface;
        _shared.KhrSurface.DestroySurface(_shared.VkInstance, surface, null);
    }
}
