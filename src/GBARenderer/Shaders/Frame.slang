static const uint WordsPerLine = 28;
static const uint Lines = 160;
static const uint VRAMSize = 0x18000;
static const uint ObjectVRAM = 0x10000;
static const uint TileVRAMEnd = 0x10000;

static const uint LayoutOfLineStart = Lines * WordsPerLine;
static const uint MotionStart = LayoutOfLineStart + Lines;
static const uint FirstWindowMotion = 4 + 128;
static const uint ObjectLinesStart = MotionStart + (FirstWindowMotion + 2) * 4;
static const uint SuppliedColumnsStart = ObjectLinesStart + Lines * 4;
static const uint MarginTilesStart = SuppliedColumnsStart + 4;

static const uint MarginColumnsPerSide = 12;
static const uint MarginRows = 32;
static const float MarginOverscan = 8;

static const uint MarginWindowsStart = MarginTilesStart + 4 * 2 * MarginColumnsPerSide * MarginRows / 2;
static const uint NoMarginWindow = 0x80008000;

static const uint CopiesStartWord = MarginWindowsStart + 2 * Lines;
static const uint LayoutsStart = CopiesStartWord + 1;
static const uint VideoMemoryKilobytes = 98;
static const uint PaletteRAMKilobyte = 96;
static const uint OAMKilobyte = 97;
static const uint WordsPerKilobyte = 0x400 / 4;

struct DrawParameters
{
    uint scale;
    float blend;
    uint margin;
    uint currentStart;
    uint previousStart;
};

[[vk::push_constant]]
ConstantBuffer<DrawParameters> parameters;

[[vk::binding(0, 0)]]
StructuredBuffer<uint> frames;

[[vk::binding(1, 0)]]
[[vk::image_format("rgba8")]]
RWTexture2D<float4> output;

static const uint DISPCNT = 0x00;
static const uint GreenSwap = 0x02;
static const uint BG0CNT = 0x08;
static const uint BG0HOFS = 0x10;
static const uint BG0VOFS = 0x12;
static const uint BG2PA = 0x20;
static const uint WIN0H = 0x40;
static const uint WIN0V = 0x44;
static const uint WININ = 0x48;
static const uint WINOUT = 0x4A;
static const uint MOSAIC = 0x4C;
static const uint BLDCNT = 0x50;
static const uint BLDALPHA = 0x52;
static const uint BLDY = 0x54;

static const uint LayerOBJ = 4;
static const uint LayerBackdrop = 5;

static const uint WindowEffects = 1u << 5;

static const uint Transparent = 0xFFFFFFFF;

struct Frame
{
    uint start;

    uint Register(uint line, uint offset)
    {
        return (frames[start + line * WordsPerLine + (offset >> 2)] >> ((offset & 2) * 8)) & 0xFFFF;
    }

    int ReferencePoint(uint line, uint index)
    {
        return int(frames[start + line * WordsPerLine + 24 + index]);
    }

    uint VideoMemoryWord(uint line, uint kilobyte, uint word)
    {
        uint layout = frames[start + LayoutOfLineStart + line];
        uint entry = LayoutsStart + layout * (VideoMemoryKilobytes / 2) + kilobyte / 2;
        uint copy = (frames[start + entry] >> ((kilobyte & 1) * 16)) & 0xFFFF;
        return frames[start + frames[start + CopiesStartWord] + copy * WordsPerKilobyte + word];
    }

    uint VRAM8(uint line, uint address)
    {
        return address < VRAMSize ? (VideoMemoryWord(line, address >> 10, (address & 0x3FF) >> 2) >> ((address & 3) * 8)) & 0xFF : 0;
    }

    uint VRAM16(uint line, uint address)
    {
        return address < VRAMSize ? (VideoMemoryWord(line, address >> 10, (address & 0x3FF) >> 2) >> ((address & 2) * 8)) & 0xFFFF : 0;
    }

