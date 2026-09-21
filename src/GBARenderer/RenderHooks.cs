using Silk.NET.Vulkan;

namespace GBARenderer;

/// <summary>
/// Lets you draw in the window on top of the game, like a user interface. Implement it and pass it to
/// <see cref="FrameRenderer"/> when you make one.
/// </summary>
/// <example>
/// <code>
/// sealed class Overlay : IRenderHooks
/// {
///     public bool Supports(Vk api, PhysicalDevice physicalDevice) => true;
///
///     // Ask for the extensions and features your drawing code needs
///     public void Configure(DeviceConfiguration configuration)
///     {
///         configuration.Extensions.Add("VK_KHR_dynamic_rendering");
///         configuration.DynamicRenderingFeatures.DynamicRendering = true;
///     }
///
///     // Make your pipelines, buffers and so on
///     public void Init(RenderDevice device) { }
///
///     // Add your drawing to commandBuffer, and leave target.Image in PresentSrcKhr
///     public void Draw(CommandBuffer commandBuffer, RenderTarget target) { }
///
///     // Free what you made in Init
///     public void Deinit() { }
/// }
///
/// var renderer = new FrameRenderer(instanceExtensions, createSurface, new Overlay());
/// </code>
/// </example>
public interface IRenderHooks
{
    /// <summary>Return false if you can't draw with this GPU, and it won't be picked.</summary>
    bool Supports(Vk api, PhysicalDevice physicalDevice);

    /// <summary>Fill in <paramref name="configuration"/> with the extensions, features and window image usage you need.</summary>
    void Configure(DeviceConfiguration configuration);

    /// <summary>Called once before the first <see cref="Draw"/>. Set up what you draw with here.</summary>
    void Init(RenderDevice device);

    /// <summary>
    /// Called every frame after the game is drawn, outside any render pass. Draw into <paramref name="target"/> and
    /// leave it in <see cref="ImageLayout.PresentSrcKhr"/>. The previous frame has always finished on the GPU by now.
    /// </summary>
    void Draw(CommandBuffer commandBuffer, RenderTarget target);

    /// <summary>Called when the renderer shuts down, once the GPU is idle. Free what you made in <see cref="Init"/>.</summary>
    void Deinit();
}

/// <summary>What you ask for in <see cref="IRenderHooks.Configure"/>. Everything starts off, so turn on only what you use.</summary>
public sealed class DeviceConfiguration
{
    /// <summary>Device extensions to enable.</summary>
    public List<string> Extensions { get; } = [];

    /// <summary>Vulkan 1.0 features to enable.</summary>
    public PhysicalDeviceFeatures Features;

    /// <summary>Vulkan 1.2 features to enable.</summary>
    public PhysicalDeviceVulkan12Features Vulkan12Features;

    /// <summary>Set <c>DynamicRendering</c> here when you add <c>VK_KHR_dynamic_rendering</c> to <see cref="Extensions"/>.</summary>
    public PhysicalDeviceDynamicRenderingFeatures DynamicRenderingFeatures;

    /// <summary>Extra usage flags for the window images, like <see cref="ImageUsageFlags.TransferSrcBit"/> if you copy from them.</summary>
    public ImageUsageFlags TargetUsage { get; set; }
}

/// <summary>The Vulkan objects you draw with, from <see cref="IRenderHooks.Init"/>.</summary>
public sealed record RenderDevice(Vk Api, PhysicalDevice PhysicalDevice, Device Device, Queue Queue, uint QueueFamily);

/// <summary>The window image you draw into in <see cref="IRenderHooks.Draw"/>. <c>Layout</c> is the layout it's in when you get it.</summary>
public readonly record struct RenderTarget(Image Image, ImageView View, Format Format, Extent2D Size, ImageLayout Layout);
