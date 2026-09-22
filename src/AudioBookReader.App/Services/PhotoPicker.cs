namespace AudioBookReader.App.Services;

/// <summary>
/// One picture, chosen from the phone's own gallery.
///
/// Separate from <see cref="BookFilePicker"/> on purpose. That one keeps a lasting reference to a
/// file somebody will come back to for months; this one hands over the bytes of a photograph that
/// is read once and forgotten, so it borrows none of the persistable-grant machinery and asks for
/// no permission at all.
/// </summary>
public partial class PhotoPicker
{
    /// <returns>The picture's bytes, or null when nothing was chosen.</returns>
    public partial Task<byte[]?> PickFromGalleryAsync();
}