    uint OAM16(uint line, uint address)
    {
        return (VideoMemoryWord(line, OAMKilobyte, (address & 0x3FF) >> 2) >> ((address & 2) * 8)) & 0xFFFF;
    }

    uint PaletteColor(uint line, uint index)
    {
        return (VideoMemoryWord(line, PaletteRAMKilobyte, index >> 1) >> ((index & 1) * 16)) & 0x7FFF;
    }

    uint ObjectsOnLine(uint line, uint group)
    {
        return frames[start + ObjectLinesStart + line * 4 + group];
    }

    uint SuppliedColumns(uint bg)
    {
        return frames[start + SuppliedColumnsStart + bg];
    }

    float2 DisplacementFrom(uint layer)
    {
        uint at = start + MotionStart + layer * 4;
        return float2(asfloat(frames[at]), asfloat(frames[at + 1]));
    }

    float2 DisplacementTo(uint layer)
    {
        uint at = start + MotionStart + layer * 4;
        return float2(asfloat(frames[at + 2]), asfloat(frames[at + 3]));
    }

    uint MarginWindow(uint window, uint line)
    {
        return frames[start + MarginWindowsStart + window * Lines + line];
    }

    uint MarginEntry(uint bg, int column, int row)
    {
        uint index = column <= 0 ? uint(-column) : uint(column - 30) + MarginColumnsPerSide;
        if (index >= 2 * MarginColumnsPerSide || (SuppliedColumns(bg) & (1u << index)) == 0 || row < 0 || row >= int(MarginRows))
        {
            return Transparent;
        }

        uint entry = (bg * 2 * MarginColumnsPerSide + index) * MarginRows + uint(row);
        return (frames[start + MarginTilesStart + (entry >> 1)] >> ((entry & 1) * 16)) & 0xFFFF;
    }
};

static Frame current;
static Frame previous;

int SignExtend(uint value, uint bits)
{
    return int(value << (32 - bits)) >> (32 - bits);
}

int Modulo(int value, uint divisor)
{
    return int(uint(value % int(divisor) + int(divisor)) % divisor);
}

float2 Displacement(uint layer)
{
    return lerp(current.DisplacementFrom(layer), current.DisplacementTo(layer), parameters.blend);
}

bool IsMoving(uint layer)
{
    return any(current.DisplacementFrom(layer) != current.DisplacementTo(layer));
}

float2 ShownInPrevious(uint layer, float2 position, float2 displacement)
{
    float2 sinceFirstShown = displacement - current.DisplacementFrom(layer);
    return position - sinceFirstShown - previous.DisplacementTo(layer);
}

bool IsSupplied(uint line, uint bg, float2 position)
{
    int scrollX = int(current.Register(line, BG0HOFS + bg * 4) & 0x1FF);
    int scrollY = int(current.Register(line, BG0VOFS + bg * 4) & 0x1FF);
    int column = ((int(floor(position.x)) + scrollX) >> 3) - (scrollX >> 3);
    int row = ((int(floor(position.y)) + scrollY) >> 3) - (scrollY >> 3);
    return current.MarginEntry(bg, column, row) != Transparent;
}

uint BeyondTileVRAM(Frame source, uint line, uint entry, bool is8Bit, uint tileX)
{
    uint dispcnt = source.Register(line, DISPCNT);
    uint textBackgrounds = (dispcnt >> 8) & ((dispcnt & 7) == 0 ? 0xF : 0x7);
    if (countbits(textBackgrounds) != 1)
    {
        return Transparent;
    }

    if (is8Bit)
    {
        uint index = (tileX & 1) != 0 ? entry >> 8 : entry & 0xFF;
        return index == 0 ? Transparent : source.PaletteColor(line, index);
    }

    uint data = (tileX & 2) != 0 ? entry >> 8 : entry & 0xFF;
    uint index = (data >> ((tileX & 1) * 4)) & 0xF;
    return index == 0 ? Transparent : source.PaletteColor(line, (entry >> 12) * 16 + index);
}

