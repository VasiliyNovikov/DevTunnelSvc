using System.Text;

namespace DevTunnelSvc.Platform;

internal sealed record TrayDialogContent(string Title, string Text, bool Scrollable = false);

internal static class TrayDialogTemplate
{
    internal const ushort IconId = 100;
    internal const ushort TextId = 101;

    // DLGTEMPLATE uses dialog units (not pixels) and DWORD-aligned DLGITEMTEMPLATE headers.
    internal static byte[] Build(TrayDialogContent content)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true);
        writer.Write(0x80C808C0u); // popup, caption, system menu, centered modal frame, explicit font
        writer.Write(0u);
        writer.Write((ushort)3);
        var (width, height) = content.Scrollable ? (360, 168) : (242, 84);
        Bounds(writer, (0, 0, width, height));
        writer.Write(0u); // no menu, default dialog class
        String(writer, content.Title);
        writer.Write((ushort)9);
        String(writer, "Segoe UI");

        Control(writer, IconId, 0x82, 0x243, (12, 12, 25, 25), ""); // SS_ICON | SS_REALSIZECONTROL | SS_CENTERIMAGE: never stretch
        var textBounds = (47, 12, width - 59, height - 43);
        if (content.Scrollable)
            Control(writer, TextId, 0x81, 0x210844, textBounds, content.Text); // read-only multiline edit, vertical scroll, tab stop
        else
            Control(writer, TextId, 0x82, 0x80, textBounds, content.Text); // SS_NOPREFIX: literal text
        Control(writer, 1, 0x80, 0x10001, (width - 62, height - 25, 50, 14), "OK");
        return buffer.ToArray();
    }

    private static void Control(BinaryWriter writer, ushort id, ushort atom, uint style,
        (int X, int Y, int Width, int Height) bounds, string caption)
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

    private static void Bounds(BinaryWriter writer, (int X, int Y, int Width, int Height) value)
    {
        writer.Write(checked((short)value.X));
        writer.Write(checked((short)value.Y));
        writer.Write(checked((short)value.Width));
        writer.Write(checked((short)value.Height));
    }

    private static void String(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.Unicode.GetBytes(value));
        writer.Write((ushort)0);
    }
}
