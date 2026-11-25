// mirrors cairo.h and cairo-xlib.h
// ReSharper disable InconsistentNaming
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Linux;

internal static partial class CairoNativeMethods
{
    private const string Cairo = "libcairo.so.2";

    internal const int CAIRO_STATUS_SUCCESS = 0;
    internal const int CAIRO_FORMAT_ARGB32 = 0;
    internal const int CAIRO_FONT_SLANT_NORMAL = 0;
    internal const int CAIRO_FONT_WEIGHT_BOLD = 1;
    internal const int CAIRO_LINE_JOIN_ROUND = 1;
    internal const int CAIRO_OPERATOR_SOURCE = 1;
    internal const int CAIRO_OPERATOR_OVER = 2;

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint cairo_image_surface_create(int format, int width, int height);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint cairo_image_surface_get_data(nint surface);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int cairo_image_surface_get_stride(nint surface);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint cairo_xlib_surface_create(nint display, nint drawable, nint visual, int width, int height);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_surface_flush(nint surface);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_surface_destroy(nint surface);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint cairo_create(nint surface);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int cairo_status(nint cr);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_destroy(nint cr);

    [LibraryImport(Cairo, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_select_font_face(nint cr, string family, int slant, int weight);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_set_font_size(nint cr, double size);

    [LibraryImport(Cairo, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_text_path(nint cr, string utf8);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_path_extents(nint cr, out double x1, out double y1, out double x2, out double y2);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_move_to(nint cr, double x, double y);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_set_source_rgba(nint cr, double red, double green, double blue, double alpha);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_set_source_surface(nint cr, nint surface, double x, double y);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_set_operator(nint cr, int op);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_set_line_width(nint cr, double width);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_set_line_join(nint cr, int join);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_stroke_preserve(nint cr);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_fill_preserve(nint cr);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_stroke(nint cr);

    [LibraryImport(Cairo)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cairo_paint(nint cr);
}