uint TextBackgroundPixel(Frame source, uint line, uint bg, int2 pixel)
{
    uint control = source.Register(line, BG0CNT + bg * 2);
    uint tileBase = ((control >> 2) & 3) * 0x4000;
    uint mapBase = ((control >> 8) & 0x1F) * 0x800;
    bool is8Bit = (control & 0x80) != 0;
    uint size = control >> 14;

    int scrollX = int(source.Register(line, BG0HOFS + bg * 4) & 0x1FF);
    int scrollY = int(source.Register(line, BG0VOFS + bg * 4) & 0x1FF);
    int mapX = pixel.x + scrollX;
    int mapY = pixel.y + scrollY;
    uint x = uint(mapX) & 0x1FF;
    uint y = uint(mapY) & 0x1FF;

    int column = (mapX >> 3) - (scrollX >> 3);
    bool isPastScreen = column < 0 || column > 30 || (parameters.margin > 0 && (pixel.x < 0 || pixel.x >= 240));

    uint entry;
    if (isPastScreen)
    {
        entry = source.MarginEntry(bg, column, (mapY >> 3) - (scrollY >> 3));
        if (entry == Transparent)
        {
            return Transparent;
        }
    }
    else
    {
        uint blockX = (size & 1) != 0 ? (x >> 8) & 1 : 0;
        uint blockY = (size & 2) != 0 ? (y >> 8) & 1 : 0;
        uint block = size == 3 ? blockY * 2 + blockX : blockX + blockY;

        entry = source.VRAM16(line, mapBase + block * 0x800 + (((y >> 3) & 31) * 32 + ((x >> 3) & 31)) * 2);
    }

    uint tile = entry & 0x3FF;
    uint tileX = (entry & 0x400) != 0 ? 7 - (x & 7) : x & 7;
    uint tileY = (entry & 0x800) != 0 ? 7 - (y & 7) : y & 7;

    if (tileBase + tile * (is8Bit ? 64 : 32) >= TileVRAMEnd)
    {
        return BeyondTileVRAM(source, line, entry, is8Bit, tileX);
    }

    if (is8Bit)
    {
        uint index = source.VRAM8(line, tileBase + tile * 64 + tileY * 8 + tileX);
        return index == 0 ? Transparent : source.PaletteColor(line, index);
    }

    uint index = (source.VRAM8(line, tileBase + tile * 32 + tileY * 4 + (tileX >> 1)) >> ((tileX & 1) * 4)) & 0xF;
    return index == 0 ? Transparent : source.PaletteColor(line, (entry >> 12) * 16 + index);
}

uint AffineBackgroundPixel(Frame source, uint line, uint bg, uint mode, int2 pixel)
{
    uint control = source.Register(line, BG0CNT + bg * 2);
    uint matrix = BG2PA + (bg - 2) * 0x10;
    int pa = SignExtend(source.Register(line, matrix), 16);
    int pb = SignExtend(source.Register(line, matrix + 2), 16);
    int pc = SignExtend(source.Register(line, matrix + 4), 16);
    int pd = SignExtend(source.Register(line, matrix + 6), 16);

    int down = pixel.y - int(line);
    int textureX = (source.ReferencePoint(line, (bg - 2) * 2) + pa * pixel.x + pb * down) >> 8;
    int textureY = (source.ReferencePoint(line, (bg - 2) * 2 + 1) + pc * pixel.x + pd * down) >> 8;

    if (mode >= 3)
    {
        int width = mode == 5 ? 160 : 240;
        int height = mode == 5 ? 128 : 160;
        if (textureX < 0 || textureX >= width || textureY < 0 || textureY >= height)
        {
            return Transparent;
        }

        uint texel = uint(textureY * width + textureX);
        uint displayFrame = mode != 3 && (source.Register(line, DISPCNT) & 0x10) != 0 ? 0xA000 : 0;
        if (mode == 4)
        {
            uint index = source.VRAM8(line, displayFrame + texel);
            return index == 0 ? Transparent : source.PaletteColor(line, index);
        }

        return source.VRAM16(line, displayFrame + texel * 2) & 0x7FFF;
    }

    int size = 128 << (control >> 14);
    if ((control & 0x2000) != 0)
    {
        textureX &= size - 1;
        textureY &= size - 1;
    }
    else if (textureX < 0 || textureX >= size || textureY < 0 || textureY >= size)
    {
        return Transparent;
    }

    uint tileBase = ((control >> 2) & 3) * 0x4000;
    uint mapBase = ((control >> 8) & 0x1F) * 0x800;
    uint tile = source.VRAM8(line, mapBase + uint(textureY >> 3) * uint(size >> 3) + uint(textureX >> 3));
    uint index = source.VRAM8(line, tileBase + tile * 64 + uint(textureY & 7) * 8 + uint(textureX & 7));
    return index == 0 ? Transparent : source.PaletteColor(line, index);
}

