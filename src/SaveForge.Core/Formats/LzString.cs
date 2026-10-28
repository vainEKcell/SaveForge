using System.Text;

namespace SaveForge.Core.Formats;

/// <summary>
/// LZString 压缩算法（lz-string.js 的 C# 移植，Base64 变体）。
///
/// RPG Maker MV / MZ 的存档就是 LZString.compressToBase64(JSON)。
/// 算法本质是 LZW：字典随编码增长，位宽按 enlargeIn 递增。
///
/// 注意：与原实现的位序必须严格一致（低位先写），否则解压会得到乱码。
/// </summary>
internal static class LzString
{
    private const string Base64Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";

    private static readonly int[] Base64Reverse = BuildReverse();

    private static int[] BuildReverse()
    {
        var reverse = new int[128];
        for (int i = 0; i < reverse.Length; i++) reverse[i] = 0;

        for (int i = 0; i < Base64Alphabet.Length; i++)
            reverse[Base64Alphabet[i]] = i;

        return reverse;
    }

    // ====================================================================
    // 对外 API
    // ====================================================================

    public static string CompressToBase64(string input) => Compress(input, 6);

    public static string DecompressFromBase64(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        return Decompress(input.Length, 32, index =>
            index < input.Length && input[index] < 128 ? Base64Reverse[input[index]] : 0);
    }

    // ====================================================================
    // 压缩
    // ====================================================================

    private static string Compress(string input, int bitsPerChar)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var dictionary = new Dictionary<string, int>();
        var pending = new HashSet<string>();

        string contextW = string.Empty;
        int enlargeIn = 2;
        int dictSize = 3;
        int numBits = 2;

        var data = new StringBuilder();
        int dataVal = 0;
        int dataPosition = 0;

        void Emit(int value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                dataVal = (dataVal << 1) | (value & 1);

                if (dataPosition == bitsPerChar - 1)
                {
                    data.Append(Base64Alphabet[dataVal]);
                    dataPosition = 0;
                    dataVal = 0;
                }
                else
                {
                    dataPosition++;
                }

                value >>= 1;
            }
        }

        void WriteLiteralChar(char c)
        {
            if (c < 256)
            {
                Emit(0, numBits);
                Emit(c, 8);
            }
            else
            {
                Emit(1, numBits);
                Emit(c, 16);
            }
        }

        foreach (char contextC in input)
        {
            string c = contextC.ToString();

            if (!dictionary.ContainsKey(c))
            {
                dictionary[c] = dictSize++;
                pending.Add(c);
            }

            string contextWc = contextW + c;

            if (dictionary.ContainsKey(contextWc))
            {
                contextW = contextWc;
                continue;
            }

            if (pending.Contains(contextW))
            {
                WriteLiteralChar(contextW[0]);
                enlargeIn--;

                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }

                pending.Remove(contextW);
            }
            else
            {
                Emit(dictionary[contextW], numBits);
            }

            enlargeIn--;

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }

            dictionary[contextWc] = dictSize++;
            contextW = c;
        }

        if (contextW.Length > 0)
        {
            if (pending.Contains(contextW))
            {
                WriteLiteralChar(contextW[0]);
                enlargeIn--;

                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }

                pending.Remove(contextW);
            }
            else
            {
                Emit(dictionary[contextW], numBits);
            }

            enlargeIn--;

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }

        // 结束标记
        Emit(2, numBits);

        // 冲刷剩余位
        while (true)
        {
            dataVal <<= 1;

            if (dataPosition == bitsPerChar - 1)
            {
                data.Append(Base64Alphabet[dataVal]);
                break;
            }

            dataPosition++;
        }

        return data.ToString();
    }

    // ====================================================================
    // 解压
    // ====================================================================

    private static string Decompress(int length, int resetValue, Func<int, int> getNextValue)
    {
        var dictionary = new Dictionary<int, string>();
        for (int i = 0; i < 3; i++) dictionary[i] = ((char)i).ToString();

        int enlargeIn = 4;
        int dictSize = 4;
        int numBits = 3;

        int dataVal = getNextValue(0);
        int dataPosition = resetValue;
        int dataIndex = 1;

        int ReadBits(int count)
        {
            int bits = 0;
            int power = 1;
            int maxPower = 1 << count;

            while (power != maxPower)
            {
                int resb = dataVal & dataPosition;
                dataPosition >>= 1;

                if (dataPosition == 0)
                {
                    dataPosition = resetValue;
                    dataVal = getNextValue(dataIndex++);
                }

                if (resb > 0) bits |= power;
                power <<= 1;
            }

            return bits;
        }

        string ReadCharLiteral()
        {
            int next = ReadBits(2);

            return next switch
            {
                0 => ((char)ReadBits(8)).ToString(),
                1 => ((char)ReadBits(16)).ToString(),
                _ => string.Empty,
            };
        }

        string c = ReadCharLiteral();
        if (c.Length == 0) return string.Empty;

        dictionary[3] = c;

        var result = new StringBuilder();
        result.Append(c);

        string w = c;

        while (true)
        {
            if (dataIndex > length) return string.Empty;

            int code = ReadBits(numBits);

            switch (code)
            {
                case 0:
                    dictionary[dictSize++] = ((char)ReadBits(8)).ToString();
                    code = dictSize - 1;
                    enlargeIn--;
                    break;

                case 1:
                    dictionary[dictSize++] = ((char)ReadBits(16)).ToString();
                    code = dictSize - 1;
                    enlargeIn--;
                    break;

                case 2:
                    return result.ToString();
            }

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }

            string entry;

            if (dictionary.TryGetValue(code, out string? existing))
            {
                entry = existing;
            }
            else if (code == dictSize)
            {
                entry = w + w[0];
            }
            else
            {
                return string.Empty;
            }

            result.Append(entry);

            dictionary[dictSize++] = w + entry[0];
            enlargeIn--;
            w = entry;

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }
    }
}
