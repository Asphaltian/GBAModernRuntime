using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace GBARenderer;

internal sealed unsafe class Swapchain : IDisposable
{
    private readonly VulkanDevice _vulkan;
    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _views = [];
    private Semaphore[] _rendered = [];
    private bool _needsRecreating;

    public Swapchain(VulkanDevice vulkan)
    {
        _vulkan = vulkan;
    }

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public Format Format { get; private set; }

    public Image ImageAt(uint index) => _images[index];

    public ImageView ViewAt(uint index) => _views[index];

    public Semaphore RenderedAt(uint index) => _rendered[index];

    public bool TryAcquire(uint width, uint height, Semaphore acquired, out uint index)
    {
        index = 0;
        if ((_needsRecreating || width != Width || height != Height) && !Recreate(width, height))
        {
            return false;
        }

        var result = _vulkan.SwapchainApi!.AcquireNextImage(_vulkan.Device, _swapchain, ulong.MaxValue, acquired, default, ref index);
        if (result == Result.ErrorOutOfDateKhr)
        {
            _needsRecreating = true;
            return false;
        }

        _needsRecreating = result == Result.SuboptimalKhr;
        if (!_needsRecreating)
        {
            VulkanDevice.Check(result, "vkAcquireNextImageKHR");
        }

        return true;
    }

    public void Present(uint index)
    {
        var swapchain = _swapchain;
        var rendered = _rendered[index];
        var info = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &rendered,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &index,
        };

        var result = _vulkan.SwapchainApi!.QueuePresent(_vulkan.Queue, &info);
        _needsRecreating = result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr;
        if (!_needsRecreating)
        {
            VulkanDevice.Check(result, "vkQueuePresentKHR");
        }
    }

    public void Dispose()
    {
        DestroyImageResources();
        _vulkan.SwapchainApi!.DestroySwapchain(_vulkan.Device, _swapchain, null);
    }

    private bool Recreate(uint width, uint height)
    {
        VulkanDevice.Check(_vulkan.Api.DeviceWaitIdle(_vulkan.Device), "vkDeviceWaitIdle");
        VulkanDevice.Check(_vulkan.SurfaceApi!.GetPhysicalDeviceSurfaceCapabilities(_vulkan.PhysicalDevice, _vulkan.Surface, out var capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        var usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit | _vulkan.TargetUsage;
        if ((capabilities.SupportedUsageFlags & usage) != usage)
        {
            throw new InvalidOperationException($"The window images can't be used for {usage}.");
        }

        var extent = capabilities.CurrentExtent.Width != uint.MaxValue
            ? capabilities.CurrentExtent
            : new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        if (extent.Width == 0 || extent.Height == 0)
        {
            return false;
        }

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
        }

        var oldSwapchain = _swapchain;
        var format = ChooseFormat();
        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _vulkan.Surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        VulkanDevice.Check(_vulkan.SwapchainApi!.CreateSwapchain(_vulkan.Device, in info, null, out _swapchain), "vkCreateSwapchainKHR");
        DestroyImageResources();
        _vulkan.SwapchainApi.DestroySwapchain(_vulkan.Device, oldSwapchain, null);

        uint count = 0;
        VulkanDevice.Check(_vulkan.SwapchainApi.GetSwapchainImages(_vulkan.Device, _swapchain, &count, null), "vkGetSwapchainImagesKHR");
        _images = new Image[count];
        fixed (Image* images = _images)
        {
            VulkanDevice.Check(_vulkan.SwapchainApi.GetSwapchainImages(_vulkan.Device, _swapchain, &count, images), "vkGetSwapchainImagesKHR");
        }

        _views = [.. _images.Select(image => CreateView(image, format.Format))];
        _rendered = [.. _images.Select(_ => CreateSemaphore())];

        (Width, Height, Format) = (extent.Width, extent.Height, format.Format);
        _needsRecreating = false;
        return true;
    }

    private SurfaceFormatKHR ChooseFormat()
    {
        uint count = 0;
        VulkanDevice.Check(_vulkan.SurfaceApi!.GetPhysicalDeviceSurfaceFormats(_vulkan.PhysicalDevice, _vulkan.Surface, &count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* pointer = formats)
        {
            VulkanDevice.Check(_vulkan.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_vulkan.PhysicalDevice, _vulkan.Surface, &count, pointer), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        }

        return formats.FirstOrDefault(
            format => format.Format is Format.B8G8R8A8Unorm or Format.R8G8B8A8Unorm && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            formats[0]);
    }

    private ImageView CreateView(Image image, Format format)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        VulkanDevice.Check(_vulkan.Api.CreateImageView(_vulkan.Device, in info, null, out var view), "vkCreateImageView");
        return view;
    }

    private Semaphore CreateSemaphore()
    {
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        VulkanDevice.Check(_vulkan.Api.CreateSemaphore(_vulkan.Device, in info, null, out var semaphore), "vkCreateSemaphore");
        return semaphore;
    }

    private void DestroyImageResources()
    {
        foreach (var view in _views)
        {
            _vulkan.Api.DestroyImageView(_vulkan.Device, view, null);
        }

        foreach (var semaphore in _rendered)
        {
            _vulkan.Api.DestroySemaphore(_vulkan.Device, semaphore, null);
        }

        (_views, _rendered) = ([], []);
    }
}