uint BackgroundPixel(uint line, uint bg, uint mode, float2 position)
{
    if ((current.Register(line, BG0CNT + bg * 2) & 0x40) != 0)
    {
        uint mosaic = current.Register(line, MOSAIC);
        int2 pixel = int2(floor(position));
        pixel.x -= Modulo(pixel.x, (mosaic & 0xF) + 1);
        pixel.y -= Modulo(pixel.y, ((mosaic >> 4) & 0xF) + 1);
        position = float2(pixel);
    }

    Frame source = current;
    float2 displacement = Displacement(bg);
    float2 shown = position - displacement;

    bool isWide = current.SuppliedColumns(bg) != 0;
    bool isOutside = shown.y < 0 || shown.y >= 161 || (!isWide && (shown.x < 0 || shown.x >= 241));
    if (isOutside && IsMoving(bg))
    {
        source = previous;
        shown = ShownInPrevious(bg, position, displacement);
    }

    float reach = float(parameters.margin) + (isWide ? MarginOverscan : 0);
    int2 pixel = int2(floor(clamp(shown, float2(-reach, 0), float2(240.99 + reach, 160.99))));

    bool isText = mode == 0 || (mode == 1 && bg < 2);
    return isText ? TextBackgroundPixel(source, line, bg, pixel) : AffineBackgroundPixel(source, line, bg, mode, pixel);
}

bool BackgroundExists(uint bg, uint mode)
{
    switch (mode)
    {
        case 0: return true;
        case 1: return bg < 3;
        case 2: return bg >= 2;
        case 3:
        case 4:
        case 5: return bg == 2;
        default: return false;
    }
}

struct ObjectPixel
{
    uint color;
    uint priority;
    bool isSemiTransparent;
    bool isInObjectWindow;
};

ObjectPixel NoObject()
{
    ObjectPixel none;
    none.color = Transparent;
    none.priority = 4;
    none.isSemiTransparent = false;
    none.isInObjectWindow = false;
    return none;
}

static const uint ObjectSizes[12] =
{
    0x0808, 0x1010, 0x2020, 0x4040,
    0x0810, 0x0820, 0x1020, 0x2040,
    0x1008, 0x2008, 0x2010, 0x4020,
};

