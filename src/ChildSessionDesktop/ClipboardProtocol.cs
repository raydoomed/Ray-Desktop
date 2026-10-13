using System;
using System.IO;

namespace ChildSessionDesktop
{
    // 剪贴板信箱文件格式：magic(int32) + version(int32=2) + guid(16B) + text(string) + count(int32) + count×path(string)。
    // 解析为纯函数（从 BinaryReader 读），IO（文件打开/路径）留在调用方，便于单元测试字节级边界。
    internal static class ClipboardProtocol
    {
        internal const int Magic = 0x31465344;
        internal const int Version = 2;
        internal const int MaxPaths = 4096;

        // 从 reader 解析一条信箱消息；格式不符/损坏返回 false，并保持 out 参数为安全默认值。
        internal static bool Parse(BinaryReader reader, out Guid messageId, out string[] paths, out string text)
        {
            messageId = Guid.Empty;
            paths = new string[0];
            text = null;
            if (reader.ReadInt32() != Magic) return false;
            if (reader.ReadInt32() != Version) return false;
            byte[] idBytes = reader.ReadBytes(16);
            if (idBytes.Length != 16) return false;
            messageId = new Guid(idBytes);
            text = reader.ReadString();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxPaths) return false;
            paths = new string[count];
            for (int i = 0; i < count; i++) paths[i] = reader.ReadString();
            return true;
        }
    }
}
