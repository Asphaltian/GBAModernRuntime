using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AGBModern;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace GBARenderer;

/// <summary>A picture the renderer drew, as RGBA bytes from the top row down.</summary>
public sealed record Screenshot(int Width, int Height, byte[] Pixels);

/// <summary>
/// Draws GBA frames with Vulkan. Please note that <see cref="Submit"/> has to be called from the
/// game's thread, and everything else from one thread of your choice.
/// </summary>
public sealed unsafe class FrameRenderer : IDisposable
{
    public const int Width = 240;
    public const int Height = 160;

    private const int ObjectGroups = FrameMotion.ObjectCount / 32;

    private const int LinesSize = Height * VideoFrame.WordsPerLine * sizeof(uint);
    private const int LayoutOfLineOffset = LinesSize;
    private const int MotionOffset = LayoutOfLineOffset + (Height * sizeof(uint));
    private const int ObjectLinesOffset = MotionOffset + ((FrameMotion.BackgroundCount + FrameMotion.ObjectCount + FrameMotion.WindowCount) * 4 * sizeof(float));
    private const int SuppliedColumnsOffset = ObjectLinesOffset + (Height * ObjectGroups * sizeof(uint));
    private const int MarginTilesOffset = SuppliedColumnsOffset + (FrameMotion.BackgroundCount * sizeof(uint));
    private const int MarginWindowsOffset = MarginTilesOffset + (FrameMotion.BackgroundCount * 2 * FrameMargins.ColumnsPerSide * FrameMargins.Rows * sizeof(ushort));
    private const int CopiesStartOffset = MarginWindowsOffset + (FrameMotion.WindowCount * Height * sizeof(uint));
    private const int LayoutsOffset = CopiesStartOffset + sizeof(uint);
    private const int LayoutSize = VideoFrame.VideoMemoryKilobytes * sizeof(ushort);
    private const int SmallestFrameSize = LayoutsOffset + LayoutSize + (VideoFrame.VideoMemoryKilobytes * VideoFrame.KilobyteSize);

    private const int LineCycles = 1210;
    private const int HBlankFreeLineCycles = 954;
    private const uint HBlankIntervalFree = 1 << 5;
    private const uint ThreadGroupSize = 8;
    private const uint MaxScale = 16;

    private static readonly long TicksPerGameFrame = Stopwatch.Frequency * Video.CyclesPerFrame / Scheduler.CyclesPerSecond;
    private static readonly byte[] ObjectWidths = [8, 16, 32, 64, 16, 32, 32, 64, 8, 8, 16, 32];
    private static readonly byte[] ObjectHeights = [8, 16, 32, 64, 8, 8, 16, 32, 16, 32, 32, 64];

    private readonly VulkanDevice _vulkan;
    private readonly Swapchain? _swapchain;
    private readonly Semaphore _acquired;
    private readonly DescriptorSetLayout _descriptorSetLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;
    private readonly DescriptorPool _descriptorPool;
    private readonly DescriptorSet _descriptorSet;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly Fence _fence;
    private readonly byte[][] _frames = [new byte[SmallestFrameSize], new byte[SmallestFrameSize]];
    private readonly int[] _frameSizes = new int[2];
    private readonly Lock _submittedLock = new();
    private readonly IRenderHooks? _hooks;

    private Buffer _frameBuffer;
    private DeviceMemory _frameMemory;
    private Buffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private byte* _staging;
    private int _frameCapacity;
    private int _currentFrame;
    private int _uploadedFrame;

    private Image _output;
    private DeviceMemory _outputMemory;
    private ImageView _outputView;
    private uint _outputWidth;
    private uint _outputHeight;
    private uint _margin;
    private bool _hooksInitialized;

    private volatile bool _hasSubmittedFrame;
    private bool _hasNewFrame;
    private bool _hasMotion;
    private uint _submittedMargin;
    private long _submittedAt;

