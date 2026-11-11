using System;
using System.Management;

namespace CCLite.Services;

/// <summary>
/// 广达模具 EC 通信缓冲区（与原版 ControlCenter 的 SMI_STRUCT_S 一致，共 32 字节）。
/// a0: 0xFA00=读 0xFB00=写；a1: 子命令码；a2~a6: 参数/返回值。
/// </summary>
public class GmWmiBuffer
{
    public ushort Op;
    public ushort Code;
    public uint A2, A3, A4, A5, A6, Rev0, Rev1;

    public byte[] ToBytes()
    {
        var b = new byte[32];
        b[0] = (byte)(Op & 0xFF); b[1] = (byte)(Op >> 8);
        b[2] = (byte)(Code & 0xFF); b[3] = (byte)(Code >> 8);
        void PutU32(int off, uint v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
        }
        PutU32(4, A2); PutU32(8, A3); PutU32(12, A4); PutU32(16, A5);
        PutU32(20, A6); PutU32(24, Rev0); PutU32(28, Rev1);
        return b;
    }

    public static GmWmiBuffer FromBytes(byte[] b)
    {
        var buf = new GmWmiBuffer();
        if (b == null || b.Length < 32) return buf;
        ushort GetU16(int off) => (ushort)(b[off] | (b[off + 1] << 8));
        uint GetU32(int off) => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        buf.Op = GetU16(0); buf.Code = GetU16(2);
        buf.A2 = GetU32(4); buf.A3 = GetU32(8); buf.A4 = GetU32(12);
        buf.A5 = GetU32(16); buf.A6 = GetU32(20); buf.Rev0 = GetU32(24); buf.Rev1 = GetU32(28);
        return buf;
    }

    public static GmWmiBuffer Write(ushort code) => new() { Op = 0xFB00, Code = code };
    public static GmWmiBuffer Read(ushort code) => new() { Op = 0xFA00, Code = code };
}

/// <summary>
/// root\wmi 命名空间 RW_GMWMI 类的 32 字节读写（写入触发 BIOS ACPI SMI → EC）。
/// 与原版 ControlCenter.exe 的 MyWmiBase 完全一致的调用方式。
/// </summary>
public static class GmWmiService
{
    private const string Namespace = "root\\wmi";
    private const string ClassName = "RW_GMWMI";

    private static ManagementScope _scope;
    private static bool _checked;
    private static readonly object _sync = new();   // 动效线程/监控线程并发写 EC 需串行

    /// <summary>机器是否支持该 WMI-EC 接口（非广达模具返回 false）。</summary>
    public static bool Available { get; private set; }

    public static bool EnsureInit()
    {
        if (_checked) return Available;
        _checked = true;
        try
        {
            _scope = new ManagementScope(Namespace);
            _scope.Connect();
            using var searcher = new ManagementObjectSearcher(_scope, new SelectQuery(ClassName));
            Available = searcher.Get().Count > 0;
        }
        catch
        {
            Available = false;
        }
        return Available;
    }

    /// <summary>
    /// 执行一次 EC 事务：写入 32 字节触发 ACPI → 回读结果填充到同一 buffer。
    /// </summary>
    public static bool Execute(GmWmiBuffer buf)
    {
        if (!EnsureInit()) return false;
        lock (_sync)
        {
        try
        {
            var data = buf.ToBytes();
            using var searcher = new ManagementObjectSearcher(_scope, new SelectQuery(ClassName));
            foreach (ManagementObject o in searcher.Get())
            {
                o.SetPropertyValue("BufferBytes", data);
                o.Put();
            }
            foreach (ManagementObject o in searcher.Get())
            {
                if (o["BufferBytes"] is byte[] ret && ret.Length == 32)
                {
                    var res = GmWmiBuffer.FromBytes(ret);
                    buf.A2 = res.A2; buf.A3 = res.A3; buf.A4 = res.A4;
                    buf.A5 = res.A5; buf.A6 = res.A6; buf.Rev0 = res.Rev0; buf.Rev1 = res.Rev1;
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
        }
    }
}
