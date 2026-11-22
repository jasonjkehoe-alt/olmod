using HarmonyLib;
using Overload;
using System;
using System.IO;
using UnityEngine;

namespace GameMod
{
    // Fast user-level load (ported from Pyro 0.3.1, Nov 21, 2026; deep dive
    // report ~/workspace/pyro-031/PYRO_FINDINGS.md §7). Pyro's claim of
    // "faster loading of non stock levels by reading and buffering bigger
    // chunks at once" is this file, and stock OLMod has no equivalent.
    //
    // Two Harmony patches on OverloadLevelConvertSerializer, both picked up
    // by harmony.PatchAll(Assembly.GetExecutingAssembly()) in GameMod.cs --
    // no explicit registration call is needed, same as every other patch
    // in this folder.
    //
    //   1. BufferReads -- prefix on the (Stream) constructor: wraps the
    //      .sp deserialization stream in a 64 KB BufferedStream. Applies
    //      always, on clients and servers alike.
    //   2. FastArrays -- prefix on SerializeIn_array: replaces the stock
    //      element-by-element reflection loop with a bulk read (ReadFully)
    //      plus Buffer.BlockCopy for primitives and manual float reads for
    //      Vector2/3/4, Color, Quaternion, Color32, BoneWeight, Matrix4x4.
    //
    // GATE (kept from Pyro, do NOT widen): the fast-array path runs only
    // when GameplayManager.IsDedicatedServer() is true, EXCEPT for Color32
    // arrays, which take the fast path on clients too. On a normal
    // single-player client only the BufferedStream wrap and the Color32
    // fast path apply. Pyro gated this because the bulk decoder is not
    // proven bit-exact for all array types on clients. A port that widens
    // the gate is a FAIL.
    //
    // Failure mode: if ReadFully hits a truncated stream it throws
    // SerializationFailureException, exactly as the stock deserializer
    // would fail on the same corrupt data. There is no fail-open fallback
    // here (unlike the switch patch): once the bulk read has partially
    // consumed the stream, the stock method cannot re-read it.
    public static class FastUserLevelLoad
    {
        [HarmonyPatch(typeof(OverloadLevelConvertSerializer), MethodType.Constructor, new Type[] { typeof(Stream) })]
        public class BufferReads
        {
            private static void Prefix(ref Stream readStream)
            {
                if (readStream != null)
                {
                    readStream = new BufferedStream(readStream, 65536);
                }
            }
        }

        [HarmonyPatch(typeof(OverloadLevelConvertSerializer), "SerializeIn_array")]
        public class FastArrays
        {
            private static bool Prefix(OverloadLevelConvertSerializer __instance, Type elementType, Stream ___m_stream, ref Array __result)
            {
                int num = ElementSize(elementType);
                // Gate (kept from Pyro, do NOT widen): bulk-array path is
                // server-only except Color32. See the header comment.
                if (num == 0 || (!GameplayManager.IsDedicatedServer() && elementType != typeof(Color32)))
                {
                    return true;
                }
                int num2 = __instance.SerializeIn_int32();
                if (num2 == -1)
                {
                    __result = null;
                    return false;
                }
                byte[] array = new byte[num2 * num];
                ReadFully(___m_stream, array);
                __result = Decode(elementType, array, num2);
                return false;
            }
        }

        private static int ElementSize(Type t)
        {
            if (t == typeof(byte))
            {
                return 1;
            }
            if (t == typeof(ushort) || t == typeof(short))
            {
                return 2;
            }
            if (t == typeof(int) || t == typeof(uint) || t == typeof(float) || t == typeof(Color32))
            {
                return 4;
            }
            if (t == typeof(Vector2))
            {
                return 8;
            }
            if (t == typeof(Vector3))
            {
                return 12;
            }
            if (t == typeof(Vector4) || t == typeof(Color) || t == typeof(Quaternion))
            {
                return 16;
            }
            if (t == typeof(BoneWeight))
            {
                return 32;
            }
            if (t == typeof(Matrix4x4))
            {
                return 64;
            }
            return 0;
        }

