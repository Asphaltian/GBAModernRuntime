using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace GBARenderer;

internal sealed unsafe class VulkanDevice : IDisposable
{
    private const string PortabilityEnumeration = "VK_KHR_portability_enumeration";
    private const string PortabilitySubset = "VK_KHR_portability_subset";

    public VulkanDevice(IReadOnlyList<string> instanceExtensions, Func<nint, ulong>? createSurface, IRenderHooks? hooks)
    {
        Api = Vk.GetApi();
        Instance = CreateInstance(instanceExtensions);
        if (createSurface is not null)
        {
            Surface = new SurfaceKHR(createSurface(Instance.Handle));
            SurfaceApi = InstanceExtension<KhrSurface>();
        }

        PhysicalDevice = ChoosePhysicalDevice(hooks, out uint queueFamily);
        QueueFamily = queueFamily;
        DeviceConfiguration? configuration = null;
        if (hooks is not null)
        {
            configuration = new DeviceConfiguration();
            hooks.Configure(configuration);
        }

        TargetUsage = configuration?.TargetUsage ?? 0;
        Device = CreateDevice(configuration);
        Api.GetDeviceQueue(Device, QueueFamily, 0, out var queue);
        Queue = queue;
        if (createSurface is not null)
        {
            SwapchainApi = DeviceExtension<KhrSwapchain>();
        }
    }

    public Vk Api { get; }

    public Instance Instance { get; }

    public PhysicalDevice PhysicalDevice { get; }

    public Device Device { get; }

    public Queue Queue { get; }

    public uint QueueFamily { get; }

    public SurfaceKHR Surface { get; }

    public KhrSurface? SurfaceApi { get; }

    public KhrSwapchain? SwapchainApi { get; }

    public ImageUsageFlags TargetUsage { get; }