    /// <summary>Draws into a window.</summary>
    /// <param name="instanceExtensions">The Vulkan instance extensions the window needs, like the ones <c>SDL_Vulkan_GetInstanceExtensions</c> lists.</param>
    /// <param name="createSurface">Makes the window's <c>VkSurfaceKHR</c> for the <c>VkInstance</c> you're given.</param>
    /// <param name="hooks">Draws on top of the game in the window, like a user interface.</param>
    public FrameRenderer(IReadOnlyList<string> instanceExtensions, Func<nint, ulong> createSurface, IRenderHooks? hooks = null)
        : this(new VulkanDevice(instanceExtensions, createSurface, hooks))
    {
        _hooks = hooks;
        _swapchain = new Swapchain(_vulkan);
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        VulkanDevice.Check(_vulkan.Api.CreateSemaphore(_vulkan.Device, in info, null, out _acquired), "vkCreateSemaphore");
    }

    /// <summary>Draws without a window, for <see cref="Render"/> and <see cref="TakeScreenshot"/> only.</summary>
    public FrameRenderer()
        : this(new VulkanDevice([], null, null))
    {
    }

    private FrameRenderer(VulkanDevice vulkan)
    {
        _vulkan = vulkan;
        _descriptorSetLayout = CreateDescriptorSetLayout();
        _pipelineLayout = CreatePipelineLayout();
        _pipeline = CreatePipeline();
        _descriptorPool = CreateDescriptorPool();
        _descriptorSet = AllocateDescriptorSet();
        _commandPool = CreateCommandPool();
        _commandBuffer = AllocateCommandBuffer();
        _fence = CreateFence();
        CreateBuffers(SmallestFrameSize);
    }

    /// <summary>
    /// Draws the motion you submit smoothly, at the display's refresh rate. Keep in mind that while
    /// it's on, the picture runs up to one game frame behind.
    /// </summary>
    public bool InterpolatesMotion { get; set; }

    /// <summary>Scales the picture by whole numbers only, with black around it.</summary>
    public bool UsesIntegerScaling { get; set; }

