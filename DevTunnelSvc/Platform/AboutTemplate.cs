using System.Text;

namespace DevTunnelSvc.Platform;

internal static class AboutTemplate
{
    internal const ushort IconId = 100;

    // DLGTEMPLATE uses dialog units (not pixels) and DWORD-aligned DLGITEMTEMPLATE headers.
    internal static byte[] Build(string message)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true);
        writer.Write(0x80C808C0u); // popup, caption, system menu, centered modal frame, explicit font
        writer.Write(0u);
        writer.Write((ushort)3);
        Bounds(writer, (0, 0, 242, 84));
        writer.Write(0u); // no menu, default dialog class
        String(writer, "About DevTunnelSvc");
        writer.Write((ushort)9);
        String(writer, "Segoe UI");

        Control(writer, IconId, 0x82, 0x43, (12, 12, 25, 25), ""); // static icon
        Control(writer, 101, 0x82, 0x80, (47, 12, 183, 41), message); // SS_NOPREFIX: version is literal text
        Control(writer, 1, 0x80, 0x10001, (180, 59, 50, 14), "OK"); // tab stop, default pushbutton
        return buffer.ToArray();
    }

    private static void Control(BinaryWriter writer, ushort id, ushort atom, uint style,
        (short X, short Y, short Width, short Height) bounds, string caption)
    {
        if ((writer.BaseStream.Position & 2) != 0) writer.Write((ushort)0);
        writer.Write(0x50000000u | style); // WS_CHILD | WS_VISIBLE
        writer.Write(0u);
        Bounds(writer, bounds);
        writer.Write(id);
        writer.Write(ushort.MaxValue);
        writer.Write(atom);
        String(writer, caption);
        writer.Write((ushort)0); // no creation data
    }

    private static void Bounds(BinaryWriter writer, (short X, short Y, short Width, short Height) value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Width);
        writer.Write(value.Height);
    }

    private static void String(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.Unicode.GetBytes(value));
        writer.Write((ushort)0);
    }
}