uint ObjectTexel(uint line, uint attribute0, uint attribute2, uint mode, bool isOneDimensional, uint width, uint2 texel)
{
    uint tile = attribute2 & 0x3FF;
    if (mode >= 3 && tile < 512)
    {
        return Transparent;
    }

    bool is8Bit = (attribute0 & 0x2000) != 0;
    uint tileStep = is8Bit ? 2 : 1;
    uint2 tileOffset = texel >> 3;
    uint2 pixel = texel & 7;

    uint firstTile = isOneDimensional || !is8Bit ? tile : tile & ~1u;
    uint rowStep = isOneDimensional ? (width >> 3) * tileStep : 32;
    uint unit = firstTile + tileOffset.y * rowStep + tileOffset.x * tileStep;

    if (is8Bit)
    {
        uint index = current.VRAM8(line, ObjectVRAM + ((unit * 32 + pixel.y * 8 + pixel.x) & 0x7FFF));
        return index == 0 ? Transparent : current.PaletteColor(line, 256 + index);
    }

    uint data = current.VRAM8(line, ObjectVRAM + ((unit * 32 + pixel.y * 4 + (pixel.x >> 1)) & 0x7FFF));
    uint index = (data >> ((pixel.x & 1) * 4)) & 0xF;
    return index == 0 ? Transparent : current.PaletteColor(line, 256 + (attribute2 >> 12) * 16 + index);
}

ObjectPixel ObjectPixelAt(uint line, uint mode, float2 position)
{
    ObjectPixel result = NoObject();

    uint dispcnt = current.Register(line, DISPCNT);
    bool isOneDimensional = (dispcnt & 0x40) != 0;
    bool findsObjectWindow = (dispcnt & 0x8000) != 0;
    uint mosaic = current.Register(line, MOSAIC);
    int2 screen = int2(floor(position));

    for (uint group = 0; group < 4; group++)
    {
        uint objects = current.ObjectsOnLine(line, group);
        while (objects != 0)
        {
            uint n = group * 32 + firstbitlow(objects);
            objects &= objects - 1;

            uint attribute0 = current.OAM16(line, n * 8);
            bool isWindow = ((attribute0 >> 10) & 3) == 2;
            if (isWindow ? !findsObjectWindow || result.isInObjectWindow : result.color != Transparent)
            {
                continue;
            }

            uint attribute1 = current.OAM16(line, n * 8 + 2);
            uint attribute2 = current.OAM16(line, n * 8 + 4);
            bool isAffine = (attribute0 & 0x100) != 0;
            bool isDoubleSize = isAffine && (attribute0 & 0x200) != 0;

            uint size = ObjectSizes[(attribute0 >> 14) * 4 + (attribute1 >> 14)];
            int width = int(size & 0xFF);
            int height = int(size >> 8);
            int boundsWidth = isDoubleSize ? width * 2 : width;
            int boundsHeight = isDoubleSize ? height * 2 : height;

            int left = int(attribute1 & 0x1FF);
            int top = int(attribute0 & 0xFF);
            if (left >= 384) left -= 512;
            if (top + boundsHeight > 256) top -= 256;

            int2 texel = int2(floor(position - float2(float(left), float(top)) - Displacement(4 + n)));
            if (texel.x < 0 || texel.x >= boundsWidth || texel.y < 0 || texel.y >= boundsHeight)
            {
                continue;
            }

            if ((attribute0 & 0x1000) != 0)
            {
                texel.x = max(texel.x - Modulo(screen.x, ((mosaic >> 8) & 0xF) + 1), 0);
                texel.y = max(texel.y - Modulo(screen.y, (mosaic >> 12) + 1), 0);
            }

            if (isAffine)
            {
                uint parameterGroup = ((attribute1 >> 9) & 0x1F) * 32;
                int pa = SignExtend(current.OAM16(line, parameterGroup + 6), 16);
                int pb = SignExtend(current.OAM16(line, parameterGroup + 14), 16);
                int pc = SignExtend(current.OAM16(line, parameterGroup + 22), 16);
                int pd = SignExtend(current.OAM16(line, parameterGroup + 30), 16);

                int2 fromCenter = texel - int2(boundsWidth, boundsHeight) / 2;
                texel = int2((pa * fromCenter.x + pb * fromCenter.y) >> 8, (pc * fromCenter.x + pd * fromCenter.y) >> 8) + int2(width, height) / 2;
                if (texel.x < 0 || texel.x >= width || texel.y < 0 || texel.y >= height)
                {
                    continue;
                }
            }
            else
            {
                if ((attribute1 & 0x1000) != 0) texel.x = width - 1 - texel.x;
                if ((attribute1 & 0x2000) != 0) texel.y = height - 1 - texel.y;
            }

            uint color = ObjectTexel(line, attribute0, attribute2, mode, isOneDimensional, uint(width), uint2(texel));
            if (color == Transparent)
            {
                continue;
            }

            if (isWindow)
            {
                result.isInObjectWindow = true;
            }
            else
            {
                result.color = color;
                result.priority = (attribute2 >> 10) & 3;
                result.isSemiTransparent = ((attribute0 >> 10) & 3) == 1;
            }

            if (result.color != Transparent && (!findsObjectWindow || result.isInObjectWindow))
            {
                return result;
            }
        }
    }

    return result;
}

