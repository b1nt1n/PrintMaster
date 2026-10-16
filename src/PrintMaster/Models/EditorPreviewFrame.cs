using System.Windows.Media.Imaging;

namespace PrintMaster.Models;

// The bitmap and its document identity are published together on the UI thread.
public sealed record EditorPreviewFrame(Guid PageId,int Revision,BitmapSource Bitmap);