    public static void Check(Result result, string call)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{call} failed with {result}.");
        }
    }

    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        Api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memory);
        for (uint i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 && (memory.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"The GPU has no memory that is {properties}.");
    }

    public void Dispose()
    {
        Api.DestroyDevice(Device, null);
        SurfaceApi?.DestroySurface(Instance, Surface, null);
        Api.DestroyInstance(Instance, null);
        Api.Dispose();
    }

    private Instance CreateInstance(IReadOnlyList<string> requiredExtensions)
    {
        bool enumeratesPortability = InstanceExtensions().Contains(PortabilityEnumeration);
        string[] extensions = enumeratesPortability ? [.. requiredExtensions, PortabilityEnumeration] : [.. requiredExtensions];
        nint names = SilkMarshal.StringArrayToPtr(extensions);
        try
        {
            var application = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version12 };
            var info = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &application,
                EnabledExtensionCount = (uint)extensions.Length,
                PpEnabledExtensionNames = (byte**)names,
                Flags = enumeratesPortability ? InstanceCreateFlags.EnumeratePortabilityBitKhr : 0,
            };

            Check(Api.CreateInstance(in info, null, out var instance), "vkCreateInstance");
            return instance;
        }
        finally
        {
            SilkMarshal.Free(names);
        }
    }

    private PhysicalDevice ChoosePhysicalDevice(IRenderHooks? hooks, out uint queueFamily)
    {
        uint count = 0;
        Check(Api.EnumeratePhysicalDevices(Instance, &count, null), "vkEnumeratePhysicalDevices");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices)
        {
            Check(Api.EnumeratePhysicalDevices(Instance, &count, pointer), "vkEnumeratePhysicalDevices");
        }

        var chosen = devices
            .Select(device => (Device: device, QueueFamily: FindQueueFamily(device), IsDiscrete: IsDiscrete(device)))
            .Where(candidate => candidate.QueueFamily >= 0 && (hooks is null || hooks.Supports(Api, candidate.Device)))
            .OrderByDescending(candidate => candidate.IsDiscrete)
            .FirstOrDefault();

        if (chosen.Device.Handle == 0)
        {
            string supported = hooks is null ? "" : "the render hooks support ";
            string window = Surface.Handle == 0 ? "" : " and show it in the window";
            throw new InvalidOperationException($"No GPU {supported}can run Vulkan compute and graphics work{window}.");
        }

        queueFamily = (uint)chosen.QueueFamily;
        return chosen.Device;
    }

    private bool IsDiscrete(PhysicalDevice device)
    {
        Api.GetPhysicalDeviceProperties(device, out var properties);
        return properties.DeviceType == PhysicalDeviceType.DiscreteGpu;
    }

    private int FindQueueFamily(PhysicalDevice device)
    {
        uint count = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pointer = families)
        {
            Api.GetPhysicalDeviceQueueFamilyProperties(device, &count, pointer);
        }

        var needed = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;
        for (int i = 0; i < families.Length; i++)
        {
            if ((families[i].QueueFlags & needed) == needed && CanPresent(device, (uint)i))
            {
                return i;
            }
        }

        return -1;
    }

    private bool CanPresent(PhysicalDevice device, uint queueFamily)
    {
        if (SurfaceApi is null)
        {
            return true;
        }

        Check(SurfaceApi.GetPhysicalDeviceSurfaceSupport(device, queueFamily, Surface, out var supported), "vkGetPhysicalDeviceSurfaceSupportKHR");
        return supported;
    }

    private Device CreateDevice(DeviceConfiguration? configuration)
    {
        var extensions = new List<string>(configuration?.Extensions ?? []);
        if (Surface.Handle != 0)
        {
            extensions.Add(KhrSwapchain.ExtensionName);
        }

        if (DeviceExtensions().Contains(PortabilitySubset))
        {
            extensions.Add(PortabilitySubset);
        }

        float priority = 1;
        var queue = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var features = configuration?.Features ?? default;
        var vulkan12 = (configuration?.Vulkan12Features ?? default) with { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = null };
        var dynamicRendering = (configuration?.DynamicRenderingFeatures ?? default) with { SType = StructureType.PhysicalDeviceDynamicRenderingFeatures, PNext = null };
        if (dynamicRendering.DynamicRendering)
        {
            vulkan12.PNext = &dynamicRendering;
        }

        extensions = [.. extensions.Distinct()];
        nint names = SilkMarshal.StringArrayToPtr(extensions);
        try
        {
            var info = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = configuration is null ? null : &vulkan12,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queue,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)names,
                PEnabledFeatures = configuration is null ? null : &features,
            };

            Check(Api.CreateDevice(PhysicalDevice, in info, null, out var device), "vkCreateDevice");
            return device;
        }
        finally
        {
            SilkMarshal.Free(names);
        }
    }

    private HashSet<string> InstanceExtensions()
    {
        uint count = 0;
        Check(Api.EnumerateInstanceExtensionProperties((byte*)null, &count, null), "vkEnumerateInstanceExtensionProperties");
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            Check(Api.EnumerateInstanceExtensionProperties((byte*)null, &count, pointer), "vkEnumerateInstanceExtensionProperties");
        }

        return [.. properties.Select(Name)];
    }

    private HashSet<string> DeviceExtensions()
    {
        uint count = 0;
        Check(Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, null), "vkEnumerateDeviceExtensionProperties");
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            Check(Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, pointer), "vkEnumerateDeviceExtensionProperties");
        }

        return [.. properties.Select(Name)];
    }

    private static string Name(ExtensionProperties properties) => SilkMarshal.PtrToString((nint)properties.ExtensionName)!;

    private T InstanceExtension<T>()
        where T : NativeExtension<Vk>
    {
        return Api.TryGetInstanceExtension<T>(Instance, out var extension)
            ? extension
            : throw new InvalidOperationException($"Could not load {typeof(T).Name}.");
    }

    private T DeviceExtension<T>()
        where T : NativeExtension<Vk>
    {
        return Api.TryGetDeviceExtension<T>(Instance, Device, out var extension)
            ? extension
            : throw new InvalidOperationException($"Could not load {typeof(T).Name}.");
    }
}