bool IsInsideSpan(int x, int left, int right)
{
    return left <= right ? x >= left && x < right : x >= left || x < right;
}

bool IsInsideWindow(uint window, float2 position)
{
    uint layer = FirstWindowMotion + window;
    float2 displacement = Displacement(layer);
    float2 shown = position - displacement;

    Frame source = current;
    if ((shown.y < 0 || shown.y >= 160) && IsMoving(layer))
    {
        source = previous;
        shown = ShownInPrevious(layer, position, displacement);
    }

    uint line = uint(clamp(floor(shown.y), 0, 159));
    int x = int(floor(shown.x));

    uint vertical = source.Register(line, WIN0V + window * 2);
    uint y1 = vertical >> 8, y2 = vertical & 0xFF;
    if (y2 > 160 || y1 > y2) y2 = 160;
    if (line < y1 || line >= y2)
    {
        return false;
    }

    if (x < 0 || x >= 240)
    {
        uint edges = source.MarginWindow(window, line);
        if (edges != NoMarginWindow)
        {
            return IsInsideSpan(x, SignExtend(edges & 0xFFFF, 16), SignExtend(edges >> 16, 16));
        }

        x = clamp(x, 0, 239);
    }

    uint horizontal = source.Register(line, WIN0H + window * 2);
    int x1 = int(horizontal >> 8), x2 = int(horizontal & 0xFF);
    if (x1 > 240 && x1 > x2) x1 = 0;
    if (x2 > 240)
    {
        x2 = 240;
        x1 = min(x1, 240);
    }

    return IsInsideSpan(x, x1, x2);
}

uint WindowControl(uint line, float2 position, bool isInObjectWindow)
{
    uint dispcnt = current.Register(line, DISPCNT);
    if ((dispcnt & 0xE000) == 0)
    {
        return 0x3F;
    }

    if ((dispcnt & 0x2000) != 0 && IsInsideWindow(0, position))
    {
        return current.Register(line, WININ) & 0x3F;
    }

    if ((dispcnt & 0x4000) != 0 && IsInsideWindow(1, position))
    {
        return (current.Register(line, WININ) >> 8) & 0x3F;
    }

    if ((dispcnt & 0x8000) != 0 && isInObjectWindow)
    {
        return (current.Register(line, WINOUT) >> 8) & 0x3F;
    }

    return current.Register(line, WINOUT) & 0x3F;
}

uint3 Channels(uint color)
{
    return uint3(color & 31, (color >> 5) & 31, (color >> 10) & 31);
}