    /// <summary>
    /// Hands the renderer the frame the game just finished. Call it from
    /// <see cref="Video.FrameFinished"/>. If you don't touch a frame's motion, things stay where the
    /// last frame's motion left them.
    /// </summary>
    public void Submit(VideoFrame frame, FrameMotion frameMotion, FrameMargins margins)
    {
        lock (_submittedLock)
        {
            int slot = 1 - _currentFrame;
            int copiesOffset = LayoutsOffset + (frame.LayoutCount * LayoutSize);
            int size = copiesOffset + (frame.CopyCount * VideoFrame.KilobyteSize);
            if (_frames[slot].Length < size)
            {
                _frames[slot] = new byte[size];
            }

            var block = _frames[slot].AsSpan(0, size);
            MemoryMarshal.AsBytes<uint>(frame.Lines).CopyTo(block);

            var layoutOfLine = MemoryMarshal.Cast<byte, uint>(block.Slice(LayoutOfLineOffset, Height * sizeof(uint)));
            for (int line = 0; line < Height; line++)
            {
                layoutOfLine[line] = frame.LayoutOfLine[line];
            }

            BinaryPrimitives.WriteUInt32LittleEndian(block[CopiesStartOffset..], (uint)(copiesOffset / sizeof(uint)));
            MemoryMarshal.AsBytes(frame.Layouts.AsSpan(0, frame.LayoutCount * VideoFrame.VideoMemoryKilobytes)).CopyTo(block[LayoutsOffset..]);
            frame.Copies.AsSpan(0, frame.CopyCount * VideoFrame.KilobyteSize).CopyTo(block[copiesOffset..]);

            var motion = MemoryMarshal.Cast<byte, Displacement>(block[MotionOffset..ObjectLinesOffset]);
            if (InterpolatesMotion)
            {
                frameMotion.Backgrounds.CopyTo(motion);
                frameMotion.Objects.CopyTo(motion[FrameMotion.BackgroundCount..]);
                frameMotion.Windows.CopyTo(motion[(FrameMotion.BackgroundCount + FrameMotion.ObjectCount)..]);
            }
            else
            {
                motion.Clear();
            }

            frameMotion.Settle();

            var objectLines = MemoryMarshal.Cast<byte, uint>(block[ObjectLinesOffset..SuppliedColumnsOffset]);
            FindObjectLines(line => frame.OnLine(line, VideoFrame.OAMKilobyte), motion.Slice(FrameMotion.BackgroundCount, FrameMotion.ObjectCount), frame.Lines, objectLines);

            MemoryMarshal.AsBytes<uint>(margins.SuppliedColumns).CopyTo(block[SuppliedColumnsOffset..]);
            MemoryMarshal.AsBytes<ushort>(margins.Tiles).CopyTo(block[MarginTilesOffset..]);
            MemoryMarshal.AsBytes<uint>(margins.WindowEdges).CopyTo(block[MarginWindowsOffset..]);
            _submittedMargin = (uint)margins.Width;

            _frameSizes[slot] = size;
            _currentFrame = slot;
            if (_frameSizes[1 - slot] == 0)
            {
                _frames[1 - slot] = block.ToArray();
                _frameSizes[1 - slot] = size;
            }

            _hasMotion = MemoryMarshal.AsBytes(motion).ContainsAnyExcept((byte)0);
            _hasSubmittedFrame = true;
            _hasNewFrame = true;
            _submittedAt = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Shows the latest frame in the window, as big as it fits, centered, with black around it. It
    /// waits for the display to be ready for another picture, so call it once per refresh.
    /// </summary>
    public void Present(int width, int height)
    {
        if (_swapchain is null)
        {
            throw new InvalidOperationException("This renderer has no window to show frames in.");
        }

        if (width <= 0 || height <= 0)
        {
            return;
        }

        WaitForGPU();
        if (!_swapchain.TryAcquire((uint)width, (uint)height, _acquired, out uint index))
        {
            return;
        }

        var target = _swapchain.ImageAt(index);
        BeginRecording();
        ClearTarget(target);
        if (_hasSubmittedFrame)
        {
            Blit(target, RenderFrame(_swapchain.Width, _swapchain.Height));
        }

        if (_hooks is null)
        {
            Barrier(target, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, AccessFlags.TransferWriteBit, 0, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);
        }
        else
        {
            DrawHooks(_hooks, target, index);
        }

        EndRecording();
        SubmitCommands(_acquired, _swapchain.RenderedAt(index));
        _swapchain.Present(index);
    }

    /// <summary>Draws the latest frame the way <see cref="Present"/> would for a window of that size, without showing it.</summary>
    public void Render(int width, int height)
    {
        WaitForGPU();
        BeginRecording();
        if (_hasSubmittedFrame)
        {
            RenderFrame((uint)width, (uint)height);
        }

        EndRecording();
        SubmitCommands(default, default);
    }

    /// <summary>The last picture drawn, before it was scaled to fit the window. Returns null if nothing was drawn yet.</summary>
    public Screenshot? TakeScreenshot()
    {
        if (_output.Handle == 0)
        {
            return null;
        }

        int size = (int)(_outputWidth * _outputHeight * 4);
        var buffer = CreateBuffer(size, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var memory);
        try
        {
            WaitForGPU();
            BeginRecording();
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(_outputWidth, _outputHeight, 1),
            };
            _vulkan.Api.CmdCopyImageToBuffer(_commandBuffer, _output, ImageLayout.TransferSrcOptimal, buffer, 1, &region);
            EndRecording();

            SubmitCommands(default, default);
            WaitForGPU();

            void* mapped;
            VulkanDevice.Check(_vulkan.Api.MapMemory(_vulkan.Device, memory, 0, (ulong)size, 0, &mapped), "vkMapMemory");
            byte[] pixels = new ReadOnlySpan<byte>(mapped, size).ToArray();
            _vulkan.Api.UnmapMemory(_vulkan.Device, memory);
            return new Screenshot((int)_outputWidth, (int)_outputHeight, pixels);
        }
        finally
        {
            _vulkan.Api.DestroyBuffer(_vulkan.Device, buffer, null);
            _vulkan.Api.FreeMemory(_vulkan.Device, memory, null);
        }
    }

    public void Dispose()
    {
        _vulkan.Api.DeviceWaitIdle(_vulkan.Device);
        if (_hooksInitialized)
        {
            _hooks!.Deinit();
        }

        _swapchain?.Dispose();
        _vulkan.Api.DestroySemaphore(_vulkan.Device, _acquired, null);
        DestroyOutput();
        DestroyBuffers();
        _vulkan.Api.DestroyFence(_vulkan.Device, _fence, null);
        _vulkan.Api.DestroyCommandPool(_vulkan.Device, _commandPool, null);
        _vulkan.Api.DestroyDescriptorPool(_vulkan.Device, _descriptorPool, null);
        _vulkan.Api.DestroyPipeline(_vulkan.Device, _pipeline, null);
        _vulkan.Api.DestroyPipelineLayout(_vulkan.Device, _pipelineLayout, null);
        _vulkan.Api.DestroyDescriptorSetLayout(_vulkan.Device, _descriptorSetLayout, null);
        _vulkan.Dispose();
    }

    internal delegate ReadOnlySpan<byte> OAMOfLine(int line);

    internal static void FindObjectLines(OAMOfLine oamOfLine, ReadOnlySpan<Displacement> displacements, ReadOnlySpan<uint> lines, Span<uint> objectLines)
    {
        objectLines.Clear();
        for (int line = 0; line < Height; line++)
        {
            var oam = oamOfLine(line);
            int cyclesLeft = (lines[line * VideoFrame.WordsPerLine] & HBlankIntervalFree) != 0 ? HBlankFreeLineCycles : LineCycles;
            for (int n = 0; n < FrameMotion.ObjectCount; n++)
            {
                ushort attribute0 = BinaryPrimitives.ReadUInt16LittleEndian(oam[(n * 8)..]);
                ushort attribute1 = BinaryPrimitives.ReadUInt16LittleEndian(oam[((n * 8) + 2)..]);
                bool isAffine = (attribute0 & 0x100) != 0;
                bool isDisabledOrDoubleSize = (attribute0 & 0x200) != 0;
                int shape = attribute0 >> 14;
                if ((!isAffine && isDisabledOrDoubleSize) || shape == 3)
                {
                    continue;
                }

                int size = (shape * 4) + (attribute1 >> 14);
                int scale = isAffine && isDisabledOrDoubleSize ? 2 : 1;
                int width = ObjectWidths[size] * scale;
                int height = ObjectHeights[size] * scale;
                int y = attribute0 & 0xFF;
                int top = y + height > 256 ? y - 256 : y;

                bool isDrawn = true;
                if (line >= top && line < top + height)
                {
                    int cycles = isAffine ? 10 + (width * 2) : width;
                    isDrawn = cyclesLeft >= cycles;
                    cyclesLeft = isDrawn ? cyclesLeft - cycles : 0;
                }

                var displacement = displacements[n];
                int first = (int)MathF.Floor(top + MathF.Min(displacement.From.Y, displacement.To.Y));
                int end = (int)MathF.Ceiling(top + height + MathF.Max(displacement.From.Y, displacement.To.Y));
                if (isDrawn && line >= first && line < end)
                {
                    objectLines[(line * ObjectGroups) + (n / 32)] |= 1u << (n % 32);
                }
            }
        }
    }

    private Rect2D RenderFrame(uint targetWidth, uint targetHeight)
    {
        bool uploaded = TryUpload(out long submittedAt, out bool hasMotion);

        uint viewWidth = Width + (2 * _margin);
        uint width, height;
        if (UsesIntegerScaling)
        {
            uint fit = Math.Max(1, Math.Min(targetWidth / viewWidth, targetHeight / Height));
            (width, height) = (Math.Min(viewWidth * fit, targetWidth), Math.Min(Height * fit, targetHeight));
        }
        else
        {
            width = Math.Min(targetWidth, targetHeight * viewWidth / Height);
            height = width * Height / viewWidth;
        }

        uint scale = Math.Clamp((height + Height - 1) / Height, 1, MaxScale);
        bool outputChanged = EnsureOutput(scale, viewWidth);

        if (uploaded || outputChanged || hasMotion)
        {
            long elapsed = Stopwatch.GetTimestamp() - submittedAt;
            float blend = Math.Clamp((float)elapsed / TicksPerGameFrame, 0, 1);
            RunShader(new DrawParameters
            {
                Scale = scale,
                Blend = blend,
                Margin = _margin,
                CurrentStart = (uint)(_uploadedFrame * _frameCapacity / sizeof(uint)),
                PreviousStart = (uint)((1 - _uploadedFrame) * _frameCapacity / sizeof(uint)),
            }, outputChanged);
        }

        return new Rect2D(new Offset2D((int)(targetWidth - width) / 2, (int)(targetHeight - height) / 2), new Extent2D(width, height));
    }

    private void Blit(Image target, Rect2D destination)
    {
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[1] = new Offset3D((int)_outputWidth, (int)_outputHeight, 1);
        blit.DstOffsets[0] = new Offset3D(destination.Offset.X, destination.Offset.Y, 0);
        blit.DstOffsets[1] = new Offset3D(destination.Offset.X + (int)destination.Extent.Width, destination.Offset.Y + (int)destination.Extent.Height, 1);

        var filter = destination.Extent.Height == _outputHeight ? Filter.Nearest : Filter.Linear;
        _vulkan.Api.CmdBlitImage(_commandBuffer, _output, ImageLayout.TransferSrcOptimal, target, ImageLayout.TransferDstOptimal, 1, &blit, filter);
    }

    private void DrawHooks(IRenderHooks hooks, Image target, uint index)
    {
        if (!_hooksInitialized)
        {
            hooks.Init(new RenderDevice(_vulkan.Api, _vulkan.PhysicalDevice, _vulkan.Device, _vulkan.Queue, _vulkan.QueueFamily));
            _hooksInitialized = true;
        }

        var extent = new Extent2D(_swapchain!.Width, _swapchain.Height);
        hooks.Draw(_commandBuffer, new RenderTarget(target, _swapchain.ViewAt(index), _swapchain.Format, extent, ImageLayout.TransferDstOptimal));
    }

    private void ClearTarget(Image target)
    {
        Barrier(target, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
        var black = new ClearColorValue { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 };
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _vulkan.Api.CmdClearColorImage(_commandBuffer, target, ImageLayout.TransferDstOptimal, &black, 1, &range);
        Barrier(target, ImageLayout.TransferDstOptimal, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
    }

    private bool TryUpload(out long submittedAt, out bool hasMotion)
    {
        lock (_submittedLock)
        {
            submittedAt = _submittedAt;
            hasMotion = _hasMotion;
            if (!_hasNewFrame)
            {
                return false;
            }

            int needed = Math.Max(_frameSizes[0], _frameSizes[1]);
            if (needed > _frameCapacity)
            {
                DestroyBuffers();
                CreateBuffers(needed * 2);
            }

            var staging = new Span<byte>(_staging, 2 * _frameCapacity);
            for (int slot = 0; slot < 2; slot++)
            {
                _frames[slot].AsSpan(0, _frameSizes[slot]).CopyTo(staging[(slot * _frameCapacity)..]);
            }

            _hasNewFrame = false;
            _margin = _submittedMargin;
            _uploadedFrame = _currentFrame;
        }

        var copy = new BufferCopy(0, 0, (ulong)(2 * _frameCapacity));
        _vulkan.Api.CmdCopyBuffer(_commandBuffer, _stagingBuffer, _frameBuffer, 1, &copy);
        var barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _frameBuffer,
            Size = Vk.WholeSize,
        };
        _vulkan.Api.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 1, &barrier, 0, null);
        return true;
    }

    private void RunShader(DrawParameters parameters, bool isNewOutput)
    {
        var oldLayout = isNewOutput ? ImageLayout.Undefined : ImageLayout.TransferSrcOptimal;
        Barrier(_output, oldLayout, ImageLayout.General, AccessFlags.TransferReadBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit);

        var descriptorSet = _descriptorSet;
        _vulkan.Api.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Compute, _pipeline);
        _vulkan.Api.CmdBindDescriptorSets(_commandBuffer, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &descriptorSet, 0, null);
        _vulkan.Api.CmdPushConstants(_commandBuffer, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(DrawParameters), &parameters);
        _vulkan.Api.CmdDispatch(
            _commandBuffer,
            (_outputWidth + ThreadGroupSize - 1) / ThreadGroupSize,
            (_outputHeight + ThreadGroupSize - 1) / ThreadGroupSize,
            1);

        Barrier(_output, ImageLayout.General, ImageLayout.TransferSrcOptimal, AccessFlags.ShaderWriteBit, AccessFlags.TransferReadBit, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.TransferBit);
    }

