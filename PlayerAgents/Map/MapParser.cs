using System;

namespace PlayerAgents.Map;

internal static class MapParser
{
    public static byte FindType(byte[] bytes)
    {
        if (bytes.Length < 4) return 0;
        if (bytes[2] == 0x43 && bytes[3] == 0x23) return 100;
        if (bytes[0] == 0) return 5;
        if (bytes[0] == 0x0F && bytes[5] == 0x53 && bytes[14] == 0x33) return 6;
        if (bytes[0] == 0x15 && bytes[4] == 0x32 && bytes[6] == 0x41 && bytes[19] == 0x31) return 4;
        if (bytes[0] == 0x10 && bytes[2] == 0x61 && bytes[7] == 0x31 && bytes[14] == 0x31) return 1;
        if ((bytes[4] == 0x0F || bytes[4] == 0x03) && bytes[18] == 0x0D && bytes[19] == 0x0A)
        {
            int w = bytes[0] + (bytes[1] << 8);
            int h = bytes[2] + (bytes[3] << 8);
            if (bytes.Length > (52 + (w * h * 14))) return 3;
            return 2;
        }
        if (bytes[0] == 0x0D && bytes[1] == 0x4C && bytes[7] == 0x20 && bytes[11] == 0x6D) return 7;
        return 0;
    }

    private static void Ensure(ref bool[,] cells, int width, int height)
    {
        if (cells.GetLength(0) == width && cells.GetLength(1) == height) return;
        cells = new bool[width, height];
    }

    public static bool[,] LoadV0(byte[] bytes)
    {
        int offset = 0;
        int width = BitConverter.ToInt16(bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(bytes, offset); offset += 2;
        bool[,] walk = new bool[width, height];
        offset = 52;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 4;
                offset += 4;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV1(byte[] bytes)
    {
        int offset = 21;
        int w = BitConverter.ToInt16(bytes, offset); offset += 2;
        int xor = BitConverter.ToInt16(bytes, offset); offset += 2;
        int h = BitConverter.ToInt16(bytes, offset); offset += 2;
        int width = w ^ xor;
        int height = h ^ xor;
        bool[,] walk = new bool[width, height];
        offset = 54;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                if (((BitConverter.ToInt32(bytes, offset) ^ 0xAA38AA38) & 0x20000000) != 0) walkable = false;
                offset += 6;
                if (((BitConverter.ToInt16(bytes, offset) ^ xor) & 0x8000) != 0) walkable = false;
                offset += 2;
                offset += 6;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV2(byte[] bytes)
    {
        int offset = 0;
        int width = BitConverter.ToInt16(bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(bytes, offset); offset += 2;
        bool[,] walk = new bool[width, height];
        offset = 52;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                offset += 7;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV3(byte[] bytes)
    {
        int offset = 0;
        int width = BitConverter.ToInt16(bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(bytes, offset); offset += 2;
        bool[,] walk = new bool[width, height];
        offset = 52;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                offset += 17;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV4(byte[] bytes)
    {
        int offset = 31;
        int w = BitConverter.ToInt16(bytes, offset); offset += 2;
        int xor = BitConverter.ToInt16(bytes, offset); offset += 2;
        int h = BitConverter.ToInt16(bytes, offset); offset += 2;
        int width = w ^ xor;
        int height = h ^ xor;
        bool[,] walk = new bool[width, height];
        offset = 64;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                offset += 6;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV5(byte[] bytes)
    {
        int offset = 22;
        int width = BitConverter.ToInt16(bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(bytes, offset); offset += 2;
        bool[,] walk = new bool[width, height];
        offset = 28 + (3 * ((width / 2) + (width % 2)) * (height / 2));
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                byte flag = bytes[offset];
                if ((flag & 0x01) != 1) walkable = false;
                else if ((flag & 0x02) != 2) walkable = false;
                offset += 14;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV6(byte[] bytes)
    {
        int offset = 16;
        int width = BitConverter.ToInt16(bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(bytes, offset); offset += 2;
        bool[,] walk = new bool[width, height];
        offset = 40;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                byte flag = bytes[offset];
                if ((flag & 0x01) != 1) walkable = false;
                else if ((flag & 0x02) != 2) walkable = false;
                offset += 20;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV7(byte[] bytes)
    {
        int offset = 21;
        int width = BitConverter.ToInt16(bytes, offset); offset += 4;
        int height = BitConverter.ToInt16(bytes, offset); offset += 2;
        bool[,] walk = new bool[width, height];
        offset = 54;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 6;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                offset += 7;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }

    public static bool[,] LoadV100(byte[] bytes)
    {
        int offset = 4;
        int width = BitConverter.ToInt16(bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(bytes, offset); offset += 2;

        bool[,] walk = new bool[width, height];
        offset = 8;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool walkable = true;
                offset += 2;
                if ((BitConverter.ToInt32(bytes, offset) & 0x20000000) != 0) walkable = false;
                offset += 10;
                if ((BitConverter.ToInt16(bytes, offset) & 0x8000) != 0) walkable = false;
                offset += 2;
                offset += 11;
                offset += 1;
                walk[x, y] = walkable;
            }
        }
        return walk;
    }
}