        private static void ReadFully(Stream stream, byte[] data)
        {
            int num;
            for (int i = 0; i < data.Length; i += num)
            {
                num = stream.Read(data, i, data.Length - i);
                if (num <= 0)
                {
                    throw new SerializationFailureException();
                }
            }
        }

        private static float F(byte[] d, int at)
        {
            return BitConverter.ToSingle(d, at);
        }

        private static Vector4 V4(byte[] d, int at)
        {
            return new Vector4(F(d, at), F(d, at + 4), F(d, at + 8), F(d, at + 12));
        }

        private static Array Decode(Type t, byte[] d, int count)
        {
            if (t == typeof(byte))
            {
                return (byte[])d.Clone();
            }
            if (t == typeof(ushort) || t == typeof(short) || t == typeof(int) || t == typeof(uint) || t == typeof(float))
            {
                Array array = Array.CreateInstance(t, count);
                Buffer.BlockCopy(d, 0, array, 0, d.Length);
                return array;
            }
            if (t == typeof(Vector2))
            {
                Vector2[] array2 = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    array2[i] = new Vector2(F(d, i * 8), F(d, i * 8 + 4));
                }
                return array2;
            }
            if (t == typeof(Vector3))
            {
                Vector3[] array3 = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    array3[i] = new Vector3(F(d, i * 12), F(d, i * 12 + 4), F(d, i * 12 + 8));
                }
                return array3;
            }
            if (t == typeof(Vector4))
            {
                Vector4[] array4 = new Vector4[count];
                for (int i = 0; i < count; i++)
                {
                    array4[i] = V4(d, i * 16);
                }
                return array4;
            }
            if (t == typeof(Color))
            {
                Color[] array5 = new Color[count];
                for (int i = 0; i < count; i++)
                {
                    array5[i] = new Color(F(d, i * 16), F(d, i * 16 + 4), F(d, i * 16 + 8), F(d, i * 16 + 12));
                }
                return array5;
            }
            if (t == typeof(Quaternion))
            {
                Quaternion[] array6 = new Quaternion[count];
                for (int i = 0; i < count; i++)
                {
                    array6[i] = new Quaternion(F(d, i * 16), F(d, i * 16 + 4), F(d, i * 16 + 8), F(d, i * 16 + 12));
                }
                return array6;
            }
            if (t == typeof(Color32))
            {
                Color32[] array7 = new Color32[count];
                for (int i = 0; i < count; i++)
                {
                    array7[i] = new Color32(d[i * 4], d[i * 4 + 1], d[i * 4 + 2], d[i * 4 + 3]);
                }
                return array7;
            }
            if (t == typeof(BoneWeight))
            {
                BoneWeight[] array8 = new BoneWeight[count];
                for (int i = 0; i < count; i++)
                {
                    BoneWeight bw = new BoneWeight();
                    bw.boneIndex0 = BitConverter.ToInt32(d, i * 32);
                    bw.boneIndex1 = BitConverter.ToInt32(d, i * 32 + 4);
                    bw.boneIndex2 = BitConverter.ToInt32(d, i * 32 + 8);
                    bw.boneIndex3 = BitConverter.ToInt32(d, i * 32 + 12);
                    bw.weight0 = F(d, i * 32 + 16);
                    bw.weight1 = F(d, i * 32 + 20);
                    bw.weight2 = F(d, i * 32 + 24);
                    bw.weight3 = F(d, i * 32 + 28);
                    array8[i] = bw;
                }
                return array8;
            }
            Matrix4x4[] array9 = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                Matrix4x4 m = new Matrix4x4();
                m.SetRow(0, V4(d, i * 64));
                m.SetRow(1, V4(d, i * 64 + 16));
                m.SetRow(2, V4(d, i * 64 + 32));
                m.SetRow(3, V4(d, i * 64 + 48));
                array9[i] = m;
            }
            return array9;
        }
    }
}