    private bool EnsureOutput(uint scale, uint viewWidth)
    {
        uint width = viewWidth * scale;
        uint height = Height * scale;
        if (width == _outputWidth && height == _outputHeight)
        {
            return false;
        }

        DestroyOutput();

        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VulkanDevice.Check(_vulkan.Api.CreateImage(_vulkan.Device, in info, null, out _output), "vkCreateImage");

        _vulkan.Api.GetImageMemoryRequirements(_vulkan.Device, _output, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _vulkan.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        VulkanDevice.Check(_vulkan.Api.AllocateMemory(_vulkan.Device, in allocation, null, out _outputMemory), "vkAllocateMemory");
        VulkanDevice.Check(_vulkan.Api.BindImageMemory(_vulkan.Device, _output, _outputMemory, 0), "vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _output,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        VulkanDevice.Check(_vulkan.Api.CreateImageView(_vulkan.Device, in viewInfo, null, out _outputView), "vkCreateImageView");

        var imageInfo = new DescriptorImageInfo { ImageView = _outputView, ImageLayout = ImageLayout.General };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &imageInfo,
        };
        _vulkan.Api.UpdateDescriptorSets(_vulkan.Device, 1, &write, 0, null);

        _outputWidth = width;
        _outputHeight = height;
        return true;
    }

    private void DestroyOutput()
    {
        _vulkan.Api.DestroyImageView(_vulkan.Device, _outputView, null);
        _vulkan.Api.DestroyImage(_vulkan.Device, _output, null);
        _vulkan.Api.FreeMemory(_vulkan.Device, _outputMemory, null);
        (_output, _outputMemory, _outputView) = (default, default, default);
    }

    private void CreateBuffers(int frameCapacity)
    {
        int size = 2 * frameCapacity;
        _frameBuffer = CreateBuffer(size, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit, out _frameMemory);
        _stagingBuffer = CreateBuffer(size, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _stagingMemory);

        void* staging;
        VulkanDevice.Check(_vulkan.Api.MapMemory(_vulkan.Device, _stagingMemory, 0, (ulong)size, 0, &staging), "vkMapMemory");
        _staging = (byte*)staging;
        _frameCapacity = frameCapacity;

        var bufferInfo = new DescriptorBufferInfo { Buffer = _frameBuffer, Range = Vk.WholeSize };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &bufferInfo,
        };
        _vulkan.Api.UpdateDescriptorSets(_vulkan.Device, 1, &write, 0, null);
    }

