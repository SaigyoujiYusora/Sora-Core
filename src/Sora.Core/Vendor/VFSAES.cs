// SPDX-License-Identifier: MIT; see licenses/AnimeStudio-MIT.txt
namespace Sora.Core.Vendor;
    internal static class VFSAES
    {
        // funky decrypt with cbc encrypt
        private static byte[] SBox = null!;
        private static byte[] Key = null!;
        private static byte[] IV = null!;
        private static ulong XORKey;

        public static void InitKeys(byte[] VFSSBox, byte[] VFSKey, byte[] VFSIV, ulong VFSXORKey)
        {
            SBox = VFSSBox;
            Key = VFSKey;
            IV = VFSIV;
            XORKey = VFSXORKey;
        }

        public static byte[] Decrypt(byte[] ciphertext)
        {
            byte[] iv = IV;
            List<byte[]> blocks = new();
            byte[] previous = iv.ToArray();

            foreach (var ct in SplitBlocks(ciphertext))
            {
                // encrypt IV
                byte[] block = EncryptBlock(previous);
                // xor keystream with ciphertext
                byte[] pt = new byte[ct.Length];
                for (int i = 0; i < ct.Length; i++) pt[i] = (byte)(ct[i] ^ block[i]);
                blocks.Add(pt);
                // derive next IV
                byte[] nextIv = new byte[16];
                int count = 0;

                for (int i = 0; i < 16; i++)
                {
                    ulong shiftSrc = XORKey >> (count & 0x38);
                    byte temp = (byte)(block[i] ^ (31 * i) ^ (byte)shiftSrc);
                    count += 8;
                    temp = (byte)((((temp >> 5) | (8 * temp)) & 0xFF));
                    temp = SBox[temp];
                    nextIv[i] = temp;
                }
                // next IV
                previous = nextIv;
            }

            return blocks.SelectMany(b => b).ToArray();
        }

        private static byte[] EncryptBlock(byte[] plaintext)
        {
            List<byte[,]> keyMats = ExpandKey();
            int nRounds = 10;
            var state = BytesToMatrix(plaintext);

            AddRoundKey(state, keyMats[0]);

            for (int r = 1; r < nRounds; r++)
            {
                SubBytes(state);
                ShiftRows(state);
                MixColumns(state);
                AddRoundKey(state, keyMats[r]);
            }

            SubBytes(state);
            ShiftRows(state);
            AddRoundKey(state, keyMats[^1]);

            return MatrixToBytes(state);
        }

        // helpers
        private static IEnumerable<byte[]> SplitBlocks(byte[] msg, int blockSize = 16)
        {
            for (int i = 0; i < msg.Length; i += blockSize)
                yield return msg.Skip(i).Take(blockSize).ToArray();
        }

        private static void SubBytes(List<byte[]> s)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    s[i][j] = SBox[s[i][j]];
        }

        private static void ShiftRows(List<byte[]> s)
        {
            (s[0][1], s[1][1], s[2][1], s[3][1]) = (s[1][1], s[2][1], s[3][1], s[0][1]);
            (s[0][2], s[1][2], s[2][2], s[3][2]) = (s[2][2], s[3][2], s[0][2], s[1][2]);
            (s[0][3], s[1][3], s[2][3], s[3][3]) = (s[3][3], s[0][3], s[1][3], s[2][3]);
        }

        private static void AddRoundKey(List<byte[]> s, byte[,] k)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    s[i][j] ^= k[i, j];
        }

        private static byte XTime(byte a) => (byte)(((a & 0x80) != 0) ? ((a << 1) ^ 0x1B) & 0xFF : (a << 1));

        private static void MixSingleColumn(byte[] a)
        {
            byte t = (byte)(a[0] ^ a[1] ^ a[2] ^ a[3]);
            byte u = a[0];
            a[0] ^= (byte)(t ^ XTime((byte)(a[0] ^ a[1])));
            a[1] ^= (byte)(t ^ XTime((byte)(a[1] ^ a[2])));
            a[2] ^= (byte)(t ^ XTime((byte)(a[2] ^ a[3])));
            a[3] ^= (byte)(t ^ XTime((byte)(a[3] ^ u)));
        }

        private static void MixColumns(List<byte[]> s)
        {
            for (int i = 0; i < 4; i++)
                MixSingleColumn(s[i]);
        }


        // key expansion
        private static List<byte[,]> ExpandKey()
        {
            int nRounds = 10; // 16 bytes keys
            byte[] masterKey = Key;
            List<byte[]> keyCols = BytesToMatrix(masterKey);
            int iterationSize = masterKey.Length / 4;
            int i = 1;
            byte[] rCon = new byte[] { 0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36, 0x6C, 0xD8, 0xAB, 0x4D, 0x9A, 0x2F, 0x5E, 0xBC, 0x63, 0xC6, 0x97, 0x35, 0x6A, 0xD4, 0xB3, 0x7D, 0xFA, 0xEF, 0xC5, 0x91, 0x39 };

            while (keyCols.Count < (nRounds + 1) * 4)
            {
                byte[] word = keyCols[^1].ToArray();

                if (keyCols.Count % iterationSize == 0)
                {
                    // rotate
                    var first = word[0];
                    Array.Copy(word, 1, word, 0, word.Length - 1);
                    word[^1] = first;

                    // sbox
                    for (int k = 0; k < 4; k++) word[k] = SBox[word[k]];

                    word[0] ^= rCon[i];
                    i++;
                }
                else if (masterKey.Length == 32 && keyCols.Count % iterationSize == 4)
                {
                    for (int k = 0; k < 4; k++) word[k] = SBox[word[k]];
                }

                byte[] prev = keyCols[keyCols.Count - iterationSize];
                for (int k = 0; k < 4; k++) word[k] ^= prev[k];

                keyCols.Add(word);
            }

            var res = new List<byte[,]>();

            for (int x = 0; x < keyCols.Count / 4; x++)
            {
                byte[,] m = new byte[4, 4];

                for (int c = 0; c < 4; c++)
                    for (int r = 0; r < 4; r++)
                        m[c, r] = keyCols[x * 4 + c][r];   // swap indices

                res.Add(m);
            }
            return res;
        }

        private static List<byte[]> BytesToMatrix(byte[] text)
        {
            var rows = new List<byte[]>();
            for (int i = 0; i < text.Length; i += 4)
                rows.Add(new byte[] { text[i], text[i + 1], text[i + 2], text[i + 3] });
            return rows;
        }

        private static byte[] MatrixToBytes(List<byte[]> m)
        {
            byte[] r = new byte[16];
            int idx = 0;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    r[idx++] = m[i][j];
            return r;
        }
    }