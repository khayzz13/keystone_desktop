// VulkanSkiaWindow — Shared Vulkan device holder + VulkanGpuContext factory.
// Equivalent of SkiaWindow.cs (Metal). The VkDevice is thread-safe (shared).
// Each window creates its own VulkanGpuContext with its own swapchain + GRContext.

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Keystone.Core.Graphics.Skia.Vulkan;

public class VulkanSkiaWindow : IDisposable
{
    static VulkanSkiaWindow? _shared;
    public static VulkanSkiaWindow Shared =>
        _shared ?? throw new InvalidOperationException("VulkanSkiaWindow not initialized");

    readonly Vk _vk;
    readonly Instance _instance;
    readonly PhysicalDevice _physicalDevice;
    readonly Device _device;
    readonly Queue _graphicsQueue;
    readonly uint _graphicsQueueFamily;
    readonly KhrSurface _khrSurface;
    bool _disposed;

    public Vk Vk => _vk;
    public Instance VkInstance => _instance;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    public Device VkDevice => _device;
    public Queue GraphicsQueue => _graphicsQueue;
    public uint GraphicsQueueFamily => _graphicsQueueFamily;
    public KhrSurface KhrSurface => _khrSurface;

    VulkanSkiaWindow(Vk vk, Instance instance, PhysicalDevice physicalDevice,
        Device device, Queue graphicsQueue, uint graphicsQueueFamily, KhrSurface khrSurface)
    {
        _vk = vk;
        _instance = instance;
        _physicalDevice = physicalDevice;
        _device = device;
        _graphicsQueue = graphicsQueue;
        _graphicsQueueFamily = graphicsQueueFamily;
        _khrSurface = khrSurface;
    }

    public static unsafe void Initialize()
    {
        if (_shared != null) return;

        var vk = Vk.GetApi();

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApiVersion = Vk.Version12
        };

        // Instance extensions for windowing (surface + X11 + Wayland)
        var instExtNames = new byte[][]
        {
            System.Text.Encoding.UTF8.GetBytes("VK_KHR_surface\0"),
            System.Text.Encoding.UTF8.GetBytes("VK_KHR_xlib_surface\0"),
            System.Text.Encoding.UTF8.GetBytes("VK_KHR_wayland_surface\0"),
        };

        fixed (byte* p0 = instExtNames[0], p1 = instExtNames[1], p2 = instExtNames[2])
        {
            var instExtPtrs = stackalloc byte*[] { p0, p1, p2 };
            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = 3,
                PpEnabledExtensionNames = instExtPtrs,
            };

            Instance instance;
            var result = vk.CreateInstance(&instanceCreateInfo, null, &instance);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create Vulkan instance: {result}");

            InitializeWithInstance(vk, instance);
        }
    }

    private static unsafe void InitializeWithInstance(Vk vk, Instance instance)
    {
        // Get KHR_surface extension
        if (!vk.TryGetInstanceExtension(instance, out KhrSurface khrSurface))
            throw new InvalidOperationException("VK_KHR_surface extension not available");

        // Pick physical device
        uint deviceCount = 0;
        vk.EnumeratePhysicalDevices(instance, &deviceCount, null);
        if (deviceCount == 0)
            throw new InvalidOperationException("No Vulkan-capable GPU found");

        var physicalDevices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = physicalDevices)
            vk.EnumeratePhysicalDevices(instance, &deviceCount, pDevices);

        // Prefer discrete GPU, fallback to first available
        var physicalDevice = physicalDevices[0];
        foreach (var pd in physicalDevices)
        {
            vk.GetPhysicalDeviceProperties(pd, out var props);
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                physicalDevice = pd;
                break;
            }
        }

        // Find graphics queue family
        uint queueFamilyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, null);
        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* pFamilies = queueFamilies)
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, pFamilies);

        uint graphicsFamily = uint.MaxValue;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if ((queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                graphicsFamily = i;
                break;
            }
        }
        if (graphicsFamily == uint.MaxValue)
            throw new InvalidOperationException("No graphics queue family found");

        // Create logical device with VK_KHR_swapchain extension
        float queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = graphicsFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        var swapchainExtName = System.Text.Encoding.UTF8.GetBytes("VK_KHR_swapchain\0");
        fixed (byte* pSwapExt = swapchainExtName)
        {
            var devExtPtrs = stackalloc byte*[] { pSwapExt };
            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = devExtPtrs,
            };

            Device device;
            var result = vk.CreateDevice(physicalDevice, &deviceCreateInfo, null, &device);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to create Vulkan device: {result}");

            Queue graphicsQueue;
            vk.GetDeviceQueue(device, graphicsFamily, 0, &graphicsQueue);

            _shared = new VulkanSkiaWindow(vk, instance, physicalDevice, device,
                graphicsQueue, graphicsFamily, khrSurface);
        }

        Console.WriteLine("[VulkanSkiaWindow] Initialized — shared Vulkan device");
    }

    /// <summary>
    /// Create a per-window GPU context with its own swapchain and GRContext.
    /// </summary>
    public static VulkanGpuContext CreateWindowContext(IntPtr gdkSurface) =>
        VulkanGpuContext.Create(Shared, gdkSurface);

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }
}