    private void DestroyBuffers()
    {
        _vulkan.Api.DestroyBuffer(_vulkan.Device, _stagingBuffer, null);
        _vulkan.Api.FreeMemory(_vulkan.Device, _stagingMemory, null);
        _vulkan.Api.DestroyBuffer(_vulkan.Device, _frameBuffer, null);
        _vulkan.Api.FreeMemory(_vulkan.Device, _frameMemory, null);
    }

    private Buffer CreateBuffer(int size, BufferUsageFlags usage, MemoryPropertyFlags properties, out DeviceMemory memory)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = (ulong)size, Usage = usage, SharingMode = SharingMode.Exclusive };
        VulkanDevice.Check(_vulkan.Api.CreateBuffer(_vulkan.Device, in info, null, out var buffer), "vkCreateBuffer");

        _vulkan.Api.GetBufferMemoryRequirements(_vulkan.Device, buffer, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _vulkan.FindMemoryType(requirements.MemoryTypeBits, properties),
        };
        VulkanDevice.Check(_vulkan.Api.AllocateMemory(_vulkan.Device, in allocation, null, out memory), "vkAllocateMemory");
        VulkanDevice.Check(_vulkan.Api.BindBufferMemory(_vulkan.Device, buffer, memory, 0), "vkBindBufferMemory");
        return buffer;
    }

    private void Barrier(Image image, ImageLayout oldLayout, ImageLayout newLayout, AccessFlags srcAccess, AccessFlags dstAccess, PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _vulkan.Api.CmdPipelineBarrier(_commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private void WaitForGPU()
    {
        var fence = _fence;
        VulkanDevice.Check(_vulkan.Api.WaitForFences(_vulkan.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
    }

    private void BeginRecording()
    {
        var fence = _fence;
        VulkanDevice.Check(_vulkan.Api.ResetFences(_vulkan.Device, 1, &fence), "vkResetFences");
        VulkanDevice.Check(_vulkan.Api.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
        var info = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        VulkanDevice.Check(_vulkan.Api.BeginCommandBuffer(_commandBuffer, in info), "vkBeginCommandBuffer");
    }

    private void SubmitCommands(Semaphore wait, Semaphore signal)
    {
        var commandBuffer = _commandBuffer;
        var waitStage = PipelineStageFlags.TransferBit;
        var info = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = wait.Handle == 0 ? 0u : 1,
            PWaitSemaphores = &wait,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = signal.Handle == 0 ? 0u : 1,
            PSignalSemaphores = &signal,
        };
        VulkanDevice.Check(_vulkan.Api.QueueSubmit(_vulkan.Queue, 1, &info, _fence), "vkQueueSubmit");
    }

    private void EndRecording()
    {
        VulkanDevice.Check(_vulkan.Api.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");
    }

    private DescriptorSetLayout CreateDescriptorSetLayout()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new() { Binding = 1, DescriptorType = DescriptorType.StorageImage, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        };
        var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        VulkanDevice.Check(_vulkan.Api.CreateDescriptorSetLayout(_vulkan.Device, in info, null, out var layout), "vkCreateDescriptorSetLayout");
        return layout;
    }

    private PipelineLayout CreatePipelineLayout()
    {
        var descriptorSetLayout = _descriptorSetLayout;
        var pushConstants = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(DrawParameters));
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };
        VulkanDevice.Check(_vulkan.Api.CreatePipelineLayout(_vulkan.Device, in info, null, out var layout), "vkCreatePipelineLayout");
        return layout;
    }

    private Pipeline CreatePipeline()
    {
        using var stream = typeof(FrameRenderer).Assembly.GetManifestResourceStream("Frame.spv")!;
        byte[] code = new byte[stream.Length];
        stream.ReadExactly(code);

        fixed (byte* codePointer = code)
        fixed (byte* entryPoint = "main\0"u8)
        {
            var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)codePointer };
            VulkanDevice.Check(_vulkan.Api.CreateShaderModule(_vulkan.Device, in moduleInfo, null, out var module), "vkCreateShaderModule");
            try
            {
                var info = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = module,
                        PName = entryPoint,
                    },
                    Layout = _pipelineLayout,
                };
                VulkanDevice.Check(_vulkan.Api.CreateComputePipelines(_vulkan.Device, default, 1, in info, null, out var pipeline), "vkCreateComputePipelines");
                return pipeline;
            }
            finally
            {
                _vulkan.Api.DestroyShaderModule(_vulkan.Device, module, null);
            }
        }
    }

    private DescriptorPool CreateDescriptorPool()
    {
        var sizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.StorageBuffer, 1),
            new(DescriptorType.StorageImage, 1),
        };
        var info = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 2, PPoolSizes = sizes };
        VulkanDevice.Check(_vulkan.Api.CreateDescriptorPool(_vulkan.Device, in info, null, out var pool), "vkCreateDescriptorPool");
        return pool;
    }

    private DescriptorSet AllocateDescriptorSet()
    {
        var layout = _descriptorSetLayout;
        var info = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        VulkanDevice.Check(_vulkan.Api.AllocateDescriptorSets(_vulkan.Device, in info, out var set), "vkAllocateDescriptorSets");
        return set;
    }

    private CommandPool CreateCommandPool()
    {
        var info = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _vulkan.QueueFamily,
        };
        VulkanDevice.Check(_vulkan.Api.CreateCommandPool(_vulkan.Device, in info, null, out var pool), "vkCreateCommandPool");
        return pool;
    }

    private CommandBuffer AllocateCommandBuffer()
    {
        var info = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        VulkanDevice.Check(_vulkan.Api.AllocateCommandBuffers(_vulkan.Device, in info, out var commandBuffer), "vkAllocateCommandBuffers");
        return commandBuffer;
    }

    private Fence CreateFence()
    {
        var info = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        VulkanDevice.Check(_vulkan.Api.CreateFence(_vulkan.Device, in info, null, out var fence), "vkCreateFence");
        return fence;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawParameters
    {
        public uint Scale;
        public float Blend;
        public uint Margin;
        public uint CurrentStart;
        public uint PreviousStart;
    }
}