uint3 ColorAt(float2 position)
{
    uint line = uint(position.y);

    bool isInMargin = position.x < 0 || position.x >= 240;

    uint dispcnt = current.Register(line, DISPCNT);
    if ((dispcnt & 0x80) != 0)
    {
        return uint3(31, 31, 31);
    }

    uint mode = dispcnt & 7;

    ObjectPixel object = NoObject();
    if ((dispcnt & 0x1000) != 0)
    {
        object = ObjectPixelAt(line, mode, position);
    }

    uint window = WindowControl(line, position, object.isInObjectWindow);
    if ((window & (1u << LayerOBJ)) == 0)
    {
        object.color = Transparent;
    }

    uint topColor = current.PaletteColor(line, 0), topLayer = LayerBackdrop;
    uint underColor = topColor, underLayer = LayerBackdrop;
    uint found = 0;
    bool isSupplied = !isInMargin;

    for (uint priority = 0; priority < 4 && found < 2; priority++)
    {
        if (object.color != Transparent && object.priority == priority)
        {
            if (found == 0) { topColor = object.color; topLayer = LayerOBJ; }
            else { underColor = object.color; underLayer = LayerOBJ; }
            found++;
        }

        for (uint bg = 0; bg < 4 && found < 2; bg++)
        {
            bool isShown = (dispcnt & (0x100u << bg)) != 0 && (window & (1u << bg)) != 0 && BackgroundExists(bg, mode);
            if (!isShown || (current.Register(line, BG0CNT + bg * 2) & 3) != priority)
            {
                continue;
            }

            isSupplied = isSupplied || IsSupplied(line, bg, position - Displacement(bg));

            uint color = BackgroundPixel(line, bg, mode, position);
            if (color == Transparent)
            {
                continue;
            }

            if (found == 0) { topColor = color; topLayer = bg; }
            else { underColor = color; underLayer = bg; }
            found++;
        }
    }

    if (!isSupplied && found == 0)
    {
        return uint3(0, 0, 0);
    }

    uint3 color = Channels(topColor);

    if ((window & WindowEffects) != 0)
    {
        uint blend = current.Register(line, BLDCNT);
        uint effect = (blend >> 6) & 3;
        bool isSemiTransparent = topLayer == LayerOBJ && object.isSemiTransparent;
        bool topIsFirstTarget = isSemiTransparent || (blend & (1u << topLayer)) != 0;
        bool underIsSecondTarget = (blend & (0x100u << underLayer)) != 0;

        if (underIsSecondTarget && (isSemiTransparent || (effect == 1 && topIsFirstTarget)))
        {
            uint alpha = current.Register(line, BLDALPHA);
            uint eva = min(alpha & 0x1F, 16);
            uint evb = min((alpha >> 8) & 0x1F, 16);
            color = min((color * eva + Channels(underColor) * evb) >> 4, uint3(31, 31, 31));
        }
        else if (effect == 2 && topIsFirstTarget)
        {
            uint evy = min(current.Register(line, BLDY) & 0x1F, 16);
            color += ((31 - color) * evy) >> 4;
        }
        else if (effect == 3 && topIsFirstTarget)
        {
            uint evy = min(current.Register(line, BLDY) & 0x1F, 16);
            color -= (color * evy) >> 4;
        }
    }

    return color;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void DrawFrame(uint3 id : SV_DispatchThreadID)
{
    current.start = parameters.currentStart;
    previous.start = parameters.previousStart;

    float2 position = float2(id.xy) / float(parameters.scale) - float2(float(parameters.margin), 0);
    if (position.x >= float(240 + parameters.margin) || position.y >= 160)
    {
        return;
    }

    bool isInMargin = position.x < 0 || position.x >= 240;
    bool hasMargins = (current.SuppliedColumns(0) | current.SuppliedColumns(1) | current.SuppliedColumns(2) | current.SuppliedColumns(3)) != 0;
    if (isInMargin && !hasMargins)
    {
        output[id.xy] = float4(0, 0, 0, 1);
        return;
    }

    uint3 color = ColorAt(position);
    if ((current.Register(uint(position.y), GreenSwap) & 1) != 0)
    {
        float2 neighbor = float2(float(int(floor(position.x)) ^ 1) + frac(position.x), position.y);
        color.g = ColorAt(neighbor).g;
    }

    output[id.xy] = float4(float3(color) / 31.0, 1);
}
